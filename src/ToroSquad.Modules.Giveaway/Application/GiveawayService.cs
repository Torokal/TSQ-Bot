using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Giveaway.Domain;
using ToroSquad.Modules.Giveaway.Persistence;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>The raw text of the giveaway form (validated by <see cref="GiveawayForm"/>).</summary>
public sealed record GiveawayRequest(string? Prize, string? Duration, string? Winners, string? Description);

/// <summary>Outcome of a giveaway operation: the message to show and the giveaway it concerns.</summary>
public sealed record GiveawayResult(OperationResult Result, long? GiveawayId = null);

/// <summary>An autocomplete suggestion: "#12 · Discord Nitro" → "12".</summary>
public sealed record GiveawaySuggestion(string Label, string Value);

/// <summary>
/// The giveaway lifecycle. Create: form → row → card (bot message) → the bot's 🎉. Draw (worker when due, or /giveaway end):
/// read ALL 🎉 reactions from Discord, keep unique non-bot users, draw with <see cref="WinnerDraw"/> (members only), then in
/// ONE write transaction re-check that the giveaway is still active and store the result and the winner announcement
/// (outbox) together. That re-check is the single gate every path passes (worker ticks, restart, manual end, cancel,
/// reroll), so a giveaway is drawn at most once and a cancelled one never is; a loser of the race only discards its
/// in-memory draw. Nothing is kept in process memory: after a restart the worker finds every due giveaway in the database.
/// Discord that cannot be read postpones the draw (backoff) instead of guessing; a deleted card or channel orphans it.
/// Only IDs and counts are logged — never the entrants.
/// </summary>
public sealed class GiveawayService(
    ToroDbContext db,
    IMessageTransport transport,
    IGiveawayReactions reactions,
    IGuildGateway guilds,
    IGiveawayRandom random,
    INotificationOutbox outbox,
    IGuildSettingsStore settings,
    IModuleGate gate,
    GiveawayCards cards,
    GiveawayAnnouncementRenderer announcements,
    GiveawayCardSync cardSync,
    DeploymentPolicy deployment,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<GiveawayService> logger)
{
    /// <summary>Creating and managing giveaways is a server-management action (as every TSQ admin command).</summary>
    public const GuildPermission ManagePermission = Authorize.ServerSettings;

    public const string KindWinners = "giveaway-winners";

    /// <summary>A winner announcement not delivered within this time is dropped rather than pinging late.</summary>
    public static readonly TimeSpan AnnouncementLifetime = TimeSpan.FromHours(1);

    /// <summary>Delivered/finished announcement rows (their payload lists the pinged user ids) are removed after this.</summary>
    public static readonly TimeSpan AnnouncementRetention = TimeSpan.FromHours(24);

    /// <summary>A row whose card was never confirmed (crash while posting) is orphaned after this.</summary>
    public static readonly TimeSpan UnpostedGrace = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

    private const int MaxWriteAttempts = 3;
    private const int DrawBatch = 25;
    private const int RecentMessages = 20;

    private DbSet<GiveawayEntity> Giveaways => db.Set<GiveawayEntity>();
    private DbSet<GiveawayWinnerEntity> Winners => db.Set<GiveawayWinnerEntity>();

    public static string SourceKey(long giveawayId) => "giveaway:" + giveawayId.ToString(CultureInfo.InvariantCulture);

    public static string AnnouncementKind(int round) =>
        round == 0 ? KindWinners : "giveaway-reroll-" + round.ToString(CultureInfo.InvariantCulture);

    // ---- create ----

    /// <summary>
    /// Before the form opens and again on submit: permission, the module enabled here (disabling stops NEW giveaways only),
    /// a channel the bot can run a giveaway in — Add Reactions included: the bot's own 🎉 is how members enter — and the
    /// per-guild limit.
    /// </summary>
    public async Task<OperationResult?> PrecheckCreateAsync(ActorContext actor, ChannelId channel, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, ManagePermission);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        if (!await gate.IsEnabledAsync(actor.GuildId, GiveawayModule.ModuleIdTyped, ct))
            return OperationResult.Fail(OperationError.ModuleDisabled, "error.module_disabled");

        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, channel, ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "giveaway.create.channel_type");
        var missing = GiveawayRules.RequiredChannelPermissions & ~access.Permissions;
        if (!access.Permissions.Grants(GiveawayRules.RequiredChannelPermissions))
        {
            return missing == GuildPermission.AddReactions
                ? OperationResult.Fail(OperationError.Forbidden, "giveaway.create.add_reactions")
                : OperationResult.Fail(OperationError.Forbidden, "giveaway.create.channel_permissions", missing.ToString());
        }

        return await ActiveCountAsync(actor.GuildId, ct) >= GiveawayRules.MaxActivePerGuild
            ? OperationResult.Fail(OperationError.Conflict, "giveaway.create.limit", GiveawayRules.MaxActivePerGuild)
            : null;
    }

    /// <summary>
    /// The submitted form: everything is checked again, the giveaway stored, its card posted as a bot message in
    /// <paramref name="channel"/> and the bot's 🎉 added. A card Discord surely did not post removes the giveaway again; an
    /// uncertain post is looked for among the latest messages before giving up.
    /// </summary>
    public async Task<GiveawayResult> CreateAsync(ActorContext actor, ChannelId channel, string creatorName, GiveawayRequest request, CancellationToken ct)
    {
        if (await PrecheckCreateAsync(actor, channel, ct) is { } refusal)
            return new(refusal);
        var form = GiveawayForm.Parse(request.Prize, request.Duration, request.Winners, request.Description);
        if (form.Input is not { } input)
            return new(OperationResult.Fail(OperationError.InvalidInput, form.ErrorKey!, [.. form.Args]));

        var now = Truncate(clock.GetUtcNow());
        var guild = actor.GuildId.Value;
        var id = await WriteAsync(async () =>
        {
            if (await Giveaways.CountAsync(x => x.GuildId == guild && x.Status == GiveawayStatus.Active, ct) >= GiveawayRules.MaxActivePerGuild)
                return 0L;
            var giveaway = new GiveawayEntity
            {
                GuildId = guild,
                ChannelId = channel.Value,
                CreatorUserId = actor.UserId.Value,
                CreatorName = Truncate(creatorName, GiveawayCards.CreatorNameMax),
                Prize = input.Prize,
                Description = input.Description,
                WinnerCount = input.Winners,
                Status = GiveawayStatus.Active,
                CreatedAt = now,
                EndsAt = now + input.Duration,
            };
            Giveaways.Add(giveaway);
            await db.SaveChangesAsync(ct);
            return giveaway.Id;
        }, ct);
        if (id == 0)
            return new(OperationResult.Fail(OperationError.Conflict, "giveaway.create.limit", GiveawayRules.MaxActivePerGuild));

        var view = (await GetAsync(id, ct))!;
        var card = cards.Render(view, (await settings.GetAsync(actor.GuildId, ct)).Language);
        var (post, message) = await PostCardAsync(channel, card, now, ct);
        if (post != CardPost.Posted)
        {
            await Giveaways.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
            logger.LogWarning("Giveaway {Giveaway} discarded: its card could not be posted ({Post}; guild {Guild}, channel {Channel})", id, post, guild, channel);
            return new(OperationResult.Fail(OperationError.ProviderUnavailable, post == CardPost.Refused ? "giveaway.create.post_failed" : "giveaway.create.post_uncertain"));
        }

        await Giveaways.Where(x => x.Id == id && x.MessageId == null).ExecuteUpdateAsync(s => s.SetProperty(x => x.MessageId, (ulong?)message.Value), ct);
        if (await reactions.AddEntryReactionAsync(channel, message, ct) != ReactionAddOutcome.Added)
        {
            // The bot's own 🎉 IS the entry point: without it the giveaway does not run. The card exists already (the
            // permission changed after the precheck, or Discord refused), so it is turned into the cancelled card.
            await WriteAsync(async () =>
            {
                if (await Giveaways.FirstOrDefaultAsync(x => x.Id == id, ct) is { Status: GiveawayStatus.Active } row)
                {
                    row.Status = GiveawayStatus.Cancelled;
                    row.EndedAt = clock.GetUtcNow();
                    row.EndedByUserId = actor.UserId.Value;
                    MarkCardStale(row);
                    await db.SaveChangesAsync(ct);
                }

                return 0;
            }, ct);
            logger.LogWarning("Giveaway {Giveaway}: the bot's reaction could not be added; cancelled at once (guild {Guild}, channel {Channel})", id, guild, channel);
            await SyncCardAsync(id, ct);
            return new(OperationResult.Fail(OperationError.Forbidden, "giveaway.create.reaction_failed"), id);
        }

        logger.LogInformation("giveaway_created {Giveaway} guild={Guild} channel={Channel} message={Message} winners={Winners} ends={EndsAt:O}",
            id, guild, channel, message, view.WinnerCount, view.EndsAt);
        return new(OperationResult.Ok("giveaway.create.done", GiveawayCards.Number(id), DiscordText.Timestamp(view.EndsAt, 'R')), id);
    }

    private enum CardPost
    {
        Posted,
        Refused,
        Uncertain,
    }

    /// <summary>
    /// The card as a new bot message (the bot's own REST credentials, so it can edit it at any later time). An ambiguous send
    /// (timeout after the request went out) is looked for among the latest messages — never posted a second time.
    /// </summary>
    private async Task<(CardPost Post, MessageId Message)> PostCardAsync(ChannelId channel, OutgoingMessage card, DateTimeOffset attempt, CancellationToken ct)
    {
        switch (await transport.SendAsync(channel, card, ct))
        {
            case SendOutcome.Sent sent:
                return (CardPost.Posted, sent.MessageId);
            case SendOutcome.Ambiguous:
                var probe = new DeliveryProbe(MessageFingerprint.Of(card), attempt, new HashSet<MessageId>());
                return await transport.FindRecentAsync(channel, probe, RecentMessages, ct) is ReconcileOutcome.Found found
                    ? (CardPost.Posted, found.MessageId)
                    : (CardPost.Uncertain, default);
            default:
                return (CardPost.Refused, default);
        }
    }

    // ---- draw ----

    /// <summary>Worker pass: draws every due giveaway (all that ended while the bot was down included). Returns how many ended.</summary>
    public async Task<int> DrawDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await Giveaways.AsNoTracking()
            .Where(x => x.Status == GiveawayStatus.Active && x.MessageId != null && x.EndsAt <= now && (x.NextDrawAttemptAt == null || x.NextDrawAttemptAt <= now))
            .OrderBy(x => x.EndsAt).Select(x => new { x.Id, x.GuildId }).Take(DrawBatch * 4).ToListAsync(ct);
        var ended = 0;
        foreach (var item in due.Where(x => deployment.IsGuildAllowed(new GuildId(x.GuildId))).Take(DrawBatch))
        {
            try
            {
                var result = await DrawAsync(item.Id, null, manual: false, ct);
                if (result.Result.Succeeded)
                    ended++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One giveaway must not hold back the others; it stays active and is tried again next pass.
                logger.LogError(ex, "Giveaway {Giveaway}: draw failed", item.Id);
                db.ChangeTracker.Clear();
            }
        }

        return ended;
    }

    /// <summary>Rows whose card was never confirmed (the process stopped while posting) end as orphaned: nobody can enter them.</summary>
    public async Task<int> OrphanUnpostedAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - UnpostedGrace;
        var orphaned = await Giveaways.Where(x => x.Status == GiveawayStatus.Active && x.MessageId == null && x.CreatedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GiveawayStatus.Orphaned).SetProperty(x => x.EndedAt, now)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (orphaned > 0)
            logger.LogWarning("{Count} giveaway(s) without a confirmed card were orphaned", orphaned);
        return orphaned;
    }

    /// <summary>/giveaway end: draws now, through exactly the same path as the worker.</summary>
    public async Task<GiveawayResult> EndAsync(ActorContext actor, string? target, CancellationToken ct)
    {
        var (giveaway, refusal) = await ResolveAsync(actor, target, ct);
        if (giveaway is null)
            return new(refusal!);
        return await DrawAsync(giveaway.Id, actor.UserId, manual: true, ct);
    }

    private async Task<GiveawayResult> DrawAsync(long id, UserId? by, bool manual, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var giveaway = await Giveaways.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (giveaway is null)
            return new(NotFound());
        if (giveaway.Status != GiveawayStatus.Active)
            return new(StateRefusal(giveaway.Status), id);
        if (!manual && (giveaway.EndsAt > now || giveaway.NextDrawAttemptAt > now))
            return new(OperationResult.Fail(OperationError.Conflict, "giveaway.end.not_due"), id);
        if (giveaway.MessageId is not { } message)
            return new(NotFound(), id); // its card is still being posted (the worker never picks these; OrphanUnpostedAsync does)

        var channel = new ChannelId(giveaway.ChannelId);
        var read = await reactions.ReadEntrantsAsync(channel, new MessageId(message), ct);
        if (read is EntrantRead.Missing missing)
            return await OrphanAsync(giveaway, missing.Reason, ct);
        if (read is not EntrantRead.Read { Users: var users })
            return await PostponeAsync(giveaway, ((EntrantRead.Unavailable)read).Reason, ct);

        var entrants = GiveawayEntrants.Valid(users);
        var guild = new GuildId(giveaway.GuildId);
        var draw = await WinnerDraw.DrawAsync(entrants, giveaway.WinnerCount, random, u => MembershipAsync(guild, u, ct));
        if (draw.Unavailable)
            return await PostponeAsync(giveaway, "member lookup unavailable", ct);

        var drawn = await WriteAsync(async () =>
        {
            var row = await Giveaways.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is not { Status: GiveawayStatus.Active })
                return (GiveawayEntity?)null; // cancelled, ended or drawn meanwhile: this draw is discarded
            row.Status = GiveawayStatus.Finished;
            row.EndedAt = now;
            row.EndedByUserId = by?.Value;
            row.EntrantCount = entrants.Count;
            row.NextDrawAttemptAt = null;
            MarkCardStale(row);
            AddWinners(id, 0, draw.Winners);
            await db.SaveChangesAsync(ct);
            await StageAnnouncementAsync(row, draw.Winners, 0, ct);
            await db.SaveChangesAsync(ct);
            return row;
        }, ct);
        if (drawn is null)
            return new(StateRefusal((await Giveaways.AsNoTracking().Where(x => x.Id == id).Select(x => (GiveawayStatus?)x.Status).FirstOrDefaultAsync(ct)) ?? GiveawayStatus.Orphaned), id);

        logger.LogInformation("giveaway_finished {Giveaway} guild={Guild} channel={Channel} message={Message} entrants={Entrants} winners={Winners} trigger={Trigger}",
            id, giveaway.GuildId, giveaway.ChannelId, message, entrants.Count, draw.Winners.Count, manual ? "manual" : "scheduled");
        await SyncCardAsync(id, ct);
        return new(draw.Winners.Count == 0
            ? OperationResult.Ok("giveaway.end.no_entrants", GiveawayCards.Number(id))
            : OperationResult.Ok("giveaway.end.done", GiveawayCards.Number(id), draw.Winners.Count, entrants.Count), id);
    }

    // ---- cancel / reroll ----

    public async Task<GiveawayResult> CancelAsync(ActorContext actor, string? target, CancellationToken ct)
    {
        var (giveaway, refusal) = await ResolveAsync(actor, target, ct);
        if (giveaway is null)
            return new(refusal!);

        var id = giveaway.Id;
        var status = await WriteAsync(async () =>
        {
            var row = await Giveaways.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is not { Status: GiveawayStatus.Active })
                return row?.Status ?? GiveawayStatus.Orphaned;
            row.Status = GiveawayStatus.Cancelled;
            row.EndedAt = clock.GetUtcNow();
            row.EndedByUserId = actor.UserId.Value;
            MarkCardStale(row);
            await db.SaveChangesAsync(ct);
            return GiveawayStatus.Active; // it was active: cancelled now
        }, ct);
        if (status != GiveawayStatus.Active)
            return new(StateRefusal(status), id);

        logger.LogInformation("giveaway_cancelled {Giveaway} guild={Guild} channel={Channel} message={Message}", id, giveaway.GuildId, giveaway.ChannelId, giveaway.MessageId);
        await SyncCardAsync(id, ct);
        return new(OperationResult.Ok("giveaway.cancel.done", GiveawayCards.Number(id)), id);
    }

    /// <summary>
    /// /giveaway reroll: a new draw for a finished giveaway from the CURRENT 🎉 reactions, leaving out everyone who has won it
    /// before (every earlier round). Fewer other entrants than winners: all of them win; none: nothing changes. Stored as the
    /// next round (earlier rounds stay as history); only one reroll of the same round can succeed.
    /// </summary>
    public async Task<GiveawayResult> RerollAsync(ActorContext actor, string? target, CancellationToken ct)
    {
        var (giveaway, refusal) = await ResolveAsync(actor, target, ct);
        if (giveaway is null)
            return new(refusal!);
        var id = giveaway.Id;
        if (giveaway.Status != GiveawayStatus.Finished)
            return new(OperationResult.Fail(OperationError.Conflict, giveaway.Status == GiveawayStatus.Active ? "giveaway.reroll.not_finished" : StateKey(giveaway.Status)), id);
        if (giveaway.MessageId is not { } message)
            return new(OperationResult.Fail(OperationError.NotFound, "giveaway.reroll.message_missing"), id);

        var read = await reactions.ReadEntrantsAsync(new ChannelId(giveaway.ChannelId), new MessageId(message), ct);
        if (read is EntrantRead.Missing)
            return new(OperationResult.Fail(OperationError.NotFound, "giveaway.reroll.message_missing"), id);
        if (read is not EntrantRead.Read { Users: var users })
            return new(OperationResult.Fail(OperationError.ProviderUnavailable, "giveaway.reroll.unavailable"), id);

        var entrants = GiveawayEntrants.Valid(users);
        var previous = (await Winners.AsNoTracking().Where(w => w.GiveawayId == id).Select(w => w.UserId).ToListAsync(ct)).ToHashSet();
        var candidates = entrants.Where(u => !previous.Contains(u.Value)).ToList();
        var guild = new GuildId(giveaway.GuildId);
        var draw = await WinnerDraw.DrawAsync(candidates, giveaway.WinnerCount, random, u => MembershipAsync(guild, u, ct));
        if (draw.Unavailable)
            return new(OperationResult.Fail(OperationError.ProviderUnavailable, "giveaway.reroll.unavailable"), id);
        if (draw.Winners.Count == 0)
            return new(OperationResult.Fail(OperationError.Conflict, "giveaway.reroll.no_candidates"), id);

        var expected = giveaway.RerollCount;
        var round = await WriteAsync(async () =>
        {
            var row = await Giveaways.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (row is not { Status: GiveawayStatus.Finished } || row.RerollCount != expected)
                return 0; // another reroll of the same round got there first
            row.RerollCount++;
            row.EntrantCount = entrants.Count;
            MarkCardStale(row);
            AddWinners(id, row.RerollCount, draw.Winners);
            await db.SaveChangesAsync(ct);
            await StageAnnouncementAsync(row, draw.Winners, row.RerollCount, ct);
            await db.SaveChangesAsync(ct);
            return row.RerollCount;
        }, ct);
        if (round == 0)
            return new(OperationResult.Fail(OperationError.Conflict, "giveaway.reroll.conflict"), id);

        logger.LogInformation("giveaway_rerolled {Giveaway} guild={Guild} round={Round} entrants={Entrants} candidates={Candidates} winners={Winners}",
            id, giveaway.GuildId, round, entrants.Count, candidates.Count, draw.Winners.Count);
        await SyncCardAsync(id, ct);
        return new(OperationResult.Ok(draw.Winners.Count < giveaway.WinnerCount ? "giveaway.reroll.done_fewer" : "giveaway.reroll.done",
            GiveawayCards.Number(id), draw.Winners.Count), id);
    }

    // ---- lookups ----

    public async Task<GiveawayView?> GetAsync(long id, CancellationToken ct)
    {
        var giveaway = await Giveaways.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return giveaway is null ? null : ToView(giveaway, await CurrentWinnersAsync(db, giveaway, ct));
    }

    /// <summary>Autocomplete for the admin commands: this guild's active (end, cancel) or finished (reroll) giveaways, newest first.</summary>
    public async Task<IReadOnlyList<GiveawaySuggestion>> SuggestAsync(ActorContext actor, bool finished, string? typed, CancellationToken ct)
    {
        if (!Authorize.Require(actor, actor.GuildId, ManagePermission).IsAllowed)
            return [];
        var status = finished ? GiveawayStatus.Finished : GiveawayStatus.Active;
        var rows = await Giveaways.AsNoTracking().Where(x => x.GuildId == actor.GuildId.Value && x.Status == status)
            .OrderByDescending(x => x.Id).Take(100).Select(x => new { x.Id, x.Prize }).ToListAsync(ct);
        var filter = typed?.Trim().TrimStart('#') ?? "";
        return rows
            .Where(x => filter.Length == 0 || GiveawayCards.Number(x.Id).StartsWith(filter, StringComparison.Ordinal) ||
                        x.Prize.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Take(DiscordLimits.AutocompleteChoicesMax)
            .Select(x => new GiveawaySuggestion(Truncate("#" + GiveawayCards.Number(x.Id) + " · " + DiscordText.UntrustedPlain(x.Prize, 90), 100), GiveawayCards.Number(x.Id)))
            .ToList();
    }

    /// <summary>Removes delivered/finished winner announcements after <see cref="AnnouncementRetention"/> (their payload holds user ids).</summary>
    public Task<int> PruneAnnouncementsAsync(CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow() - AnnouncementRetention;
        return db.Outbox.Where(o => o.ModuleId == GiveawayModule.ModuleIdValue && o.UpdatedAt < cutoff &&
                                    (o.Status == OutboxStatus.Cancelled || o.Status == OutboxStatus.Expired || o.Status == OutboxStatus.Failed ||
                                     o.Status == OutboxStatus.Simulated || (o.Status == OutboxStatus.Sent && !o.EditPending) ||
                                     (o.Status == OutboxStatus.DeliveryUnknown && o.NextAttemptAt == null)))
            .ExecuteDeleteAsync(ct);
    }

    public static GiveawayView ToView(GiveawayEntity g, IReadOnlyList<UserId> winners) => new(
        g.Id,
        new GuildId(g.GuildId),
        new ChannelId(g.ChannelId),
        g.MessageId is { } m ? new MessageId(m) : null,
        new UserId(g.CreatorUserId),
        g.CreatorName,
        g.Prize,
        g.Description,
        g.WinnerCount,
        g.Status,
        g.CreatedAt,
        g.EndsAt,
        g.EndedAt,
        g.EntrantCount,
        g.RerollCount,
        winners);

    /// <summary>The winners of the latest round, in place order.</summary>
    public static async Task<IReadOnlyList<UserId>> CurrentWinnersAsync(ToroDbContext db, GiveawayEntity giveaway, CancellationToken ct) =>
        (await db.Set<GiveawayWinnerEntity>().AsNoTracking().Where(w => w.GiveawayId == giveaway.Id && w.Round == giveaway.RerollCount)
            .OrderBy(w => w.Place).Select(w => w.UserId).ToListAsync(ct)).Select(u => new UserId(u)).ToList();

    // ---- helpers ----

    /// <summary>Permission first, then the target in THIS guild only (another guild's giveaway is indistinguishable from a missing one).</summary>
    private async Task<(GiveawayEntity? Giveaway, OperationResult? Refusal)> ResolveAsync(ActorContext actor, string? target, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, ManagePermission);
        if (!auth.IsAllowed)
            return (null, OperationResult.Forbidden(auth));
        var parsed = GiveawayTarget.Parse(target);
        if (parsed is null || (parsed.LinkGuildId is { } linked && linked != actor.GuildId.Value))
            return (null, NotFound());
        var guild = actor.GuildId.Value;
        var query = Giveaways.AsNoTracking().Where(x => x.GuildId == guild);
        var giveaway = parsed.GiveawayId is { } id
            ? await query.FirstOrDefaultAsync(x => x.Id == id, ct)
            : await query.FirstOrDefaultAsync(x => x.MessageId == parsed.MessageId, ct);
        return giveaway is null ? (null, NotFound()) : (giveaway, null);
    }

    private async Task<MemberLookupOutcome> MembershipAsync(GuildId guild, UserId user, CancellationToken ct) =>
        (await guilds.GetMemberAsync(guild, user, ct)).Outcome;

    private async Task<GiveawayResult> OrphanAsync(GiveawayEntity giveaway, string reason, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var changed = await Giveaways.Where(x => x.Id == giveaway.Id && x.Status == GiveawayStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, GiveawayStatus.Orphaned).SetProperty(x => x.EndedAt, now)
                .SetProperty(x => x.CardStale, false).SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (changed > 0)
            logger.LogWarning("Giveaway {Giveaway}: card is gone ({Reason}); orphaned without a draw (guild {Guild}, channel {Channel})",
                giveaway.Id, reason, giveaway.GuildId, giveaway.ChannelId);
        return new(OperationResult.Fail(OperationError.NotFound, "giveaway.end.message_missing"), giveaway.Id);
    }

    /// <summary>Discord could not be read: the giveaway stays active and is tried again after a growing delay (at most 30 minutes).</summary>
    private async Task<GiveawayResult> PostponeAsync(GiveawayEntity giveaway, string reason, CancellationToken ct)
    {
        var attempts = giveaway.DrawAttempts + 1;
        var delay = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(attempts - 1, 10)), MaxRetryDelay.TotalMinutes));
        var next = clock.GetUtcNow() + delay;
        await Giveaways.Where(x => x.Id == giveaway.Id && x.Status == GiveawayStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DrawAttempts, attempts).SetProperty(x => x.NextDrawAttemptAt, next), ct);
        logger.LogWarning("Giveaway {Giveaway}: draw postponed ({Reason}), attempt {Attempt}, next at {Next:O}", giveaway.Id, reason, attempts, next);
        return new(OperationResult.Fail(OperationError.ProviderUnavailable, "giveaway.end.retry"), giveaway.Id);
    }

    private void AddWinners(long id, int round, IReadOnlyList<UserId> winners)
    {
        for (var i = 0; i < winners.Count; i++)
            Winners.Add(new GiveawayWinnerEntity { GiveawayId = id, Round = round, Place = i + 1, UserId = winners[i].Value });
    }

    /// <summary>Staged in the caller's transaction, together with the result it announces (no winners: no announcement).</summary>
    private async Task StageAnnouncementAsync(GiveawayEntity giveaway, IReadOnlyList<UserId> winners, int round, CancellationToken ct)
    {
        if (winners.Count == 0)
            return;
        var view = ToView(giveaway, winners);
        var language = (await settings.GetAsync(view.Guild, ct)).Language;
        await outbox.StageAsync(new NotificationRequest(view.Guild, GiveawayModule.ModuleIdTyped, SourceKey(giveaway.Id), view.Channel, AnnouncementKind(round),
            announcements.Render(view, winners, round, language), clock.GetUtcNow() + AnnouncementLifetime, delivery.Value.Mode != DeliveryMode.Send), ct);
    }

    private static void MarkCardStale(GiveawayEntity giveaway)
    {
        giveaway.CardStale = giveaway.MessageId is not null;
        giveaway.CardSyncAttempts = 0;
        giveaway.Version++;
    }

    /// <summary>The state is stored; a failed edit only leaves the card to the worker (it stays marked stale).</summary>
    private async Task SyncCardAsync(long id, CancellationToken ct)
    {
        try
        {
            await cardSync.SyncAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Giveaway {Giveaway}: card update failed; the worker retries it", id);
            db.ChangeTracker.Clear();
        }
    }

    private Task<int> ActiveCountAsync(GuildId guild, CancellationToken ct) =>
        Giveaways.AsNoTracking().CountAsync(x => x.GuildId == guild.Value && x.Status == GiveawayStatus.Active, ct);

    private async Task<T> WriteAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            // SQLite: BeginTransaction = BEGIN IMMEDIATE (write lock first, other writers wait up to the busy timeout).
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await work();
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxWriteAttempts)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private static OperationResult NotFound() => OperationResult.Fail(OperationError.NotFound, "giveaway.not_found");

    private static OperationResult StateRefusal(GiveawayStatus status) => OperationResult.Fail(OperationError.Conflict, StateKey(status));

    private static string StateKey(GiveawayStatus status) => status switch
    {
        GiveawayStatus.Finished => "giveaway.state.finished",
        GiveawayStatus.Cancelled => "giveaway.state.cancelled",
        _ => "giveaway.state.orphaned",
    };

    /// <summary>Whole seconds: the card shows Discord timestamps, which have no sub-second part.</summary>
    private static DateTimeOffset Truncate(DateTimeOffset instant) => DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
