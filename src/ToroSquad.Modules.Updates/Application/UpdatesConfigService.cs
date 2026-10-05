using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Persistence;

namespace ToroSquad.Modules.Updates.Application;

public enum UpdatesCheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record UpdatesCheck(string LabelKey, UpdatesCheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>One registered game as a guild sees it.</summary>
/// <param name="ChannelId">The game's own channel in this guild; null: it posts to the guild's Updates channel.</param>
/// <param name="ChannelProblem">The last permanent delivery problem in the game's own channel.</param>
public sealed record UpdatesGameStatus(
    GameUpdateDefinition Game,
    string ProviderName,
    bool Enabled,
    UpdatesSourceStateEntity? Source,
    UpdatesItemEntity? LastDiscovered,
    DateTimeOffset? LastCardAt,
    ulong? ChannelId = null,
    string? ChannelProblem = null);

/// <summary>What /tsq-admin modul:updates islem:status shows (no secrets, no user data).</summary>
public sealed record UpdatesStatus(
    UpdatesMode Mode,
    UpdatesMode EffectiveMode,
    bool ModuleEnabled,
    ulong? ChannelId,
    bool Paused,
    string? ChannelProblem,
    IReadOnlyList<UpdatesGameStatus> Games);

/// <summary>A card for the admin preview: the latest real update of the game, or an explicitly synthetic sample.</summary>
public sealed record UpdatesPreview(OutgoingMessage Message, bool Synthetic);

/// <summary>
/// /tsq-admin modul:updates islem:configure|games|game-enable|game-disable|game-channel|pause|resume|preview|status|doctor. Every method
/// authorizes the actor (Manage Server) and only touches rows of <c>actor.GuildId</c>. Nothing here requests a provider or
/// sends to a channel; games can only be chosen from the registered definitions.
/// </summary>
public sealed class UpdatesConfigService(
    ToroDbContext db,
    IGuildGateway guilds,
    IModuleGate gate,
    GameUpdateCatalog catalog,
    UpdatesPlanner planner,
    UpdateCardRenderer renderer,
    IOptions<UpdatesOptions> options,
    TimeProvider clock)
{
    public const GuildPermission RequiredChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks;

    public IReadOnlyList<GameUpdateDefinition> RegisteredGames => catalog.Games;

    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "updates.config.channel_invalid");

        var now = clock.GetUtcNow();
        var config = await ConfigAsync(actor.GuildId, create: true, ct);
        if (config!.ChannelId is null)
        {
            // First configuration: only updates published from now on (nothing from before is ever dumped into the channel).
            UpdatesWindow.Restart(config, planner.EffectiveMode, now);
        }

        config.ChannelId = channelId;
        config.ChannelProblem = null;
        config.ChannelProblemAt = null;
        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
        var missing = RequiredChannelPermissions & ~access.Permissions;
        return access.Permissions.Grants(RequiredChannelPermissions)
            ? OperationResult.Ok("updates.config.saved", mention)
            : OperationResult.Ok("updates.config.saved_missing_permissions", mention, missing.ToString());
    }

    public async Task<OperationResult> SetPausedAsync(ActorContext actor, bool paused, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        if (config?.ChannelId is null)
            return OperationResult.Fail(OperationError.InvalidInput, "updates.config.no_channel");
        if (config.Paused == paused)
            return OperationResult.Ok(paused ? "updates.pause.already" : "updates.resume.already");
        var now = clock.GetUtcNow();
        config.Paused = paused;
        if (!paused)
        {
            // Updates of the paused period are skipped, not caught up.
            UpdatesWindow.Restart(config, planner.EffectiveMode, now);
        }

        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(paused ? "updates.pause.done" : "updates.resume.done");
    }

    /// <summary>Follows or unfollows a registered game. Enabling starts that game's own window: no earlier update is posted.</summary>
    public async Task<OperationResult> SetGameEnabledAsync(ActorContext actor, string? gameKey, bool enabled, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        if (catalog.Find(gameKey) is not { } game)
            return OperationResult.Fail(OperationError.InvalidInput, "updates.game.unknown");

        var now = clock.GetUtcNow();
        var set = db.Set<UpdatesSubscriptionEntity>();
        var row = await set.FirstOrDefaultAsync(s => s.GuildId == actor.GuildId.Value && s.GameKey == game.Key, ct);
        var name = game.DisplayName;
        if ((row?.Enabled ?? false) == enabled)
            return OperationResult.Ok(enabled ? "updates.game.already_on" : "updates.game.already_off", name);
        if (row is null)
        {
            row = new UpdatesSubscriptionEntity { GuildId = actor.GuildId.Value, GameKey = game.Key, CreatedAt = now };
            set.Add(row);
        }

        row.Enabled = enabled;
        if (enabled)
            row.EnabledAt = now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (!enabled)
            return OperationResult.Ok("updates.game.disabled", name);
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        return OperationResult.Ok(config?.ChannelId is null ? "updates.game.enabled_no_channel" : "updates.game.enabled", name);
    }

    /// <summary>
    /// Gives a registered game its own channel in this guild, or (<paramref name="channelId"/> null) sends it back to the
    /// guild's Updates channel. The guild's Updates channel must exist first: it is the default every game falls back to.
    /// Nothing is posted again because of a channel change, and cards already posted stay where they are. The channel can
    /// be chosen before the game is turned on.
    /// </summary>
    public async Task<OperationResult> SetGameChannelAsync(ActorContext actor, string? gameKey, ulong? channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        if (catalog.Find(gameKey) is not { } game)
            return OperationResult.Fail(OperationError.InvalidInput, "updates.game.unknown");
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        if (config?.ChannelId is not { } common)
            return OperationResult.Fail(OperationError.InvalidInput, "updates.config.no_channel");

        BotChannelAccess? access = null;
        if (channelId is { } requested)
        {
            access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(requested), ct);
            if (!access.Exists || !access.IsTextBased)
                return OperationResult.Fail(OperationError.InvalidInput, "updates.config.channel_invalid");
        }

        var now = clock.GetUtcNow();
        var set = db.Set<UpdatesSubscriptionEntity>();
        var row = await set.FirstOrDefaultAsync(s => s.GuildId == actor.GuildId.Value && s.GameKey == game.Key, ct);
        var name = game.DisplayName;
        var commonMention = Mention(common);
        if (row?.ChannelId == channelId)
            return channelId is { } same ? OperationResult.Ok("updates.game.channel_already", name, Mention(same)) : OperationResult.Ok("updates.game.channel_already_common", name, commonMention);
        if (row is null)
        {
            // The channel can be chosen before the game is followed; following stays its own decision.
            row = new UpdatesSubscriptionEntity { GuildId = actor.GuildId.Value, GameKey = game.Key, CreatedAt = now };
            set.Add(row);
        }

        row.ChannelId = channelId;
        row.ChannelProblem = null;
        row.ChannelProblemAt = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        if (channelId is not { } chosen)
            return OperationResult.Ok("updates.game.channel_common", name, commonMention);
        var missing = RequiredChannelPermissions & ~access!.Permissions;
        return access.Permissions.Grants(RequiredChannelPermissions)
            ? OperationResult.Ok(row.Enabled ? "updates.game.channel_saved" : "updates.game.channel_saved_game_off", name, Mention(chosen))
            : OperationResult.Ok("updates.game.channel_saved_missing_permissions", name, Mention(chosen), missing.ToString());
    }

    private static string Mention(ulong channel) => "<#" + channel.ToString(CultureInfo.InvariantCulture) + ">";

    public async Task<(OperationResult Auth, UpdatesStatus? Status)> StatusAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        var enabled = await gate.IsEnabledAsync(actor.GuildId, UpdatesModule.ModuleIdTyped, ct);
        var subscriptions = await db.Set<UpdatesSubscriptionEntity>().AsNoTracking().Where(s => s.GuildId == actor.GuildId.Value).ToListAsync(ct);
        const int update = (int)UpdateClassification.Update;
        var games = new List<UpdatesGameStatus>();
        foreach (var game in catalog.Games)
        {
            var source = await db.Set<UpdatesSourceStateEntity>().AsNoTracking().FirstOrDefaultAsync(s => s.Provider == game.Provider && s.GameKey == game.Key, ct);
            var discovered = await db.Set<UpdatesItemEntity>().AsNoTracking()
                .Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.Classification == update && !a.Baseline && a.Title != null)
                .OrderByDescending(a => a.FirstSeenAt).ThenByDescending(a => a.ExternalId).FirstOrDefaultAsync(ct);
            // "Last card" is a card that really reached Discord (a planned card can still be cancelled or fail).
            var liveKind = UpdatesPlanner.Kind(game.Key, dryRun: false);
            var lastCard = await db.Outbox.AsNoTracking()
                .Where(o => o.GuildId == actor.GuildId.Value && o.ModuleId == UpdatesModule.ModuleIdValue && o.Kind == liveKind && o.Status == OutboxStatus.Sent)
                .OrderByDescending(o => o.SentAt).ThenByDescending(o => o.Id).Select(o => o.SentAt).FirstOrDefaultAsync(ct);
            var subscription = subscriptions.FirstOrDefault(s => s.GameKey == game.Key);
            games.Add(new UpdatesGameStatus(game, catalog.ProviderOf(game).DisplayName, subscription?.Enabled == true, source, discovered, lastCard,
                subscription?.ChannelId, subscription?.ChannelProblem));
        }

        return (OperationResult.Ok("updates.status.title"), new UpdatesStatus(options.Value.Mode, planner.EffectiveMode, enabled, config?.ChannelId,
            config?.Paused == true, config?.ChannelProblem, games));
    }

    public async Task<(OperationResult Auth, UpdatesPreview? Preview)> PreviewAsync(ActorContext actor, string? gameKey, string language, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);
        if (catalog.Find(gameKey) is not { } game)
            return (OperationResult.Fail(OperationError.InvalidInput, "updates.game.unknown"), null);
        var provider = catalog.ProviderOf(game);
        const int update = (int)UpdateClassification.Update;
        var candidates = await db.Set<UpdatesItemEntity>().AsNoTracking()
            .Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.Classification == update && a.Title != null)
            .OrderByDescending(a => a.PublishedAt ?? a.FirstSeenAt).ThenByDescending(a => a.ExternalId).Take(5).ToListAsync(ct);
        var latest = candidates.FirstOrDefault(a => provider.IsCanonicalUrl(a.Url));
        if (latest is not null)
            return (OperationResult.Ok("updates.preview.real"), new UpdatesPreview(
                renderer.Render(game, provider, latest.Url, latest.Title!, latest.PublishedAt, language, UpdateHighlightsJson.Parse(latest.Highlights)), false));

        // No stored update yet: a made-up card in the same layout, without a link.
        return (OperationResult.Ok("updates.preview.synthetic"), new UpdatesPreview(renderer.RenderSample(game, provider, clock.GetUtcNow(), language), true));
    }

    /// <summary>Configuration, channel and permissions, followed games, sources, baseline and stored state. Reads the database only.</summary>
    public async Task<(OperationResult Auth, IReadOnlyList<UpdatesCheck> Checks)> DoctorAsync(ActorContext actor, CancellationToken ct)
    {
        var (auth, status) = await StatusAsync(actor, ct);
        if (status is null)
            return (auth, []);
        var o = options.Value;
        var now = clock.GetUtcNow();
        var checks = new List<UpdatesCheck>
        {
            status.Mode switch
            {
                UpdatesMode.Off => new("updates.doctor.mode", UpdatesCheckState.Warning, "updates.doctor.mode_off", []),
                UpdatesMode.DryRun => new("updates.doctor.mode", UpdatesCheckState.Info, "updates.doctor.mode_dry", []),
                _ => status.EffectiveMode == UpdatesMode.Live
                    ? new("updates.doctor.mode", UpdatesCheckState.Ok, "updates.doctor.mode_live", [])
                    : new("updates.doctor.mode", UpdatesCheckState.Info, "updates.doctor.mode_live_delivery_dry", []),
            },
            new("updates.doctor.module", status.ModuleEnabled ? UpdatesCheckState.Ok : UpdatesCheckState.Warning, status.ModuleEnabled ? "updates.doctor.module_on" : "updates.doctor.module_off", []),
        };

        if (status.ChannelId is not { } channelId)
        {
            checks.Add(new("updates.doctor.channel", UpdatesCheckState.Problem, "updates.doctor.channel_missing", []));
        }
        else
        {
            var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
            var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
            if (!access.Exists || !access.IsTextBased)
                checks.Add(new("updates.doctor.channel", UpdatesCheckState.Problem, "updates.doctor.channel_gone", [mention]));
            else if (!access.Permissions.Grants(RequiredChannelPermissions))
                checks.Add(new("updates.doctor.channel", UpdatesCheckState.Problem, "updates.doctor.channel_permissions", [mention, (RequiredChannelPermissions & ~access.Permissions).ToString()]));
            else
                checks.Add(new("updates.doctor.channel", status.Paused ? UpdatesCheckState.Warning : UpdatesCheckState.Ok, status.Paused ? "updates.doctor.channel_paused" : "updates.doctor.channel_ok", [mention]));
            if (status.ChannelProblem is { } problem)
                checks.Add(new("updates.doctor.channel", UpdatesCheckState.Problem, "updates.doctor.channel_problem", [problem]));
        }

        var followed = status.Games.Where(g => g.Enabled).ToList();
        checks.Add(followed.Count == 0
            ? new("updates.doctor.games", UpdatesCheckState.Warning, "updates.doctor.games_none", [])
            : new("updates.doctor.games", UpdatesCheckState.Ok, "updates.doctor.games_value", [string.Join(", ", followed.Select(g => g.Game.DisplayName))]));

        const int update = (int)UpdateClassification.Update;
        foreach (var game in followed)
        {
            var name = game.Game.ShortName;
            var source = game.Source;
            if (game.ChannelId is { } own)
            {
                // The game's own channel is checked like the guild's: the card goes there, so the bot must be able to post there.
                var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(own), ct);
                var mention = Mention(own);
                if (!access.Exists || !access.IsTextBased)
                    checks.Add(new("updates.doctor.game_channel", UpdatesCheckState.Problem, "updates.doctor.game_channel_gone", [name, mention]));
                else if (!access.Permissions.Grants(RequiredChannelPermissions))
                    checks.Add(new("updates.doctor.game_channel", UpdatesCheckState.Problem, "updates.doctor.game_channel_permissions",
                        [name, mention, (RequiredChannelPermissions & ~access.Permissions).ToString()]));
                else
                    checks.Add(new("updates.doctor.game_channel", UpdatesCheckState.Ok, "updates.doctor.game_channel_ok", [name, mention]));
                if (game.ChannelProblem is { } problem)
                    checks.Add(new("updates.doctor.game_channel", UpdatesCheckState.Problem, "updates.doctor.game_channel_problem", [name, problem]));
            }

            if (source?.LastAttemptAt is not { } attempt)
            {
                checks.Add(new("updates.doctor.source", UpdatesCheckState.Info, "updates.doctor.source_never", [name, game.ProviderName]));
                continue;
            }

            var success = source.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—";
            var state = source.ConsecutiveFailures == 0 ? UpdatesCheckState.Ok : source.ConsecutiveFailures >= 3 ? UpdatesCheckState.Problem : UpdatesCheckState.Warning;
            checks.Add(new("updates.doctor.source", state, "updates.doctor.source_value",
                [name, game.ProviderName, success, DiscordText.Timestamp(attempt, 'R'), OutcomeName(source), source.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—",
                    source.ConsecutiveFailures, source.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—", DiscordText.UntrustedPlain(source.LastDetail ?? "—", 120)]));
            checks.Add(new("updates.doctor.baseline", source.BaselineAt is null ? UpdatesCheckState.Warning : UpdatesCheckState.Ok,
                source.BaselineAt is null ? "updates.doctor.baseline_none" : "updates.doctor.baseline_value",
                source.BaselineAt is { } baseline ? [name, DiscordText.Timestamp(baseline, 'f')] : [name]));
            checks.Add(new("updates.doctor.posts", source.LastSkippedCount > 0 || source.LastAmbiguousCount > 0 ? UpdatesCheckState.Info : UpdatesCheckState.Ok, "updates.doctor.posts_value",
                [name, source.LastItemCount, source.LastUpdateCount, source.LastAmbiguousCount, source.LastSkippedCount,
                    source.SourceCacheSeconds is { } cache ? (cache / 60).ToString(CultureInfo.InvariantCulture) : "—"]));
            if (source.LastSuccessAt is { } ok && now - ok > TimeSpan.FromHours(o.CatchUpHours))
                checks.Add(new("updates.doctor.coverage_gap", UpdatesCheckState.Warning, "updates.doctor.coverage_gap_value", [name, o.CatchUpHours]));

            var liveKind = UpdatesPlanner.Kind(game.Game.Key, dryRun: false);
            var stored = await db.Set<UpdatesItemEntity>().AsNoTracking().CountAsync(a => a.Provider == game.Game.Provider && a.GameKey == game.Game.Key, ct);
            var cards = await db.Set<UpdatesDeliveryEntity>().AsNoTracking()
                .Where(d => d.GuildId == actor.GuildId.Value && d.Provider == game.Game.Provider && d.GameKey == game.Game.Key && d.Kind == liveKind)
                .Select(d => d.ExternalId).ToListAsync(ct);
            var withdrawn = cards.Count == 0 ? 0 : await db.Set<UpdatesItemEntity>().AsNoTracking()
                .CountAsync(a => a.Provider == game.Game.Provider && a.GameKey == game.Game.Key && a.Classification != update && cards.Contains(a.ExternalId), ct);
            checks.Add(new("updates.doctor.stored", withdrawn > 0 ? UpdatesCheckState.Warning : UpdatesCheckState.Info, withdrawn > 0 ? "updates.doctor.stored_withdrawn" : "updates.doctor.stored_value",
                withdrawn > 0 ? [name, stored, cards.Count, withdrawn] : [name, stored, cards.Count]));
        }

        return (auth, checks);
    }

    /// <summary>
    /// The last result as status and doctor name it: a failure by its kind, a success by whether it brought new posts — or
    /// as partial when the source said it could not do everything in that round (the detail says what).
    /// </summary>
    public static string OutcomeName(UpdatesSourceStateEntity source) => (UpdateFetchOutcome)source.LastOutcome switch
    {
        UpdateFetchOutcome.Ok when source.LastDetail?.StartsWith(UpdateFetchResult.PartialDetailPrefix, StringComparison.Ordinal) == true => "SuccessPartial",
        UpdateFetchOutcome.Ok => source.LastNewCount > 0 ? "SuccessItems" : "SuccessNoNewItems",
        UpdateFetchOutcome.Empty => "SuccessEmpty",
        var failure => failure.ToString(),
    };

    private async Task<UpdatesGuildConfigEntity?> ConfigAsync(GuildId guild, bool create, CancellationToken ct)
    {
        var set = db.Set<UpdatesGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config is null && create)
        {
            config = new UpdatesGuildConfigEntity { GuildId = guild.Value, UpdatedAt = clock.GetUtcNow() };
            set.Add(config);
        }

        return config;
    }
}

/// <summary>
/// The module stores no per-user data. Its guild rows (channel, followed games, delivery records) are purged with the rest
/// of a guild's data once the bot has left and retention elapsed — delivery records must not outlive the outbox rows they
/// describe. Posts and source state are not guild data and stay.
/// </summary>
public sealed class UpdatesUserData(ToroDbContext db) : IUserDataContributor
{
    public ModuleId Module => UpdatesModule.ModuleIdTyped;

    public Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new JsonObject { ["storesPersonalData"] = false });

    public Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionPreviewItem>>([]);

    public Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionReport(Module, 0, []));

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var n = await db.Set<UpdatesDeliveryEntity>().Where(d => d.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<UpdatesSubscriptionEntity>().Where(s => s.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<UpdatesGuildConfigEntity>().Where(c => c.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        return n;
    }
}

/// <summary>(Re-)enabling the module starts a new window: updates published while it was off are never posted in bulk.</summary>
public sealed class UpdatesLifecycle(ToroDbContext db, UpdatesPlanner planner, TimeProvider clock) : IModuleLifecycleHandler
{
    public ModuleId Module => UpdatesModule.ModuleIdTyped;

    public async Task OnEnabledAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var config = await db.Set<UpdatesGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
            return; // the first /tsq-admin modul:updates islem:configure starts the window
        UpdatesWindow.Restart(config, planner.EffectiveMode, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task OnDisabledAsync(GuildId guild, CancellationToken cancellationToken) =>
        // Nothing is deleted. Queued cards are cancelled at dispatch time by the module gate.
        Task.CompletedTask;
}

/// <summary>
/// Checked by the outbox dispatcher right before each send, edit and post-reconciliation resend (after the module gate,
/// BEFORE the dispatcher looks at IsDryRun). The effective mode applies to cards already queued: Off cancels every Updates
/// card and drops every queued edit; DryRun (also: Live on a host that does not send) does the same for live cards and lets
/// dry-run cards through to the simulation. Then
/// the guild's current state decides: no channel, paused, game no longer followed or channel changed (the card's channel
/// is no longer the game's: its own one, or the guild's Updates channel when it has none) all cancel. Cancelled
/// cards are terminal — they are not sent when the mode is Live again. A message that already reached Discord is never
/// taken back.
/// </summary>
public sealed class UpdatesDeliveryPolicy(ToroDbContext db, GameUpdateCatalog catalog, IOptions<UpdatesOptions> options, IOptions<DeliveryOptions> delivery, TimeProvider clock)
    : IDeliveryPolicy
{
    public ModuleId Module => UpdatesModule.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        if (!UpdatesPlanner.TryParseKind(kind, out var dryRun, out var gameKey))
            return new DeliveryDecision.Cancel("unknown_kind");
        // The effective mode: a host that does not send (Delivery:Mode) is a dry run for this module, also for queued cards.
        switch (UpdatesModes.Effective(options.Value, delivery.Value))
        {
            case UpdatesMode.Off:
                return new DeliveryDecision.Cancel("updates_mode_off");
            case UpdatesMode.DryRun when !dryRun:
                return new DeliveryDecision.Cancel("updates_mode_dry_run");
        }

        if (catalog.Find(gameKey) is null)
            return new DeliveryDecision.Cancel("game_unregistered");
        var config = await db.Set<UpdatesGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config?.ChannelId is null)
            return new DeliveryDecision.Cancel("not_configured");
        if (config.Paused)
            return new DeliveryDecision.Cancel("paused");
        var followed = await db.Set<UpdatesSubscriptionEntity>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.GuildId == guild.Value && s.GameKey == gameKey && s.Enabled, cancellationToken);
        if (followed is null)
            return new DeliveryDecision.Cancel("game_disabled");
        // The game's own channel when it has one, the guild's Updates channel otherwise — exactly what the planner uses.
        return (followed.ChannelId ?? config.ChannelId) == channel.Value ? DeliveryDecision.Allowed : new DeliveryDecision.Cancel("channel_changed");
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        // The problem belongs to whoever uses that channel now: the guild's Updates channel, a game's own channel, or both.
        var now = clock.GetUtcNow();
        var reported = false;
        var config = await db.Set<UpdatesGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is not null && config.ChannelId == channel.Value)
        {
            config.ChannelProblem = kind.ToString();
            config.ChannelProblemAt = now;
            reported = true;
        }

        foreach (var subscription in await db.Set<UpdatesSubscriptionEntity>().Where(s => s.GuildId == guild.Value && s.ChannelId == channel.Value).ToListAsync(cancellationToken))
        {
            subscription.ChannelProblem = kind.ToString();
            subscription.ChannelProblemAt = now;
            reported = true;
        }

        if (reported)
            await db.SaveChangesAsync(cancellationToken);
    }
}
