using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.News.Domain;
using ToroSquad.Modules.News.Persistence;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Modules.News.Application;

public enum NewsCheckState
{
    Ok = 0,
    Warning = 1,
    Problem = 2,
    Info = 3,
}

public sealed record NewsCheck(string LabelKey, NewsCheckState State, string DetailKey, IReadOnlyList<object> Args);

/// <summary>What /news-admin status shows (no secrets, no user data).</summary>
public sealed record NewsStatus(
    NewsMode Mode,
    bool DryRun,
    bool ModuleEnabled,
    ulong? ChannelId,
    bool Paused,
    string? ChannelProblem,
    NewsFeedStateEntity? Feed,
    RosterSnapshot? Roster,
    bool RosterFresh);

/// <summary>A card for the admin preview: the latest matched article, or an explicitly synthetic sample.</summary>
public sealed record NewsPreview(OutgoingMessage Message, bool Synthetic);

/// <summary>
/// /news-admin configure|pause|resume|status|preview|doctor. Every method authorizes the actor (Manage Server) and only touches
/// the row of <c>actor.GuildId</c>. Nothing here requests the feed or sends to a channel.
/// </summary>
public sealed class NewsConfigService(
    ToroDbContext db,
    IGuildGateway guilds,
    IModuleGate gate,
    NewsPlanner planner,
    NewsCardRenderer renderer,
    IOptions<NewsOptions> options,
    TimeProvider clock)
{
    public const GuildPermission RequiredChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks;

    /// <summary>The synthetic preview headline (clearly marked as a sample; never delivered).</summary>
    public const string SampleUrl = "https://www.hltv.org/news/1/sample";

    public async Task<OperationResult> SetChannelAsync(ActorContext actor, ulong channelId, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
        if (!access.Exists || !access.IsTextBased)
            return OperationResult.Fail(OperationError.InvalidInput, "news.config.channel_invalid");

        var now = clock.GetUtcNow();
        var config = await ConfigAsync(actor.GuildId, create: true, ct);
        if (config!.ChannelId is null)
        {
            // First configuration: only news first seen from now on (nothing from before is ever dumped into the channel).
            config.LiveSince = now;
            config.DryRunSince = now;
        }

        config.ChannelId = channelId;
        config.ChannelProblem = null;
        config.ChannelProblemAt = null;
        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
        var missing = RequiredChannelPermissions & ~access.Permissions;
        return access.Permissions.Grants(RequiredChannelPermissions)
            ? OperationResult.Ok("news.config.saved", mention)
            : OperationResult.Ok("news.config.saved_missing_permissions", mention, missing.ToString());
    }

    public async Task<OperationResult> SetPausedAsync(ActorContext actor, bool paused, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        if (config?.ChannelId is null)
            return OperationResult.Fail(OperationError.InvalidInput, "news.config.no_channel");
        if (config.Paused == paused)
            return OperationResult.Ok(paused ? "news.pause.already" : "news.resume.already");
        var now = clock.GetUtcNow();
        config.Paused = paused;
        if (!paused)
        {
            // News of the paused period is skipped, not caught up.
            config.LiveSince = now;
            config.DryRunSince = now;
        }

        config.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(paused ? "news.pause.done" : "news.resume.done");
    }

    public async Task<(OperationResult Auth, NewsStatus? Status)> StatusAsync(ActorContext actor, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);
        var o = options.Value;
        var config = await ConfigAsync(actor.GuildId, create: false, ct);
        var feed = await db.Set<NewsFeedStateEntity>().AsNoTracking().FirstOrDefaultAsync(s => s.Key == NewsPlanner.FeedKey, ct);
        var roster = feed is null ? planner.Roster(new NewsFeedStateEntity()) : planner.Roster(feed);
        var enabled = await gate.IsEnabledAsync(actor.GuildId, NewsModule.ModuleIdTyped, ct);
        return (OperationResult.Ok("news.status.title"), new NewsStatus(o.Mode, planner.DryRun, enabled, config?.ChannelId, config?.Paused == true,
            config?.ChannelProblem, feed, roster, roster?.IsFresh(clock.GetUtcNow(), TimeSpan.FromDays(o.Roster.MaxAgeDays)) == true));
    }

    public async Task<(OperationResult Auth, NewsPreview? Preview)> PreviewAsync(ActorContext actor, string language, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return (OperationResult.Forbidden(auth), null);
        var latest = await db.Set<NewsArticleEntity>().AsNoTracking()
            .Where(a => a.Source == NewsArticle.Source && a.Relevant && a.Title != null)
            .OrderByDescending(a => a.FirstSeenAt).ThenByDescending(a => a.ArticleId).FirstOrDefaultAsync(ct);
        return latest is null
            ? (OperationResult.Ok("news.preview.synthetic"), new NewsPreview(renderer.Render(SampleUrl, "Aurora: sample headline (synthetic preview)", clock.GetUtcNow(), language, null), true))
            : (OperationResult.Ok("news.preview.real"), new NewsPreview(renderer.Render(latest.Url, latest.Title!, latest.PublishedAt, language, null), false));
    }

    /// <summary>Configuration, channel and permissions, feed, roster and coverage. Reads cache and database only.</summary>
    public async Task<(OperationResult Auth, IReadOnlyList<NewsCheck> Checks)> DoctorAsync(ActorContext actor, CancellationToken ct)
    {
        var (auth, status) = await StatusAsync(actor, ct);
        if (status is null)
            return (auth, []);
        var o = options.Value;
        var now = clock.GetUtcNow();
        var checks = new List<NewsCheck>
        {
            status.Mode switch
            {
                NewsMode.Off => new("news.doctor.mode", NewsCheckState.Warning, "news.doctor.mode_off", []),
                NewsMode.DryRun => new("news.doctor.mode", NewsCheckState.Info, "news.doctor.mode_dry", []),
                _ => new("news.doctor.mode", status.DryRun ? NewsCheckState.Info : NewsCheckState.Ok, status.DryRun ? "news.doctor.mode_live_delivery_dry" : "news.doctor.mode_live", []),
            },
            new("news.doctor.module", status.ModuleEnabled ? NewsCheckState.Ok : NewsCheckState.Warning, status.ModuleEnabled ? "news.doctor.module_on" : "news.doctor.module_off", []),
        };

        if (status.ChannelId is not { } channelId)
        {
            checks.Add(new("news.doctor.channel", NewsCheckState.Problem, "news.doctor.channel_missing", []));
        }
        else
        {
            var access = await guilds.GetBotChannelAccessAsync(actor.GuildId, new ChannelId(channelId), ct);
            var mention = "<#" + channelId.ToString(CultureInfo.InvariantCulture) + ">";
            if (!access.Exists || !access.IsTextBased)
                checks.Add(new("news.doctor.channel", NewsCheckState.Problem, "news.doctor.channel_gone", [mention]));
            else if (!access.Permissions.Grants(RequiredChannelPermissions))
                checks.Add(new("news.doctor.channel", NewsCheckState.Problem, "news.doctor.channel_permissions", [mention, (RequiredChannelPermissions & ~access.Permissions).ToString()]));
            else
                checks.Add(new("news.doctor.channel", status.Paused ? NewsCheckState.Warning : NewsCheckState.Ok, status.Paused ? "news.doctor.channel_paused" : "news.doctor.channel_ok", [mention]));
            if (status.ChannelProblem is { } problem)
                checks.Add(new("news.doctor.channel", NewsCheckState.Problem, "news.doctor.channel_problem", [problem]));
        }

        var feed = status.Feed;
        if (feed?.LastAttemptAt is not { } attempt)
            checks.Add(new("news.doctor.feed", NewsCheckState.Info, "news.doctor.feed_never", []));
        else
        {
            var outcome = ((FeedOutcome)feed.LastOutcome).ToString();
            var success = feed.LastSuccessAt is { } s ? DiscordText.Timestamp(s, 'R') : "—";
            var state = feed.ConsecutiveFailures == 0 ? NewsCheckState.Ok : feed.ConsecutiveFailures >= 3 ? NewsCheckState.Problem : NewsCheckState.Warning;
            checks.Add(new("news.doctor.feed", state, "news.doctor.feed_value",
                [success, DiscordText.Timestamp(attempt, 'R'), outcome, feed.LastHttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "—", feed.ConsecutiveFailures,
                    feed.NextPollAt is { } next ? DiscordText.Timestamp(next, 'R') : "—", DiscordText.UntrustedPlain(feed.LastDetail ?? "—", 120)]));
            checks.Add(new("news.doctor.items", NewsCheckState.Info, "news.doctor.items_value",
                [feed.LastItemCount, feed.LastSkippedCount, feed.LastRelevantCount, feed.TtlMinutes?.ToString(CultureInfo.InvariantCulture) ?? "—",
                    feed.LastDeliveryStagedAt is { } d ? DiscordText.Timestamp(d, 'R') : "—"]));
            checks.Add(new("news.doctor.baseline", feed.BaselineAt is null ? NewsCheckState.Warning : NewsCheckState.Ok,
                feed.BaselineAt is { } b ? "news.doctor.baseline_value" : "news.doctor.baseline_none", feed.BaselineAt is { } bb ? [DiscordText.Timestamp(bb, 'f')] : []));
            if (feed.LastSuccessAt is { } ok && now - ok > TimeSpan.FromHours(o.CatchUpHours))
                checks.Add(new("news.doctor.coverage_gap", NewsCheckState.Warning, "news.doctor.coverage_gap_value", [o.CatchUpHours]));
        }

        if (status.Roster is { } roster)
            checks.Add(new("news.doctor.roster", status.RosterFresh ? NewsCheckState.Ok : NewsCheckState.Warning, status.RosterFresh ? "news.doctor.roster_value" : "news.doctor.roster_stale",
                [roster.Source, roster.Players.Count, DiscordText.Timestamp(roster.VerifiedAt, 'R'), o.Roster.MaxAgeDays]));
        else
            checks.Add(new("news.doctor.roster", NewsCheckState.Warning, "news.doctor.roster_none", []));
        if (feed?.RosterLastAttemptAt is { } rosterAttempt && feed.RosterFailures > 0)
            checks.Add(new("news.doctor.roster_sync", NewsCheckState.Warning, "news.doctor.roster_sync_failed",
                [DiscordText.Timestamp(rosterAttempt, 'R'), feed.RosterFailures, DiscordText.UntrustedPlain(feed.RosterDetail ?? "—", 120)]));

        checks.Add(new("news.doctor.coverage", NewsCheckState.Info, "news.doctor.coverage_value", []));
        checks.Add(new("news.doctor.terms", NewsCheckState.Info, "news.doctor.terms_value", []));
        return (auth, checks);
    }

    private async Task<NewsGuildConfigEntity?> ConfigAsync(GuildId guild, bool create, CancellationToken ct)
    {
        var set = db.Set<NewsGuildConfigEntity>();
        var config = await set.FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        if (config is null && create)
        {
            config = new NewsGuildConfigEntity { GuildId = guild.Value, UpdatedAt = clock.GetUtcNow() };
            set.Add(config);
        }

        return config;
    }
}

/// <summary>
/// Checked by the outbox dispatcher right before each send, edit and post-reconciliation resend (after the module gate,
/// BEFORE the dispatcher looks at IsDryRun). News:Mode applies to cards already queued: Off cancels every News card and
/// drops every queued News edit; DryRun does the same for live cards (kind <see cref="NewsPlanner.ArticleKind"/>) and lets
/// DryRun cards (kind <see cref="NewsPlanner.DryRunArticleKind"/>) through to the simulation. Cancelled cards are terminal:
/// they are not sent when the mode is Live again. A message that already reached Discord is never taken back.
/// </summary>
public sealed class NewsDeliveryPolicy(ToroDbContext db, IOptions<NewsOptions> options, TimeProvider clock) : IDeliveryPolicy
{
    public ModuleId Module => NewsModule.ModuleIdTyped;

    public async Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken)
    {
        switch (options.Value.Mode)
        {
            case NewsMode.Off:
                return new DeliveryDecision.Cancel("news_mode_off");
            case NewsMode.DryRun when kind != NewsPlanner.DryRunArticleKind:
                return new DeliveryDecision.Cancel("news_mode_dry_run");
        }

        var config = await db.Set<NewsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config?.ChannelId is null)
            return new DeliveryDecision.Cancel("not_configured");
        if (config.Paused)
            return new DeliveryDecision.Cancel("paused");
        return config.ChannelId == channel.Value ? DeliveryDecision.Allowed : new DeliveryDecision.Cancel("channel_changed");
    }

    public async Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        var config = await db.Set<NewsGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == guild.Value, cancellationToken);
        if (config is null || config.ChannelId != channel.Value)
            return;
        config.ChannelProblem = kind.ToString();
        config.ChannelProblemAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
