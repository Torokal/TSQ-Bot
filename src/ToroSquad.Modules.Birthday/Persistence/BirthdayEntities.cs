using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Modules.Birthday.Persistence;

/// <summary>A member's birthday in one guild: day and month only — the year is never asked for or stored.</summary>
public sealed class BirthdayRegistrationEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public int Day { get; set; }
    public int Month { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum BirthdayRoleState
{
    /// <summary>The bot should give the role (on the day, while the module is enabled).</summary>
    Pending = 0,

    /// <summary>The bot gave the role and removes it when the day is over.</summary>
    Active = 1,

    /// <summary>The member already had the role (given by someone else): the bot never touches it.</summary>
    NotManaged = 2,

    Removed = 3,

    /// <summary>Not given: the day ended first, the member left, or no role is configured.</summary>
    Skipped = 4,

    /// <summary>Gave up after repeated Discord failures (visible in /tsq-admin birthday doctor).</summary>
    Failed = 5,
}

/// <summary>
/// One celebration: a member's birthday in one guild in one year. Unique (guild, user, year): a member is celebrated at most
/// once a year, whatever happens to their registration (changing the date again and again does not give the role every day).
/// It also records the role the bot manages for that day — only roles with <see cref="BirthdayRoleState.Active"/> were given
/// by the bot and are removed by it; a role the member already had is never removed.
/// </summary>
public sealed class BirthdayCelebrationEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public int Year { get; set; }

    /// <summary>The celebrated day in the celebration time zone.</summary>
    public DateOnly LocalDate { get; set; }

    /// <summary>Named in that day's announcement (the per-day announcement row is the duplicate guard).</summary>
    public bool Announced { get; set; }

    public BirthdayRoleState RoleState { get; set; }

    /// <summary>Discord calls made for the current role step (add or remove); reset when the role was given.</summary>
    public int RoleAttempts { get; set; }

    /// <summary>Last role problem (policy or Discord outcome name); never user input.</summary>
    public string? RoleError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? RoleGrantedAt { get; set; }
    public DateTimeOffset? RoleRemovedAt { get; set; }
}

public enum BirthdayAnnouncementState
{
    Queued = 0,
    Sent = 1,
    Failed = 2,

    /// <summary>Delivery:Mode=DryRun — logged, not sent.</summary>
    Simulated = 3,
}

/// <summary>
/// The one announcement of a guild's day. Unique (guild, local date): restarts, redeploys and overlapping passes can never
/// create a second one. Created in the same transaction as the outbox row that delivers it.
/// </summary>
public sealed class BirthdayAnnouncementEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public DateOnly LocalDate { get; set; }
    public ulong ChannelId { get; set; }
    public int Celebrants { get; set; }
    public BirthdayAnnouncementState State { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-guild settings: the announcement channel (never guessed) and a channel problem Discord reported.</summary>
public sealed class BirthdayGuildConfigEntity
{
    public ulong GuildId { get; set; }
    public ulong? ChannelId { get; set; }
    public string? ChannelProblem { get; set; }
    public DateTimeOffset? ChannelProblemAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

/// <summary>
/// TSQ Birthday tables (additive; no other module's table is touched): birthday_registration, birthday_celebration,
/// birthday_announcement, birthday_guild_config.
/// </summary>
public sealed class BirthdayModelContributor : IModelContributor
{
    public string Name => "birthday";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BirthdayRegistrationEntity>(e =>
        {
            e.ToTable("birthday_registration");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.GuildId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.Month, x.Day }); // today's birthdays of a guild
            e.HasIndex(x => x.UserId); // privacy export/delete
        });
        modelBuilder.Entity<BirthdayCelebrationEntity>(e =>
        {
            e.ToTable("birthday_celebration");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.RoleState).HasConversion<int>();
            e.Property(x => x.RoleError).HasMaxLength(100);
            e.HasIndex(x => new { x.GuildId, x.UserId, x.Year }).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.LocalDate });
            e.HasIndex(x => x.RoleState).HasFilter("\"RoleState\" IN (0, 1)"); // open role work only
        });
        modelBuilder.Entity<BirthdayAnnouncementEntity>(e =>
        {
            e.ToTable("birthday_announcement");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.Detail).HasMaxLength(100);
            e.HasIndex(x => new { x.GuildId, x.LocalDate }).IsUnique();
        });
        modelBuilder.Entity<BirthdayGuildConfigEntity>(e =>
        {
            e.ToTable("birthday_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
            e.Property(x => x.ChannelProblem).HasMaxLength(32);
        });
    }
}
