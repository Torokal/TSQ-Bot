using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Domain;
using ToroSquad.Modules.Birthday.Persistence;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>
/// /birthday set|show|remove work on the CALLER's own registration in the CALLER's guild — both come from the interaction,
/// never from an option. The one exception is <see cref="SetForMemberAsync"/> (/birthday-admin set): it changes another
/// member's day + month and requires Discord's Administrator permission (or guild ownership), not Manage Server. Both paths
/// write the same unique (guild, user) registration. Self-service logs never contain the typed text or the date; the admin
/// path logs day + month as an audit trail (never a year — there is none).
/// </summary>
public sealed class BirthdayService(ToroDbContext db, TimeProvider clock, ILogger<BirthdayService> logger)
{
    private const int SqliteConstraint = 19;

    /// <summary>Changing someone else's personal data: Administrator only (<see cref="ActorContext.Has"/> also admits the guild owner).</summary>
    public const GuildPermission SetForMemberPermission = GuildPermission.Administrator;

    private DbSet<BirthdayRegistrationEntity> Registrations => db.Set<BirthdayRegistrationEntity>();

    private enum Change
    {
        Created,
        Updated,
        Unchanged,
    }

    /// <summary>Saves (or updates) the caller's birthday. Result args: the day and the month number (the command names the month).</summary>
    public async Task<OperationResult> SetAsync(ActorContext actor, string? input, CancellationToken ct)
    {
        if (!BirthdayDate.TryParse(input, out var date))
            return OperationResult.Fail(OperationError.InvalidInput, "birthday.set.invalid");

        var change = await UpsertAsync(actor.GuildId, actor.UserId, date, ct);
        if (change == Change.Created)
            logger.LogInformation("birthday_registered guild={Guild} user={User} source=self", actor.GuildId, actor.UserId);
        else if (change == Change.Updated)
            logger.LogInformation("birthday_updated guild={Guild} user={User} source=self", actor.GuildId, actor.UserId);
        return OperationResult.Ok(change switch
        {
            Change.Created => "birthday.set.saved",
            Change.Updated => "birthday.set.updated",
            _ => "birthday.set.unchanged",
        }, date.Day, date.Month);
    }

    /// <summary>
    /// /birthday-admin set: creates or updates <paramref name="member"/>'s birthday. Authorization is checked first, from the
    /// caller's effective permissions in the interaction (never a role name); <paramref name="memberIsEligible"/> is whether the
    /// target is a human member of this guild. Result args: the day and the month number.
    /// </summary>
    public async Task<OperationResult> SetForMemberAsync(ActorContext actor, UserId member, bool memberIsEligible, string? input, CancellationToken ct)
    {
        if (!Authorize.Require(actor, actor.GuildId, SetForMemberPermission).IsAllowed)
            return OperationResult.Fail(OperationError.Forbidden, "birthday.admin_set.forbidden");
        if (!memberIsEligible)
            return OperationResult.Fail(OperationError.InvalidInput, "birthday.admin_set.not_member");
        if (!BirthdayDate.TryParse(input, out var date))
            return OperationResult.Fail(OperationError.InvalidInput, "birthday.set.invalid");

        var change = await UpsertAsync(actor.GuildId, member, date, ct);
        if (change == Change.Created)
            logger.LogInformation("birthday_registered guild={Guild} user={User} source=admin admin={Admin} day={Day} month={Month}",
                actor.GuildId, member, actor.UserId, date.Day, date.Month);
        else if (change == Change.Updated)
            logger.LogInformation("birthday_updated guild={Guild} user={User} source=admin admin={Admin} day={Day} month={Month}",
                actor.GuildId, member, actor.UserId, date.Day, date.Month);
        return OperationResult.Ok(change switch
        {
            Change.Created => "birthday.admin_set.saved",
            Change.Updated => "birthday.admin_set.updated",
            _ => "birthday.admin_set.unchanged",
        }, date.Day, date.Month);
    }

    private async Task<Change> UpsertAsync(GuildId guild, UserId user, BirthdayDate date, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var existing = await Registrations.FirstOrDefaultAsync(r => r.GuildId == guild.Value && r.UserId == user.Value, ct);
        if (existing is not null)
        {
            if (existing.Day == date.Day && existing.Month == date.Month)
                return Change.Unchanged;
            existing.Day = date.Day;
            existing.Month = date.Month;
            existing.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return Change.Updated;
        }

        Registrations.Add(new BirthdayRegistrationEntity
        {
            GuildId = guild.Value,
            UserId = user.Value,
            Day = date.Day,
            Month = date.Month,
            CreatedAt = now,
            UpdatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: SqliteConstraint })
        {
            // Another request saved this member's registration first (unique guild + user): apply ours to that row instead.
            db.ChangeTracker.Clear();
            return await UpsertAsync(guild, user, date, ct);
        }

        return Change.Created;
    }

    public async Task<BirthdayDate?> GetAsync(ActorContext actor, CancellationToken ct)
    {
        var row = await Registrations.AsNoTracking().FirstOrDefaultAsync(r => r.GuildId == actor.GuildId.Value && r.UserId == actor.UserId.Value, ct);
        return row is null ? null : BirthdayDate.Create(row.Day, row.Month);
    }

    /// <summary>
    /// Deletes the caller's registration. A role the bot gave for today is taken back by the next reconciliation (the
    /// celebration no longer has a registration behind it).
    /// </summary>
    public async Task<OperationResult> RemoveAsync(ActorContext actor, CancellationToken ct)
    {
        var removed = await Registrations.Where(r => r.GuildId == actor.GuildId.Value && r.UserId == actor.UserId.Value).ExecuteDeleteAsync(ct);
        if (removed == 0)
            return OperationResult.Ok("birthday.remove.none");
        logger.LogInformation("birthday_removed guild={Guild} user={User}", actor.GuildId, actor.UserId);
        return OperationResult.Ok("birthday.remove.done");
    }

    public Task<int> CountAsync(GuildId guild, CancellationToken ct) => Registrations.CountAsync(r => r.GuildId == guild.Value, ct);
}
