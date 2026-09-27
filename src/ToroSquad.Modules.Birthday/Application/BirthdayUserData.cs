using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Birthday.Persistence;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>
/// /privacy for TSQ Birthday: the member's day + month and their celebrations in one guild. Deletion removes both and takes
/// back a birthday role the bot gave and has not removed yet (a role the member already had is kept). Announcements that
/// named the member are ordinary channel messages; their outbox rows are pruned two days after delivery.
/// </summary>
public sealed class BirthdayUserData(ToroDbContext db, IGuildGateway guilds, IOptions<BirthdayOptions> options) : IUserDataContributor
{
    public ModuleId Module => BirthdayModule.ModuleIdTyped;

    public async Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var registration = await db.Set<BirthdayRegistrationEntity>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.GuildId == guild.Value && r.UserId == user.Value, cancellationToken);
        var celebrations = new JsonArray();
        foreach (var c in await Celebrations(guild, user).AsNoTracking().OrderBy(c => c.Id).ToListAsync(cancellationToken))
        {
            celebrations.Add(new JsonObject
            {
                ["year"] = c.Year,
                ["announced"] = c.Announced,
                ["role"] = c.RoleState.ToString(),
            });
        }

        return new JsonObject
        {
            ["birthday"] = registration is null ? null : new JsonObject { ["day"] = registration.Day, ["month"] = registration.Month },
            ["registeredAtUtc"] = registration?.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["celebrations"] = celebrations,
        };
    }

    public async Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var items = new List<DeletionPreviewItem>();
        if (await db.Set<BirthdayRegistrationEntity>().AnyAsync(r => r.GuildId == guild.Value && r.UserId == user.Value, cancellationToken))
            items.Add(new DeletionPreviewItem("birthday.privacy.registration", 1));
        var active = await Celebrations(guild, user).CountAsync(c => c.RoleState == BirthdayRoleState.Active, cancellationToken);
        if (active > 0)
            items.Add(new DeletionPreviewItem("birthday.privacy.role", active));
        return items;
    }

    public async Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (options.Value.Role is { } role && await Celebrations(guild, user).AnyAsync(c => c.RoleState == BirthdayRoleState.Active, cancellationToken))
        {
            var outcome = await guilds.RemoveRoleAsync(guild, user, role, "TSQ Birthday: member deleted their data", cancellationToken);
            if (outcome is not (RoleOperationOutcome.Success or RoleOperationOutcome.UnknownMember or RoleOperationOutcome.UnknownRole))
                warnings.Add("birthday.privacy.role_not_removed");
        }

        var deleted = await db.Set<BirthdayRegistrationEntity>().Where(r => r.GuildId == guild.Value && r.UserId == user.Value).ExecuteDeleteAsync(cancellationToken);
        deleted += await Celebrations(guild, user).ExecuteDeleteAsync(cancellationToken);
        return new DeletionReport(Module, deleted, warnings);
    }

    public async Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var n = await db.Set<BirthdayRegistrationEntity>().Where(x => x.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<BirthdayCelebrationEntity>().Where(x => x.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<BirthdayAnnouncementEntity>().Where(x => x.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        n += await db.Set<BirthdayGuildConfigEntity>().Where(x => x.GuildId == guild.Value).ExecuteDeleteAsync(cancellationToken);
        return n;
    }

    private IQueryable<BirthdayCelebrationEntity> Celebrations(GuildId guild, UserId user) =>
        db.Set<BirthdayCelebrationEntity>().Where(c => c.GuildId == guild.Value && c.UserId == user.Value);
}
