using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Persistence;
using ToroSquad.Modules.Letterboxd.Providers;

namespace ToroSquad.Modules.Letterboxd.Application;

/// <summary>What one feed round changed (logs, doctor and tests).</summary>
public sealed record LetterboxdRoundSummary(int Items, int NewItems, int Staged, int Edits, bool BaselineEstablished, bool CoverageGap, int Conflicts = 0);

/// <summary>
/// Applies one feed result and plans the cards — everything in ONE SaveChanges (one SQLite transaction): the entries, the
/// feed validators and counters, the per-guild delivery records and the outbox rows. If that transaction fails nothing of it
/// is kept, so new validators can never hide entries that were not stored.
/// <list type="bullet">
/// <item>Mode activation: the effective mode (Off/DryRun/Live) and the instant it became active
/// (<see cref="LetterboxdSourceStateEntity.ModeSince"/>) are stored by <see cref="RecordModeAsync"/> BEFORE the first request
/// in that mode, and every guild window starts there. A restart in the same mode changes nothing.</item>
/// <item>History: in the first successful read of a mode (the very first read included) an entry seen for the first time is
/// history — never sent — unless the feed dates it (&lt;pubDate&gt;) at or after the mode became active; such an entry is new
/// even when the first successful read comes later (failed requests, restart). An undated entry is history. In that same
/// read a change of an already known entry cannot be dated (the feed has no edit time): it edits an existing card but never
/// creates a card, so nothing from Off or DryRun is moved to Live in bulk. A failed, empty or unproven (304 without stored
/// state) answer establishes nothing.</item>
/// <item>Identity is the account + viewing id (guid "letterboxd-watch-N" and "letterboxd-review-N" are the same N), never the
/// film: a new rewatch is a new entry and a new card; a review added to an entry is the same entry. A stored entry whose
/// film identity (TMDB id, else the film slug) differs from the item is a conflict: the item is ignored, nothing merged.</item>
/// <item>A card is due when the entry is worth one (review, rewatch or rating) and became so after the guild's window start:
/// new, or a later change of what the member controls (rating, rewatch, watched date, review) — also for an entry seen
/// before without a card. Metadata changes (film title, year, poster, link) only edit an existing card. New cards are
/// bounded by <see cref="LetterboxdOptions.CatchUpHours"/> (published or changed within it) and
/// <see cref="LetterboxdOptions.MaxCardsPerRound"/>.</item>
/// <item>A changed card-visible field of an already delivered entry re-stages the same logical key: the outbox edits the same
/// message silently (identical payload = no edit). An entry that left the feed window is not "deleted": nothing is removed.</item>
/// <item>One guild gets an entry at most once per mode (dry-run records never count as live), whatever channel it had then.</item>
/// </list>
/// </summary>
public sealed class LetterboxdPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    IGuildSettingsStore guildSettings,
    LetterboxdCardRenderer renderer,
    IOptions<LetterboxdOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<LetterboxdPlanner> logger)
{
    public const string EntryKind = "entry";

    /// <summary>
    /// Kind of a dry-run card. The delivery policy only sees guild, channel and kind (it runs before the outbox looks at
    /// IsDryRun), so the kind tells it which cards may still be simulated in DryRun.
    /// </summary>
    public const string DryRunEntryKind = "entry-dry";

    public static string StateKey(string account) => "rss:" + account;

    public static string SourceKey(string account, long viewingId) =>
        "letterboxd:" + account + ":" + viewingId.ToString(CultureInfo.InvariantCulture);

    public LetterboxdMode EffectiveMode => LetterboxdModes.Effective(options.Value, delivery.Value);

    public bool DryRun => EffectiveMode == LetterboxdMode.DryRun;

    public string Account => options.Value.Account;

    public async Task<LetterboxdSourceStateEntity> StateAsync(CancellationToken ct)
    {
        var key = StateKey(Account);
        var set = db.Set<LetterboxdSourceStateEntity>();
        var row = set.Local.FirstOrDefault(s => s.Key == key) ?? await set.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new LetterboxdSourceStateEntity { Key = key };
            set.Add(row);
        }

        return row;
    }

    /// <summary>
    /// Stores the effective mode and the instant it became active, without a request (also Off, which never polls). Called
    /// at host start and before every round, so the activation instant is durable before the first request in a new mode.
    /// A change restarts every guild window of the account at that instant. Off on a fresh database writes nothing.
    /// </summary>
    public async Task RecordModeAsync(CancellationToken ct)
    {
        var mode = EffectiveMode;
        var key = StateKey(Account);
        var exists = db.Set<LetterboxdSourceStateEntity>().Local.Any(s => s.Key == key) ||
                     await db.Set<LetterboxdSourceStateEntity>().AnyAsync(s => s.Key == key, ct);
        if (!exists && mode == LetterboxdMode.Off)
            return;
        var state = await StateAsync(ct);
        if (state.LastMode == (int)mode && state.ModeSince is not null)
            return;
        await ActivateAsync(state, mode, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task ActivateAsync(LetterboxdSourceStateEntity state, LetterboxdMode mode, DateTimeOffset at, CancellationToken ct)
    {
        var previous = state.LastMode;
        state.LastMode = (int)mode;
        state.ModeSince = at;
        if (previous is null)
            return; // first activation: the guild windows were started by configure
        foreach (var config in await db.Set<LetterboxdGuildConfigEntity>().Where(c => c.Account == Account).ToListAsync(ct))
        {
            config.LiveSince = at;
            config.DryRunSince = at;
        }

        logger.LogInformation("letterboxd mode {Previous} → {Mode} active since {At:u}: guild windows restart there", (LetterboxdMode)previous, mode, at);
    }

    /// <summary>True when at least one guild would receive cards now (module on, channel set, same account, not paused).</summary>
    public async Task<bool> HasActiveGuildAsync(CancellationToken ct)
    {
        var account = Account;
        foreach (var config in await db.Set<LetterboxdGuildConfigEntity>().AsNoTracking()
                     .Where(c => c.ChannelId != null && !c.Paused && c.Account == account).ToListAsync(ct))
        {
            if (await gate.IsEnabledAsync(new GuildId(config.GuildId), LetterboxdModule.ModuleIdTyped, ct))
                return true;
        }

        return false;
    }

    public async Task<LetterboxdRoundSummary> ApplyAsync(FeedFetchResult result, DateTimeOffset nextPollAt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var account = Account;
        var mode = EffectiveMode;
        var state = await StateAsync(ct);
        if (state.LastMode != (int)mode || state.ModeSince is null)
            await ActivateAsync(state, mode, now, ct); // not recorded before the request (direct callers): active from now
        state.LastAttemptAt = now;
        state.LastHttpStatus = result.HttpStatus;
        state.NextPollAt = nextPollAt;

        var outcome = result.Outcome;
        if (outcome == FeedOutcome.NotModified && state.BaselineAt is null)
            outcome = FeedOutcome.Malformed; // a 304 without stored content proves nothing

        if (!result.Succeeded || outcome == FeedOutcome.Malformed)
        {
            // Nothing else moves: no validators, no baseline, no entry is marked gone.
            state.LastOutcome = (int)outcome;
            state.LastDetail = Clip(result.Detail ?? outcome.ToString(), 300);
            state.ConsecutiveFailures++;
            await db.SaveChangesAsync(ct);
            return new LetterboxdRoundSummary(0, 0, 0, 0, false, false);
        }

        var activeSince = state.ModeSince!.Value;
        // The first successful read of this mode (the very first read included): undatable changes are history.
        var firstRead = state.LastSuccessAt is null || state.LastSuccessAt < activeSince;
        var changed = new List<long>();
        var newItems = 0;
        var conflicts = result.Skipped.Conflict;
        var baselineNow = false;
        var gap = false;
        if (outcome == FeedOutcome.Ok)
        {
            var ids = result.Items.Select(i => i.ViewingId).ToList();
            var known = await db.Set<LetterboxdEntryEntity>().Where(e => e.Account == account && ids.Contains(e.ViewingId)).ToDictionaryAsync(e => e.ViewingId, ct);
            var knownBefore = known.Count;
            foreach (var item in result.Items)
            {
                var view = item.View;
                if (!known.TryGetValue(item.ViewingId, out var row))
                {
                    var history = firstRead && !(view.PublishedAt >= activeSince);
                    var baseline = history || item.ViewingId <= state.PrunedBelowViewingId;
                    row = new LetterboxdEntryEntity
                    {
                        Account = account,
                        ViewingId = item.ViewingId,
                        FirstSeenAt = now,
                        Baseline = baseline,
                        EligibleAt = !baseline && view.Postable ? now : null,
                    };
                    Apply(row, item, now);
                    db.Set<LetterboxdEntryEntity>().Add(row);
                    known[item.ViewingId] = row;
                    newItems++;
                    continue;
                }

                if (!SameFilm(row, item))
                {
                    // The same viewing id now names another film: never merged, the stored entry and its card stay as they are.
                    conflicts++;
                    logger.LogWarning("letterboxd identity conflict: entry {Entry} names another film in the feed; item ignored", item.ViewingId);
                    continue;
                }

                row.LastSeenAt = now;
                if (row.ContentHash == view.ContentHash)
                    continue;
                var memberChanged = row.MemberHash != view.MemberHash;
                Apply(row, item, now);
                row.ContentChangedAt = now;
                if (memberChanged && view.Postable && !firstRead)
                    row.EligibleAt = now; // a new action of the member on this entry (an existing card is edited instead)
                changed.Add(item.ViewingId);
            }

            // The feed has no paging: a full window sharing no entry with what we knew MAY have skipped entries (how many is unknown).
            if (state.BaselineAt is not null && knownBefore == 0 && result.TotalItems >= Math.Max(1, state.MaxItemCount))
            {
                gap = true;
                state.CoverageGapAt = now;
                logger.LogWarning("letterboxd potential coverage gap: {Count} feed items and none of them known before", result.Items.Count);
            }

            if (state.BaselineAt is null && result.Items.Count > 0)
            {
                baselineNow = true;
                state.BaselineAt = now;
                logger.LogInformation("letterboxd baseline with {Count} feed entries (only entries dated after {Since:u} can be posted)", result.Items.Count, activeSince);
            }

            state.ETag = Clip(result.ETag, 200);
            state.LastModified = Clip(result.LastModified, 100);
            state.TtlMinutes = result.TtlMinutes;
            state.LastItemCount = result.Items.Count;
            state.LastSkippedCount = result.Skipped.Total;
            state.LastNewCount = newItems;
            state.MaxItemCount = Math.Max(state.MaxItemCount, result.TotalItems);
            state.OldestItemPublishedAt = result.Items.Min(i => i.View.PublishedAt);
        }
        else if (outcome == FeedOutcome.Empty)
        {
            state.LastItemCount = 0;
            state.LastSkippedCount = 0;
            state.LastNewCount = 0;
        }

        state.LastConflictCount = conflicts;
        state.LastOutcome = (int)outcome;
        state.LastDetail = Clip(result.Detail, 300);
        state.LastSuccessAt = now;
        state.ConsecutiveFailures = 0;

        var (staged, edits) = state.BaselineAt is null ? (0, 0) : await PlanDeliveriesAsync(state, changed, now, ct);
        await PruneAsync(state, now, ct);
        await db.SaveChangesAsync(ct);
        return new LetterboxdRoundSummary(outcome == FeedOutcome.Ok ? result.Items.Count : 0, newItems, staged, edits, baselineNow, gap, conflicts);
    }

    /// <summary>Same film: TMDB ids when both sides have one, else the film slug (a slug rename with the same TMDB id is the same film).</summary>
    private static bool SameFilm(LetterboxdEntryEntity row, LetterboxdEntry item)
    {
        if (row.TmdbMovieId is not null || row.TmdbTvId is not null)
        {
            if (item.TmdbMovieId is not null || item.TmdbTvId is not null)
                return row.TmdbMovieId == item.TmdbMovieId && row.TmdbTvId == item.TmdbTvId;
        }

        return string.Equals(row.FilmSlug, item.FilmSlug, StringComparison.Ordinal);
    }

    private static void Apply(LetterboxdEntryEntity row, LetterboxdEntry item, DateTimeOffset now)
    {
        var view = item.View;
        row.GuidKind = (int)item.GuidKind;
        row.FilmSlug = item.FilmSlug;
        row.TmdbMovieId = item.TmdbMovieId;
        row.TmdbTvId = item.TmdbTvId;
        row.Url = view.Url;
        row.FilmTitle = view.FilmTitle;
        row.FilmYear = view.FilmYear;
        row.RatingHalves = view.RatingHalves;
        row.Rewatch = view.Rewatch;
        row.WatchedDate = view.WatchedDate;
        row.PublishedAt = view.PublishedAt;
        row.ReviewExcerpt = view.Review;
        row.ReviewTruncated = view.ReviewTruncated;
        row.Spoilers = view.Spoilers;
        row.PosterUrl = view.PosterUrl;
        row.HadReview = view.HasReview;
        row.ContentHash = view.ContentHash;
        row.MemberHash = view.MemberHash;
        row.LastSeenAt = now;
    }

    private async Task<(int Staged, int Edits)> PlanDeliveriesAsync(LetterboxdSourceStateEntity state, IReadOnlyList<long> changed, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var mode = EffectiveMode;
        if (mode == LetterboxdMode.Off)
            return (0, 0);
        var dryRun = mode == LetterboxdMode.DryRun;
        var account = Account;
        var catchUpFrom = now - TimeSpan.FromHours(o.CatchUpHours);
        var staged = 0;
        var edits = 0;
        var entries = db.Set<LetterboxdEntryEntity>();
        var deliveries = db.Set<LetterboxdDeliveryEntity>();
        foreach (var config in await db.Set<LetterboxdGuildConfigEntity>().Where(c => c.ChannelId != null && !c.Paused && c.Account == account).ToListAsync(ct))
        {
            var guild = new GuildId(config.GuildId);
            if (!await gate.IsEnabledAsync(guild, LetterboxdModule.ModuleIdTyped, ct))
                continue;
            var channel = new ChannelId(config.ChannelId!.Value);
            var language = (await guildSettings.GetAsync(guild, ct)).Language;
            var kind = dryRun ? DryRunEntryKind : EntryKind;
            var since = dryRun ? (config.DryRunSince ??= now) : (config.LiveSince ??= now);
            var delivered = await deliveries.AsNoTracking().Where(d => d.GuildId == config.GuildId && d.Account == account && d.DryRun == dryRun)
                .Select(d => d.ViewingId).ToListAsync(ct);

            // Tracked rows (this round's new and changed entries) win over their stored versions.
            var tracked = entries.Local.Where(e => e.Account == account).ToList();
            var trackedIds = tracked.Select(e => e.ViewingId).ToList();
            var stored = await entries.AsNoTracking()
                .Where(e => e.Account == account && e.EligibleAt != null && e.EligibleAt >= since && !delivered.Contains(e.ViewingId) && !trackedIds.Contains(e.ViewingId))
                .ToListAsync(ct);
            var notBefore = since > catchUpFrom ? since : catchUpFrom;
            var candidates = tracked.Concat(stored)
                .Where(e => e.EligibleAt is { } at && at >= notBefore && !delivered.Contains(e.ViewingId) && e.View().Postable &&
                            // A new entry must also be recent itself; a change of an older entry counts from when it was seen.
                            (at > e.FirstSeenAt || (e.PublishedAt ?? e.FirstSeenAt) >= notBefore))
                .OrderBy(e => e.PublishedAt ?? e.EligibleAt).ThenBy(e => e.ViewingId)
                .Take(o.MaxCardsPerRound)
                .ToList();
            foreach (var entry in candidates)
            {
                var outcome = await outbox.StageAsync(new NotificationRequest(guild, LetterboxdModule.ModuleIdTyped, SourceKey(account, entry.ViewingId), channel, kind,
                    renderer.Render(entry.View(), language), now + TimeSpan.FromHours(o.CatchUpHours), dryRun), ct);
                deliveries.Add(new LetterboxdDeliveryEntity
                {
                    GuildId = config.GuildId,
                    Account = account,
                    ViewingId = entry.ViewingId,
                    DryRun = dryRun,
                    ChannelId = channel.Value,
                    Kind = kind,
                    StagedAt = now,
                });
                staged++;
                state.LastDeliveryStagedAt = now;
                logger.LogInformation("letterboxd card staged guild={Guild} entry={Entry} card={Card} dryRun={DryRun} outcome={Outcome}",
                    config.GuildId, entry.ViewingId, entry.View().CardKind, dryRun, outcome);
            }

            if (changed.Count == 0)
                continue;
            // Corrections of already delivered entries: the same logical key (its original channel) → a silent edit.
            foreach (var record in await deliveries.AsNoTracking()
                         .Where(d => d.GuildId == config.GuildId && d.Account == account && d.DryRun == dryRun && changed.Contains(d.ViewingId)).ToListAsync(ct))
            {
                var entry = entries.Local.FirstOrDefault(e => e.Account == account && e.ViewingId == record.ViewingId);
                if (entry is null)
                    continue;
                var outcome = await outbox.StageAsync(new NotificationRequest(guild, LetterboxdModule.ModuleIdTyped, SourceKey(account, entry.ViewingId),
                    new ChannelId(record.ChannelId), record.Kind, renderer.Render(entry.View(), language), now + TimeSpan.FromHours(o.CatchUpHours), dryRun), ct);
                if (outcome is StageOutcome.EditScheduled or StageOutcome.UpdatedPending)
                {
                    edits++;
                    logger.LogInformation("letterboxd card updated guild={Guild} entry={Entry} outcome={Outcome} (edit, no mention)", config.GuildId, entry.ViewingId, outcome);
                }
            }
        }

        return (staged, edits);
    }

    /// <summary>
    /// Review excerpts are dropped after TextRetentionDays without the entry in the feed; ids and delivery records after
    /// DedupRetentionDays. Pruned ids stay covered by a watermark: an entry at or below it is baseline if it ever reappears.
    /// An entry that merely left the feed window is kept as is (never "deleted", its card is never touched).
    /// </summary>
    private async Task PruneAsync(LetterboxdSourceStateEntity state, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var account = Account;
        var textCutoff = now - TimeSpan.FromDays(o.TextRetentionDays);
        var idCutoff = now - TimeSpan.FromDays(o.DedupRetentionDays);
        foreach (var old in await db.Set<LetterboxdEntryEntity>().Where(e => e.Account == account && e.ReviewExcerpt != null && e.LastSeenAt < textCutoff).ToListAsync(ct))
            old.ReviewExcerpt = null;

        var expired = await db.Set<LetterboxdEntryEntity>().Where(e => e.Account == account && e.LastSeenAt < idCutoff).ToListAsync(ct);
        if (expired.Count > 0)
        {
            state.PrunedBelowViewingId = Math.Max(state.PrunedBelowViewingId, expired.Max(e => e.ViewingId));
            db.Set<LetterboxdEntryEntity>().RemoveRange(expired);
        }

        db.Set<LetterboxdDeliveryEntity>().RemoveRange(await db.Set<LetterboxdDeliveryEntity>().Where(d => d.StagedAt < idCutoff).ToListAsync(ct));
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
