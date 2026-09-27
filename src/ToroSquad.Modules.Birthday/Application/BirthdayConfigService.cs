using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Domain;
using ToroSquad.Modules.Birthday.Persistence;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>What /birthday-admin status shows for one guild (counts only — never who has a birthday when).</summary>
public sealed record BirthdayGuildStatus(
    bool Enabled,
    ulong? ChannelId,
    string? ChannelProblem,
    ulong RoleId,
    string TimeZone,
    DateOnly Today,
    int Registered,
    int CelebratingToday,
    BirthdayAnnouncementState? TodaysAnnouncement,
    int ActiveRoles,
    int RoleProblems);

/// <summary>
/// /birthday-admin configure|status. Every method authorizes the actor (Manage Server) and only touches the row of
/// <c>actor.GuildId</c>.
/// </summary>
public sealed class BirthdayConfigService(ToroDbContext db, IGuildGateway guilds, IModuleGate gate, IOptions<BirthdayOptions> options, TimeProvider clock)
{
    /// <summary>The bot writes a plain text message: it only needs to see the channel and send there.</summary>
    public const GuildPermission RequiredChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages;

    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        // The channel must exist IN THIS GUILD as the bot sees it; there is never a fallback to another channel.
        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "birthday.config.channel_invalid");

        var set = db.Set<BirthdayGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct);
        if (config is null)
        {
            config = new BirthdayGuildConfigEntity { GuildId = actor.GuildId.Value };
            set.Add(config);
        }

        config.ChannelId = channelId;
        config.ChannelProblem = null;
        config.ChannelProblemAt = null;
        config.UpdatedAt = clock.GetUtcNow();
        config.UpdatedBy = actor.UserId.Value;
        await db.SaveChangesAsync(ct);

        var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
        var missing = RequiredChannelPermissions & ~access.Permissions;
        return access.Permissions.Grants(RequiredChannelPermissions)
            ? OperationResult.Ok("birthday.config.saved", mention)
            : OperationResult.Ok("birthday.config.saved_missing_permissions", mention, missing.ToString());
    }

    public async Task<(OperationResult Auth, BirthdayGuildStatus? Status)> StatusAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);

        var o = options.Value;
        var guild = actor.GuildId.Value;
        var zone = GuildTime.TryResolve(o.TimeZone, out var z) ? z : TimeZoneInfo.Utc;
        var today = BirthdayCalendar.LocalDate(clock.GetUtcNow(), zone);
        var config = await db.Set<BirthdayGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild, ct);
        var registered = await db.Set<BirthdayRegistrationEntity>().CountAsync(r => r.GuildId == guild, ct);
        var celebrations = db.Set<BirthdayCelebrationEntity>().AsNoTracking().Where(c => c.GuildId == guild);
        var celebratingToday = await celebrations.CountAsync(c => c.LocalDate == today, ct);
        var activeRoles = await celebrations.CountAsync(c => c.RoleState == BirthdayRoleState.Active, ct);
        var roleProblems = await celebrations.CountAsync(c => c.RoleState == BirthdayRoleState.Failed ||
                                                              ((c.RoleState == BirthdayRoleState.Pending || c.RoleState == BirthdayRoleState.Active) && c.RoleError != null), ct);
        var announcement = await db.Set<BirthdayAnnouncementEntity>().AsNoTracking()
            .Where(a => a.GuildId == guild && a.LocalDate == today).Select(a => (BirthdayAnnouncementState?)a.State).FirstOrDefaultAsync(ct);
        var enabled = await gate.IsEnabledAsync(actor.GuildId, BirthdayModule.ModuleIdTyped, ct);
        return (OperationResult.Ok("birthday.status.title"),
            new BirthdayGuildStatus(enabled, config?.ChannelId, config?.ChannelProblem, o.RoleId, o.TimeZone, today, registered, celebratingToday, announcement,
                activeRoles, roleProblems));
    }
}

/// <summary>Checked by the outbox dispatcher immediately before the announcement is sent.</summary>
public sealed class BirthdayDeliveryPolicy(ToroDbContext db, TimeProvider clock) : IDeliveryPolicy
{
    public ModuleId Module => BirthdayModule.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<BirthdayGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config?.ChannelId is null)
            return new DeliveryDecision.Cancel("not_configured");
        return config.ChannelId == channel.Value ? DeliveryDecision.Allowed : new DeliveryDecision.Cancel("channel_changed");
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<BirthdayGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null || config.ChannelId != channel.Value)
            return;
        // No more announcements to this channel until an admin configures it again (no retry storm, no silent fallback).
        config.ChannelProblem = kind.ToString();
        config.ChannelProblemAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
