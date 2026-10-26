using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Domain;

namespace ToroSquad.Modules.Sor.Persistence;

/// <summary>Per-guild settings set by /tsq-admin modul:sor. Null limits mean the configured defaults.</summary>
public sealed class SorGuildConfigEntity
{
    public ulong GuildId { get; set; }

    /// <summary>Allowed channels (comma-separated ids; a thread follows its parent). Empty: every channel.</summary>
    public string AllowedChannelIds { get; set; } = "";

    /// <summary>Allowed roles (comma-separated ids, any one is enough). Empty: every member.</summary>
    public string AllowedRoleIds { get; set; } = "";

    public int? UserCooldownSeconds { get; set; }
    public int? UserDailyLimit { get; set; }
    public int? GuildDailyLimit { get; set; }
    public int? MaxContextTurns { get; set; }
    public int? IdleMinutes { get; set; }
    public int? HistoryHours { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ulong UpdatedBy { get; set; }
}

/// <summary>
/// A member's /sor settings in one guild: whether lasting memory is on, whether the first-use note was shown, and a random
/// <see cref="Stamp"/> that changes whenever the member's memory or its permissions change (switched off/on, a note deleted,
/// everything deleted). A request remembers the stamp it started with and is not delivered or saved when it changed. A row
/// deleted and created again gets a new random stamp: an old request can never become valid again.
/// </summary>
public sealed class SorUserEntity
{
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public bool MemoryEnabled { get; set; } = true;
    public DateTimeOffset? NoticeShownAt { get; set; }
    public string Stamp { get; set; } = SorStamps.New();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SorConversationState
{
    Open = 0,
    Closed = 1,
}

/// <summary>
/// One conversation: owner, guild, channel or thread, visibility. Only its owner's /sor questions are ever added; another
/// member's question in the same channel has its own conversation. Deleted (with its turns) when its history retention ends
/// or when a note it used or taught is deleted, changed or unshared.
/// </summary>
public sealed class SorConversationEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong OwnerUserId { get; set; }
    public ulong ChannelId { get; set; }
    public bool IsPrivate { get; set; }
    public SorConversationState State { get; set; }
    public string Stamp { get; set; } = SorStamps.New();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
}

/// <summary>One completed and DELIVERED question/answer pair (an undelivered answer never becomes history).</summary>
public sealed class SorTurnEntity
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public int Seq { get; set; }
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A note that went into a conversation's context (so its change can invalidate the conversation).</summary>
public sealed class SorMemoryUseEntity
{
    public long ConversationId { get; set; }
    public long MemoryId { get; set; }
}

/// <summary>
/// A remembered note. Owner (guild + user) is always the asker the application identified — never a model output. Keeps only
/// minimal source metadata (channel, conversation and interaction ids), never the message it came from; deleting the
/// conversation does not delete the note. <see cref="Stamp"/> changes on every edit (version check before use and save).
/// </summary>
public sealed class SorMemoryEntity
{
    public long Id { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public SorMemoryKind Kind { get; set; }
    public string Text { get; set; } = "";
    public SorMemoryScope Scope { get; set; }

    /// <summary>The channel or thread it was said in (null: a private conversation or written by hand).</summary>
    public ulong? SourceChannelId { get; set; }

    public long? SourceConversationId { get; set; }
    public ulong? SourceInteractionId { get; set; }
    public SorMemoryOrigin Origin { get; set; }
    public int Version { get; set; } = 1;
    public string Stamp { get; set; } = SorStamps.New();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SorRequestState
{
    /// <summary>Admitted and counted; the AI request is being prepared or running.</summary>
    Generating = 0,

    /// <summary>The answer is here (pending, with its proposals and context versions); not yet delivered.</summary>
    Generated = 1,

    /// <summary>Delivered; history and memory changes are done.</summary>
    Completed = 2,

    /// <summary>No answer: the provider failed, the answer was unusable, or Discord refused the delivery.</summary>
    Failed = 3,

    /// <summary>Not delivered: memory or permissions changed while the answer was being written.</summary>
    Discarded = 4,

    /// <summary>Refused before any AI request (not counted).</summary>
    Released = 5,

    /// <summary>The outcome is unknown (restart while generating, or a delivery that could not be confirmed). Counted.</summary>
    Unknown = 6,
}

/// <summary>
/// One /sor request, unique per Discord interaction (a repeated event can never start a second generation, count twice or
/// save twice). Holds the pending answer only between generation and finalization (cleared then, and by the cleanup within
/// minutes otherwise); afterwards only metadata (state, failure class, token counts) remains until the idempotency window
/// ends. Personal (guild + user): exported and deleted by /privacy.
/// </summary>
public sealed class SorRequestEntity
{
    public long Id { get; set; }
    public ulong InteractionId { get; set; }
    public ulong GuildId { get; set; }
    public ulong UserId { get; set; }
    public ulong ChannelId { get; set; }
    public bool IsPrivate { get; set; }
    public long? ConversationId { get; set; }

    /// <summary>The guild-time-zone day the request counts for ("yyyy-MM-dd").</summary>
    public string DayKey { get; set; } = "";

    public SorRequestState State { get; set; }

    /// <summary>Stage and class of a failure (e.g. "provider:RateLimited", "delivery"); never text.</summary>
    public string? Failure { get; set; }

    public string? TraceCode { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public string? PendingAnswer { get; set; }
    public string? PendingMemory { get; set; }
    public string? PendingContext { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// The guild's daily totals — no member id, so the guild limit survives a member's /privacy delete. Kept 35 days for the
/// /tsq-admin usage view.
/// </summary>
public sealed class SorGuildUsageEntity
{
    public ulong GuildId { get; set; }
    public string DayKey { get; set; } = "";
    public int Requests { get; set; }
    public int Completed { get; set; }
    public int ProviderFailures { get; set; }
    public int Timeouts { get; set; }
    public int ParseFailures { get; set; }
    public int DeliveryFailures { get; set; }
    public int Discarded { get; set; }
    public int UnknownUsage { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long ReasoningTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SorStamps
{
    public static string New() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}

/// <summary>
/// TSQ Sor tables (additive; no other module's table is touched): sor_guild_config, sor_user, sor_conversation, sor_turn,
/// sor_memory_use, sor_memory, sor_request, sor_guild_usage.
/// </summary>
public sealed class SorModelContributor : IModelContributor
{
    public const int MaxStoredQuestion = 4000;
    public const int MaxStoredAnswer = 12000;

    public string Name => "sor";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SorGuildConfigEntity>(e =>
        {
            e.ToTable("sor_guild_config");
            e.HasKey(x => x.GuildId);
            e.Property(x => x.GuildId).ValueGeneratedNever();
            e.Property(x => x.AllowedChannelIds).HasMaxLength(1000);
            e.Property(x => x.AllowedRoleIds).HasMaxLength(1000);
        });
        modelBuilder.Entity<SorUserEntity>(e =>
        {
            e.ToTable("sor_user");
            e.HasKey(x => new { x.GuildId, x.UserId });
            e.Property(x => x.Stamp).HasMaxLength(16);
            e.HasIndex(x => x.UserId);
        });
        modelBuilder.Entity<SorConversationEntity>(e =>
        {
            e.ToTable("sor_conversation");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.Stamp).HasMaxLength(16);
            e.HasIndex(x => new { x.GuildId, x.OwnerUserId, x.ChannelId, x.IsPrivate, x.State });
            e.HasIndex(x => x.LastActivityAt);
        });
        modelBuilder.Entity<SorTurnEntity>(e =>
        {
            e.ToTable("sor_turn");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Question).HasMaxLength(MaxStoredQuestion);
            e.Property(x => x.Answer).HasMaxLength(MaxStoredAnswer);
            e.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.UserId });
            e.HasOne<SorConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SorMemoryUseEntity>(e =>
        {
            e.ToTable("sor_memory_use");
            e.HasKey(x => new { x.ConversationId, x.MemoryId });
            e.HasIndex(x => x.MemoryId);
            e.HasOne<SorConversationEntity>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<SorMemoryEntity>(e =>
        {
            e.ToTable("sor_memory");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Scope).HasConversion<int>();
            e.Property(x => x.Origin).HasConversion<int>();
            e.Property(x => x.Text).HasMaxLength(500);
            e.Property(x => x.Stamp).HasMaxLength(16);
            e.HasIndex(x => new { x.GuildId, x.UserId });
            e.HasIndex(x => x.SourceConversationId);
        });
        modelBuilder.Entity<SorRequestEntity>(e =>
        {
            e.ToTable("sor_request");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.DayKey).HasMaxLength(10);
            e.Property(x => x.Failure).HasMaxLength(40);
            e.Property(x => x.TraceCode).HasMaxLength(16);
            e.Property(x => x.PendingAnswer).HasMaxLength(MaxStoredAnswer);
            e.Property(x => x.PendingMemory).HasMaxLength(4000);
            e.Property(x => x.PendingContext).HasMaxLength(4000);
            e.HasIndex(x => x.InteractionId).IsUnique();
            e.HasIndex(x => new { x.GuildId, x.UserId, x.DayKey });
            e.HasIndex(x => new { x.State, x.UpdatedAt });
            e.HasIndex(x => x.CreatedAt);
        });
        modelBuilder.Entity<SorGuildUsageEntity>(e =>
        {
            e.ToTable("sor_guild_usage");
            e.HasKey(x => new { x.GuildId, x.DayKey });
            e.Property(x => x.DayKey).HasMaxLength(10);
        });
    }
}
