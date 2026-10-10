using Microsoft.EntityFrameworkCore;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Persistence;

/// <summary>Provider health per platform (doctor and /bot status survive restarts). No token or secret is ever stored.</summary>
public sealed class LiveProviderStateEntity
{
    public string Key { get; set; } = "";
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public int LastOutcome { get; set; }
    public string? LastDetail { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public int ConsecutiveFailures { get; set; }
}

/// <summary>
/// TSQ Live tables (additive; no other module's table is touched): <c>live_creator_state</c> (session, announcement
/// reference), <c>live_platform_state</c> (per channel status, title, ordering watermarks), <c>live_provider_state</c>
/// and <c>live_session_category</c> (the categories observed during a creator session, one row per distinct category).
/// </summary>
public sealed class LiveModelContributor : IModelContributor
{
    public string Name => "live";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CreatorState>(e =>
        {
            e.ToTable("live_creator_state");
            e.HasKey(x => x.CreatorKey);
            e.Property(x => x.CreatorKey).HasMaxLength(32);
            e.Property(x => x.Phase).HasConversion<int>();
            e.Property(x => x.SessionPlatforms).HasMaxLength(32);
            e.Property(x => x.NotAnnouncedReason).HasMaxLength(32);
            e.Property(x => x.AnnouncementKind).HasMaxLength(32);
        });
        modelBuilder.Entity<PlatformState>(e =>
        {
            e.ToTable("live_platform_state");
            e.HasKey(x => new { x.CreatorKey, x.Platform });
            e.Property(x => x.CreatorKey).HasMaxLength(32);
            e.Property(x => x.Platform).HasConversion<int>();
            e.Property(x => x.Login).HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<int>();
            e.Property(x => x.StreamId).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(LiveText.TitleMax);
            e.Property(x => x.Category).HasMaxLength(100);
            e.Property(x => x.AvatarUrl).HasMaxLength(512);
            e.Property(x => x.LastEventId).HasMaxLength(64);
        });
        modelBuilder.Entity<SessionCategory>(e =>
        {
            e.ToTable("live_session_category");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatorKey).HasMaxLength(32);
            e.Property(x => x.NameKey).HasMaxLength(SessionCategories.NameMax);
            e.Property(x => x.Name).HasMaxLength(SessionCategories.NameMax);
            e.Property(x => x.FirstPlatform).HasConversion<int>();
            e.Property(x => x.TwitchCategoryId).HasMaxLength(SessionCategories.IdMax);
            e.Property(x => x.KickCategoryId).HasMaxLength(SessionCategories.IdMax);
            // The final duplicate guard: one row per distinct category name of a session.
            e.HasIndex(x => new { x.CreatorKey, x.SessionNumber, x.NameKey }).IsUnique();
        });
        modelBuilder.Entity<LiveProviderStateEntity>(e =>
        {
            e.ToTable("live_provider_state");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(32);
            e.Property(x => x.LastDetail).HasMaxLength(300);
        });
    }
}
