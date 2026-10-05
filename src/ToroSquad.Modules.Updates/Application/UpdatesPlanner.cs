using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Persistence;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>What one round for one game changed (logs, status and tests).</summary>
public sealed record UpdatesRoundSummary(int Items, int NewItems, int Updates, int Staged, int Edits, bool BaselineEstablished);

/// <summary>The delivery window of a guild: when a mode's window starts, and the one rule for mode changes.</summary>
public static class UpdatesWindow
{
    /// <summary>
    /// Called wherever a guild is about to be planned (and once at startup). If the guild was last planned in another mode,
    /// the window of the mode now running starts at <paramref name="now"/>: nothing seen while DryRun or Off can be posted live
    /// later, and the other way round. The same mode again (a plain restart) changes nothing, so an outage is caught up.
    /// </summary>
    public static bool Enter(UpdatesGuildConfigEntity config, UpdatesMode mode, DateTimeOffset now)
    {
        if (config.PlannedMode == (int)mode)
            return false;
        if (mode == UpdatesMode.Live)
            config.LiveSince = now;
        else if (mode == UpdatesMode.DryRun)
            config.DryRunSince = now;
        config.PlannedMode = (int)mode;
        return true;
    }

    /// <summary>First channel, resume, module re-enabled: both windows start now (nothing from before is ever caught up).</summary>
    public static void Restart(UpdatesGuildConfigEntity config, UpdatesMode mode, DateTimeOffset now)
    {
        config.LiveSince = now;
        config.DryRunSince = now;
        config.PlannedMode = (int)mode;
    }
}

/// <summary>
/// Applies one provider answer for one game and plans the cards — everything in ONE SaveChanges (one SQLite transaction):
/// the posts with their classification, the source state, the per-guild delivery records and the outbox rows. If that
/// transaction fails nothing of it is kept, so a new source state can never hide posts that were not stored.
/// <list type="bullet">
/// <item>Baseline: every post of the first valid, non-empty answer is baseline and never posted, in any mode; a failed or
/// empty first answer establishes nothing. A game that later moves to another place at its provider takes no second
/// baseline: posts published there before the move are history, posts published after it are new.</item>
/// <item>Identity is provider + game + the provider's post id. A guild gets a post at most once per kind (dry-run records
/// never count as live), whatever channel it had then — a channel change never posts again.</item>
/// <item>Only posts classified <see cref="UpdateClassification.Update"/> are planned. A guild gets those first seen AND
/// published after its window started (first channel, resume, module or game enabled, mode change), published within
/// <see cref="UpdatesOptions.CatchUpHours"/>, at most <see cref="UpdatesOptions.MaxCardsPerRound"/> per round. A post without a provider
/// publication time is never posted.</item>
/// <item>A changed title, link or time of an update the guild has in its current channel re-stages the same logical key
/// in that round: the outbox edits the same message silently (identical payload = no edit). A correction never creates a
/// card. A post that stops being an update is recorded, never deleted.</item>
/// </list>
/// The only class of this module that writes to the outbox.
/// </summary>
public sealed class UpdatesPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    IGuildSettingsStore guildSettings,
    GameUpdateCatalog catalog,
    UpdateCardRenderer renderer,
    IOptions<UpdatesOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<UpdatesPlanner> logger)
{
    public const string LiveKindPrefix = "update";
    public const string DryRunKindPrefix = "update-dry";

    /// <summary>
    /// The outbox kind of a card. The delivery policy only sees guild, channel and kind (and runs before the outbox looks at
    /// IsDryRun), so the kind says both whether the card is a simulation and which game it belongs to.
    /// </summary>
    public static string Kind(string gameKey, bool dryRun) => (dryRun ? DryRunKindPrefix : LiveKindPrefix) + ":" + gameKey;

    public static bool TryParseKind(string kind, out bool dryRun, out string gameKey)
    {
        dryRun = false;
        gameKey = "";
        var colon = kind.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == kind.Length - 1)
            return false;
        var prefix = kind[..colon];
        if (prefix != LiveKindPrefix && prefix != DryRunKindPrefix)
            return false;
        dryRun = prefix == DryRunKindPrefix;
        gameKey = kind[(colon + 1)..];
        return true;
    }

    /// <summary>The outbox source key of a post, e.g. <c>steam:730:1845383656387875</c>.</summary>
    public static string SourceKey(GameUpdateDefinition game, string externalId) => game.Provider + ":" + game.ProviderGameId + ":" + externalId;

    /// <summary>Updates:Mode, with Live reduced to DryRun while the host's Delivery:Mode does not send.</summary>
    public UpdatesMode EffectiveMode => UpdatesModes.Effective(options.Value, delivery.Value);

    /// <summary>Starts the running mode's window for every guild that was last planned in another mode (also when Off).</summary>
    public async Task EnterModeAsync(CancellationToken ct)
    {
        var mode = EffectiveMode;
        var now = clock.GetUtcNow();
        var changed = 0;
        foreach (var config in await db.Set<UpdatesGuildConfigEntity>().ToListAsync(ct))
        {
            if (UpdatesWindow.Enter(config, mode, now))
                changed++;
        }

        if (changed == 0)
            return;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("updates mode is {Mode}: delivery window restarted for {Guilds} guild(s) (nothing from the previous mode is posted)", mode, changed);
    }

    /// <summary>
    /// Records, for every registered game, that it now lives at another place of its provider (another forum category, another
    /// app id) — at startup, before anything is requested, because configuration only changes with a restart. See
    /// <see cref="MoveSource"/>.
    /// </summary>
    public async Task EnterSourcesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var moved = false;
        foreach (var state in await db.Set<UpdatesSourceStateEntity>().ToListAsync(ct))
        {
            if (catalog.Find(state.GameKey) is { } game && game.Provider == state.Provider)
                moved |= MoveSource(state, game, now);
        }

        if (moved)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A game that was already followed moved to another place at its provider. Nothing is thrown away and no second
    /// "first answer" baseline is taken (that would swallow a real update published before the first successful answer
    /// from the new place): the move itself is the line. Whatever was published there up to now is history and never
    /// posted; whatever is published from now on is new. The old backoff and poll time belonged to the old place.
    /// A source without a baseline has no history to protect: its first valid answer is its baseline as always.
    /// </summary>
    private bool MoveSource(UpdatesSourceStateEntity state, GameUpdateDefinition game, DateTimeOffset now)
    {
        if (state.ProviderGameId == game.ProviderGameId)
            return false;
        logger.LogInformation("{Game} update source moved at its provider ({From} -> {To}): posts published until now are history, later ones are new",
            game.Key, state.ProviderGameId, game.ProviderGameId);
        state.ProviderGameId = game.ProviderGameId;
        if (state.BaselineAt is not null && (state.PrunedThroughPublishedAt is not { } watermark || watermark < now))
            state.PrunedThroughPublishedAt = now;
        state.NextPollAt = null;
        state.ConsecutiveFailures = 0;
        return true;
    }

    /// <summary>
    /// What a round may use of what the module knows.
    /// <para>The catch-up point (<see cref="UpdateFetchContext.Since"/>): the last round that answered completely
    /// (<see cref="UpdatesSourceStateEntity.LastSuccessAt"/>), never further back than <see cref="UpdatesOptions.CatchUpHours"/>
    /// — nothing older could be posted anyway. A source that only shows its newest posts looks back that far after an
    /// outage, within its own bounds.</para>
    /// <para>The threads the provider should keep reading: those of posts verified as updates within the provider's follow
    /// window, most recent first, at most the provider's maximum — next to the threads the game's definition already names
    /// (those are read anyway and take none of the places). Derived from the stored posts on every round — a thread retires
    /// by itself once its last verified update is older than the window. Empty for providers that follow no threads.</para>
    /// </summary>
    public async Task<UpdateFetchContext> FetchContextAsync(GameUpdateDefinition game, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var states = db.Set<UpdatesSourceStateEntity>();
        var state = states.Local.FirstOrDefault(s => s.Provider == game.Provider && s.GameKey == game.Key)
                    ?? await states.AsNoTracking().FirstOrDefaultAsync(s => s.Provider == game.Provider && s.GameKey == game.Key, ct);
        var floor = now - TimeSpan.FromHours(options.Value.CatchUpHours);
        DateTimeOffset? since = state?.LastSuccessAt is { } complete ? (complete > floor ? complete : floor) : null;

        IReadOnlyList<string> threads = [];
        var provider = catalog.ProviderOf(game);
        if (provider.ThreadFollow is { MaxThreads: > 0 } rule && rule.Window > TimeSpan.Zero)
        {
            var cutoff = now - rule.Window;
            const int update = (int)UpdateClassification.Update;
            var recent = await db.Set<UpdatesItemEntity>().AsNoTracking()
                .Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.Classification == update && a.PublishedAt != null && a.PublishedAt >= cutoff)
                .OrderByDescending(a => a.PublishedAt).ThenBy(a => a.ExternalId)
                .Select(a => a.ExternalId)
                .Take(MaxFollowScan)
                .ToListAsync(ct);
            threads = recent.Select(provider.ThreadIdOf).OfType<string>().Distinct(StringComparer.Ordinal)
                .Where(thread => !game.WatchedThreadIds.Contains(thread, StringComparer.Ordinal))
                .Take(rule.MaxThreads).ToList();
        }

        return threads.Count == 0 && since is null ? UpdateFetchContext.None : new UpdateFetchContext(threads, since);
    }

    /// <summary>Upper bound of recent update posts looked at to find the threads to follow.</summary>
    public const int MaxFollowScan = 200;

    public async Task<UpdatesSourceStateEntity> StateAsync(GameUpdateDefinition game, CancellationToken ct)
    {
        var set = db.Set<UpdatesSourceStateEntity>();
        var row = set.Local.FirstOrDefault(s => s.Provider == game.Provider && s.GameKey == game.Key)
                  ?? await set.FirstOrDefaultAsync(s => s.Provider == game.Provider && s.GameKey == game.Key, ct);
        if (row is null)
        {
            row = new UpdatesSourceStateEntity { Provider = game.Provider, GameKey = game.Key, ProviderGameId = game.ProviderGameId };
            set.Add(row);
        }

        return row;
    }

    /// <summary>True when at least one guild would receive cards for this game now (module on, channel set, not paused, game on).</summary>
    public async Task<bool> HasActiveGuildAsync(GameUpdateDefinition game, CancellationToken ct)
    {
        var guilds = await (from s in db.Set<UpdatesSubscriptionEntity>().AsNoTracking()
                            join c in db.Set<UpdatesGuildConfigEntity>().AsNoTracking() on s.GuildId equals c.GuildId
                            where s.GameKey == game.Key && s.Enabled && c.ChannelId != null && !c.Paused
                            orderby s.GuildId
                            select s.GuildId).ToListAsync(ct);
        foreach (var guild in guilds)
        {
            if (await gate.IsEnabledAsync(new GuildId(guild), UpdatesModule.ModuleIdTyped, ct))
                return true;
        }

        return false;
    }

    public async Task<UpdatesRoundSummary> ApplyAsync(GameUpdateDefinition game, UpdateFetchResult result, DateTimeOffset nextPollAt, CancellationToken ct)
    {
        // A provider answers for exactly the game it was asked about; anything else in the list is not trusted.
        result = result.ForGame(game);
        var now = clock.GetUtcNow();
        var state = await StateAsync(game, ct);
        MoveSource(state, game, now); // normally done at startup already; here only if that step has not run

        state.LastAttemptAt = now;
        state.LastHttpStatus = result.HttpStatus;
        state.NextPollAt = nextPollAt;

        var items = result.Items;
        var outcome = result.Outcome;
        if (!result.Succeeded)
        {
            state.LastOutcome = (int)outcome;
            state.LastDetail = Clip(result.Detail ?? outcome.ToString(), 300);
            state.LastSkippedCount = result.SkippedItems;
            state.ConsecutiveFailures++;
            await db.SaveChangesAsync(ct);
            return new UpdatesRoundSummary(0, 0, 0, 0, 0, false);
        }

        var changed = new List<string>();
        var newItems = 0;
        var baselineNow = false;
        if (outcome == UpdateFetchOutcome.Ok)
        {
            var set = db.Set<UpdatesItemEntity>();
            var ids = items.Select(i => i.ExternalId).ToList();
            var known = await set.Where(a => a.Provider == game.Provider && a.GameKey == game.Key && ids.Contains(a.ExternalId)).ToDictionaryAsync(a => a.ExternalId, ct);
            var establishing = state.BaselineAt is null;
            foreach (var item in items)
            {
                var hash = item.ContentHash;
                if (!known.TryGetValue(item.ExternalId, out var row))
                {
                    var verdict = game.Classifier.Classify(item);
                    var baseline = establishing || (state.PrunedThroughPublishedAt is { } watermark && item.PublishedAt is { } published && published <= watermark);
                    row = new UpdatesItemEntity
                    {
                        Provider = game.Provider,
                        GameKey = game.Key,
                        ExternalId = item.ExternalId,
                        Url = item.CanonicalUrl,
                        Title = item.Title,
                        Highlights = UpdateHighlightsJson.Serialize(item.Highlights),
                        PublishedAt = item.PublishedAt,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        ContentHash = hash,
                        Baseline = baseline,
                        Classification = (int)verdict.Classification,
                        ClassificationReason = Clip(verdict.Reason, 48)!,
                    };
                    set.Add(row);
                    known[item.ExternalId] = row;
                    newItems++;
                    if (baseline)
                        continue;
                    if (verdict.Classification == UpdateClassification.Update)
                    {
                        state.LastDiscoveredAt = now;
                        logger.LogInformation("new {Game} update discovered: post {Post} ({Reason})", game.Key, item.ExternalId, verdict.Reason);
                    }
                    else if (verdict.Classification == UpdateClassification.Ambiguous)
                    {
                        logger.LogWarning("{Game} post {Post} may be an update but is not conclusive ({Reason}); it is not posted", game.Key, item.ExternalId, verdict.Reason);
                    }

                    continue;
                }

                row.LastSeenAt = now;
                if (row.Title is null)
                {
                    // Still listed: what retention dropped is known again.
                    row.Title = item.Title;
                    row.Highlights = UpdateHighlightsJson.Serialize(item.Highlights);
                }

                if (row.ContentHash == hash)
                    continue;
                var again = game.Classifier.Classify(item);
                row.Url = item.CanonicalUrl;
                row.Title = item.Title;
                row.Highlights = UpdateHighlightsJson.Serialize(item.Highlights);
                row.PublishedAt = item.PublishedAt;
                row.ContentHash = hash;
                row.ContentChangedAt = now;
                if (row.Classification != (int)again.Classification)
                {
                    logger.LogInformation("{Game} post {Post} reclassified {From} -> {To} ({Reason})", game.Key, item.ExternalId,
                        (UpdateClassification)row.Classification, again.Classification, again.Reason);
                    if (!row.Baseline && again.Classification == UpdateClassification.Update)
                        state.LastDiscoveredAt = now;
                    row.Classification = (int)again.Classification;
                    row.ClassificationChangedAt = now;
                }

                row.ClassificationReason = Clip(again.Reason, 48)!;
                changed.Add(item.ExternalId);
            }

            if (establishing)
            {
                state.BaselineAt = now;
                baselineNow = true;
                logger.LogInformation("{Game} updates baseline established with {Count} existing posts (none of them will be posted)", game.Key, items.Count);
            }

            state.LastItemCount = items.Count;
            state.LastSkippedCount = result.SkippedItems;
            state.LastNewCount = newItems;
            state.LastUpdateCount = known.Values.Count(a => a.Classification == (int)UpdateClassification.Update);
            state.LastAmbiguousCount = known.Values.Count(a => a.Classification == (int)UpdateClassification.Ambiguous);
        }
        else
        {
            state.LastItemCount = 0;
            state.LastSkippedCount = 0;
            state.LastNewCount = 0;
            state.LastUpdateCount = 0;
            state.LastAmbiguousCount = 0;
        }

        state.LastOutcome = (int)outcome;
        state.LastDetail = Clip(result.Detail, 300);
        // The catch-up point only moves with an answer that is complete. An incomplete one (something new could not be read
        // yet) is applied like any other, but the next round looks back as far again — so what is still to be read cannot
        // fall out of the source's window. Without an earlier point there is nothing to keep.
        if (!result.Incomplete || state.LastSuccessAt is null)
            state.LastSuccessAt = now;
        state.ConsecutiveFailures = 0;
        state.SourceCacheSeconds = result.CacheLifetime is { } lifetime ? (int)Math.Min(lifetime.TotalSeconds, int.MaxValue) : null;

        var (staged, edits) = state.BaselineAt is null ? (0, 0) : await PlanDeliveriesAsync(game, state, changed, now, ct);
        await PruneAsync(game, state, now, ct);
        await db.SaveChangesAsync(ct);
        return new UpdatesRoundSummary(outcome == UpdateFetchOutcome.Ok ? items.Count : 0, newItems, state.LastUpdateCount, staged, edits, baselineNow);
    }

    private async Task<(int Staged, int Edits)> PlanDeliveriesAsync(GameUpdateDefinition game, UpdatesSourceStateEntity state, IReadOnlyList<string> changed, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var mode = EffectiveMode;
        if (mode == UpdatesMode.Off)
            return (0, 0);
        var dryRun = mode == UpdatesMode.DryRun;
        var kind = Kind(game.Key, dryRun);
        var provider = catalog.ProviderOf(game);
        var catchUpFrom = now - TimeSpan.FromHours(o.CatchUpHours);
        var expiresAt = now + TimeSpan.FromHours(o.CatchUpHours);
        var staged = 0;
        var edits = 0;
        var itemSet = db.Set<UpdatesItemEntity>();
        var deliveries = db.Set<UpdatesDeliveryEntity>();
        const int update = (int)UpdateClassification.Update;

        foreach (var subscription in await db.Set<UpdatesSubscriptionEntity>().Where(s => s.GameKey == game.Key && s.Enabled).OrderBy(s => s.GuildId).ToListAsync(ct))
        {
            var config = await db.Set<UpdatesGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == subscription.GuildId, ct);
            if (config?.ChannelId is null || config.Paused)
                continue;
            var guild = new GuildId(config.GuildId);
            if (!await gate.IsEnabledAsync(guild, UpdatesModule.ModuleIdTyped, ct))
                continue;
            UpdatesWindow.Enter(config, mode, now);
            var channel = new ChannelId(config.ChannelId.Value);
            var language = (await guildSettings.GetAsync(guild, ct)).Language;
            DateTimeOffset since;
            if (dryRun)
                since = config.DryRunSince ??= now;
            else
                since = config.LiveSince ??= now;
            var enabledAt = subscription.EnabledAt ??= now;
            if (enabledAt > since)
                since = enabledAt;

            var delivered = await deliveries.AsNoTracking()
                .Where(d => d.GuildId == config.GuildId && d.Provider == game.Provider && d.GameKey == game.Key && d.Kind == kind)
                .Select(d => d.ExternalId).ToListAsync(ct);
            // Tracked rows (this round's new and changed posts) win over their stored versions.
            var tracked = itemSet.Local.Where(a => a.Provider == game.Provider && a.GameKey == game.Key).ToList();
            var trackedIds = tracked.Select(a => a.ExternalId).ToList();
            var stored = await itemSet.AsNoTracking()
                .Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.Classification == update && !a.Baseline && a.Title != null &&
                            a.FirstSeenAt >= since && !delivered.Contains(a.ExternalId) && !trackedIds.Contains(a.ExternalId))
                .ToListAsync(ct);
            // First seen AND published after the window started, and published within catch-up. A post without a provider
            // time cannot prove either, so it is never posted automatically.
            var notBefore = since > catchUpFrom ? since : catchUpFrom;
            var candidates = tracked.Concat(stored)
                .Where(a => a.Classification == update && !a.Baseline && a.Title != null && a.FirstSeenAt >= since && !delivered.Contains(a.ExternalId) &&
                            a.PublishedAt is { } published && published >= notBefore && provider.IsCanonicalUrl(a.Url))
                .OrderBy(a => a.PublishedAt).ThenBy(a => a.ExternalId, StringComparer.Ordinal)
                .Take(o.MaxCardsPerRound)
                .ToList();
            foreach (var item in candidates)
            {
                var message = renderer.Render(game, provider, item.Url, item.Title!, item.PublishedAt, language, UpdateHighlightsJson.Parse(item.Highlights));
                var result = await outbox.StageAsync(new NotificationRequest(guild, UpdatesModule.ModuleIdTyped, SourceKey(game, item.ExternalId), channel, kind,
                    message, expiresAt, dryRun), ct);
                deliveries.Add(new UpdatesDeliveryEntity
                {
                    GuildId = config.GuildId,
                    Provider = game.Provider,
                    GameKey = game.Key,
                    ExternalId = item.ExternalId,
                    Kind = kind,
                    ChannelId = channel.Value,
                    StagedAt = now,
                });
                staged++;
                state.LastDeliveryStagedAt = now;
                logger.LogInformation("{Game} update card staged guild={Guild} post={Post} dryRun={DryRun} outcome={Outcome}",
                    game.Key, config.GuildId, item.ExternalId, dryRun, result);
            }

            if (changed.Count == 0)
                continue;
            // Corrections of cards this guild already has in its CURRENT channel: the same logical key → a silent edit.
            // A card in a former channel is left as it is (the delivery check would refuse the edit anyway).
            foreach (var record in await deliveries.AsNoTracking()
                         .Where(d => d.GuildId == config.GuildId && d.Provider == game.Provider && d.GameKey == game.Key && d.Kind == kind &&
                                     d.ChannelId == channel.Value && changed.Contains(d.ExternalId))
                         .OrderBy(d => d.ExternalId).ToListAsync(ct))
            {
                var item = itemSet.Local.FirstOrDefault(a => a.Provider == game.Provider && a.GameKey == game.Key && a.ExternalId == record.ExternalId);
                // A post that is no longer an update keeps its card as it is: nothing is deleted or rewritten automatically.
                if (item?.Title is null || item.Classification != update || !provider.IsCanonicalUrl(item.Url))
                    continue;
                // A correction only ever edits: if the card's outbox row is gone (guild data purged), staging would post anew.
                var logicalKey = NotificationRequest.BuildLogicalKey(guild, UpdatesModule.ModuleIdTyped, SourceKey(game, item.ExternalId), channel, record.Kind, dryRun);
                if (!await db.Outbox.AsNoTracking().AnyAsync(o => o.LogicalKey == logicalKey, ct))
                    continue;
                var message = renderer.Render(game, provider, item.Url, item.Title, item.PublishedAt, language, UpdateHighlightsJson.Parse(item.Highlights));
                var result = await outbox.StageAsync(new NotificationRequest(guild, UpdatesModule.ModuleIdTyped, SourceKey(game, item.ExternalId),
                    new ChannelId(record.ChannelId), record.Kind, message, expiresAt, dryRun), ct);
                if (result is StageOutcome.EditScheduled or StageOutcome.UpdatedPending)
                {
                    edits++;
                    logger.LogInformation("{Game} update card corrected guild={Guild} post={Post} outcome={Outcome} (edit, no mention)", game.Key, config.GuildId, item.ExternalId, result);
                }
            }
        }

        return (staged, edits);
    }

    /// <summary>
    /// Titles are dropped after TextRetentionDays; ids and delivery records after DedupRetentionDays. Only a post with a
    /// provider time inside the catch-up window is ever planned, so a pruned post that shows up again cannot be posted; the
    /// publication-time watermark additionally marks it baseline. Post ids are not assumed to be ordered.
    /// </summary>
    private async Task PruneAsync(GameUpdateDefinition game, UpdatesSourceStateEntity state, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var textCutoff = now - TimeSpan.FromDays(o.TextRetentionDays);
        var idCutoff = now - TimeSpan.FromDays(o.DedupRetentionDays);
        var set = db.Set<UpdatesItemEntity>();
        // The queries see the stored values; posts seen again in this round are tracked with their new LastSeenAt, so the
        // age is checked once more on the entity (a post that is still listed is never pruned, however long the gap was).
        foreach (var old in (await set.Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.Title != null && a.LastSeenAt < textCutoff).ToListAsync(ct))
                 .Where(a => a.LastSeenAt < textCutoff))
        {
            old.Title = null;
            old.Highlights = null;
        }

        var expired = (await set.Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.LastSeenAt < idCutoff).ToListAsync(ct))
            .Where(a => a.LastSeenAt < idCutoff).ToList();
        if (expired.Count > 0)
        {
            if (expired.Max(a => a.PublishedAt) is { } newest && (state.PrunedThroughPublishedAt is not { } current || newest > current))
                state.PrunedThroughPublishedAt = newest;
            set.RemoveRange(expired);
        }

        var deliveries = db.Set<UpdatesDeliveryEntity>();
        deliveries.RemoveRange(await deliveries.Where(d => d.Provider == game.Provider && d.GameKey == game.Key && d.StagedAt < idCutoff).ToListAsync(ct));
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
