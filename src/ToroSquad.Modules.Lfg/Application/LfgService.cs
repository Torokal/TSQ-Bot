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
/// What the /ekip form asked for. <see cref="StartAt"/>: empty = now, otherwise a date and time (read in the guild's time
/// zone). The notices are explicit opt-ins and need a later start. <see cref="VoiceChannel"/>: optional guild voice channel.
/// </summary>
public sealed record LfgCreateInput(
    string? Game,
    int Players,
    string? Details = null,
    int? DurationMinutes = null,
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
        var (draft, refusal) = await PrepareCreateAsync(actor, channel, input, now, ct);
        if (draft is null)
            return Refused(refusal!);

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

    /// <summary>
    /// Before the /ekip form opens: the refusals that do not depend on what will be typed (listing channel, active-listing
    /// limit), so nobody fills a form for nothing. Advisory only — saving checks everything again.
    /// </summary>
    public async Task<OperationResult?> PrecheckCreateAsync(ActorContext actor, ChannelId channel, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        if (await WrongChannelAsync(actor, channel, ct) is { } wrong)
            return wrong;
        var active = await Listings.AsNoTracking().CountAsync(x => x.GuildId == actor.GuildId.Value && x.OwnerUserId == actor.UserId.Value &&
                                                                   (x.Status == LfgStatus.Open || x.Status == LfgStatus.Full) && x.ExpiresAt > now, ct);
        return active >= o.MaxActiveListingsPerUser ? No(OperationError.Conflict, "lfg.create.limit", o.MaxActiveListingsPerUser) : null;
    }

    /// <summary>The submitted /ekip form checked exactly like <see cref="CreateAsync"/> would, without storing anything.</summary>
    public async Task<LfgFormCheck> CheckCreateAsync(ActorContext actor, ChannelId channel, LfgCreateInput input, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var (draft, refusal) = await PrepareCreateAsync(actor, channel, input, now, ct);
        if (draft is null)
            return new LfgFormCheck(refusal!, null);
        var (eventAt, _) = draft.Schedule(now); // the start date (or none = now); the settings step offers notices only for a date
        return new LfgFormCheck(OperationResult.Ok("lfg.form.checked"),
            new LfgFormPreview(draft.GameName, draft.Details, draft.MaxPlayers, draft.Start ?? LfgStart.Now, eventAt, draft.Duration));
    }

    /// <summary>
    /// ✏️ Düzenle: only the owner, only while the listing is active. Returns the listing and its form (texts, start in the
    /// guild's time zone, duration). Nothing changes here; the save re-checks everything against the stored state.
    /// </summary>
    public async Task<LfgEditOpening> OpenEditAsync(ActorContext actor, long listingId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var listing = await GetAsync(listingId, actor.GuildId, ct);
        if (listing is null)
            return new(NotFound(), null, null);
        if (listing.Owner != actor.UserId)
            return new(No(OperationError.Forbidden, "lfg.edit.forbidden"), null, null);
        if (!listing.IsActive || listing.ExpiresAt <= now)
            return new(No(OperationError.Conflict, "lfg.edit.ended"), null, null);
        var voice = listing.VoiceChannel is { } v && (await guilds.GetVoiceChannelAccessAsync(actor.GuildId, v, ct)).Usable ? v : (ChannelId?)null;
        return new(OperationResult.Ok("lfg.edit.open"), listing, LfgForm.Prefill(listing, (await ZoneAsync(actor.GuildId, ct)).Zone), voice);
    }

    /// <summary>
    /// The submitted edit form checked exactly like <see cref="EditAsync"/> would, without storing anything and without the
    /// write lock (it answers a modal within Discord's 3 seconds); the save decides again under the lock.
    /// </summary>
    public async Task<LfgFormCheck> CheckEditAsync(ActorContext actor, long listingId, LfgEditInput input, CancellationToken ct)
    {
        var (result, preview) = await EditCoreAsync(actor, listingId, input, save: false, ct);
        return new LfgFormCheck(result, preview);
    }

    /// <summary>
    /// The owner saves the edit form: game, details, size, start, duration, the two notice opt-ins and the voice channel.
    /// Owner, participants and status are never edited. Everything is decided on the state read inside the write
    /// transaction (never on what the form showed): only the owner, only while active; the size never below the Joined
    /// players (Maybe never counts; equal = Full); the start only while it is still ahead (a date, never in the past, or
    /// empty = start now); <c>ExpiresAt = (EventAt ?? CreatedAt) + duration</c>, so an edit never restarts the listing's
    /// lifetime. A notice that was already handled (queued or skipped) is never handled again — a new start or switching
    /// it off and on again cannot repeat it; one switched off while still waiting in the outbox is cancelled; one switched
    /// on after its moment is consumed as skipped, never sent late. All or nothing: a refused edit changes no field.
    /// </summary>
    public async Task<LfgResult> EditAsync(ActorContext actor, long listingId, LfgEditInput input, CancellationToken ct)
    {
        var (result, _) = await EditCoreAsync(actor, listingId, input, save: true, ct);
        return new LfgResult(result, await GetAsync(listingId, actor.GuildId, ct), result.Succeeded && result.MessageKey == "lfg.edit.done");
    }

    private async Task<(OperationResult Result, LfgFormPreview? Preview)> EditCoreAsync(ActorContext actor, long listingId, LfgEditInput input, bool save, CancellationToken ct)
    {
        var o = options.Value;
        var (zone, zoneKnown) = await ZoneAsync(actor.GuildId, ct);
        // A chosen voice channel must be a plain voice channel of THIS guild as the bot sees it. An untouched one is checked
        // below, as the channel that will actually be stored.
        var voiceUntouched = input.OpenedSettings is { } shown && input.VoiceChannel == shown.VoiceChannel;
        if (!voiceUntouched && input.VoiceChannel is { } voice && !(await guilds.GetVoiceChannelAccessAsync(actor.GuildId, voice, ct)).Usable)
            return (No(OperationError.InvalidInput, "lfg.create.voice_invalid"), null);

        async Task<(OperationResult, LfgFormPreview?)> Work()
        {
            var now = clock.GetUtcNow();
            var listing = await FindAsync(actor, listingId, ct);
            if (listing is null)
                return (NotFound(), (LfgFormPreview?)null);
            if (listing.OwnerUserId != actor.UserId.Value)
                return (No(OperationError.Forbidden, "lfg.edit.forbidden"), null);
            var expired = save ? await ExpireIfDueAsync(listing, now, cardStale: true, ct) : listing.ExpiresAt <= now;
            if (expired || Ended(listing) is not null)
                return (No(OperationError.Conflict, "lfg.edit.ended"), null);

            // Untouched (as the form found it) = the value stored NOW: a stale form never reverts a newer edit.
            var form = input.Form;
            var opened = input.Opened;
            var typedGame = opened is not null && LfgForm.SameValue(form.Game, opened.Game) ? listing.GameName : form.Game;
            var typedDetails = opened is not null && LfgForm.SameValue(form.Details, opened.Details) ? listing.Details : form.Details;
            var playersUntouched = opened is not null && LfgForm.SameValue(form.Players, opened.Players);
            var maxPlayers = playersUntouched ? listing.MaxPlayers : LfgFormText.Players(form.Players);
            // An untouched size stays valid even if the configured maximum was lowered after the listing was opened.
            var sizeLimit = playersUntouched ? Math.Max(o.MaxPlayersPerListing, listing.MaxPlayers) : o.MaxPlayersPerListing;
            var (game, details, textError) = LfgRules.ValidateTexts(typedGame, typedDetails, maxPlayers, sizeLimit);
            if (textError != LfgDraftError.None)
                return (Refusal(textError), null);
            var settings = input.OpenedSettings;
            var notifyBefore = settings is not null && input.NotifyBeforeStart == settings.NotifyBeforeStart ? listing.NotifyBeforeStart : input.NotifyBeforeStart;
            var notifyAtStart = settings is not null && input.NotifyAtStart == settings.NotifyAtStart ? listing.NotifyAtStart : input.NotifyAtStart;
            var storedVoice = listing.VoiceChannelId is { } sv ? new ChannelId(sv) : (ChannelId?)null;
            var voice = input.VoiceChannel;
            if (voiceUntouched)
            {
                // Untouched keeps the channel stored NOW — except the unusable one the form did not offer: if it is still the
                // stored one, keeping "none" removes it.
                var droppedAtOpen = settings!.VoiceChannel is null && settings.StoredVoiceChannel is not null;
                voice = droppedAtOpen && storedVoice == settings.StoredVoiceChannel ? null : storedVoice;
                if (voice is { } kept && !(await guilds.GetVoiceChannelAccessAsync(actor.GuildId, kept, ct)).Usable)
                    return (No(OperationError.InvalidInput, "lfg.create.voice_invalid"), null);
            }

            var members = await Participants.Where(p => p.ListingId == listing.Id).ToListAsync(ct);
            var joined = members.Count(p => p.Response == LfgResponse.Joined); // Maybe never counts
            if (maxPlayers < joined)
                return (No(OperationError.InvalidInput, "lfg.edit.players_below_joined", joined), null);

            var current = ToView(listing, members);
            var shown = input.Opened ?? LfgForm.Prefill(current, zone); // "untouched" = as the form showed it

            // Start: untouched, or new while the start is still ahead. Started = its moment passed (a "now" listing started
            // at creation) or its start notice was already handled.
            var started = (listing.EventAt ?? listing.CreatedAt) <= now || listing.StartNoticeState != LfgNoticeState.Pending;
            var eventAt = listing.EventAt;
            var start = listing.EventAt is { } at ? LfgStart.AtInstant(at) : LfgStart.Now;
            if (!LfgForm.SameStart(form.Start, shown.Start, TimeZoneInfo.ConvertTime(now, zone).Year))
            {
                if (started)
                    return (Refusal(LfgDraftError.StartLocked), null);
                var typed = LfgRules.Normalize(form.Start);
                if (typed is not null && !zoneKnown)
                    return (Refusal(LfgDraftError.TimeZoneInvalid), null); // like create: never guess a zone for a typed date
                var (resolved, startError) = LfgRules.ResolveStart(typed, zone, now);
                if (resolved is null)
                    return (Refusal(startError), null);
                start = resolved;
                eventAt = resolved.At ?? now; // emptied on a scheduled listing: it starts at this moment
            }

            // Duration: untouched keeps the current one (even a configured default that is not one of the choices).
            var duration = LfgForm.Duration(current);
            if (!LfgForm.SameText(form.Duration, shown.Duration))
            {
                var minutes = LfgFormText.DurationMinutes(form.Duration);
                if (minutes is { } m && !LfgRules.DurationChoicesMinutes.Contains(m))
                    return (Refusal(LfgDraftError.DurationInvalid), null);
                duration = TimeSpan.FromMinutes(minutes ?? o.DefaultExpirationMinutes);
            }

            var expiresAt = (eventAt ?? listing.CreatedAt) + duration;
            if (expiresAt <= now)
                return (Refusal(LfgDraftError.ExpiryPassed), null);
            if ((notifyBefore || notifyAtStart) && eventAt is null)
                return (Refusal(LfgDraftError.NoticeNeedsStart), null);

            var preview = new LfgFormPreview(game!, details, maxPlayers, start, eventAt, duration);
            var voiceId = voice?.Value;
            var unchanged = listing.GameName == game && listing.Details == details && listing.MaxPlayers == maxPlayers && listing.EventAt == eventAt &&
                            listing.ExpiresAt == expiresAt && listing.NotifyBeforeStart == notifyBefore &&
                            listing.NotifyAtStart == notifyAtStart && listing.VoiceChannelId == voiceId;
            if (!save)
                return (OperationResult.Ok("lfg.form.checked"), preview);
            if (unchanged)
                return (OperationResult.Ok("lfg.edit.unchanged"), preview);

            // Notices: handled once. Off while waiting in the outbox, or its start moved -> the undelivered one is cancelled
            // (the existing close semantics; its text names the old time); on after its moment -> consumed as skipped; a
            // queued or skipped notice is never reset by a new start or a re-enable.
            if (eventAt != listing.EventAt && listing.ReminderState == LfgNoticeState.Queued)
                await LfgNoticePlanner.CancelPendingAsync(db, listing.Id, "start_moved", now, ct, LfgNoticePlanner.KindReminder);
            else if (listing.NotifyBeforeStart && !notifyBefore && listing.ReminderState == LfgNoticeState.Queued)
                await LfgNoticePlanner.CancelPendingAsync(db, listing.Id, "reminder_switched_off", now, ct, LfgNoticePlanner.KindReminder);
            if (listing.NotifyAtStart && !notifyAtStart && listing.StartNoticeState == LfgNoticeState.Queued)
                await LfgNoticePlanner.CancelPendingAsync(db, listing.Id, "start_notice_switched_off", now, ct, LfgNoticePlanner.KindStart);
            if (notifyBefore && !listing.NotifyBeforeStart && listing.ReminderState == LfgNoticeState.Pending && eventAt <= now)
            {
                listing.ReminderState = LfgNoticeState.Skipped;
                listing.ReminderHandledAt = now;
            }

            if (notifyAtStart && !listing.NotifyAtStart && listing.StartNoticeState == LfgNoticeState.Pending && eventAt <= now)
            {
                listing.StartNoticeState = LfgNoticeState.Skipped;
                listing.StartNoticeHandledAt = now;
            }

            listing.GameName = game!;
            listing.Details = details;
            listing.MaxPlayers = maxPlayers;
            listing.EventAt = eventAt;
            listing.ExpiresAt = expiresAt;
            listing.NotifyBeforeStart = notifyBefore;
            listing.NotifyAtStart = notifyAtStart;
            listing.VoiceChannelId = voiceId;
            listing.Status = joined >= maxPlayers ? LfgStatus.Full : LfgStatus.Open;
            listing.CardStale = listing.MessageId is not null; // the form lives in another (ephemeral) message: the card is edited separately
            listing.CardSyncAttempts = 0;
            listing.Version++;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("LFG listing {Listing} edited by its owner: {Max} players, starts {EventAt:O}, expires {ExpiresAt:O}",
                listing.Id, maxPlayers, eventAt ?? listing.CreatedAt, expiresAt);
            return (OperationResult.Ok("lfg.edit.done"), preview);
        }

        if (save)
            return await WriteAsync(Work, ct);
        db.ChangeTracker.Clear();
        return await Work(); // read only: nothing is saved on this path
    }

    /// <summary>
    /// Everything about a new listing that does not need the write lock: the texts and numbers, the start (a custom date is
    /// wall-clock time in the guild's existing /setup time zone — default Europe/Istanbul), the listing channel and the voice
    /// channel. The active-listing limit is checked under the lock when the listing is inserted.
    /// </summary>
    private async Task<(LfgDraft? Draft, OperationResult? Refusal)> PrepareCreateAsync(ActorContext actor, ChannelId channel, LfgCreateInput input, DateTimeOffset now,
        CancellationToken ct)
    {
        var o = options.Value;
        TimeZoneInfo? zone = null;
        if (!string.IsNullOrWhiteSpace(input.StartAt) && GuildTime.TryResolve((await settings.GetAsync(actor.GuildId, ct)).TimeZoneId, out var guildZone))
            zone = guildZone;
        var (draft, error) = LfgRules.Validate(input.Game, input.Details, input.Players, input.DurationMinutes, o.MaxPlayersPerListing, o.DefaultExpirationMinutes,
            input.NotifyBeforeStart || input.NotifyAtStart, input.StartAt, zone, now);
        if (draft is null)
            return (null, Refusal(error));

        if (await WrongChannelAsync(actor, channel, ct) is { } wrong)
            return (null, wrong);

        // Must be a plain voice channel of THIS guild as the bot sees it (another guild's id is simply unknown here).
        if (input.VoiceChannel is { } voice && !(await guilds.GetVoiceChannelAccessAsync(actor.GuildId, voice, ct)).Usable)
            return (null, No(OperationError.InvalidInput, "lfg.create.voice_invalid"));
        return (draft, null);
    }

    private async Task<OperationResult?> WrongChannelAsync(ActorContext actor, ChannelId channel, CancellationToken ct)
    {
        var config = await db.Set<LfgGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        return config?.ChannelId is { } only && only != channel.Value
            ? No(OperationError.InvalidInput, "lfg.create.wrong_channel", "<#" + only.ToString(CultureInfo.InvariantCulture) + ">")
            : null;
    }

    /// <summary>
    /// The guild's time zone (existing /setup setting). An unknown id is reported (<c>Known = false</c>: a typed date is then
    /// refused, like on create); the default zone (then UTC) is only used to show a stored start.
    /// </summary>
    private async Task<(TimeZoneInfo Zone, bool Known)> ZoneAsync(GuildId guild, CancellationToken ct)
    {
        if (GuildTime.TryResolve((await settings.GetAsync(guild, ct)).TimeZoneId, out var zone))
            return (zone, true);
        return (GuildTime.TryResolve(GuildSettings.Default(guild).TimeZoneId, out var fallback) ? fallback : TimeZoneInfo.Utc, false);
    }

    private OperationResult Refusal(LfgDraftError error)
    {
        var o = options.Value;
        return error switch
        {
            LfgDraftError.GameMissing or LfgDraftError.GameTooShort => No(OperationError.InvalidInput, "lfg.create.game_too_short", LfgRules.GameNameMinLength),
            LfgDraftError.GameTooLong => No(OperationError.InvalidInput, "lfg.create.game_too_long", LfgRules.GameNameMaxLength),
            LfgDraftError.DetailsTooLong => No(OperationError.InvalidInput, "lfg.create.details_too_long", LfgRules.DetailsMaxLength),
            LfgDraftError.PlayersOutOfRange => No(OperationError.InvalidInput, "lfg.create.players_range", LfgRules.MinPlayers, Math.Min(o.MaxPlayersPerListing, LfgRules.HardMaxPlayers)),
            LfgDraftError.NoticeNeedsStart => No(OperationError.InvalidInput, "lfg.create.notice_needs_start"),
            LfgDraftError.DateFormat => No(OperationError.InvalidInput, "lfg.create.date_format"),
            LfgDraftError.DateNotInTimeZone => No(OperationError.InvalidInput, "lfg.create.date_not_in_zone"),
            LfgDraftError.DateAmbiguous => No(OperationError.InvalidInput, "lfg.create.date_ambiguous"),
            LfgDraftError.DateNotInFuture => No(OperationError.InvalidInput, "lfg.create.date_not_future"),
            LfgDraftError.DateTooFar => No(OperationError.InvalidInput, "lfg.create.date_too_far"),
            LfgDraftError.TimeZoneInvalid => No(OperationError.InvalidInput, "lfg.create.timezone_invalid"),
            LfgDraftError.StartLocked => No(OperationError.Conflict, "lfg.edit.start_locked"),
            LfgDraftError.ExpiryPassed => No(OperationError.InvalidInput, "lfg.edit.expiry_passed"),
            _ => No(OperationError.InvalidInput, "lfg.create.duration_invalid"),
        };
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
        // After the delete (the planner re-reads the listing under its lock, so nothing new can be planned now): a notice
        // planned in the meantime (short start) never goes out for a listing nobody saw.
        await LfgNoticePlanner.CancelPendingAsync(db, listingId, "listing_discarded", clock.GetUtcNow(), ct);
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
            listing.VoiceChannelId is { } v ? new ChannelId(v) : null,
            listing.NotifyBeforeStart,
            listing.NotifyAtStart);
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
