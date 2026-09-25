using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Persistence;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>Optional changes to the notification switches (null = keep).</summary>
public sealed record F1NotificationChanges(
    bool? PracticeStart = null,
    bool? PracticeResults = null,
    bool? SprintStart = null,
    bool? SprintResults = null,
    bool? RaceStart = null,
    bool? RaceResults = null,
    bool? Standings = null,
    bool? QualifyingStart = null,
    bool? QualifyingResults = null,
    bool? SprintQualifyingStart = null,
    bool? SprintQualifyingResults = null);

/// <summary>
/// /f1-admin configure|pause|resume. Every method authorizes the actor (Manage Server) and only touches rows of
/// <c>actor.GuildId</c> — ids supplied by the caller never select the guild. Anything that could re-announce history
/// (new channel, a notification type switched on, resume) moves the guild's watermark to "now".
/// </summary>
public sealed class Formula1ConfigService(ToroDbContext db, IGuildGateway guilds, TimeProvider clock)
{
    public async Task<Formula1GuildConfigEntity?> GetAsync(GuildId guild, CancellationToken ct) =>
        await db.Set<Formula1GuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);

    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        // The channel must exist IN THIS GUILD as the bot sees it; there is never a fallback to another channel.
        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "f1.config.channel_invalid");

        var config = await GetOrCreateAsync(actor, ct);
        if (config.ChannelId != channelId)
            config.WatermarkUtc = clock.GetUtcNow(); // outbox keys include the channel: never re-post history to a new channel
        config.ChannelId = channelId;
        config.ChannelProblem = null;
        config.ChannelProblemAt = null;
        Touch(config, actor);
        await db.SaveChangesAsync(ct);
        return access.MissingRequired == GuildPermission.None
            ? OperationResult.Ok("f1.config.saved")
            : OperationResult.Ok("f1.config.saved_missing_permissions", access.MissingRequired.ToString());
    }

    public async Task<OperationResult> SetNotificationsAsync(ActorContext actor, F1NotificationChanges changes, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var c = await GetOrCreateAsync(actor, ct);
        var turnedOn = false;
        bool Apply(bool current, bool? requested)
        {
            if (requested == true && !current)
                turnedOn = true;
            return requested ?? current;
        }

        c.NotifyPracticeStart = Apply(c.NotifyPracticeStart, changes.PracticeStart);
        c.NotifyPracticeResults = Apply(c.NotifyPracticeResults, changes.PracticeResults);
        c.NotifySprintStart = Apply(c.NotifySprintStart, changes.SprintStart);
        c.NotifySprintResults = Apply(c.NotifySprintResults, changes.SprintResults);
        c.NotifyRaceStart = Apply(c.NotifyRaceStart, changes.RaceStart);
        c.NotifyRaceResults = Apply(c.NotifyRaceResults, changes.RaceResults);
        c.NotifyStandings = Apply(c.NotifyStandings, changes.Standings);
        c.NotifyQualifyingStart = Apply(c.NotifyQualifyingStart, changes.QualifyingStart);
        c.NotifyQualifyingResults = Apply(c.NotifyQualifyingResults, changes.QualifyingResults);
        c.NotifySprintQualifyingStart = Apply(c.NotifySprintQualifyingStart, changes.SprintQualifyingStart);
        c.NotifySprintQualifyingResults = Apply(c.NotifySprintQualifyingResults, changes.SprintQualifyingResults);
        if (turnedOn)
            c.WatermarkUtc = clock.GetUtcNow(); // re-enabling a type never announces what happened while it was off
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("f1.config.saved");
    }

    /// <summary>Sets (or clears, with <paramref name="roleId"/> null) the optional notification role.</summary>
    public async Task<OperationResult> SetRoleAsync(ActorContext actor, ulong? roleId, bool? pingOnStarts, bool? pingOnResults, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        string? warning = null;
        if (roleId is { } id)
        {
            // Never @everyone (its id equals the guild id), and the role must exist in THIS guild.
            if (id == actor.GuildId.Value)
                return OperationResult.Fail(OperationError.Unsafe, "f1.config.role_everyone");
            var snapshot = await guilds.GetRoleSnapshotAsync(actor.GuildId, ct);
            if (snapshot is null)
                return OperationResult.Fail(OperationError.ProviderUnavailable, "f1.config.role_unverifiable");
            var (valid, pingWorks) = SelfServiceRolePolicy.EvaluateMentionTarget(snapshot, new RoleId(id));
            if (!valid)
                return OperationResult.Fail(OperationError.InvalidInput, "f1.config.role_invalid");
            if (!pingWorks)
                warning = "f1.config.role_saved_not_mentionable";
        }

        var c = await GetOrCreateAsync(actor, ct);
        c.PingRoleId = roleId;
        c.PingOnStarts = pingOnStarts ?? c.PingOnStarts;
        c.PingOnResults = pingOnResults ?? c.PingOnResults;
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(roleId is null ? "f1.config.role_cleared" : warning ?? "f1.config.role_saved");
    }

    public async Task<OperationResult> SetSpoilersAsync(ActorContext actor, bool enabled, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var c = await GetOrCreateAsync(actor, ct);
        c.SpoilerMode = enabled;
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(enabled ? "f1.config.spoilers_on" : "f1.config.spoilers_off");
    }

    public async Task<OperationResult> PauseAsync(ActorContext actor, bool pause, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var c = await GetOrCreateAsync(actor, ct);
        if (c.Paused == pause)
            return OperationResult.Ok(pause ? "f1.pause.already" : "f1.resume.already");
        c.Paused = pause;
        if (!pause)
            c.WatermarkUtc = clock.GetUtcNow(); // nothing that happened while paused is sent
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(pause ? "f1.pause.done" : "f1.resume.done");
    }

    private async Task<Formula1GuildConfigEntity> GetOrCreateAsync(ActorContext actor, CancellationToken ct)
    {
        var set = db.Set<Formula1GuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config is null)
        {
            config = new Formula1GuildConfigEntity { GuildId = actor.GuildId.Value, WatermarkUtc = clock.GetUtcNow() };
            set.Add(config);
        }

        return config;
    }

    private void Touch(Formula1GuildConfigEntity config, ActorContext actor)
    {
        config.UpdatedAt = clock.GetUtcNow();
        config.UpdatedBy = actor.UserId.Value;
    }
}

/// <summary>Enable/disable hooks: (re-)enabling moves the watermark so old sessions are never delivered in bulk.</summary>
public sealed class Formula1Lifecycle(ToroDbContext db, TimeProvider clock) : IModuleLifecycleHandler
{
    public ModuleId Module => Formula1Module.ModuleIdTyped;

    public async Task OnEnabledAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var set = db.Set<Formula1GuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
        {
            config = new Formula1GuildConfigEntity { GuildId = guild.Value };
            set.Add(config);
        }

        config.WatermarkUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task OnDisabledAsync(GuildId guild, CancellationToken cancellationToken) =>
        // Nothing is deleted. Pending outbox rows are cancelled at dispatch time by the module gate.
        Task.CompletedTask;
}

/// <summary>Checked by the outbox dispatcher immediately before each send/edit.</summary>
public sealed class Formula1DeliveryPolicy(ToroDbContext db, TimeProvider clock) : IDeliveryPolicy
{
    public ModuleId Module => Formula1Module.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<Formula1GuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
            return new DeliveryDecision.Cancel("not_configured");
        if (config.Paused)
            return new DeliveryDecision.Cancel("paused");
        if (config.ChannelId != channel.Value)
            return new DeliveryDecision.Cancel("channel_changed");
        if (config.ChannelProblem is not null)
            return new DeliveryDecision.Cancel("channel_problem");

        var start = kind.StartsWith(Formula1NotificationPlanner.StartedPrefix, StringComparison.Ordinal);
        var result = kind.StartsWith(Formula1NotificationPlanner.ResultPrefix, StringComparison.Ordinal);
        if (!start && !result)
            return new DeliveryDecision.Cancel("unknown_kind");
        var slug = kind[(kind.IndexOf(':', StringComparison.Ordinal) + 1)..];
        if (!F1SessionTypes.TryParseSlug(slug, out var type) || !Formula1NotificationPlanner.Enabled(config, type, start))
            return new DeliveryDecision.Cancel("notification_type_disabled");
        return DeliveryDecision.Allowed;
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<Formula1GuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null || config.ChannelId != channel.Value)
            return;
        // Stop sending to this channel until an admin fixes it (no retry storm, no silent fallback, other guilds unaffected).
        config.ChannelProblem = kind.ToString();
        config.ChannelProblemAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
