using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;

namespace ToroSquad.Modules.Live.Application;

public enum LiveCheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record LiveDoctorCheck(string LabelKey, LiveCheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>
/// /live-admin doctor — actionable diagnostics from cached/persisted state only (no provider request, never a token or
/// secret): switches, Discord target and permissions (incl. Mention Everyone), delivery mode, per-provider auth and last
/// successful reconciliation, push transport state, every creator's session/platform state and announcement message.
/// </summary>
public sealed class LiveDoctor(
    ToroDbContext db,
    IModuleGate gate,
    IGuildGateway guilds,
    LiveHealth health,
    IEnumerable<ILiveStatusProvider> providers,
    DeploymentPolicy deployment,
    IOptions<LiveOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock)
{
    public async Task<(OperationResult Auth, IReadOnlyList<LiveDoctorCheck> Checks)> RunAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), []);

        var o = options.Value;
        var now = clock.GetUtcNow();
        var checks = new List<LiveDoctorCheck>
        {
            new("live.doctor.enabled", o.Enabled ? LiveCheckState.Ok : LiveCheckState.Warning, o.Enabled ? "live.doctor.enabled_on" : "live.doctor.enabled_off", []),
        };

        var guild = o.ResolveGuild(deployment);
        if (guild is { } g)
        {
            var moduleOn = await gate.IsEnabledAsync(g, LiveModule.ModuleIdTyped, ct);
            checks.Add(new("live.doctor.module", moduleOn ? LiveCheckState.Ok : LiveCheckState.Warning, moduleOn ? "live.doctor.module_on" : "live.doctor.module_off", []));
        }

        if (guild is null || o.DiscordChannelId == 0)
        {
            checks.Add(new("live.doctor.target", LiveCheckState.Problem, "live.doctor.target_missing", []));
        }
        else
        {
            var channel = new ChannelId(o.DiscordChannelId);
            if (guild.Value != actor.GuildId)
                checks.Add(new("live.doctor.target", LiveCheckState.Info, "live.doctor.target_other_guild", [guild.Value.ToString()]));
            var access = await guilds.GetBotChannelAccessAsync(guild.Value, channel, ct);
            if (!access.Exists)
            {
                checks.Add(new("live.doctor.target", LiveCheckState.Problem, "live.doctor.target_gone", [ChannelMention(o.DiscordChannelId)]));
            }
            else
            {
                checks.Add(new("live.doctor.target", LiveCheckState.Ok, "live.doctor.target_ok", [ChannelMention(o.DiscordChannelId)]));
                foreach (var (perm, missingState) in new[]
                         {
                             (GuildPermission.ViewChannel, LiveCheckState.Problem), (GuildPermission.SendMessages, LiveCheckState.Problem),
                             (GuildPermission.EmbedLinks, LiveCheckState.Problem), (GuildPermission.MentionEveryone, LiveCheckState.Problem),
                             (GuildPermission.ReadMessageHistory, LiveCheckState.Warning),
                         })
                {
                    var granted = access.Permissions.Grants(perm);
                    checks.Add(new("live.doctor.perm", granted ? LiveCheckState.Ok : missingState, granted ? "live.doctor.perm_ok" : "live.doctor.perm_missing", [perm.ToString()]));
                }
            }

            if (health.ChannelProblem is { } problem)
                checks.Add(new("live.doctor.target", LiveCheckState.Problem, "live.doctor.channel_problem", [problem, At(health.ChannelProblemAt)]));
        }

        if (delivery.Value.Mode != DeliveryMode.Send)
            checks.Add(new("live.doctor.delivery_mode", LiveCheckState.Warning, "live.doctor.delivery_dry_run", []));

        foreach (var provider in providers.OrderBy(p => p.Platform))
            checks.AddRange(Provider(provider, now));
        checks.Add(new("live.doctor.push", LiveCheckState.Info, "live.doctor.push_value", [o.ReconciliationIntervalSeconds, o.ReconnectGraceSeconds]));

        var creators = o.TrackedCreators();
        var states = await db.Set<CreatorState>().AsNoTracking().ToListAsync(ct);
        var platforms = await db.Set<PlatformState>().AsNoTracking().ToListAsync(ct);
        checks.Add(new("live.doctor.creators", LiveCheckState.Info, "live.doctor.creators_value", [creators.Count, string.Join(", ", creators.Select(c => c.DisplayName))]));
        foreach (var creator in creators)
            checks.Add(Creator(creator, states.FirstOrDefault(s => s.CreatorKey == creator.Key), platforms));

        var since = now - TimeSpan.FromDays(1);
        var rows = await db.Outbox.AsNoTracking()
            .Where(x => x.ModuleId == LiveModule.ModuleIdValue && x.UpdatedAt >= since)
            .Select(x => x.Status)
            .ToListAsync(ct);
        var failed = rows.Count(s => s == OutboxStatus.Failed);
        var unknown = rows.Count(s => s == OutboxStatus.DeliveryUnknown);
        var expired = rows.Count(s => s == OutboxStatus.Expired);
        checks.Add(new("live.doctor.delivery", failed + unknown + expired > 0 ? LiveCheckState.Warning : LiveCheckState.Ok, "live.doctor.delivery_stats",
            [rows.Count(s => s == OutboxStatus.Sent), rows.Count(s => s == OutboxStatus.Pending), failed, unknown, expired]));
        return (OperationResult.Ok("live.doctor.done"), checks);
    }

    private IEnumerable<LiveDoctorCheck> Provider(ILiveStatusProvider provider, DateTimeOffset now)
    {
        var label = provider.Platform == LivePlatform.Twitch ? "live.doctor.twitch" : "live.doctor.kick";
        if (!provider.IsConfigured)
        {
            yield return new(label, LiveCheckState.Problem, "live.doctor.provider_not_configured", []);
            yield break;
        }

        var authState = provider.Auth;
        yield return authState.LastOutcome switch
        {
            null => new(label, LiveCheckState.Info, "live.doctor.auth_unknown", []),
            LiveProviderOutcome.Ok => new(label, LiveCheckState.Ok, "live.doctor.auth_ok", [At(authState.TokenValidUntil), At(authState.LastValidatedAt)]),
            { } outcome => new(label, LiveCheckState.Problem, "live.doctor.auth_failed", [outcome.ToString()]),
        };

        var feed = health.Feed(provider.Platform);
        var stale = feed.LastSuccessAt is not { } success || now - success > TimeSpan.FromSeconds(Math.Max(300, options.Value.ReconciliationIntervalSeconds * 10));
        var state = feed.LastAttemptAt is null ? LiveCheckState.Info
            : feed.ConsecutiveFailures == 0 && !stale ? LiveCheckState.Ok
            : stale ? LiveCheckState.Problem : LiveCheckState.Warning;
        yield return new(label, state, "live.doctor.feed_state",
            [At(feed.LastSuccessAt), At(feed.LastAttemptAt), feed.ConsecutiveFailures, feed.LastOutcome?.ToString() ?? "-", Short(feed.LastDetail), At(feed.LastErrorAt)]);
        if (feed.Warnings.Count > 0)
            yield return new(label, LiveCheckState.Warning, "live.doctor.feed_warnings", [Short(string.Join("; ", feed.Warnings))]);
    }

    private static LiveDoctorCheck Creator(TrackedCreator creator, CreatorState? state, IReadOnlyList<PlatformState> platforms)
    {
        var channels = string.Join(" · ", creator.Channels.Select(ch =>
        {
            var p = platforms.FirstOrDefault(x => x.CreatorKey == creator.Key && x.Platform == ch.Platform);
            var status = p?.Status ?? PlatformStatus.Unknown;
            return $"{ch.Platform.Name()} ({ch.Login}): {status}";
        }));
        if (state is null || state.SessionNumber == 0)
            return new("live.doctor.creator", LiveCheckState.Info, "live.doctor.creator_idle", [DiscordText.Untrusted(creator.DisplayName, 40), state?.Phase.ToString() ?? "-", channels]);

        var title = LiveCardRenderer.MainPlatform(platforms.Where(p => p.CreatorKey == creator.Key).ToList(), LivePlatforms.All)?.Title;
        var announcement = !state.Announced ? "not announced: " + (state.NotAnnouncedReason ?? "-")
            : state.AnnouncementMessageId is { } m && state.AnnouncementGuildId is { } g && state.AnnouncementChannelId is { } c
                ? string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{g}/{c}/{m} ({state.AnnouncementKind})")
                : "pending (" + state.AnnouncementKind + ")";
        return new("live.doctor.creator", state.Phase == CreatorPhase.Offline ? LiveCheckState.Info : LiveCheckState.Ok, "live.doctor.creator_value",
            [DiscordText.Untrusted(creator.DisplayName, 40), state.Phase.ToString(), state.SessionNumber, channels, announcement,
             title is null ? "-" : DiscordText.Untrusted(title, 80), At(state.SessionStartedAt)]);
    }

    private static string ChannelMention(ulong channel) => "<#" + channel.ToString(CultureInfo.InvariantCulture) + ">";

    private static string At(DateTimeOffset? at) => at is { } a ? DiscordText.Timestamp(a, 'R') : "-";

    private static string Short(string? detail) => string.IsNullOrWhiteSpace(detail) ? "-" : DiscordText.Untrusted(detail, 160);
}

/// <summary>Cheap health summary for /bot status (cached state only; never calls providers, never shows secrets).</summary>
public sealed class LiveHealthCheck(LiveHealth health, IEnumerable<ILiveStatusProvider> providers, IOptions<LiveOptions> options, TimeProvider clock) : IModuleHealthCheck
{
    public ModuleId Module => LiveModule.ModuleIdTyped;

    public Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var entries = new List<HealthEntry>();
        if (!o.Enabled)
        {
            entries.Add(new HealthEntry("live.health.module", HealthState.NotConfigured, "live.health.disabled"));
            return Task.FromResult(new ModuleHealthReport(Module, entries));
        }

        var now = clock.GetUtcNow();
        foreach (var provider in providers.OrderBy(p => p.Platform))
        {
            var label = provider.Platform == LivePlatform.Twitch ? "live.health.twitch" : "live.health.kick";
            if (!provider.IsConfigured)
            {
                entries.Add(new HealthEntry(label, HealthState.NotConfigured, "live.health.not_configured"));
                continue;
            }

            var feed = health.Feed(provider.Platform);
            if (feed.LastSuccessAt is not { } success)
            {
                entries.Add(new HealthEntry(label, feed.LastAttemptAt is null ? HealthState.Degraded : HealthState.Unavailable, "live.health.no_data_yet"));
                continue;
            }

            var stale = now - success > TimeSpan.FromSeconds(Math.Max(300, o.ReconciliationIntervalSeconds * 10));
            entries.Add(new HealthEntry(label, stale ? HealthState.Unavailable : feed.ConsecutiveFailures > 0 ? HealthState.Degraded : HealthState.Healthy,
                "live.health.data_age", [DiscordText.Timestamp(success, 'R')]));
        }

        return Task.FromResult(new ModuleHealthReport(Module, entries));
    }
}

/// <summary>Checked by the outbox dispatcher immediately before each send/edit.</summary>
public sealed class LiveDeliveryPolicy(IOptions<LiveOptions> options, LiveHealth health, TimeProvider clock, ILogger<LiveDeliveryPolicy> logger) : IDeliveryPolicy
{
    public ModuleId Module => LiveModule.ModuleIdTyped;

    public Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Enabled)
            return Task.FromResult<DeliveryDecision>(new DeliveryDecision.Cancel("live_disabled"));
        return Task.FromResult(o.DiscordChannelId == channel.Value ? DeliveryDecision.Allowed : new DeliveryDecision.Cancel("channel_changed"));
    }

    public Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        // Surfaced in doctor; the outbox already stopped (no retry storm, no fallback to another channel).
        health.ReportChannelProblem(kind.ToString(), clock.GetUtcNow());
        logger.LogWarning("live announcement channel problem guild={Guild} channel={Channel}: {Kind}", guild.Value, channel.Value, kind);
        return Task.CompletedTask;
    }
}

/// <summary>TSQ Live stores no per-user data (creators and their public channels are configuration).</summary>
public sealed class LiveUserData : IUserDataContributor
{
    public ModuleId Module => LiveModule.ModuleIdTyped;

    public Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new JsonObject { ["storesPersonalData"] = false });

    public Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeletionPreviewItem>>([]);

    public Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(new DeletionReport(Module, 0, []));

    public Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken) => Task.FromResult(0);
}
