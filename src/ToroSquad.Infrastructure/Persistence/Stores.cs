using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Privacy;

namespace ToroSquad.Infrastructure.Persistence;

public sealed class GuildSettingsStore(ToroDbContext db, TimeProvider clock) : IGuildSettingsStore
{
    public async Task<GuildSettings> GetAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var row = await db.GuildSettings.AsNoTracking().FirstOrDefaultAsync(x => x.GuildId == guild.Value, cancellationToken);
        return row is null
            ? GuildSettings.Default(guild)
            : new GuildSettings(guild, row.Language, row.TimeZoneId, row.SetupCompleted);
    }

    public async Task SaveAsync(GuildSettings settings, UserId changedBy, CancellationToken cancellationToken)
    {
        var row = await db.GuildSettings.FirstOrDefaultAsync(x => x.GuildId == settings.GuildId.Value, cancellationToken);
        if (row is null)
        {
            row = new GuildSettingsEntity { GuildId = settings.GuildId.Value };
            db.GuildSettings.Add(row);
        }

        row.Language = settings.Language;
        row.TimeZoneId = settings.TimeZoneId;
        row.SetupCompleted = settings.SetupCompleted;
        row.UpdatedAt = clock.GetUtcNow();
        row.UpdatedBy = changedBy.Value;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ModuleStateStore(ToroDbContext db, TimeProvider clock) : IModuleStateStore
{
    public async Task<bool?> GetExplicitStateAsync(GuildId guild, ModuleId module, CancellationToken cancellationToken)
    {
        var row = await db.GuildModuleStates.AsNoTracking()
            .FirstOrDefaultAsync(x => x.GuildId == guild.Value && x.ModuleId == module.Value, cancellationToken);
        return row?.Enabled;
    }

    public async Task SetStateAsync(GuildId guild, ModuleId module, bool enabled, UserId changedBy, CancellationToken cancellationToken)
    {
        var row = await db.GuildModuleStates.FirstOrDefaultAsync(x => x.GuildId == guild.Value && x.ModuleId == module.Value, cancellationToken);
        if (row is null)
        {
            row = new GuildModuleStateEntity { GuildId = guild.Value, ModuleId = module.Value };
            db.GuildModuleStates.Add(row);
        }

        row.Enabled = enabled;
        row.ChangedAt = clock.GetUtcNow();
        row.ChangedBy = changedBy.Value;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<ModuleId, bool>> GetExplicitStatesAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var rows = await db.GuildModuleStates.AsNoTracking().Where(x => x.GuildId == guild.Value).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => new ModuleId(r.ModuleId), r => r.Enabled);
    }

    public async Task<IReadOnlyList<GuildId>> GetGuildsWithModuleEnabledAsync(ModuleId module, CancellationToken cancellationToken)
    {
        var ids = await db.GuildModuleStates.AsNoTracking()
            .Where(x => x.ModuleId == module.Value && x.Enabled)
            .Select(x => x.GuildId)
            .ToListAsync(cancellationToken);
        return ids.Select(id => new GuildId(id)).ToList();
    }
}

public sealed class ConfirmationStore(ToroDbContext db, TimeProvider clock) : IConfirmationStore
{
    public async Task<string> CreateAsync(GuildId guild, UserId user, string action, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        // Opportunistic cleanup keeps the table tiny.
        await db.Confirmations.Where(x => x.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        db.Confirmations.Add(new ConfirmationEntity
        {
            Id = id,
            GuildId = guild.Value,
            UserId = user.Value,
            Action = action,
            ExpiresAt = now + lifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<bool> TryConsumeAsync(string id, GuildId guild, UserId user, string action, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 40)
            return false;
        var now = clock.GetUtcNow();
        // Single atomic statement: only the matching, unexpired row is deleted — a second click finds nothing.
        var deleted = await db.Confirmations
            .Where(x => x.Id == id && x.GuildId == guild.Value && x.UserId == user.Value && x.Action == action && x.ExpiresAt >= now)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }
}

/// <summary>Guild join/leave bookkeeping for the retention policy (docs/PRIVACY_AND_DATA.md).</summary>
public sealed class GuildPresenceStore(ToroDbContext db, TimeProvider clock) : IGuildPresenceTracker
{
    public async Task MarkPresentAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var row = await db.GuildPresence.FirstOrDefaultAsync(x => x.GuildId == guild.Value, cancellationToken);
        if (row is null)
        {
            db.GuildPresence.Add(new GuildPresenceEntity { GuildId = guild.Value, FirstSeenAt = clock.GetUtcNow() });
        }
        else
        {
            row.LeftAt = null;
            row.PurgedAt = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkLeftAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var row = await db.GuildPresence.FirstOrDefaultAsync(x => x.GuildId == guild.Value, cancellationToken);
        if (row is null)
        {
            row = new GuildPresenceEntity { GuildId = guild.Value, FirstSeenAt = clock.GetUtcNow() };
            db.GuildPresence.Add(row);
        }

        row.LeftAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GuildId>> GetDueForPurgeAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - retention;
        var ids = await db.GuildPresence.AsNoTracking()
            .Where(x => x.LeftAt != null && x.LeftAt <= cutoff && x.PurgedAt == null)
            .Select(x => x.GuildId)
            .ToListAsync(cancellationToken);
        return ids.Select(i => new GuildId(i)).ToList();
    }

    public async Task MarkPurgedAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var row = await db.GuildPresence.FirstAsync(x => x.GuildId == guild.Value, cancellationToken);
        row.PurgedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Core (non-module) guild data purge: settings, module states, outbox rows, confirmations.</summary>
public sealed class CoreGuildDataPurger(ToroDbContext db)
{
    public async Task<int> PurgeAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var g = guild.Value;
        var n = await db.GuildSettings.Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.GuildModuleStates.Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Outbox.Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        n += await db.Confirmations.Where(x => x.GuildId == g).ExecuteDeleteAsync(cancellationToken);
        return n;
    }
}

public sealed class ManagedCommandStore(ToroDbContext db, TimeProvider clock) : IManagedCommandStore
{
    public async Task<IReadOnlyDictionary<string, ulong>> GetAsync(ulong applicationId, string scopeKey, CancellationToken cancellationToken)
    {
        var rows = await db.ManagedCommands.AsNoTracking()
            .Where(x => x.ApplicationId == applicationId && x.Scope == scopeKey)
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Name, r => r.CommandId, StringComparer.Ordinal);
    }

    public async Task UpsertAsync(ulong applicationId, string scopeKey, string name, ulong commandId, string hash, CancellationToken cancellationToken)
    {
        var row = await db.ManagedCommands.FirstOrDefaultAsync(x => x.ApplicationId == applicationId && x.Scope == scopeKey && x.Name == name, cancellationToken);
        if (row is null)
        {
            row = new ManagedCommandEntity { ApplicationId = applicationId, Scope = scopeKey, Name = name };
            db.ManagedCommands.Add(row);
        }

        row.CommandId = commandId;
        row.Hash = hash;
        row.SyncedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(ulong applicationId, string scopeKey, string name, CancellationToken cancellationToken) =>
        await db.ManagedCommands.Where(x => x.ApplicationId == applicationId && x.Scope == scopeKey && x.Name == name).ExecuteDeleteAsync(cancellationToken);
}
