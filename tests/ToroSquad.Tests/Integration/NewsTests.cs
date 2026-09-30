using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.News.Application;
using ToroSquad.Modules.News.Persistence;
using ToroSquad.Modules.News.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ News end to end against the real SQLite database, the production wiring, the real outbox and a scripted feed:
/// baseline, one card per article, corrections as silent edits, dry-run separation, pause/resume, channel changes,
/// bounded catch-up, retention, module gate, failures and backoff across restarts, atomic rounds, ambiguous sends,
/// admin authorization and the roster refresh. Nothing here touches the network.
/// </summary>
public sealed class NewsTests
{
    private static readonly GuildId Guild = new(950);
    private static readonly GuildId OtherGuild = new(951);
    private static readonly ChannelId Channel = new(9501);
    private static readonly ChannelId Channel2 = new(9502);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed record Rig(TestHost Host, NewsFeedServer Feed, NewsFeedServer Wiki) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> RigAsync(string mode = "Live", Dictionary<string, string?>? extra = null, bool configure = true,
        Action<IServiceCollection>? replace = null)
    {
        var feed = new NewsFeedServer();
        var wiki = new NewsFeedServer();
        var overrides = new Dictionary<string, string?>
        {
            ["News:Mode"] = mode,
            ["News:Roster:SyncFromLiquipedia"] = "false",
        };
        foreach (var (k, v) in extra ?? [])
            overrides[k] = v;
        var host = await TestHost.CreateAsync(overrides, Start, services =>
        {
            services.AddHttpClient(HltvRssClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(feed));
            services.AddHttpClient(LiquipediaRosterClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(wiki));
            replace?.Invoke(services);
        });
        if (configure)
            await ConfigureGuildAsync(host, Guild, Channel);
        return new Rig(host, feed, wiki);
    }

    private static async Task ConfigureGuildAsync(TestHost host, GuildId guild, ChannelId channel, bool enable = true)
    {
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild));
        host.Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
        await host.InScopeAsync(async sp =>
        {
            (await sp.GetRequiredService<NewsConfigService>().SetChannelAsync(TestHost.Admin(guild), channel.Value, Ct)).Succeeded.Should().BeTrue();
            if (enable)
                (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(guild), "news", true, Ct)).Succeeded.Should().BeTrue();
        });
    }

    /// <summary>Moves the clock to the next due poll (never backwards) and runs one tick.</summary>
    private static async Task<bool> PollAsync(Rig rig, TimeSpan? after = null)
    {
        var state = await StateAsync(rig.Host);
        var now = rig.Host.Clock.GetUtcNow();
        if (state?.NextPollAt is { } next && next > now)
            rig.Host.Clock.SetUtcNow(next);
        if (after is { } a)
            rig.Host.Clock.Advance(a);
        return await rig.Host.Services.GetRequiredService<NewsPoller>().TickAsync(Ct);
    }

    private static async Task DeliverAsync(TestHost host)
    {
        var processor = host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private static Task<NewsFeedStateEntity?> StateAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<NewsFeedStateEntity>().AsNoTracking().FirstOrDefaultAsync(Ct));

    private static Task<List<OutboxMessageEntity>> OutboxAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "news").OrderBy(o => o.Id).ToListAsync(Ct));

    private static Task<List<NewsArticleEntity>> ArticlesAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<NewsArticleEntity>().AsNoTracking().OrderBy(a => a.ArticleId).ToListAsync(Ct));

    private static FeedItem Aurora(long id, DateTimeOffset published, string? title = null, string? slug = null) =>
        new(id, title ?? $"Aurora headline {id}", "Synthetic description.", published, slug);

    private static FeedItem Other(long id, DateTimeOffset published) => new(id, $"Unrelated headline {id}", "Nothing here.", published);

    private static int FeedRequests(Rig rig) => rig.Feed.Requests.Count(NewsFeedRequests.IsFeed);

    // ---------- baseline and new items ----------

    [Fact]
    public async Task The_first_feed_is_a_baseline_and_only_later_items_are_posted_once_across_repolls_and_restarts()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Aurora(100, Start.AddHours(-1)), Other(101, Start.AddHours(-1))]);
        (await PollAsync(rig)).Should().BeTrue();

        (await OutboxAsync(rig.Host)).Should().BeEmpty("existing news is never dumped into the channel");
        (await ArticlesAsync(rig.Host)).Should().OnlyContain(a => a.Baseline);
        (await StateAsync(rig.Host))!.BaselineAt.Should().NotBeNull();

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(30));
        var now = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(102, now.AddMinutes(-5), "Aurora sign a new AWPer"), Other(103, now), Aurora(100, Start.AddHours(-1)), Other(101, Start.AddHours(-1))]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Channel.Should().Be(Channel);
        message.Pinged.Should().BeFalse();
        message.Message.Embed!.Title.Should().Be("📰 Aurora — HLTV");
        message.Message.Embed.Description.Should().Contain("Aurora sign a new AWPer").And.Contain("(https://www.hltv.org/news/102/synthetic-102)");
        message.Message.Embed.Footer.Should().Be("Kaynak: HLTV");
        message.Message.Embed.Timestamp.Should().Be(now.AddMinutes(-5).AddTicks(-(now.AddMinutes(-5).Ticks % TimeSpan.TicksPerSecond)));

        await PollAsync(rig);
        await PollAsync(rig);
        var restarted = ActivatorUtilities.CreateInstance<NewsPoller>(rig.Host.Services);
        rig.Host.Clock.Advance(TimeSpan.FromHours(2));
        await restarted.TickAsync(Ct);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle("re-polls and a restart never post the same article again");
        (await OutboxAsync(rig.Host)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_first_request_establishes_no_baseline()
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => NewsFeeds.Status(HttpStatusCode.ServiceUnavailable);
        await PollAsync(rig);
        var state = await StateAsync(rig.Host);
        state!.BaselineAt.Should().BeNull();
        state.ConsecutiveFailures.Should().Be(1);
        state.LastOutcome.Should().Be((int)FeedOutcome.ServerError);

        rig.Feed.Serve([Aurora(200, Start)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty("the first successful feed is the baseline, whenever it comes");
        (await StateAsync(rig.Host))!.BaselineAt.Should().NotBeNull();

        rig.Feed.Serve([]);
        await PollAsync(rig);
        (await StateAsync(rig.Host))!.LastOutcome.Should().Be((int)FeedOutcome.Empty);
    }

    [Fact]
    public async Task An_empty_first_feed_establishes_no_baseline()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([]);
        await PollAsync(rig);
        (await StateAsync(rig.Host))!.BaselineAt.Should().BeNull();
        rig.Feed.Serve([Aurora(210, Start)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty();
    }

    // ---------- corrections ----------

    [Fact]
    public async Task A_corrected_headline_edits_the_same_message_silently_and_only_when_it_changed()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(300, t, "Aurora sing new player", "aurora-sing")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;

        rig.Feed.Serve([Aurora(300, t, "Aurora sign new player", "aurora-sign")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle("a correction never posts a second card");
        message.Edits.Should().ContainSingle();
        message.Edits[0].Embed!.Description.Should().Contain("Aurora sign new player").And.Contain("/news/300/aurora-sign)");
        message.Edits[0].Mentions.PingsAnything.Should().BeFalse();

        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        message.Edits.Should().ContainSingle("no change, no edit");
    }

    [Fact]
    public async Task A_deleted_card_is_not_recreated_by_a_later_correction()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(310, t, "Aurora first")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.DeleteMessage(rig.Host.Transport.Messages.Single().Id);

        rig.Feed.Serve([Aurora(310, t, "Aurora first (updated)")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.SendCalls.Should().Be(1, "a moderator's deletion is respected");
    }

    [Fact]
    public async Task The_same_headline_under_two_ids_is_two_articles()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(320, t, "Aurora news", "aurora-news"), Aurora(321, t, "Aurora news", "aurora-news")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_ambiguous_send_is_reconciled_to_its_own_message_not_to_a_same_titled_one()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(330, t, "Aurora news", "aurora-news")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        var first = rig.Host.Transport.Messages.Single();

        rig.Feed.Serve([Aurora(330, t, "Aurora news", "aurora-news"), Aurora(331, t.AddMinutes(1), "Aurora news", "aurora-news")]);
        await PollAsync(rig);
        rig.Host.Transport.ScriptAcceptedButTimedOut();
        await DeliverAsync(rig.Host);
        for (var i = 0; i < 4; i++)
        {
            rig.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            await DeliverAsync(rig.Host);
        }

        var rows = await OutboxAsync(rig.Host);
        rows.Should().OnlyContain(r => r.Status == OutboxStatus.Sent);
        rows[1].DiscordMessageId.Should().NotBe(first.Id.Value, "the link in the description tells the two cards apart");
        rig.Host.Transport.Messages.Should().HaveCount(2, "no duplicate");
    }

    // ---------- modes, pause, channel ----------

    [Fact]
    public async Task Dry_run_never_reaches_discord_and_is_not_counted_as_live()
    {
        await using var rig = await RigAsync("DryRun");
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(400, t)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Transport.SendCalls.Should().Be(0);
        (await OutboxAsync(rig.Host)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Simulated);
        var deliveries = await rig.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<NewsDeliveryEntity>().AsNoTracking().ToListAsync(Ct));
        deliveries.Should().ContainSingle().Which.DryRun.Should().BeTrue();

        // The same database under Live: the dry-run record does not count as delivered.
        await rig.Host.InScopeAsync(async sp =>
        {
            var live = ActivatorUtilities.CreateInstance<NewsPlanner>(sp, Microsoft.Extensions.Options.Options.Create(new NewsOptions { Mode = NewsMode.Live }));
            await live.ApplyAsync(FeedFetchResult.Fail(FeedOutcome.NotModified, 304, "", rig.Host.Clock.GetUtcNow()) with { Detail = null },
                rig.Host.Clock.GetUtcNow().AddMinutes(60), Ct);
        });
        var rows = await OutboxAsync(rig.Host);
        rows.Should().HaveCount(2);
        rows.Count(r => r.LogicalKey.StartsWith("live|", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public async Task Mode_off_makes_no_request_at_all()
    {
        await using var rig = await RigAsync("Off");
        rig.Feed.Serve([Aurora(1, Start)]);
        (await PollAsync(rig)).Should().BeFalse();
        FeedRequests(rig).Should().Be(0);
        (await StateAsync(rig.Host)).Should().BeNull();
    }

    [Fact]
    public async Task News_of_a_pause_is_skipped_and_only_news_after_resume_is_posted()
    {
        await using var rig = await RigAsync();
        await ConfigureGuildAsync(rig.Host, OtherGuild, Channel2); // keeps the feed polled while Guild is paused
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        (await rig.Host.InScopeAsync(sp => sp.GetRequiredService<NewsConfigService>().SetPausedAsync(TestHost.Admin(Guild), true, Ct))).MessageKey.Should().Be("news.pause.done");

        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(500, t)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle(m => m.Channel == Channel2, "the other guild is not paused");
        rig.Host.Transport.Messages.Should().NotContain(m => m.Channel == Channel);

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(10));
        (await rig.Host.InScopeAsync(sp => sp.GetRequiredService<NewsConfigService>().SetPausedAsync(TestHost.Admin(Guild), false, Ct))).MessageKey.Should().Be("news.resume.done");
        var t2 = rig.Host.Clock.GetUtcNow().AddMinutes(1);
        rig.Feed.Serve([Aurora(500, t), Aurora(501, t2)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Where(m => m.Channel == Channel).Should().ContainSingle()
            .Which.Message.Embed!.Description.Should().Contain("/news/501/", "the paused period is not caught up");
    }

    [Fact]
    public async Task A_paused_guild_alone_causes_no_feed_request()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        await rig.Host.InScopeAsync(sp => sp.GetRequiredService<NewsConfigService>().SetPausedAsync(TestHost.Admin(Guild), true, Ct));
        var before = FeedRequests(rig);
        await PollAsync(rig, TimeSpan.FromHours(3));
        FeedRequests(rig).Should().Be(before);
    }

    [Fact]
    public async Task A_channel_change_never_resends_and_new_news_goes_to_the_new_channel()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(600, t)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Guilds.SetChannel(Guild, Channel2, new BotChannelAccess(true, true, GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks));
        await rig.Host.InScopeAsync(sp => sp.GetRequiredService<NewsConfigService>().SetChannelAsync(TestHost.Admin(Guild), Channel2.Value, Ct));
        rig.Feed.Serve([Aurora(600, t), Aurora(601, rig.Host.Clock.GetUtcNow().AddMinutes(1))]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().HaveCount(2);
        rig.Host.Transport.Messages.Should().ContainSingle(m => m.Channel == Channel && m.Message.Embed!.Description!.Contains("/news/600/"));
        rig.Host.Transport.Messages.Should().ContainSingle(m => m.Channel == Channel2 && m.Message.Embed!.Description!.Contains("/news/601/"));
    }

    [Fact]
    public async Task The_module_gate_stops_polling_and_cancels_a_pending_card()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(700, t)]);
        await PollAsync(rig);
        (await OutboxAsync(rig.Host)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);

        await rig.Host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "news", false, Ct));
        await DeliverAsync(rig.Host);
        rig.Host.Transport.SendCalls.Should().Be(0, "the queue never bypasses the module gate");
        (await OutboxAsync(rig.Host)).Single().Status.Should().Be(OutboxStatus.Cancelled);

        var before = FeedRequests(rig);
        await PollAsync(rig, TimeSpan.FromHours(2));
        FeedRequests(rig).Should().Be(before, "no guild wants news: no feed request");
    }

    // ---------- catch-up and retention ----------

    [Fact]
    public async Task Catch_up_after_downtime_is_bounded_in_time_and_per_round()
    {
        await using var rig = await RigAsync();
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        rig.Host.Clock.Advance(TimeSpan.FromHours(10)); // the bot was down
        var now = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([
            Aurora(800, now.AddHours(-8)),
            Aurora(801, now.AddHours(-5)), Aurora(802, now.AddHours(-4)), Aurora(803, now.AddHours(-3)), Aurora(804, now.AddHours(-2)), Aurora(805, now.AddHours(-1)),
        ]);
        await rig.Host.Services.GetRequiredService<NewsPoller>().TickAsync(Ct);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Select(m => m.Message.Embed!.Description!).Should().HaveCount(3)
            .And.Satisfy(d => d.Contains("/news/801/"), d => d.Contains("/news/802/"), d => d.Contains("/news/803/"));

        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().HaveCount(5, "the rest follows on the next round");
        rig.Host.Transport.Messages.Should().NotContain(m => m.Message.Embed!.Description!.Contains("/news/800/"), "older than the catch-up window");
    }

    [Fact]
    public async Task Retention_prunes_text_and_ids_and_a_pruned_article_never_comes_back()
    {
        await using var rig = await RigAsync(extra: new() { ["News:TextRetentionDays"] = "7", ["News:DedupRetentionDays"] = "30" });
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Aurora(900, t)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();

        rig.Feed.Serve([Other(902, t)]);
        await PollAsync(rig, TimeSpan.FromDays(8));
        (await ArticlesAsync(rig.Host)).Single(a => a.ArticleId == 900).Title.Should().BeNull("headlines are kept for 7 days");

        await PollAsync(rig, TimeSpan.FromDays(25));
        (await ArticlesAsync(rig.Host)).Should().NotContain(a => a.ArticleId == 900);
        (await StateAsync(rig.Host))!.PrunedBelowArticleId.Should().BeGreaterThanOrEqualTo(900);

        // The old article reappears without a date (worst case): below the watermark it is baseline, never re-sent.
        rig.Feed.Serve([new FeedItem(900, "Aurora headline 900", "x")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();
    }

    // ---------- failures ----------

    [Fact]
    public async Task Rate_limits_and_blocks_back_off_and_the_wait_survives_a_restart()
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => NewsFeeds.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromHours(2));
        await PollAsync(rig);
        var state = await StateAsync(rig.Host);
        state!.LastOutcome.Should().Be((int)FeedOutcome.RateLimited);
        state.NextPollAt.Should().BeOnOrAfter(rig.Host.Clock.GetUtcNow().AddHours(2));

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(90));
        var restarted = ActivatorUtilities.CreateInstance<NewsPoller>(rig.Host.Services);
        await restarted.TickAsync(Ct);
        FeedRequests(rig).Should().Be(1, "a restart does not reset the backoff");

        rig.Feed.Respond = _ => NewsFeeds.Status(HttpStatusCode.Forbidden);
        await PollAsync(rig);
        (await StateAsync(rig.Host))!.NextPollAt.Should().BeOnOrAfter(rig.Host.Clock.GetUtcNow().AddHours(6));
        FeedRequests(rig).Should().Be(2, "no retry storm");
    }

    [Fact]
    public async Task A_throwing_provider_and_a_malformed_feed_stay_inside_the_module()
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => throw new InvalidOperationException("provider bug");
        (await PollAsync(rig)).Should().BeTrue();
        (await StateAsync(rig.Host))!.LastOutcome.Should().Be((int)FeedOutcome.TransportError);

        rig.Feed.Respond = _ => NewsFeeds.Ok("<rss><channel><item>");
        await PollAsync(rig);
        var state = await StateAsync(rig.Host);
        state!.LastOutcome.Should().Be((int)FeedOutcome.Malformed);
        state.ConsecutiveFailures.Should().Be(2);
        state.LastSuccessAt.Should().BeNull("a failure is never reported as 'no news'");

        var report = await rig.Host.Services.GetServices<IModuleHealthCheck>().Single(h => h.Module.Value == "news").CheckAsync(Ct);
        report.Overall.Should().NotBe(HealthState.Healthy);
    }

    [Fact]
    public async Task Validators_are_reused_and_304_means_unchanged_but_only_after_a_baseline()
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => NewsFeeds.Status(HttpStatusCode.NotModified);
        await PollAsync(rig);
        (await StateAsync(rig.Host))!.BaselineAt.Should().BeNull("a 304 without stored content proves nothing");

        rig.Feed.Respond = _ => NewsFeeds.Ok(NewsFeeds.Rss([Other(1, Start)]), etag: "\"v1\"");
        await PollAsync(rig);
        (await StateAsync(rig.Host))!.ETag.Should().Be("\"v1\"");

        rig.Feed.Respond = _ => NewsFeeds.Status(HttpStatusCode.NotModified);
        await PollAsync(rig);
        rig.Feed.Requests.Last().Headers.GetValues("If-None-Match").Should().Equal("\"v1\"");
        var state = await StateAsync(rig.Host);
        state!.LastOutcome.Should().Be((int)FeedOutcome.NotModified);
        state.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task Nothing_of_a_round_is_kept_when_it_cannot_be_stored_so_no_article_is_lost_behind_new_validators()
    {
        var failOnce = new FailOnceOutbox();
        await using var rig = await RigAsync(replace: s => s.AddScoped<INotificationOutbox>(sp => failOnce.Wrap(ActivatorUtilities.CreateInstance<NotificationOutbox>(sp))));
        rig.Feed.Respond = _ => NewsFeeds.Ok(NewsFeeds.Rss([Other(1, Start)]), etag: "\"v1\"");
        await PollAsync(rig);

        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Respond = _ => NewsFeeds.Ok(NewsFeeds.Rss([Other(1, Start), Aurora(1000, t)]), etag: "\"v2\"");
        failOnce.Armed = true;
        await rig.Invoking(r => PollAsync(r)).Should().ThrowAsync<InvalidOperationException>();
        var state = await StateAsync(rig.Host);
        state!.ETag.Should().Be("\"v1\"", "the new validator was not committed");
        (await ArticlesAsync(rig.Host)).Should().NotContain(a => a.ArticleId == 1000);

        await rig.Host.Services.GetRequiredService<NewsPoller>().TickAsync(Ct);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle(m => m.Message.Embed!.Description!.Contains("/news/1000/"));
        rig.Feed.Requests.Last().Headers.GetValues("If-None-Match").Should().Equal("\"v1\"");
    }

    // ---------- admin ----------

    [Fact]
    public async Task Admin_operations_need_manage_server_and_stay_in_their_guild()
    {
        await using var rig = await RigAsync();
        var member = TestHost.Member(Guild);
        await rig.Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<NewsConfigService>();
            (await config.SetChannelAsync(member, Channel.Value, Ct)).Error.Should().Be(OperationError.Forbidden);
            (await config.SetPausedAsync(member, true, Ct)).Error.Should().Be(OperationError.Forbidden);
            (await config.StatusAsync(member, Ct)).Status.Should().BeNull();
            (await config.PreviewAsync(member, "tr", Ct)).Preview.Should().BeNull();
            (await config.DoctorAsync(member, Ct)).Checks.Should().BeEmpty();

            var other = await config.StatusAsync(TestHost.Admin(OtherGuild), Ct);
            other.Status!.ChannelId.Should().BeNull("another guild's settings are never visible");
            (await config.SetChannelAsync(TestHost.Admin(Guild), 424242, Ct)).Error.Should().Be(OperationError.InvalidInput, "unknown channel");
        });
    }

    [Fact]
    public async Task Preview_status_and_doctor_describe_the_state_without_sending_anything()
    {
        await using var rig = await RigAsync();
        await rig.Host.InScopeAsync(async sp =>
        {
            var (_, preview) = await sp.GetRequiredService<NewsConfigService>().PreviewAsync(TestHost.Admin(Guild), "tr", Ct);
            preview!.Synthetic.Should().BeTrue();
        });
        rig.Feed.Serve([Other(1, Start)]);
        await PollAsync(rig);
        rig.Feed.Serve([Aurora(1100, rig.Host.Clock.GetUtcNow(), "Aurora preview headline")]);
        await PollAsync(rig);

        await rig.Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<NewsConfigService>();
            var (_, preview) = await config.PreviewAsync(TestHost.Admin(Guild), "tr", Ct);
            preview!.Synthetic.Should().BeFalse();
            preview.Message.Embed!.Description.Should().Contain("Aurora preview headline");

            var (_, status) = await config.StatusAsync(TestHost.Admin(Guild), Ct);
            status!.Mode.Should().Be(NewsMode.Live);
            status.ModuleEnabled.Should().BeTrue();
            status.ChannelId.Should().Be(Channel.Value);
            status.Feed!.LastItemCount.Should().Be(1);
            status.RosterFresh.Should().BeTrue("the dated seed roster is still within its validity");

            var (_, checks) = await config.DoctorAsync(TestHost.Admin(Guild), Ct);
            checks.Should().Contain(c => c.LabelKey == "news.doctor.channel" && c.State == NewsCheckState.Ok);
            checks.Should().Contain(c => c.LabelKey == "news.doctor.terms");
            checks.Should().Contain(c => c.LabelKey == "news.doctor.coverage");
        });
        rig.Host.Transport.SendCalls.Should().Be(0, "preview, status and doctor post nothing");
    }

    // ---------- roster ----------

    [Fact]
    public async Task The_roster_refresh_updates_the_players_and_a_failure_keeps_the_previous_list()
    {
        await using var rig = await RigAsync(extra: new() { ["News:Roster:SyncFromLiquipedia"] = "true" });
        rig.Feed.Serve([Other(1, Start)]);
        rig.Wiki.Respond = _ => StubHttpHandler.Json("""
            {"query":{"pages":[{"title":"Aurora Gaming","revisions":[{"slots":{"main":{"content":"===Active===\n{{Squad|status=active\n|{{Person|id=XANTARES}}\n|{{Person|id=woxic}}\n|{{Person|id=NewGuy}}\n|{{Person|id=ashhh|role=Coach}}\n}}\n===Inactive===\n"}}}]}]}}
            """);
        await PollAsync(rig);
        var state = await StateAsync(rig.Host);
        state!.RosterPlayers.Should().Be("XANTARES,woxic,NewGuy");
        state.RosterSource.Should().Be("liquipedia");
        rig.Wiki.Requests.Should().ContainSingle().Which.RequestUri!.Host.Should().Be("liquipedia.net");

        await PollAsync(rig);
        rig.Wiki.Requests.Should().ContainSingle("the roster is refreshed at most once per refresh period");

        rig.Wiki.Respond = _ => NewsFeeds.Status(HttpStatusCode.ServiceUnavailable);
        await PollAsync(rig, TimeSpan.FromHours(25));
        var after = await StateAsync(rig.Host);
        after!.RosterPlayers.Should().Be("XANTARES,woxic,NewGuy", "a failed refresh never empties the roster");
        after.RosterFailures.Should().Be(1);

        // A current player's name alone is now enough (fresh roster).
        var t = rig.Host.Clock.GetUtcNow();
        rig.Feed.Serve([Other(1, Start), new FeedItem(1200, "NewGuy: \"a dream come true\"", "", t)]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();
    }

    /// <summary>Throws on the first staging while armed (a failing round) and delegates otherwise.</summary>
    private sealed class FailOnceOutbox
    {
        public bool Armed { get; set; }

        public INotificationOutbox Wrap(INotificationOutbox inner) => new Wrapper(this, inner);

        private sealed class Wrapper(FailOnceOutbox owner, INotificationOutbox inner) : INotificationOutbox
        {
            public Task<StageOutcome> StageAsync(NotificationRequest request, CancellationToken cancellationToken)
            {
                if (owner.Armed && request.Module.Value == "news")
                {
                    owner.Armed = false;
                    throw new InvalidOperationException("simulated failure while storing the round");
                }

                return inner.StageAsync(request, cancellationToken);
            }
        }
    }
}
