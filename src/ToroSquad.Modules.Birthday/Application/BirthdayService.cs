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
/// never from an option. The only exceptions are <see cref="SetForMemberAsync"/> (/tsq-admin birthday set) and
/// <see cref="GetForMemberAsync"/> (/tsq-admin birthday show): they change or read another member's day + month and require
/// Discord's Administrator permission (or guild ownership), not Manage Server. Both set paths write the same unique
/// (guild, user) registration. Self-service logs never contain the typed text or the date; the admin set logs day + month
/// as an audit trail, the admin lookup logs who looked at whose record without the date (never a year — there is none).
/// </summary>
public sealed class BirthdayService(ToroDbContext db, TimeProvider clock, ILogger<BirthdayService> logger)
{
    private const int SqliteConstraint = 19;

    /// <summary>
    /// Reading or changing someone else's birthday: Administrator only (<see cref="ActorContext.Has"/> also admits the guild
    /// owner). Used by /tsq-admin birthday set and /tsq-admin birthday show.
    /// </summary>
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
    /// /tsq-admin birthday set: creates or updates <paramref name="member"/>'s birthday. Authorization is checked first, from the
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

    /// <summary>
    /// /tsq-admin birthday show: another member's saved day + month. Same authorization as <see cref="SetForMemberAsync"/>
    /// (Administrator or guild owner), checked before the database is read, so a refused caller learns nothing — not even
    /// whether a registration exists. Only rows of <c>actor.GuildId</c> are read. Every lookup is audited (who looked at
    /// whose record, and whether one existed); the day and month are not logged.
    /// </summary>
    public async Task<(OperationResult Result, BirthdayDate? Date)> GetForMemberAsync(ActorContext actor, UserId member, bool memberIsEligible, CancellationToken ct)
    {
        if (!Authorize.Require(actor, actor.GuildId, SetForMemberPermission).IsAllowed)
            return (OperationResult.Fail(OperationError.Forbidden, "birthday.admin_set.forbidden"), null);
        if (!memberIsEligible)
            return (OperationResult.Fail(OperationError.InvalidInput, "birthday.admin_show.not_member"), null);

        var row = await Registrations.AsNoTracking().FirstOrDefaultAsync(r => r.GuildId == actor.GuildId.Value && r.UserId == member.Value, ct);
        var date = row is null ? null : BirthdayDate.Create(row.Day, row.Month);
        logger.LogInformation("birthday_admin_viewed guild={Guild} admin={Admin} target={TargetUser} found={Found}", actor.GuildId, actor.UserId, member, date is not null);
        return date is null ? (OperationResult.Ok("birthday.admin_show.none"), null) : (OperationResult.Ok("birthday.admin_show.value"), date);
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
