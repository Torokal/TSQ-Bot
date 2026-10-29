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
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Persistence;
using ToroSquad.Modules.Letterboxd.Providers;

namespace ToroSquad.Modules.Letterboxd.Application;

public enum LetterboxdCheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record LetterboxdCheck(string LabelKey, LetterboxdCheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>What /tsq-admin modul:letterboxd islem:status shows (no secrets, no Discord user data).</summary>
public sealed record LetterboxdStatus(
    LetterboxdMode Mode,
    LetterboxdMode EffectiveMode,
    bool ModuleEnabled,
    string Account,
    string? GuildAccount,
    ulong? ChannelId,
    bool Paused,
    string? ChannelProblem,
    LetterboxdSourceStateEntity? Feed,
    IReadOnlyList<Capability> Capabilities,
    bool Stale);

/// <summary>A card for the admin preview: the latest real entry worth a card, or an explicitly synthetic sample.</summary>
public sealed record LetterboxdPreview(OutgoingMessage Message, bool Synthetic);

/// <summary>
/// /tsq-admin modul:letterboxd islem:configure|pause|resume|status|preview|doctor. Every method authorizes the actor (Manage
/// Server) and only touches the row of <c>actor.GuildId</c>. Nothing here requests the feed or sends to a channel.
/// </summary>
public sealed class LetterboxdConfigService(
    ToroDbContext db,
    IGuildGateway guilds,
    IModuleGate gate,
    LetterboxdPlanner planner,
    LetterboxdCardRenderer renderer,
    IOptions<LetterboxdOptions> options,
    TimeProvider clock)
{
    public const GuildPermission RequiredChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks;

    /// <summary>The synthetic preview: an unrated, unreviewed rewatch (clearly marked as a sample; never delivered).</summary>
    public static EntryView Sample(string account, DateTimeOffset now) => new(
        LetterboxdUrl.Profile(account) + "film/sample/", "Sample film (synthetic preview)", 2000, null, true,
        DateOnly.FromDateTime(now.UtcDateTime), now, null, false, null, null);

    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "letterboxd.config.channel_invalid");

        var now = clock.GetUtcNow();
        var config = await ConfigAsync(actor.GuildId, create: true, ct);
        if (config!.ChannelId is null || config.Account != planner.Account)
        {
            // First configuration (or another account): only entries from now on — the history is never dumped.
            config.LiveSince = now;
            config.DryRunSince = now;
        }

        config.Account = planner.Account;
        config.ChannelId = channelId;
        config.ChannelProblem = null;
        config.ChannelProblemAt = null;
        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
        var missing = RequiredChannelPermissions & ~access.Permissions;
        return access.Permissions.Grants(RequiredChannelPermissions)
            ? OperationResult.Ok("letterboxd.config.saved", mention, DiscordText.UntrustedPlain(planner.Account, 40))
            : OperationResult.Ok("letterboxd.config.saved_missing_permissions", mention, missing.ToString());
    }

    public async Task<OperationResult> SetPausedAsync(ActorContext actor, bool paused, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        if (config?.ChannelId is null)
            return OperationResult.Fail(OperationError.InvalidInput, "letterboxd.config.no_channel");
        if (config.Paused == paused)
            return OperationResult.Ok(paused ? "letterboxd.pause.already" : "letterboxd.resume.already");
        var now = clock.GetUtcNow();
        config.Paused = paused;
        if (!paused)
        {
            // Entries of the paused period are skipped, not caught up.
            config.LiveSince = now;
            config.DryRunSince = now;
        }

        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(paused ? "letterboxd.pause.done" : "letterboxd.resume.done");
    }

    public async Task<(OperationResult Auth, LetterboxdStatus? Status)> StatusAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        var key = LetterboxdPlanner.StateKey(planner.Account);
        var feed = await db.Set<LetterboxdSourceStateEntity>().AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        var enabled = await gate.IsEnabledAsync(actor.GuildId, LetterboxdModule.ModuleIdTyped, ct);
        return (OperationResult.Ok("letterboxd.status.title"), new LetterboxdStatus(options.Value.Mode, planner.EffectiveMode, enabled, planner.Account,
            config?.Account is { Length: > 0 } a ? a : null, config?.ChannelId, config?.Paused == true, config?.ChannelProblem, feed, LetterboxdCapabilities.All,
            IsStale(feed, clock.GetUtcNow(), options.Value)));
    }

    public async Task<(OperationResult Auth, LetterboxdPreview? Preview)> PreviewAsync(ActorContext actor, string language, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);
        var account = planner.Account;
        var recent = await db.Set<LetterboxdEntryEntity>().AsNoTracking().Where(e => e.Account == account)
            .OrderByDescending(e => e.ViewingId).Take(50).ToListAsync(ct);
        var latest = recent.FirstOrDefault(e => e.View().Postable);
        return latest is null
            ? (OperationResult.Ok("letterboxd.preview.synthetic"), new LetterboxdPreview(renderer.Render(Sample(account, clock.GetUtcNow()), language), true))
            : (OperationResult.Ok("letterboxd.preview.real"), new LetterboxdPreview(renderer.Render(latest.View(), language), false));
    }

    /// <summary>Mode, module, account, channel and permissions, feed, baseline, coverage and source capabilities. Database only.</summary>
    public async Task<(OperationResult Auth, IReadOnlyList<LetterboxdCheck> Checks)> DoctorAsync(ActorContext actor, CancellationToken ct)
    {
        var (auth, status) = await StatusAsync(actor, ct);
        if (status is null)
            return (auth, []);
        var o = options.Value;
        var now = clock.GetUtcNow();
        var checks = new List<LetterboxdCheck>
        {
            status.Mode switch
            {
                LetterboxdMode.Off => new("letterboxd.doctor.mode", LetterboxdCheckState.Warning, "letterboxd.doctor.mode_off", []),
                LetterboxdMode.DryRun => new("letterboxd.doctor.mode", LetterboxdCheckState.Info, "letterboxd.doctor.mode_dry", []),
                _ => new("letterboxd.doctor.mode", status.EffectiveMode == LetterboxdMode.Live ? LetterboxdCheckState.Ok : LetterboxdCheckState.Info,
                    status.EffectiveMode == LetterboxdMode.Live ? "letterboxd.doctor.mode_live" : "letterboxd.doctor.mode_live_delivery_dry", []),
            },
            new("letterboxd.doctor.module", status.ModuleEnabled ? LetterboxdCheckState.Ok : LetterboxdCheckState.Warning,
                status.ModuleEnabled ? "letterboxd.doctor.module_on" : "letterboxd.doctor.module_off", []),
            status.GuildAccount is { } guildAccount && guildAccount != status.Account
                ? new("letterboxd.doctor.account", LetterboxdCheckState.Problem, "letterboxd.doctor.account_changed", [guildAccount, status.Account])
                : new("letterboxd.doctor.account", LetterboxdCheckState.Ok, "letterboxd.doctor.account_value", [status.Account, LetterboxdUrl.Profile(status.Account)]),
        };

        if (status.ChannelId is not { } channelId)
        {
            checks.Add(new("letterboxd.doctor.channel", LetterboxdCheckState.Problem, "letterboxd.doctor.channel_missing", []));
        }
        else
        {
            var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
            var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
            if (!access.Exists || !access.IsTextBased)
                checks.Add(new("letterboxd.doctor.channel", LetterboxdCheckState.Problem, "letterboxd.doctor.channel_gone", [mention]));
            else if (!access.Permissions.Grants(RequiredChannelPermissions))
                checks.Add(new("letterboxd.doctor.channel", LetterboxdCheckState.Problem, "letterboxd.doctor.channel_permissions",
                    [mention, (RequiredChannelPermissions & ~access.Permissions).ToString()]));
            else
            {
                checks.Add(new("letterboxd.doctor.channel", status.Paused ? LetterboxdCheckState.Warning : LetterboxdCheckState.Ok,
                    status.Paused ? "letterboxd.doctor.channel_paused" : "letterboxd.doctor.channel_ok", [mention]));
                if (!access.Permissions.Grants(GuildPermission.ReadMessageHistory))
                    checks.Add(new("letterboxd.doctor.history", LetterboxdCheckState.Info, "letterboxd.doctor.history_missing", []));
            }

            if (status.ChannelProblem is { } problem)
                checks.Add(new("letterboxd.doctor.channel", LetterboxdCheckState.Problem, "letterboxd.doctor.channel_problem", [problem]));
        }

        var feed = status.Feed;
        if (feed?.LastAttemptAt is not { } attempt)
            checks.Add(new("letterboxd.doctor.feed", LetterboxdCheckState.Info, "letterboxd.doctor.feed_never", []));
        else
        {
            var outcome = ((FeedOutcome)feed.LastOutcome).ToString();
            var state = feed.ConsecutiveFailures == 0 ? LetterboxdCheckState.Ok : feed.ConsecutiveFailures >= 3 ? LetterboxdCheckState.Problem : LetterboxdCheckState.Warning;
            checks.Add(new("letterboxd.doctor.feed", state, "letterboxd.doctor.feed_value",
                [feed.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—", DiscordText.Timestamp(attempt, 'R'), outcome,
                    feed.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", feed.ConsecutiveFailures,
                    feed.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—", DiscordText.UntrustedPlain(feed.LastDetail ?? "—", 120)]));
            checks.Add(new("letterboxd.doctor.items", LetterboxdCheckState.Info, "letterboxd.doctor.items_value",
                [feed.LastItemCount, feed.LastSkippedCount, feed.MaxItemCount, feed.OldestItemPublishedAt is { } oldest ? DiscordText.Timestamp(oldest, 'R') : "—",
                    feed.LastDeliveryStagedAt is { } d ? DiscordText.Timestamp(d, 'R') : "—"]));
            checks.Add(new("letterboxd.doctor.baseline", feed.BaselineAt is null ? LetterboxdCheckState.Warning : LetterboxdCheckState.Ok,
                feed.BaselineAt is null ? "letterboxd.doctor.baseline_none" : "letterboxd.doctor.baseline_value",
                feed.BaselineAt is { } b ? [DiscordText.Timestamp(b, 'f')] : []));
            checks.Add(new("letterboxd.doctor.mode_since", LetterboxdCheckState.Info, "letterboxd.doctor.mode_since_value",
                [feed.LastMode is { } lm ? ((LetterboxdMode)lm).ToString() : "—", feed.ModeSince is { } ms ? DiscordText.Timestamp(ms, 'f') : "—"]));
        }

        // Three different statements, never merged into one: the source is stale, a gap is possible, a capability is missing.
        if (IsStale(feed, now, o))
            checks.Add(new("letterboxd.doctor.stale", LetterboxdCheckState.Warning, "letterboxd.doctor.stale_value",
                [feed!.LastSuccessAt is { } ok ? DiscordText.Timestamp(ok, 'R') : "—", o.CatchUpHours]));
        if (feed?.CoverageGapAt is { } gap)
            checks.Add(new("letterboxd.doctor.coverage_gap", LetterboxdCheckState.Warning, "letterboxd.doctor.coverage_gap_value",
                [DiscordText.Timestamp(gap, 'f'), feed.MaxItemCount]));
        if (feed?.LastConflictCount is > 0)
            checks.Add(new("letterboxd.doctor.conflicts", LetterboxdCheckState.Warning, "letterboxd.doctor.conflicts_value", [feed.LastConflictCount]));

        foreach (var capability in status.Capabilities)
            checks.Add(new("letterboxd.doctor.capability", capability.State == CapabilityState.Supported ? LetterboxdCheckState.Ok : LetterboxdCheckState.Warning,
                capability.ReasonKey, [capability.Key, capability.State.ToString()]));
        checks.Add(new("letterboxd.doctor.coverage", LetterboxdCapabilities.FullCoverage ? LetterboxdCheckState.Ok : LetterboxdCheckState.Warning,
            LetterboxdCapabilities.FullCoverage ? "letterboxd.doctor.coverage_full" : "letterboxd.doctor.coverage_partial", []));
        checks.Add(new("letterboxd.doctor.terms", LetterboxdCheckState.Info, "letterboxd.doctor.terms_value", []));
        return (auth, checks);
    }

    /// <summary>No successful read for longer than the catch-up window (or a request was made but never succeeded).</summary>
    public static bool IsStale(LetterboxdSourceStateEntity? feed, DateTimeOffset now, LetterboxdOptions o) =>
        feed?.LastAttemptAt is not null && (feed.LastSuccessAt is not { } ok || now - ok > TimeSpan.FromHours(o.CatchUpHours));

    private async Task<LetterboxdGuildConfigEntity?> ConfigAsync(GuildId guild, bool create, CancellationToken ct)
    {
        var set = db.Set<LetterboxdGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config is null && create)
        {
            config = new LetterboxdGuildConfigEntity { GuildId = guild.Value, UpdatedAt = clock.GetUtcNow() };
            set.Add(config);
        }

        return config;
    }
}

/// <summary>
/// Checked by the outbox dispatcher right before each send, edit and post-reconciliation resend (after the module gate,
/// BEFORE the dispatcher looks at IsDryRun). The effective mode applies to cards already queued: Off cancels every card
/// and drops every queued edit; DryRun does the same for live cards and lets dry-run cards through to the simulation. Then
/// the guild's current state decides: no channel, paused, another followed account or a changed channel all cancel.
/// Cancelled cards are terminal — they are not sent when the mode is Live again. A message that already reached Discord
/// is never taken back.
/// </summary>
public sealed class LetterboxdDeliveryPolicy(ToroDbContext db, IOptions<LetterboxdOptions> options, IOptions<DeliveryOptions> delivery, TimeProvider clock)
    : IDeliveryPolicy
{
    public ModuleId Module => LetterboxdModule.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        if (kind is not (LetterboxdPlanner.EntryKind or LetterboxdPlanner.DryRunEntryKind))
            return new DeliveryDecision.Cancel("unknown_kind");
        switch (LetterboxdModes.Effective(options.Value, delivery.Value))
        {
            case LetterboxdMode.Off:
                return new DeliveryDecision.Cancel("letterboxd_mode_off");
            case LetterboxdMode.DryRun when kind != LetterboxdPlanner.DryRunEntryKind:
                return new DeliveryDecision.Cancel("letterboxd_mode_dry_run");
        }

        var config = await db.Set<LetterboxdGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config?.ChannelId is null)
            return new DeliveryDecision.Cancel("not_configured");
        if (config.Paused)
            return new DeliveryDecision.Cancel("paused");
        if (config.Account != options.Value.Account)
            return new DeliveryDecision.Cancel("account_changed");
        return config.ChannelId == channel.Value ? DeliveryDecision.Allowed : new DeliveryDecision.Cancel("channel_changed");
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<LetterboxdGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null || config.ChannelId != channel.Value)
            return;
        config.ChannelProblem = kind.ToString();
        config.ChannelProblemAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>(Re-)enabling the module starts a new window: entries made while it was off are never posted in bulk.</summary>
public sealed class LetterboxdLifecycle(ToroDbContext db, TimeProvider clock) : IModuleLifecycleHandler
{
    public ModuleId Module => LetterboxdModule.ModuleIdTyped;

    public async Task OnEnabledAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var config = await db.Set<LetterboxdGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
            return; // the first /tsq-admin modul:letterboxd islem:configure starts the window
        var now = clock.GetUtcNow();
        config.LiveSince = now;
        config.DryRunSince = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task OnDisabledAsync(GuildId guild, CancellationToken cancellationToken) =>
        // Nothing is deleted. Queued cards are cancelled at dispatch time by the module gate.
        Task.CompletedTask;
}

/// <summary>
/// The module stores no Discord user data: it follows one public Letterboxd account named in configuration. The guild rows
/// (channel, window, delivery records) are purged with the rest of a guild's data once the bot has left and retention
/// elapsed. The entries of the followed account are not guild data and stay (bounded by their own retention).
/// </summary>
public sealed class LetterboxdUserData(ToroDbContext db) : IUserDataContributor
{
    public ModuleId Module => LetterboxdModule.ModuleIdTyped;

    public Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new JsonObject { ["storesPersonalData"] = false });

    public Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionPreviewItem>>([]);

    public Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionReport(Module, 0, []));

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var n = await db.Set<LetterboxdDeliveryEntity>().Where(d => d.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<LetterboxdGuildConfigEntity>().Where(c => c.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        return n;
    }
}
