using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>Outcome of a listing operation: the message to show, the listing as stored afterwards, whether its card should be redrawn.</summary>
public sealed record LfgResult(OperationResult Result, LfgListingView? Listing, bool RefreshCard);

/// <summary>
/// What /ekip asked for. <see cref="StartMinutes"/>: one of <see cref="LfgRules.StartChoicesMinutes"/> (null/0 = now).
/// The notices are explicit opt-ins and need a later start. <see cref="VoiceChannel"/>: optional guild voice channel.
/// </summary>
public sealed record LfgCreateInput(
    string? Game,
    int Players,
    string? Details = null,
    int? DurationMinutes = null,
    int? StartMinutes = null,
    bool NotifyBeforeStart = false,
    bool NotifyAtStart = false,
    ChannelId? VoiceChannel = null,
    string? StartAt = null);

/// <summary>
/// Outcome of the voice button. <see cref="OpenChannelUrl"/> is set when the bot did not move the member (not connected to
/// voice, or the bot may not move members): a link that OPENS the channel in Discord — it connects nobody by itself.
/// </summary>
public sealed record LfgVoiceResult(OperationResult Result, string? OpenChannelUrl = null);

/// <summary>
/// The LFG business rules: create, join / maybe / leave, close, expire, the voice action. Every operation re-reads the
/// listing and re-checks guild, state, expiry, membership, capacity and permission server-side — the state of a button in
/// Discord is never trusted. Only Joined players fill slots (Maybe never counts). Every state change runs in a write
/// transaction that takes SQLite's write lock before reading (BEGIN IMMEDIATE), so check-then-write (free slot,
/// membership, active-listing limit) is serialized across connections; the (ListingId, UserId) primary key and the
/// listing's version token are the backstops.
/// </summary>
public sealed class LfgService(
    ToroDbContext db,
    IGuildGateway guilds,
    IGuildSettingsStore settings,
    IOptions<LfgOptions> options,
    TimeProvider clock,
    ILogger<LfgService> logger)
{
    /// <summary>Guild moderators (Discord's "Manage Messages", or Administrator) may close any listing.</summary>
    public const GuildPermission ModeratorPermission = GuildPermission.ManageMessages;

    private const int MaxWriteAttempts = 3;
    private const int ExpiryBatch = 100;
    private const int SqliteConstraint = 19;

    private DbSet<LfgListingEntity> Listings => db.Set<LfgListingEntity>();
    private DbSet<LfgParticipantEntity> Participants => db.Set<LfgParticipantEntity>();

    public Task<LfgResult> CreateAsync(ActorContext actor, ChannelId channel, string? game, int players, string? details, int? durationMinutes, CancellationToken ct) =>
        CreateAsync(actor, channel, new LfgCreateInput(game, players, details, durationMinutes), ct);

    public async Task<LfgResult> CreateAsync(ActorContext actor, ChannelId channel, LfgCreateInput input, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        // A custom date is wall-clock time in the guild's (existing, /setup-managed) time zone — default Europe/Istanbul.
        TimeZoneInfo? zone = null;
        if (!string.IsNullOrWhiteSpace(input.StartAt) && GuildTime.TryResolve((await settings.GetAsync(actor.GuildId, ct)).TimeZoneId, out var guildZone))
            zone = guildZone;
        var (draft, error) = LfgRules.Validate(input.Game, input.Details, input.Players, input.DurationMinutes, o.MaxPlayersPerListing, o.DefaultExpirationMinutes,
            input.StartMinutes, input.NotifyBeforeStart || input.NotifyAtStart, input.StartAt, zone, now);
        if (draft is null)
            return Refused(error switch
            {
                LfgDraftError.GameMissing or LfgDraftError.GameTooShort => No(OperationError.InvalidInput, "lfg.create.game_too_short", LfgRules.GameNameMinLength),
                LfgDraftError.GameTooLong => No(OperationError.InvalidInput, "lfg.create.game_too_long", LfgRules.GameNameMaxLength),
                LfgDraftError.DetailsTooLong => No(OperationError.InvalidInput, "lfg.create.details_too_long", LfgRules.DetailsMaxLength),
                LfgDraftError.PlayersOutOfRange => No(OperationError.InvalidInput, "lfg.create.players_range", LfgRules.MinPlayers, Math.Min(o.MaxPlayersPerListing, LfgRules.HardMaxPlayers)),
                LfgDraftError.StartInvalid => No(OperationError.InvalidInput, "lfg.create.start_invalid"),
                LfgDraftError.NoticeNeedsStart => No(OperationError.InvalidInput, "lfg.create.notice_needs_start"),
                LfgDraftError.StartConflict => No(OperationError.InvalidInput, "lfg.create.start_conflict"),
                LfgDraftError.DateFormat => No(OperationError.InvalidInput, "lfg.create.date_format"),
                LfgDraftError.DateNotInTimeZone => No(OperationError.InvalidInput, "lfg.create.date_not_in_zone"),
                LfgDraftError.DateAmbiguous => No(OperationError.InvalidInput, "lfg.create.date_ambiguous"),
                LfgDraftError.DateNotInFuture => No(OperationError.InvalidInput, "lfg.create.date_not_future"),
                LfgDraftError.DateTooFar => No(OperationError.InvalidInput, "lfg.create.date_too_far"),
                LfgDraftError.TimeZoneInvalid => No(OperationError.InvalidInput, "lfg.create.timezone_invalid"),
                _ => No(OperationError.InvalidInput, "lfg.create.duration_invalid"),
            });

        var config = await db.Set<LfgGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config?.ChannelId is { } only && only != channel.Value)
            return Refused(No(OperationError.InvalidInput, "lfg.create.wrong_channel", "<#" + only.ToString(CultureInfo.InvariantCulture) + ">"));

        // Must be a plain voice channel of THIS guild as the bot sees it (another guild's id is simply unknown here).
        if (input.VoiceChannel is { } voice && !(await guilds.GetVoiceChannelAccessAsync(actor.GuildId, voice, ct)).Usable)
            return Refused(No(OperationError.InvalidInput, "lfg.create.voice_invalid"));

        var (eventAt, expiresAt) = draft.Schedule(now); // the same instant the custom date was validated against
        var guild = actor.GuildId.Value;
        var owner = actor.UserId.Value;
        var id = await WriteAsync(async () =>
        {
            var active = await Listings.CountAsync(x => x.GuildId == guild && x.OwnerUserId == owner &&
                                                        (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.ExpiresAt > now, ct);
            if (active >= o.MaxActiveListingsPerUser)
                return 0L;
            var listing = new LfgListingEntity
            {
                GuildId = guild,
                ChannelId = channel.Value,
                OwnerUserId = owner,
                GameName = draft.GameName,
                Details = draft.Details,
                MaxPlayers = draft.MaxPlayers,
                Status = LfgStatus.Open,
                CreatedAt = now,
                EventAt = eventAt,
                ExpiresAt = expiresAt,
                VoiceChannelId = input.VoiceChannel?.Value,
                NotifyBeforeStart = input.NotifyBeforeStart,
                NotifyAtStart = input.NotifyAtStart,
                Participants = [new LfgParticipantEntity { UserId = owner, Response = LfgResponse.Joined, JoinedAt = now }], // the owner is the first player
            };
            Listings.Add(listing);
            await db.SaveChangesAsync(ct);
            return listing.Id;
        }, ct);

        if (id == 0)
            return Refused(No(OperationError.Conflict, "lfg.create.limit", o.MaxActiveListingsPerUser));
        logger.LogInformation("LFG listing {Listing} created in guild {Guild} channel {Channel}: {Max} players, starts {EventAt:O}, expires {ExpiresAt:O}",
            id, guild, channel, draft.MaxPlayers, eventAt ?? now, expiresAt);
        return new LfgResult(OperationResult.Ok("lfg.create.done"), await GetAsync(id, ct), RefreshCard: true);
    }

    /// <summary>Records the card message once Discord confirmed it (or a later button click reveals it). Never overwrites.</summary>
    public async Task AttachMessageAsync(long listingId, GuildId guild, ChannelId channel, MessageId message, CancellationToken ct) =>
        await Listings.Where(x => x.Id == listingId && x.GuildId == guild.Value && x.MessageId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MessageId, (ulong?)message.Value).SetProperty(x => x.ChannelId, channel.Value), ct);

    /// <summary>The card could not be posted at all: the listing never existed for anyone, so it is removed (frees the owner's slot).</summary>
    public async Task DiscardAsync(long listingId, CancellationToken ct)
    {
        await Participants.Where(p => p.ListingId == listingId).ExecuteDeleteAsync(ct);
        await Listings.Where(x => x.Id == listingId).ExecuteDeleteAsync(ct);
        logger.LogWarning("LFG listing {Listing} discarded: its card could not be posted", listingId);
    }

    /// <summary>"Katıl": a new player, or a Maybe who commits. Only Joined players count against <c>MaxPlayers</c>.</summary>
    public async Task<LfgResult> JoinAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = actor.UserId.Value;
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);

            var members = await Participants.Where(p => p.ListingId == listing.Id).ToListAsync(ct);
            var mine = members.FirstOrDefault(p => p.UserId == user);
            if (mine is { Response: LfgResponse.Joined })
                return (No(OperationError.Conflict, "lfg.join.already"), false);
            var joined = members.Count(p => p.Response == LfgResponse.Joined); // only confirmed players fill slots
            if (joined >= listing.MaxPlayers)
            {
                if (listing.Status != LfgStatus.Full)
                {
                    listing.Status = LfgStatus.Full;
                    listing.Version++;
                    await db.SaveChangesAsync(ct);
                }

                // A Maybe stays Maybe; the clicked card still offered a slot.
                return (No(OperationError.Conflict, mine is null ? "lfg.join.full" : "lfg.join.full_stays_maybe"), true);
            }

            if (mine is null)
            {
                Participants.Add(new LfgParticipantEntity { ListingId = listing.Id, UserId = user, Response = LfgResponse.Joined, JoinedAt = now });
            }
            else
            {
                mine.Response = LfgResponse.Joined; // Maybe -> Joined
                mine.JoinedAt = now;
            }

            var full = joined + 1 >= listing.MaxPlayers;
            if (full)
                listing.Status = LfgStatus.Full;
            listing.Version++;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
            {
                // Unreachable while the write lock is held; the primary key still guarantees no duplicate participant.
                return (No(OperationError.Conflict, "lfg.join.already"), false);
            }

            if (full)
                logger.LogInformation("LFG listing {Listing} is full ({Players} players)", listing.Id, listing.MaxPlayers);
            return (OperationResult.Ok("lfg.join.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, actor.GuildId, ct), refresh);
    }

    /// <summary>
    /// "Belki": not a confirmed player — no slot, not counted for Full, never pinged. From Joined it frees the slot (Full
    /// reopens). Allowed while Full. The owner is always Joined.
    /// </summary>
    public async Task<LfgResult> MaybeAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = actor.UserId.Value;
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);
            if (listing.OwnerUserId == user)
                return (No(OperationError.InvalidInput, "lfg.maybe.owner"), false);

            var mine = await Participants.FirstOrDefaultAsync(p => p.ListingId == listing.Id && p.UserId == user, ct);
            if (mine is { Response: LfgResponse.Maybe })
                return (No(OperationError.Conflict, "lfg.maybe.already"), false);
            if (mine is null)
            {
                Participants.Add(new LfgParticipantEntity { ListingId = listing.Id, UserId = user, Response = LfgResponse.Maybe, JoinedAt = now });
            }
            else
            {
                mine.Response = LfgResponse.Maybe; // Joined -> Maybe frees the slot
                mine.JoinedAt = now;
                if (listing.Status == LfgStatus.Full)
                    listing.Status = LfgStatus.Open;
            }

            listing.Version++;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
            {
                return (No(OperationError.Conflict, "lfg.maybe.already"), false);
            }

            return (OperationResult.Ok("lfg.maybe.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, actor.GuildId, ct), refresh);
    }

    /// <summary>"Ayrıl": removes a Joined or Maybe member completely; a Joined one frees a slot (Full reopens).</summary>
    public async Task<LfgResult> LeaveAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var user = actor.UserId.Value;
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);
            if (listing.OwnerUserId == user)
                return (No(OperationError.InvalidInput, "lfg.leave.owner"), false);

            var participant = await Participants.FirstOrDefaultAsync(p => p.ListingId == listing.Id && p.UserId == user, ct);
            if (participant is null)
                return (No(OperationError.NotFound, "lfg.leave.not_member"), false);

            Participants.Remove(participant);
            if (participant.Response == LfgResponse.Joined && listing.Status == LfgStatus.Full)
                listing.Status = LfgStatus.Open; // a slot is free again and the listing has not expired
            listing.Version++;
            await db.SaveChangesAsync(ct);
            return (OperationResult.Ok("lfg.leave.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, actor.GuildId, ct), refresh);
    }

    /// <summary>First step of closing (before the confirmation is shown): same checks as <see cref="CloseAsync"/>, no change.</summary>
    public async Task<LfgResult> CheckCloseAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (!MayClose(actor, listing))
                return (No(OperationError.Forbidden, "lfg.close.forbidden"), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: false, ct))
                return (Expired(), true);
            if (Ended(listing) is { } ended)
                return (ended, true);
            return (OperationResult.Ok("lfg.close.question"), false);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, actor.GuildId, ct), refresh);
    }

    /// <summary>
    /// Owner or moderator closes the listing. Idempotent: closing a closed listing succeeds without a change. The message
    /// is kept (history); the card is marked stale so it is redrawn as closed with disabled buttons; a notice still waiting
    /// in the outbox is cancelled.
    /// </summary>
    public async Task<LfgResult> CloseAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (result, refresh) = await WriteAsync(async () =>
        {
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), false);
            if (!MayClose(actor, listing))
                return (No(OperationError.Forbidden, "lfg.close.forbidden"), false);
            if (await ExpireIfDueAsync(listing, now, cardStale: true, ct))
                return (Expired(), true);
            if (listing.Status is LfgStatus.Closed or LfgStatus.Orphaned)
                return (OperationResult.Ok("lfg.close.already"), false);
            if (listing.Status == LfgStatus.Expired)
                return (Expired(), false);

            listing.Status = LfgStatus.Closed;
            listing.ClosedAt = now;
            listing.ClosedByUserId = actor.UserId.Value;
            listing.CardStale = true; // the confirmation lives in another (ephemeral) message: the card is edited separately
            listing.CardSyncAttempts = 0;
            listing.Version++;
            await db.SaveChangesAsync(ct);
            await LfgNoticePlanner.CancelPendingAsync(db, listing.Id, "listing_closed", now, ct);
            logger.LogInformation("LFG listing {Listing} closed by its {Who}", listing.Id, listing.OwnerUserId == actor.UserId.Value ? "owner" : "moderator");
            return (OperationResult.Ok("lfg.close.done"), true);
        }, ct);
        return new LfgResult(result, await GetAsync(listingId, actor.GuildId, ct), refresh);
    }

    /// <summary>
    /// Worker pass: every active listing whose expiry has passed (including all that expired while the bot was down)
    /// becomes Expired and is queued for a card edit. Returns the expired ids.
    /// </summary>
    public async Task<IReadOnlyList<long>> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var expired = await WriteAsync(async () =>
        {
            var due = await Listings
                .Where(x => (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.ExpiresAt <= now)
                .OrderBy(x => x.ExpiresAt)
                .Take(ExpiryBatch)
                .ToListAsync(ct);
            foreach (var listing in due)
                Expire(listing, cardStale: true);
            await db.SaveChangesAsync(ct);
            return due.Select(x => (x.Id, x.GuildId)).ToList();
        }, ct);
        foreach (var (id, guild) in expired)
            logger.LogInformation("LFG listing {Listing} expired (guild {Guild})", id, guild);
        return expired.Select(x => x.Id).ToList();
    }

    /// <summary>
    /// The voice button (card or notice). Re-reads everything: guild, active listing, the caller is a Joined player, the
    /// voice channel still exists as a voice channel of this guild. A member who is already connected to voice is moved there
    /// when the bot may (Move Members + Connect) and the member may connect themselves. Discord gives bots no way to connect a
    /// member who is not in voice: then (or without Move Members) the answer is the channel and a link that opens it — never a
    /// claimed "join". A vanished channel only disables the voice feature; the listing itself is untouched.
    /// </summary>
    public async Task<LfgVoiceResult> VoiceAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var listing = await Listings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == listingId && x.GuildId == actor.GuildId.Value, ct);
        if (listing is null)
            return new(NotFound());
        if ((Ended(listing) ?? (listing.ExpiresAt <= now ? Expired() : null)) is { } ended)
            return new(ended);
        if (listing.VoiceChannelId is not { } voiceId)
            return new(No(OperationError.NotFound, "lfg.voice.none"));
        var response = await Participants.AsNoTracking().Where(p => p.ListingId == listing.Id && p.UserId == actor.UserId.Value)
            .Select(p => (LfgResponse?)p.Response).FirstOrDefaultAsync(ct);
        if (response != LfgResponse.Joined)
            return new(No(OperationError.Forbidden, "lfg.voice.join_first")); // Maybe or not in the team

        var channel = new ChannelId(voiceId);
        var mention = "<#" + voiceId.ToString(CultureInfo.InvariantCulture) + ">";
        var access = await guilds.GetVoiceChannelAccessAsync(actor.GuildId, channel, ct);
        if (!access.Usable)
            return await VoiceGoneAsync(listing.Id, voiceId, ct);

        if (access.BotCanMove)
        {
            switch (await guilds.MoveMemberToVoiceAsync(actor.GuildId, actor.UserId, channel, ct))
            {
                case VoiceMoveOutcome.Moved:
                    return new(OperationResult.Ok("lfg.voice.moved", mention));
                case VoiceMoveOutcome.MemberCannotConnect:
                    return new(No(OperationError.Forbidden, "lfg.voice.no_access", mention));
                case VoiceMoveOutcome.ChannelUnavailable:
                    return await VoiceGoneAsync(listing.Id, voiceId, ct);
                case VoiceMoveOutcome.LimitedChannel:
                    return new(OperationResult.Ok("lfg.voice.open", mention), ChannelUrl(listing.GuildId, voiceId));
                default:
                    break; // not connected to voice, permission race, transient failure: the link below
            }
        }

        return new(OperationResult.Ok(access.BotCanMove ? "lfg.voice.open_not_connected" : "lfg.voice.open", mention), ChannelUrl(listing.GuildId, voiceId));
    }

    /// <summary>Opens the channel in Discord (fixed host, numeric ids only); it connects nobody by itself.</summary>
    private static string ChannelUrl(ulong guild, ulong channel) =>
        string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{guild}/{channel}");

    /// <summary>An interactive card update failed: the worker takes over.</summary>
    public async Task MarkCardStaleAsync(long listingId, CancellationToken ct) =>
        await Listings.Where(x => x.Id == listingId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CardStale, true), ct);

    public Task<LfgListingView?> GetAsync(long listingId, CancellationToken ct) => GetAsync(listingId, null, ct);

    /// <summary>With a guild, another guild's listing is null — an actor never receives a foreign listing, not even on refusal.</summary>
    public async Task<LfgListingView?> GetAsync(long listingId, GuildId? guild, CancellationToken ct)
    {
        var listing = guild is { } only
            ? await Listings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == listingId && x.GuildId == only.Value, ct)
            : await Listings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == listingId, ct);
        if (listing is null)
            return null;
        var players = await Participants.AsNoTracking().Where(p => p.ListingId == listingId).ToListAsync(ct);
        return ToView(listing, players);
    }

    public static LfgListingView ToView(LfgListingEntity listing, IEnumerable<LfgParticipantEntity> players)
    {
        var all = players.ToList();
        return new LfgListingView(
            listing.Id,
            new GuildId(listing.GuildId),
            new ChannelId(listing.ChannelId),
            listing.MessageId is { } m ? new MessageId(m) : null,
            new UserId(listing.OwnerUserId),
            listing.GameName,
            listing.Details,
            listing.MaxPlayers,
            listing.Status,
            listing.CreatedAt,
            listing.ExpiresAt,
            listing.ClosedAt,
            Ordered(listing, all, LfgResponse.Joined),
            listing.Version,
            Ordered(listing, all, LfgResponse.Maybe),
            listing.EventAt,
            listing.VoiceChannelId is { } v ? new ChannelId(v) : null);
    }

    /// <summary>Owner first, then in the order of their answer.</summary>
    private static List<UserId> Ordered(LfgListingEntity listing, IEnumerable<LfgParticipantEntity> players, LfgResponse response) =>
        players.Where(p => p.Response == response)
            .OrderBy(p => p.UserId == listing.OwnerUserId ? 0 : 1).ThenBy(p => p.JoinedAt).ThenBy(p => p.UserId)
            .Select(p => new UserId(p.UserId)).ToList();

    /// <summary>The chosen voice channel is gone: the voice feature is dropped (card redrawn without it); the listing stays.</summary>
    private async Task<LfgVoiceResult> VoiceGoneAsync(long listingId, ulong voiceId, CancellationToken ct)
    {
        var cleared = await Listings.Where(x => x.Id == listingId && x.VoiceChannelId == voiceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.VoiceChannelId, (ulong?)null)
                .SetProperty(x => x.CardStale, x => x.MessageId != null)
                .SetProperty(x => x.CardSyncAttempts, 0)
                .SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (cleared > 0)
            logger.LogInformation("LFG listing {Listing}: its voice channel no longer exists; voice feature removed", listingId);
        return new(No(OperationError.NotFound, "lfg.voice.gone"));
    }

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

    /// <summary>Another guild's listing is indistinguishable from a missing one.</summary>
    private Task<LfgListingEntity?> FindAsync(ActorContext actor, long listingId, CancellationToken ct) =>
        Listings.FirstOrDefaultAsync(x => x.Id == listingId && x.GuildId == actor.GuildId.Value, ct);

    private static bool MayClose(ActorContext actor, LfgListingEntity listing) =>
        listing.OwnerUserId == actor.UserId.Value || Authorize.Require(actor, new GuildId(listing.GuildId), ModeratorPermission).IsAllowed;

    /// <summary>Lazy expiry on interaction: a listing past its expiry is expired right here, even before the worker gets to it.</summary>
    private async Task<bool> ExpireIfDueAsync(LfgListingEntity listing, DateTimeOffset now, bool cardStale, CancellationToken ct)
    {
        if (listing.Status is not (LfgStatus.Open or LfgStatus.Full) || listing.ExpiresAt > now)
            return false;
        Expire(listing, cardStale);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("LFG listing {Listing} expired (guild {Guild})", listing.Id, listing.GuildId);
        return true;
    }

    private static void Expire(LfgListingEntity listing, bool cardStale)
    {
        listing.Status = LfgStatus.Expired;
        listing.ClosedAt = listing.ExpiresAt;
        if (cardStale)
        {
            listing.CardStale = true;
            listing.CardSyncAttempts = 0;
        }

        listing.Version++;
    }

    private static OperationResult? Ended(LfgListingEntity listing) => listing.Status switch
    {
        LfgStatus.Expired => Expired(),
        LfgStatus.Closed or LfgStatus.Orphaned => No(OperationError.Conflict, "lfg.closed"),
        _ => null,
    };

    private static OperationResult NotFound() => No(OperationError.NotFound, "lfg.not_found");

    private static OperationResult Expired() => No(OperationError.Expired, "lfg.expired");

    /// <summary>Expected refusals (full, already joined, …) are normal outcomes: no trace code and no log line per click.</summary>
    private static OperationResult No(OperationError error, string key, params object[] args) => new(false, key, args, error);

    private static LfgResult Refused(OperationResult result) => new(result, null, RefreshCard: false);
}
