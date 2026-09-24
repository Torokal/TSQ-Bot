using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ToroSquad.Core.Notifications;

namespace ToroSquad.Infrastructure.Persistence;

/// <summary>
/// Lets a module add its own tables to the single application database without Infrastructure referencing the
/// module (dependency points module → infrastructure). All modules are compiled in and registered explicitly,
/// so the model is static per build and migrations (in ToroSquad.Bot) cover every table.
/// </summary>
public interface IModelContributor
{
    string Name { get; }
    void Configure(ModelBuilder modelBuilder);
}

public sealed class ToroDbContext(DbContextOptions<ToroDbContext> options, IEnumerable<IModelContributor> contributors)
    : DbContext(options), IUnitOfWork
{
    internal string ContributorKey { get; } = string.Join(",", contributors.Select(c => c.Name).Order(StringComparer.Ordinal));

    private readonly IReadOnlyList<IModelContributor> _contributors = contributors.ToList();

    public DbSet<GuildSettingsEntity> GuildSettings => Set<GuildSettingsEntity>();
    public DbSet<GuildModuleStateEntity> GuildModuleStates => Set<GuildModuleStateEntity>();
    public DbSet<GuildPresenceEntity> GuildPresence => Set<GuildPresenceEntity>();
    public DbSet<OutboxMessageEntity> Outbox => Set<OutboxMessageEntity>();
    public DbSet<ConfirmationEntity> Confirmations => Set<ConfirmationEntity>();
    public DbSet<ManagedCommandEntity> ManagedCommands => Set<ManagedCommandEntity>();

    Task IUnitOfWork.SaveChangesAsync(CancellationToken cancellationToken) => SaveChangesAsync(cancellationToken);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // Optimistic concurrency for outbox rows: the planner and the dispatcher may touch the same row.
        foreach (var entry in ChangeTracker.Entries<OutboxMessageEntity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.Version++;
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Discord snowflakes: bit-for-bit ulong <-> INTEGER, lossless for the full ulong range.
        configurationBuilder.Properties<ulong>().HaveConversion<UInt64BitsConverter>();
        configurationBuilder.Properties<ulong?>().HaveConversion<UInt64BitsConverter>();
        // Instants: always stored as UTC ticks (INTEGER) so SQLite can compare/order them server-side.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuildSettingsEntity>(e =>
        {
            e.ToTable("guild_settings");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
        });

        modelBuilder.Entity<GuildModuleStateEntity>(e =>
        {
            e.ToTable("guild_module_state");
            e.HasKey(x => new { x.GuildId, x.ModuleId });
            e.Property(x => x.ModuleId).HasMaxLength(32);
        });

        modelBuilder.Entity<GuildPresenceEntity>(e =>
        {
            e.ToTable("guild_presence");
            e.HasKey(x => x.GuildId);
        });

        modelBuilder.Entity<OutboxMessageEntity>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.LogicalKey).HasMaxLength(400);
            e.HasIndex(x => x.LogicalKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.GuildId, x.ModuleId });
            e.Property(x => x.ModuleId).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.SourceKey).HasMaxLength(200);
            e.Property(x => x.PayloadHash).HasMaxLength(64);
            e.Property(x => x.Marker).HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(500);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<ConfirmationEntity>(e =>
        {
            e.ToTable("confirmation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(40);
            e.Property(x => x.Action).HasMaxLength(64);
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<ManagedCommandEntity>(e =>
        {
            e.ToTable("managed_command");
            e.HasKey(x => new { x.ApplicationId, x.Scope, x.Name });
            e.Property(x => x.Scope).HasMaxLength(40);
            e.Property(x => x.Name).HasMaxLength(32);
            e.Property(x => x.Hash).HasMaxLength(64);
        });

        foreach (var contributor in _contributors)
            contributor.Configure(modelBuilder);
    }
}

/// <summary>The model differs per contributor set (tests use subsets), so the cache key must include it.</summary>
public sealed class ContributorModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is ToroDbContext toro ? (context.GetType(), toro.ContributorKey, designTime) : (object)(context.GetType(), designTime);
}

public sealed class UInt64BitsConverter() : ValueConverter<ulong, long>(v => unchecked((long)v), v => unchecked((ulong)v));

public sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

public sealed class GuildSettingsEntity
{
    public ulong GuildId { get; set; }
    public string Language { get; set; } = "tr";
    public string TimeZoneId { get; set; } = "Europe/Istanbul";
    public bool SetupCompleted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

public sealed class GuildModuleStateEntity
{
    public ulong GuildId { get; set; }
    public string ModuleId { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
    public ulong ChangedBy { get; set; }
}

/// <summary>Tracks when the bot joined/left a guild, driving the data-retention purge after removal.</summary>
public sealed class GuildPresenceEntity
{
    public ulong GuildId { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }
}

public sealed class OutboxMessageEntity
{
    public long Id { get; set; }
    public string LogicalKey { get; set; } = "";
    public ulong GuildId { get; set; }
    public string ModuleId { get; set; } = "";
    public ulong ChannelId { get; set; }
    public string Kind { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string Marker { get; set; } = "";
    public bool IsDryRun { get; set; }

    public string PayloadJson { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public ulong? DiscordMessageId { get; set; }
    public string? LastError { get; set; }

    /// <summary>Hash of the payload currently visible in Discord (differs from PayloadHash while an edit is pending).</summary>
    public string? DeliveredPayloadHash { get; set; }
    public bool EditPending { get; set; }
    public int EditAttempts { get; set; }
    public int ReconcileAttempts { get; set; }

    /// <summary>Optimistic concurrency token (incremented on every update).</summary>
    public long Version { get; set; }
}

public sealed class ConfirmationEntity
{
    public string Id { get; set; } = "";
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public string Action { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Commands this application created through the sync tool — the only ones sync may ever prune.</summary>
public sealed class ManagedCommandEntity
{
    public ulong ApplicationId { get; set; }
    public string Scope { get; set; } = "";
    public string Name { get; set; } = "";
    public ulong CommandId { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset SyncedAt { get; set; }
}
