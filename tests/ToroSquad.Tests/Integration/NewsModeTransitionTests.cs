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
/// TSQ News mode changes across restarts on the SAME database (a second host over the first host's data directory, as a
/// Railway restart with another News:Mode would be): queued live cards, queued edits and reconciliation resends must never
/// reach Discord while the mode is Off or DryRun; DryRun keeps simulating; and the first switch from DryRun to Live.
/// </summary>
public sealed class NewsModeTransitionTests
{
    private static readonly GuildId Guild = new(960);
    private static readonly ChannelId Channel = new(9601);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class Rig(TestHost host, NewsFeedServer feed) : IAsyncDisposable
    {
        public TestHost Host { get; } = host;
        public NewsFeedServer Feed { get; } = feed;

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>A host in <paramref name="mode"/>; with <paramref name="shareWith"/> it reopens that host's database at that host's time.</summary>
    private static async Task<Rig> RigAsync(string mode, Rig? shareWith = null)
    {
        var feed = new NewsFeedServer();
        var overrides = new Dictionary<string, string?> { ["News:Mode"] = mode, ["News:Roster:SyncFromLiquipedia"] = "false" };
        if (shareWith is not null)
            overrides["Bot:DataDirectory"] = shareWith.Host.Directory;
        var host = await TestHost.CreateAsync(overrides, shareWith?.Host.Clock.GetUtcNow() ?? Start,
            s => s.AddHttpClient(HltvRssClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(feed)));
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild));
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
        if (shareWith is null)
        {
            await host.InScopeAsync(async sp =>
            {
                (await sp.GetRequiredService<NewsConfigService>().SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct)).Succeeded.Should().BeTrue();
                (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "news", true, Ct)).Succeeded.Should().BeTrue();
            });
        }

        return new Rig(host, feed);
    }

    private static async Task PollAsync(Rig rig)
    {
        var state = await rig.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<NewsFeedStateEntity>().AsNoTracking().FirstOrDefaultAsync(Ct));
        if (state?.NextPollAt is { } next && next > rig.Host.Clock.GetUtcNow())
            rig.Host.Clock.SetUtcNow(next);
        await rig.Host.Services.GetRequiredService<NewsPoller>().TickAsync(Ct);
    }

    private static async Task DeliverAsync(Rig rig)
    {
        var processor = rig.Host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private static Task<List<OutboxMessageEntity>> RowsAsync(Rig rig) =>
        rig.Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "news").OrderBy(o => o.Id).ToListAsync(Ct));

    private static FeedItem Aurora(long id, DateTimeOffset published, string? title = null) => new(id, title ?? $"Aurora headline {id}", "", published);

    private static FeedItem Other(long id) => new(id, $"Unrelated {id}", "", Start);

    /// <summary>Baseline, then one relevant article staged (not delivered yet) in Live.</summary>
    private static async Task<Rig> LiveWithPendingCardAsync()
    {
        var live = await RigAsync("Live");
        live.Feed.Serve([Other(1)]);
        await PollAsync(live);
        live.Feed.Serve([Other(1), Aurora(10, live.Host.Clock.GetUtcNow())]);
        await PollAsync(live);
        (await RowsAsync(live)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Pending);
        return live;
    }

    [Fact]
    public async Task Off_cancels_a_queued_live_card_and_live_does_not_resend_it_later()
    {
        await using var live = await LiveWithPendingCardAsync();
        await using (var off = await RigAsync("Off", live))
        {
            await DeliverAsync(off);
            off.Host.Transport.SendCalls.Should().Be(0, "News Off never reaches Discord, even for a card queued under Live");
            var row = (await RowsAsync(off)).Single();
            row.Status.Should().Be(OutboxStatus.Cancelled);
            row.LastError.Should().Be("news_mode_off");
        }

        await using var again = await RigAsync("Live", live);
        await DeliverAsync(again);
        again.Feed.Serve([Other(1), Aurora(10, Start)]);
        await PollAsync(again);
        await DeliverAsync(again);
        again.Host.Transport.SendCalls.Should().Be(0, "a card cancelled while Off is not revived when Live returns (its delivery record stays)");
        (await RowsAsync(again)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Cancelled);
    }

    [Fact]
    public async Task DryRun_cancels_queued_live_sends_drops_queued_live_edits_and_keeps_simulating()
    {
        await using var live = await RigAsync("Live");
        live.Feed.Serve([Other(1)]);
        await PollAsync(live);
        var t = live.Host.Clock.GetUtcNow();
        live.Feed.Serve([Other(1), Aurora(20, t, "Aurora first title")]);
        await PollAsync(live);
        await DeliverAsync(live);
        live.Host.Transport.SendCalls.Should().Be(1);
        live.Feed.Serve([Other(1), Aurora(20, t, "Aurora corrected title"), Aurora(21, t)]);
        await PollAsync(live);
        var queued = await RowsAsync(live);
        queued.Single(r => r.LogicalKey.Contains(":20|", StringComparison.Ordinal)).EditPending.Should().BeTrue();
        queued.Single(r => r.LogicalKey.Contains(":21|", StringComparison.Ordinal)).Status.Should().Be(OutboxStatus.Pending);

        await using var dry = await RigAsync("DryRun", live);
        await DeliverAsync(dry);
        dry.Host.Transport.SendCalls.Should().Be(0);
        dry.Host.Transport.EditCalls.Should().Be(0);
        var rows = await RowsAsync(dry);
        var edited = rows.Single(r => r.LogicalKey.Contains(":20|", StringComparison.Ordinal));
        edited.Status.Should().Be(OutboxStatus.Sent, "the message already in Discord stays; News DryRun cannot take it back");
        edited.EditPending.Should().BeFalse();
        edited.LastError.Should().Be("edit_skipped_news_mode_dry_run");
        var cancelled = rows.Single(r => r.LogicalKey.Contains(":21|", StringComparison.Ordinal));
        cancelled.Status.Should().Be(OutboxStatus.Cancelled);
        cancelled.LastError.Should().Be("news_mode_dry_run");

        // Simulation and diagnostics still work in DryRun.
        dry.Feed.Serve([Other(1), Aurora(20, t, "Aurora corrected title"), Aurora(21, t), Aurora(22, dry.Host.Clock.GetUtcNow())]);
        await PollAsync(dry);
        await DeliverAsync(dry);
        // DryRun keeps its own delivery records: it simulates every article it would post (20, 21 and the new 22).
        var simulated = (await RowsAsync(dry)).Where(r => r.IsDryRun).ToList();
        simulated.Should().HaveCount(3).And.OnlyContain(r => r.Status == OutboxStatus.Simulated && r.Kind == NewsPlanner.DryRunArticleKind);
        dry.Host.Transport.SendCalls.Should().Be(0);
        await dry.Host.InScopeAsync(async sp =>
        {
            var (_, checks) = await sp.GetRequiredService<NewsConfigService>().DoctorAsync(TestHost.Admin(Guild), Ct);
            checks.Should().Contain(c => c.LabelKey == "news.doctor.mode" && c.DetailKey == "news.doctor.mode_dry");
        });
    }

    [Fact]
    public async Task A_reconciliation_resend_after_a_restart_into_Off_never_sends()
    {
        await using var live = await LiveWithPendingCardAsync();
        live.Host.Transport.ScriptAcceptedButTimedOut();
        await DeliverAsync(live);
        (await RowsAsync(live)).Single().Status.Should().Be(OutboxStatus.DeliveryUnknown);

        await using var off = await RigAsync("Off", live);
        for (var i = 0; i < 4; i++)
        {
            off.Host.Clock.Advance(TimeSpan.FromMinutes(1));
            await DeliverAsync(off);
        }

        off.Host.Transport.SendCalls.Should().Be(0, "the new process cannot see the lost message; its resend is stopped by the News mode");
        var row = (await RowsAsync(off)).Single();
        row.Status.Should().Be(OutboxStatus.Cancelled);
        row.LastError.Should().Be("news_mode_off");
    }

    [Fact]
    public async Task Other_modules_are_not_affected_by_the_news_mode()
    {
        await using var off = await RigAsync("Off");
        await off.Host.InScopeAsync(async sp =>
        {
            var outbox = sp.GetRequiredService<INotificationOutbox>();
            await outbox.StageAsync(new NotificationRequest(Guild, new ModuleId("core"), "x", Channel, "test",
                new ToroSquad.Core.Messaging.OutgoingMessage("hello", null, ToroSquad.Core.Messaging.MentionPolicy.None), Start.AddHours(1), false), Ct);
            await sp.GetRequiredService<ToroDbContext>().SaveChangesAsync(Ct);
        });
        await DeliverAsync(off);
        off.Host.Transport.SendCalls.Should().Be(1);
    }

    // ---------- DryRun → Live ----------

    [Fact]
    public async Task Switching_straight_from_DryRun_to_Live_posts_a_recent_article_first_seen_during_DryRun_once()
    {
        await using var dry = await RigAsync("DryRun");
        dry.Feed.Serve([Other(1)]);
        await PollAsync(dry);
        dry.Feed.Serve([Other(1), Aurora(30, dry.Host.Clock.GetUtcNow())]);
        await PollAsync(dry);
        await DeliverAsync(dry);
        (await RowsAsync(dry)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Simulated);

        await using var live = await RigAsync("Live", dry);
        live.Feed.Serve([Other(1), Aurora(30, dry.Host.Clock.GetUtcNow())]);
        await PollAsync(live);
        await DeliverAsync(live);
        await PollAsync(live);
        await DeliverAsync(live);
        live.Host.Transport.SendCalls.Should().Be(1,
            "an article simulated during DryRun (within the 6 h window) is posted live once — the first live post, not a duplicate");
    }

    [Fact]
    public async Task Pause_then_Live_then_resume_starts_with_news_published_after_the_resume()
    {
        await using var dry = await RigAsync("DryRun");
        dry.Feed.Serve([Other(1)]);
        await PollAsync(dry);
        var t = dry.Host.Clock.GetUtcNow();
        dry.Feed.Serve([Other(1), Aurora(40, t)]);
        await PollAsync(dry);
        await DeliverAsync(dry);
        (await dry.Host.InScopeAsync(sp => sp.GetRequiredService<NewsConfigService>().SetPausedAsync(TestHost.Admin(Guild), true, Ct))).Succeeded.Should().BeTrue();

        await using var live = await RigAsync("Live", dry);
        live.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        (await live.Host.InScopeAsync(sp => sp.GetRequiredService<NewsConfigService>().SetPausedAsync(TestHost.Admin(Guild), false, Ct))).MessageKey
            .Should().Be("news.resume.done");
        var after = live.Host.Clock.GetUtcNow().AddMinutes(5);
        live.Feed.Serve([Other(1), Aurora(40, t), Aurora(41, after)]);
        await PollAsync(live);
        await DeliverAsync(live);

        var sent = live.Host.Transport.Messages.Should().ContainSingle().Subject;
        sent.Message.Embed!.Description.Should().Contain("/news/41/", "the DryRun-period article is skipped; news after the resume is posted");
    }
}
