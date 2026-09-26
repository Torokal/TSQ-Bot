using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Application;

/// <summary>
/// Turns creator session state into the ONE announcement message of each session, through the durable outbox (unique
/// logical key guild + module + creator:session + channel + kind; restart safe; retries; ambiguous sends are reconciled,
/// never blindly resent). It stages into the caller's DbContext and never saves or sends itself. Rules:
/// <list type="bullet">
/// <item>Only an announced session (<see cref="CreatorState.Announced"/>) has a message; the first staging is the only
/// payload that may carry @everyone. Every later change (title, category, a second platform, a platform going offline,
/// the end of the session) re-renders the same payload key → the outbox edits the same Discord message, and edits never
/// ping.</item>
/// <item>During the reconnect grace the card is left as it is (a flap causes no edit).</item>
/// <item>An announcement nobody delivered within <see cref="LiveOptions.AnnouncementMaxDelayMinutes"/> expires; the ended
/// card is never sent as a new message.</item>
/// <item>A message deleted in Discord (the outbox reports edit_target_deleted) is replaced at most
/// <see cref="MaxReplacements"/> time per session, while the session is live, without any mention.</item>
/// </list>
/// </summary>
public sealed class LiveAnnouncementPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IGuildSettingsStore guildSettings,
    LiveCardRenderer renderer,
    IOptions<LiveOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<LiveAnnouncementPlanner> logger)
{
    public const string AnnounceKind = "announce";
    public const string ReplacementPrefix = "announce-r";
    public const int MaxReplacements = 1;

    /// <summary>Set by the outbox when an edit found the message deleted (it never re-posts on its own).</summary>
    public const string DeletedMarker = "edit_target_deleted";

    public static string SourceKey(CreatorState state) => state.CreatorKey + ":" + state.SessionNumber.ToString(CultureInfo.InvariantCulture);

    public async Task PlanAsync(IReadOnlyList<TrackedCreator> creators, IReadOnlyList<CreatorState> states, IReadOnlyList<PlatformState> platforms,
        GuildId? guild, ChannelId? channel, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var dryRun = delivery.Value.Mode != DeliveryMode.Send;
        foreach (var creator in creators)
        {
            var state = states.FirstOrDefault(s => s.CreatorKey == creator.Key);
            if (state is null || !state.Announced || state.SessionNumber == 0)
                continue;

            if (state.AnnouncementKind is null)
            {
                // A new session: fix its target now, so a later channel change never splits (or re-pings) this session.
                if (guild is null || channel is null)
                    continue;
                state.AnnouncementKind = AnnounceKind;
                state.AnnouncementGuildId = guild.Value.Value;
                state.AnnouncementChannelId = channel.Value.Value;
                state.AnnouncedAt = now;
            }

            var target = new GuildId(state.AnnouncementGuildId!.Value);
            var targetChannel = new ChannelId(state.AnnouncementChannelId!.Value);
            var sourceKey = SourceKey(state);
            var row = await RowAsync(target, sourceKey, targetChannel, state.AnnouncementKind, dryRun, ct);
            if (row?.DiscordMessageId is { } messageId)
                state.AnnouncementMessageId = messageId;

            if (row is { Status: OutboxStatus.Sent, LastError: DeletedMarker } && state.Phase != CreatorPhase.Offline)
            {
                if (state.Replacements >= MaxReplacements)
                    continue; // spam-safe: one replacement per session at most, then the card simply stays gone
                state.Replacements++;
                state.AnnouncementKind = ReplacementPrefix + state.Replacements.ToString(CultureInfo.InvariantCulture);
                state.AnnouncementMessageId = null;
                state.AnnouncedAt = now;
                state.UpdatedAt = now;
                row = null;
                logger.LogWarning("live announcement message missing creator={Creator} session={Session}: posting one replacement card without mentions",
                    creator.Key, state.SessionNumber);
            }

            var perCreator = platforms.Where(p => p.CreatorKey == creator.Key && creator.Channel(p.Platform) is not null).ToList();
            var language = (await guildSettings.GetAsync(target, ct)).Language;
            Core.Messaging.OutgoingMessage message;
            DateTimeOffset expiresAt;
            switch (state.Phase)
            {
                case CreatorPhase.Live:
                    message = renderer.Live(creator, state, perCreator, language, withEveryone: state.AnnouncementKind == AnnounceKind);
                    expiresAt = (state.AnnouncedAt ?? now) + TimeSpan.FromMinutes(o.AnnouncementMaxDelayMinutes);
                    break;
                case CreatorPhase.Offline when state.SessionEndedAt is not null && row is not null:
                    // Edit only: an undelivered announcement is expired by the past expiry, never sent as an "ended" message.
                    message = renderer.Ended(creator, state, perCreator, language);
                    expiresAt = now - TimeSpan.FromSeconds(1);
                    break;
                default:
                    continue; // reconnect grace: leave the card as it is
            }

            var outcome = await outbox.StageAsync(new NotificationRequest(target, LiveModule.ModuleIdTyped, sourceKey, targetChannel,
                state.AnnouncementKind!, message, expiresAt, dryRun), ct);
            switch (outcome)
            {
                case StageOutcome.Created:
                    logger.LogInformation("live announcement created creator={Creator} session={Session} kind={Kind} mention={Mention}",
                        creator.Key, state.SessionNumber, state.AnnouncementKind, message.Mentions.Everyone ? "everyone" : "none");
                    break;
                case StageOutcome.EditScheduled:
                    logger.LogInformation("live announcement updated creator={Creator} session={Session} phase={Phase} (edit, no mention)",
                        creator.Key, state.SessionNumber, state.Phase);
                    break;
                case StageOutcome.IgnoredTerminal:
                    logger.LogDebug("live announcement not updatable creator={Creator} session={Session} (terminal outbox row)", creator.Key, state.SessionNumber);
                    break;
            }
        }
    }

    private async Task<OutboxMessageEntity?> RowAsync(GuildId guild, string sourceKey, ChannelId channel, string kind, bool dryRun, CancellationToken ct)
    {
        var key = NotificationRequest.BuildLogicalKey(guild, LiveModule.ModuleIdTyped, sourceKey, channel, kind, dryRun);
        return db.Outbox.Local.FirstOrDefault(x => x.LogicalKey == key) ?? await db.Outbox.AsNoTracking().FirstOrDefaultAsync(x => x.LogicalKey == key, ct);
    }
}
