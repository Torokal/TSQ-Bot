using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Volleyball.Persistence;

namespace ToroSquad.Modules.Volleyball.Application;

/// <summary>Optional changes to the notification switches (null = keep).</summary>
public sealed record VbNotificationChanges(
    bool? Reminder = null,
    bool? Started = null,
    bool? Sets = null,
    bool? Final = null,
    bool? PostponedCancelled = null);

/// <summary>
/// /volleyball-admin configure|pause|resume. Every method authorizes the actor (Manage Server) and only touches the row of
/// <c>actor.GuildId</c>. Anything that could re-announce history (new channel, a notification type switched on, resume)
/// moves the guild's watermark to "now".
/// </summary>
public sealed class VolleyballConfigService(ToroDbContext db, IGuildGateway guilds, TimeProvider clock)
{
    public async Task<VolleyballGuildConfigEntity?> GetAsync(GuildId guild, CancellationToken ct) =>
        await db.Set<VolleyballGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);

    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        // The channel must exist IN THIS GUILD as the bot sees it; there is never a fallback to another channel.
        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "vb.config.channel_invalid");

        var config = await GetOrCreateAsync(actor, ct);
        if (config.ChannelId != channelId)
            config.WatermarkUtc = clock.GetUtcNow(); // outbox keys include the channel: never re-post history to a new channel
        config.ChannelId = channelId;
        config.ChannelProblem = null;
        config.ChannelProblemAt = null;
        Touch(config, actor);
        await db.SaveChangesAsync(ct);
        return access.MissingRequired == GuildPermission.None
            ? OperationResult.Ok("vb.config.saved")
            : OperationResult.Ok("vb.config.saved_missing_permissions", access.MissingRequired.ToString());
    }

    public async Task<OperationResult> SetNotificationsAsync(ActorContext actor, VbNotificationChanges changes, CancellationToken ct)
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

        c.NotifyReminder = Apply(c.NotifyReminder, changes.Reminder);
        c.NotifyStarted = Apply(c.NotifyStarted, changes.Started);
        c.NotifySets = Apply(c.NotifySets, changes.Sets);
        c.NotifyFinal = Apply(c.NotifyFinal, changes.Final);
        c.NotifyPostponedCancelled = Apply(c.NotifyPostponedCancelled, changes.PostponedCancelled);
        if (turnedOn)
            c.WatermarkUtc = clock.GetUtcNow(); // re-enabling a type never announces what happened while it was off
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("vb.config.saved");
    }

    /// <summary>Sets (or clears, with <paramref name="roleId"/> null) the optional notification role.</summary>
    public async Task<OperationResult> SetRoleAsync(ActorContext actor, ulong? roleId, bool? pingOnReminder, bool? pingOnFinal, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        string? warning = null;
        if (roleId is { } id)
        {
            // Never @everyone (its id equals the guild id), and the role must exist in THIS guild.
            if (id == actor.GuildId.Value)
                return OperationResult.Fail(OperationError.Unsafe, "vb.config.role_everyone");
            var snapshot = await guilds.GetRoleSnapshotAsync(actor.GuildId, ct);
            if (snapshot is null)
                return OperationResult.Fail(OperationError.ProviderUnavailable, "vb.config.role_unverifiable");
            var (valid, pingWorks) = SelfServiceRolePolicy.EvaluateMentionTarget(snapshot, new RoleId(id));
            if (!valid)
                return OperationResult.Fail(OperationError.InvalidInput, "vb.config.role_invalid");
            if (!pingWorks)
                warning = "vb.config.role_saved_not_mentionable";
        }

        var c = await GetOrCreateAsync(actor, ct);
        c.PingRoleId = roleId;
        c.PingOnReminder = pingOnReminder ?? c.PingOnReminder;
        c.PingOnFinal = pingOnFinal ?? c.PingOnFinal;
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(roleId is null ? "vb.config.role_cleared" : warning ?? "vb.config.role_saved");
    }

    public async Task<OperationResult> PauseAsync(ActorContext actor, bool pause, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var c = await GetOrCreateAsync(actor, ct);
        if (c.Paused == pause)
            return OperationResult.Ok(pause ? "vb.pause.already" : "vb.resume.already");
        c.Paused = pause;
        if (!pause)
            c.WatermarkUtc = clock.GetUtcNow(); // nothing that happened while paused is sent
        Touch(c, actor);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(pause ? "vb.pause.done" : "vb.resume.done");
    }

    private async Task<VolleyballGuildConfigEntity> GetOrCreateAsync(ActorContext actor, CancellationToken ct)
    {
        var set = db.Set<VolleyballGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config is null)
        {
            config = new VolleyballGuildConfigEntity { GuildId = actor.GuildId.Value, WatermarkUtc = clock.GetUtcNow() };
            set.Add(config);
        }

        return config;
    }

    private void Touch(VolleyballGuildConfigEntity config, ActorContext actor)
    {
        config.UpdatedAt = clock.GetUtcNow();
        config.UpdatedBy = actor.UserId.Value;
    }
}

/// <summary>Enable/disable hooks: (re-)enabling moves the watermark so old matches are never delivered in bulk.</summary>
public sealed class VolleyballLifecycle(ToroDbContext db, TimeProvider clock) : IModuleLifecycleHandler
{
    public ModuleId Module => VolleyballModule.ModuleIdTyped;

    public async Task OnEnabledAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var set = db.Set<VolleyballGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
        {
            config = new VolleyballGuildConfigEntity { GuildId = guild.Value };
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
public sealed class VolleyballDeliveryPolicy(ToroDbContext db, TimeProvider clock) : IDeliveryPolicy
{
    public ModuleId Module => VolleyballModule.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<VolleyballGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null)
            return new DeliveryDecision.Cancel("not_configured");
        if (config.Paused)
            return new DeliveryDecision.Cancel("paused");
        if (config.ChannelId != channel.Value)
            return new DeliveryDecision.Cancel("channel_changed");
        if (config.ChannelProblem is not null)
            return new DeliveryDecision.Cancel("channel_problem");
        return VolleyballNotificationPlanner.Enabled(config, kind) ? DeliveryDecision.Allowed : new DeliveryDecision.Cancel("notification_type_disabled");
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<VolleyballGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null || config.ChannelId != channel.Value)
            return;
        // Stop sending to this channel until an admin fixes it (no retry storm, no silent fallback, other guilds unaffected).
        config.ChannelProblem = kind.ToString();
        config.ChannelProblemAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
