using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Letterboxd.Application;
using ToroSquad.Modules.Letterboxd.Domain;
using ToroSquad.Modules.Letterboxd.Persistence;
using ToroSquad.Modules.Letterboxd.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Letterboxd end to end against the real SQLite database, the production wiring, the real outbox and a scripted feed:
/// baseline, one card per entry (review, rated entry, unrated rewatch), no card for an unrated first watch, a new rewatch as
/// its own card, review/rating/rewatch edits as silent edits of the same message, metadata changes, restarts, mode changes,
/// failure kinds and backoff, partial answers, coverage gaps, old watched dates, pause/resume, channel and account changes,
/// atomic rounds, send failures, retention and admin authorization. Nothing here touches the network.
/// </summary>
public sealed class LetterboxdTests
{
    private static readonly GuildId Guild = new(960);
    private static readonly ChannelId Channel = new(9601);
    private static readonly ChannelId Channel2 = new(9602);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);

    private sealed record Rig(TestHost Host, LetterboxdFeedServer Feed) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> RigAsync(string mode = "Live", Dictionary<string, string?>? extra = null, bool configure = true, Action<IServiceCollection>? replace = null)
    {
        var feed = new LetterboxdFeedServer();
        var overrides = new Dictionary<string, string?> { ["Letterboxd:Mode"] = mode, ["Letterboxd:MaxCardsPerRound"] = "10" };
        foreach (var (k, v) in extra ?? [])
            overrides[k] = v;
        var host = await TestHost.CreateAsync(overrides, Start, services =>
        {
            services.AddHttpClient(LetterboxdRssClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(feed));
            replace?.Invoke(services);
        });
        if (configure)
            await ConfigureGuildAsync(host, Guild, Channel);
        return new Rig(host, feed);
    }

    private static async Task ConfigureGuildAsync(TestHost host, GuildId guild, ChannelId channel, bool enable = true)
    {
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild));
        host.Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
        await host.InScopeAsync(async sp =>
        {
            (await sp.GetRequiredService<LetterboxdConfigService>().SetChannelAsync(TestHost.Admin(guild), channel.Value, Ct)).Succeeded.Should().BeTrue();
            if (enable)
                (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(guild), "letterboxd", true, Ct)).Succeeded.Should().BeTrue();
        });
    }

    /// <summary>Moves the clock to the next due poll (never backwards), optionally further, and runs one tick.</summary>
    private static async Task<bool> PollAsync(Rig rig, TimeSpan? after = null, LetterboxdPoller? poller = null)
    {
        var state = await StateAsync(rig.Host);
        if (state?.NextPollAt is { } next && next > rig.Host.Clock.GetUtcNow())
            rig.Host.Clock.SetUtcNow(next);
        if (after is { } a)
            rig.Host.Clock.Advance(a);
        return await (poller ?? rig.Host.Services.GetRequiredService<LetterboxdPoller>()).TickAsync(Ct);
    }

    private static async Task DeliverAsync(TestHost host)
    {
        var processor = host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    private static Task<LetterboxdSourceStateEntity?> StateAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<LetterboxdSourceStateEntity>().AsNoTracking().FirstOrDefaultAsync(Ct));

    private static Task<List<OutboxMessageEntity>> OutboxAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "letterboxd").OrderBy(o => o.Id).ToListAsync(Ct));

    private static Task<List<LetterboxdEntryEntity>> EntriesAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<LetterboxdEntryEntity>().AsNoTracking().OrderBy(e => e.ViewingId).ToListAsync(Ct));

    private static Task UpdateConfigAsync(TestHost host, Action<LetterboxdGuildConfigEntity> change) =>
        host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            change(await db.Set<LetterboxdGuildConfigEntity>().SingleAsync(c => c.GuildId == Guild.Value, Ct));
            await db.SaveChangesAsync(Ct);
        });

    private static int FeedRequests(Rig rig) => rig.Feed.Requests.Count;

    /// <summary>Old history for the baseline: a rated entry and a review, both long before the test.</summary>
    private static readonly LbItem[] History =
    [
        new(100, "Old Rated", Start.AddDays(-20), Rating: "3.0"),
        new(101, "Old Review", Start.AddDays(-10), Rating: "4.0", Review: "<p>Old words.</p>"),
    ];

    private static async Task<Rig> BaselinedAsync(string mode = "Live", Dictionary<string, string?>? extra = null, Action<IServiceCollection>? replace = null)
    {
        var rig = await RigAsync(mode, extra, replace: replace);
        rig.Feed.Serve(History);
        (await PollAsync(rig)).Should().BeTrue();
        (await OutboxAsync(rig.Host)).Should().BeEmpty();
        return rig;
    }

    private static DateTimeOffset Now(Rig rig) => rig.Host.Clock.GetUtcNow();

    // ---------- 1-5, 13-14: baseline and the three behaviours ----------

    [Fact]
    public async Task The_first_feed_is_a_baseline_and_nothing_of_the_history_is_posted()
    {
        await using var rig = await RigAsync();
        rig.Feed.ServeXml(LetterboxdFeeds.Recorded);
        (await PollAsync(rig)).Should().BeTrue();
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().BeEmpty("existing ratings, reviews and rewatches are never dumped into the channel");
        (await EntriesAsync(rig.Host)).Should().HaveCount(7).And.OnlyContain(e => e.Baseline && e.EligibleAt == null);
        var state = (await StateAsync(rig.Host))!;
        state.BaselineAt.Should().NotBeNull();
        state.MaxItemCount.Should().Be(7);
        state.LastMode.Should().Be((int)LetterboxdMode.Live);

        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty("re-reading the same feed changes nothing");
    }

    [Fact]
    public async Task Rated_review_unrated_review_rated_entry_and_unrated_rewatch_each_get_exactly_one_card_and_a_plain_watch_none()
    {
        await using var rig = await BaselinedAsync();
        var now = Now(rig).AddMinutes(15);
        rig.Feed.Serve([
            new(200, "Rated Review", now.AddMinutes(-5), Rating: "4.5", Review: "<p>Great.</p>"),
            new(201, "Unrated Review", now.AddMinutes(-4), Rating: null, Review: "<p>No stars from me.</p>"),
            new(202, "Rated Entry", now.AddMinutes(-3), Rating: "2.0"),
            new(203, "World War Z", now.AddMinutes(-2), Rating: null, Rewatch: true, Year: 2013, Slug: "world-war-z"),
            new(204, "Plain Watch", now.AddMinutes(-1), Rating: null, Rewatch: false),
            new(205, "All Three", now, Rating: "3.5", Rewatch: true, Review: "<p>Again, better.</p>"),
            .. History,
        ]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var messages = rig.Host.Transport.Messages;
        messages.Should().HaveCount(5, "the plain first watch without rating or review gets no card");
        messages.Should().OnlyContain(m => m.Channel == Channel && !m.Pinged && !m.Message.Mentions.PingsAnything);
        var byFilm = messages.ToDictionary(m => m.Message.Embed!.Description!.Split('\n')[0]);
        byFilm["**Rated Review** (2020)"].Message.Embed!.Title.Should().Be("🎬 LordToro değerlendirdi");
        byFilm["**Rated Review** (2020)"].Message.Embed!.Fields.Single(f => f.Name == "Puan").Value.Should().Be("★★★★½ · 4,5/5");
        var unrated = byFilm["**Unrated Review** (2020)"].Message.Embed!;
        unrated.Title.Should().Be("🎬 LordToro değerlendirdi");
        unrated.Fields.Should().NotContain(f => f.Name == "Puan", "no fake rating for an unrated review");
        unrated.Description.Should().Contain("||No stars from me.||");
        byFilm["**Rated Entry** (2020)"].Message.Embed!.Title.Should().Be("⭐ LordToro puan verdi");
        var wwz = byFilm["**World War Z** (2013)"].Message.Embed!;
        wwz.Title.Should().Be("🔁 LordToro yeniden izledi");
        wwz.Fields.Select(f => f.Name).Should().Equal("Yeniden izleme", "İzleme tarihi");
        wwz.Description.Should().NotContain("İnceleme");
        var all = byFilm["**All Three** (2020)"].Message.Embed!;
        all.Title.Should().Be("🎬 LordToro değerlendirdi", "rating + review + rewatch in one entry is ONE card");
        all.Fields.Select(f => f.Name).Should().Equal("Puan", "Yeniden izleme", "İzleme tarihi");
        (await EntriesAsync(rig.Host)).Single(e => e.ViewingId == 204).EligibleAt.Should().BeNull();
    }

    [Fact]
    public async Task A_new_rewatch_of_an_already_posted_film_is_its_own_card_and_the_old_card_is_untouched()
    {
        await using var rig = await BaselinedAsync();
        var first = new LbItem(300, "Same Film", Now(rig).AddMinutes(10), Rating: "3.0", Slug: "same-film");
        rig.Feed.Serve([first, .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var second = new LbItem(301, "Same Film", Now(rig).AddMinutes(10), Rating: null, Rewatch: true, Slug: "same-film", Index: 1);
        rig.Feed.Serve([second, first, .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var messages = rig.Host.Transport.Messages;
        messages.Should().HaveCount(2, "same film, another entry: another card (film identity is not entry identity)");
        messages[0].Edits.Should().BeEmpty();
        var rewatch = messages[1].Message.Embed!;
        rewatch.Title.Should().Be("🔁 LordToro yeniden izledi");
        rewatch.Url.Should().Be("https://letterboxd.com/lordtoro/film/same-film/1/");
        rewatch.Fields.Should().NotContain(f => f.Name == "Puan", "the earlier entry's rating is never carried over to the rewatch");
    }

    [Fact]
    public async Task Two_entries_of_the_same_film_on_the_same_day_stay_two_cards()
    {
        await using var rig = await BaselinedAsync();
        var day = new DateOnly(2026, 10, 29);
        rig.Feed.Serve([
            new(310, "Twice", Now(rig).AddMinutes(5), Rating: "2.0", Slug: "twice", Watched: day),
            new(311, "Twice", Now(rig).AddMinutes(6), Rating: "2.0", Rewatch: true, Slug: "twice", Index: 1, Watched: day),
            .. History,
        ]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().HaveCount(2, "film + date + rating is not an identity");
    }

    // ---------- 9-11: edits ----------

    [Fact]
    public async Task A_review_added_to_a_posted_entry_edits_the_same_message_and_creates_no_second_card()
    {
        await using var rig = await BaselinedAsync();
        var published = Now(rig).AddMinutes(10);
        rig.Feed.Serve([new LbItem(400, "Edited Film", published, Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Message.Embed!.Title.Should().Be("⭐ LordToro puan verdi");

        // watch-400 → review-400: the same viewing id (only the guid kind changes).
        rig.Feed.Serve([new LbItem(400, "Edited Film", published, Rating: "3.0", Review: "<p>Words added later.</p>"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var after = rig.Host.Transport.Messages.Should().ContainSingle("a review added later is not a new watch").Subject;
        after.Edits.Should().ContainSingle();
        after.Edits[0].Embed!.Title.Should().Be("🎬 LordToro değerlendirdi");
        after.Edits[0].Embed!.Description.Should().Contain("||Words added later.||");
        after.Edits[0].Mentions.PingsAnything.Should().BeFalse();
        (await OutboxAsync(rig.Host)).Should().ContainSingle();
    }

    [Fact]
    public async Task Rating_changes_and_rewatch_corrections_edit_the_same_card_and_unchanged_rounds_do_not()
    {
        await using var rig = await BaselinedAsync();
        var published = Now(rig).AddMinutes(10);
        rig.Feed.Serve([new LbItem(410, "Changing", published, Rating: "3.5"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Feed.Serve([new LbItem(410, "Changing", published, Rating: "4.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Feed.Serve([new LbItem(410, "Changing", published, Rating: "4.0", Rewatch: true), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        await PollAsync(rig);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().HaveCount(2, "one edit per real change; repeated identical rounds (new ObservedAt) edit nothing");
        message.Edits[0].Embed!.Fields.Single(f => f.Name == "Puan").Value.Should().Be("★★★★ · 4/5");
        message.Edits[1].Embed!.Title.Should().Be("🔁 LordToro yeniden izledi");
        rig.Host.Transport.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_rating_removed_from_a_posted_entry_edits_the_card_without_a_rating_never_to_zero()
    {
        await using var rig = await BaselinedAsync();
        var published = Now(rig).AddMinutes(10);
        rig.Feed.Serve([new LbItem(420, "Unrated Later", published, Rating: "2.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Feed.Serve([new LbItem(420, "Unrated Later", published, Rating: null), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var edit = rig.Host.Transport.Messages.Single().Edits.Should().ContainSingle().Subject.Embed!;
        edit.Title.Should().Be("🎞️ LordToro izledi");
        edit.Fields.Should().NotContain(f => f.Name == "Puan");
    }

    [Fact]
    public async Task Baseline_entries_get_a_card_only_for_a_later_member_action_never_for_metadata_changes()
    {
        await using var rig = await BaselinedAsync();
        // Letterboxd renames the film and changes the poster version: metadata only → nothing posted.
        rig.Feed.Serve([new LbItem(100, "Old Rated (Director's Cut)", Start.AddDays(-20), Rating: "3.0", Year: 2021), History[1]]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty();

        // The member adds a review to the old entry today: a new review → a card (old watched/published date or not).
        rig.Feed.Serve([new LbItem(100, "Old Rated (Director's Cut)", Start.AddDays(-20), Rating: "3.0", Year: 2021, Review: "<p>Rewritten today.</p>"), History[1]]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        var card = rig.Host.Transport.Messages.Should().ContainSingle().Subject.Message.Embed!;
        card.Title.Should().Be("🎬 LordToro değerlendirdi");
        card.Description.Should().Contain("||Rewritten today.||");
    }

    // ---------- 15, 22: restarts ----------

    [Fact]
    public async Task A_restart_neither_resets_the_baseline_nor_resends_and_a_pending_card_survives_it()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(500, "Before Restart", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        (await OutboxAsync(rig.Host)).Should().ContainSingle(o => o.Status == OutboxStatus.Pending, "staged, not yet delivered");

        var restarted = ActivatorUtilities.CreateInstance<LetterboxdPoller>(rig.Host.Services);
        await PollAsync(rig, poller: restarted);
        await PollAsync(rig, poller: restarted);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();
        (await OutboxAsync(rig.Host)).Should().ContainSingle();
        (await StateAsync(rig.Host))!.ModeSince.Should().Be(Start, "a normal restart is not a mode change");
    }

    // ---------- Off / DryRun / Live: the mode activation boundary ----------

    /// <summary>
    /// A real restart into another mode: a second host on the SAME database file (the first one stays undisposed until the
    /// test ends; nothing else touches the file meanwhile), with its own scripted feed and the same guild channel.
    /// </summary>
    private static async Task<Rig> RestartAsync(Rig previous, string mode, DateTimeOffset at)
    {
        var feed = new LetterboxdFeedServer();
        var host = await TestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Bot:DataDirectory"] = previous.Host.Directory,
            ["Letterboxd:Mode"] = mode,
            ["Letterboxd:MaxCardsPerRound"] = "10",
        }, at, services => services.AddHttpClient(LetterboxdRssClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(feed)));
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild));
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
        return new Rig(host, feed);
    }

    private static Task<List<LetterboxdDeliveryEntity>> DeliveriesAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<LetterboxdDeliveryEntity>().AsNoTracking().OrderBy(d => d.ViewingId).ToListAsync(Ct));

    private static string Film(OutgoingMessage message) => message.Embed!.Description!.Split('\n')[0];

    [Fact]
    public async Task DryRun_to_Live_posts_entries_dated_after_activation_even_after_a_failed_first_request_and_a_restart()
    {
        await using var dry = await RigAsync("DryRun");
        dry.Feed.Serve(History);
        await PollAsync(dry);
        var dryEntry = new LbItem(700, "Seen In DryRun", Now(dry).AddMinutes(5), Rating: "3.0");
        dry.Feed.Serve([dryEntry, .. History]);
        await PollAsync(dry);
        await DeliverAsync(dry.Host);
        (await OutboxAsync(dry.Host)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Simulated);

        // Restart into Live at t0 (whole seconds); the activation is stored before the first request, which fails.
        var t0 = Now(dry).AddHours(1);
        await using var live = await RestartAsync(dry, "Live", t0);
        live.Feed.Respond = _ => LetterboxdFeeds.Status(HttpStatusCode.ServiceUnavailable);
        (await live.Host.Services.GetRequiredService<LetterboxdPoller>().TickAsync(Ct)).Should().BeTrue();
        var state = (await StateAsync(live.Host))!;
        state.LastMode.Should().Be((int)LetterboxdMode.Live);
        state.ModeSince.Should().Be(t0, "the boundary is the activation, not the first successful read");
        state.LastSuccessAt.Should().BeBefore(t0);

        // Same Live setting again (poller restart): not a new transition.
        var restarted = ActivatorUtilities.CreateInstance<LetterboxdPoller>(live.Host.Services);
        live.Feed.Serve([
            new LbItem(704, "After Activation", t0.AddMinutes(10), Rating: "4.0", Review: "<p>New.</p>"),
            new LbItem(703, "Exactly At Activation", t0, Rewatch: true),
            new LbItem(702, "One Second Before", t0.AddSeconds(-1), Rating: "2.0"),
            new LbItem(701, "Between Last DryRun Read And Activation", t0.AddMinutes(-10), Rating: "2.0"),
            dryEntry, .. History,
        ]);
        await PollAsync(live, after: TimeSpan.FromMinutes(30), poller: restarted);
        await DeliverAsync(live.Host);

        (await StateAsync(live.Host))!.ModeSince.Should().Be(t0, "a restart with the same mode keeps the activation");
        live.Host.Transport.Messages.Select(m => Film(m.Message)).Should().BeEquivalentTo(["**After Activation** (2020)", "**Exactly At Activation** (2020)"],
            "dated at/after t0 = new; before t0 (also one second before) = history of the previous mode; the DryRun card is never sent live");
        var deliveries = await DeliveriesAsync(live.Host);
        deliveries.Should().Contain(d => d.ViewingId == 700 && d.DryRun, "the dry-run history is kept, not reset");
        deliveries.Where(d => !d.DryRun).Select(d => d.ViewingId).Should().BeEquivalentTo([703L, 704L]);
        (await EntriesAsync(live.Host)).Single(e => e.ViewingId == 701).Baseline.Should().BeTrue();

        // Later rounds in the same mode: only genuinely new entries.
        live.Feed.Serve([new LbItem(705, "Later", Now(live).AddMinutes(20), Rating: "3.0"), new LbItem(704, "After Activation", t0.AddMinutes(10), Rating: "4.0", Review: "<p>New.</p>"), dryEntry, .. History]);
        await PollAsync(live);
        await DeliverAsync(live.Host);
        live.Host.Transport.Messages.Should().HaveCount(3);
    }

    [Fact]
    public async Task Off_to_Live_skips_what_happened_while_off_and_cannot_date_changes_of_known_entries()
    {
        await using var first = await BaselinedAsync();
        var posted = new LbItem(800, "Posted Before Off", Now(first).AddMinutes(5), Rating: "3.0");
        first.Feed.Serve([posted, .. History]);
        await PollAsync(first);
        await DeliverAsync(first.Host);
        first.Host.Transport.Messages.Should().ContainSingle();

        // A run with Letterboxd:Mode=Off records the mode at startup, without a request.
        var offAt = Now(first).AddHours(1);
        await using var off = await RestartAsync(first, "Off", offAt);
        (await off.Host.Services.GetRequiredService<LetterboxdPoller>().TickAsync(Ct)).Should().BeFalse();
        off.Feed.Requests.Should().BeEmpty();
        (await StateAsync(off.Host))!.LastMode.Should().Be((int)LetterboxdMode.Off);

        var t0 = offAt.AddDays(2);
        await using var live = await RestartAsync(first, "Live", t0);
        live.Feed.Serve([
            new LbItem(802, "After Live Started", t0.AddMinutes(5), Rating: "4.0"),
            new LbItem(801, "Made While Off", t0.AddHours(-3), Rating: "4.0"),
            posted with { Rating = "4.5" },                                                 // edited while off: the posted card is edited
            History[0] with { Review = "<p>Review added while off (or after t0?)</p>" },     // known, no card: cannot be dated → no card
            History[1],
        ]);
        await PollAsync(live);
        (await OutboxAsync(live.Host)).Should().ContainSingle(o => o.SourceKey == LetterboxdPlanner.SourceKey("lordtoro", 800) && o.EditPending,
            "an existing card still follows its entry (edit, no second card)");
        (await EntriesAsync(live.Host)).Single(e => e.ViewingId == 100).EligibleAt.Should().BeNull("a change seen in the first read of a mode opens no card");
        (await EntriesAsync(live.Host)).Single(e => e.ViewingId == 801).Baseline.Should().BeTrue("dated before Live started");

        await DeliverAsync(live.Host);
        live.Host.Transport.Messages.Should().ContainSingle("only the entry dated after activation is new").Which.Message.Embed!.Description.Should().StartWith("**After Live Started**");
    }

    [Fact]
    public async Task On_a_fresh_install_the_history_is_not_posted_but_an_entry_dated_after_activation_survives_a_failed_first_request()
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => LetterboxdFeeds.Status(HttpStatusCode.BadGateway);
        await PollAsync(rig);
        var activated = (await StateAsync(rig.Host))!.ModeSince!.Value;
        (await StateAsync(rig.Host))!.BaselineAt.Should().BeNull();

        rig.Feed.Serve([new LbItem(900, "Logged After Activation", activated.AddMinutes(20), Rating: "3.5"), .. History]);
        await PollAsync(rig, after: TimeSpan.FromHours(1));
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().ContainSingle().Which.Message.Embed!.Description.Should().StartWith("**Logged After Activation**");
        (await EntriesAsync(rig.Host)).Where(e => e.ViewingId is 100 or 101).Should().OnlyContain(e => e.Baseline, "the history is never dumped");
    }

    [Fact]
    public async Task Off_records_the_mode_without_any_request()
    {
        await using var rig = await RigAsync("Off");
        await rig.Host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<LetterboxdSourceStateEntity>().Add(new LetterboxdSourceStateEntity { Key = LetterboxdPlanner.StateKey("lordtoro"), LastMode = (int)LetterboxdMode.Live });
            await db.SaveChangesAsync(Ct);
        });

        (await rig.Host.Services.GetRequiredService<LetterboxdPoller>().TickAsync(Ct)).Should().BeFalse();
        FeedRequests(rig).Should().Be(0);
        var state = (await StateAsync(rig.Host))!;
        state.LastMode.Should().Be((int)LetterboxdMode.Off);
        state.ModeSince.Should().Be(Start);
    }

    [Fact]
    public async Task DryRun_only_simulates_and_its_delivery_records_never_count_as_live()
    {
        await using var rig = await RigAsync("DryRun");
        rig.Feed.Serve(History);
        await PollAsync(rig);
        rig.Feed.Serve([new LbItem(700, "Dry Film", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().BeEmpty("DryRun never reaches Discord");
        var row = (await OutboxAsync(rig.Host)).Should().ContainSingle().Subject;
        row.Status.Should().Be(OutboxStatus.Simulated);
        row.IsDryRun.Should().BeTrue();
        await rig.Host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<ToroDbContext>().Set<LetterboxdDeliveryEntity>().SingleAsync(Ct)).DryRun.Should().BeTrue());
    }

    [Fact]
    public async Task The_delivery_policy_cancels_by_mode_before_anything_else()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Start);
        var policy = new LetterboxdDeliveryPolicy(null!, Microsoft.Extensions.Options.Options.Create(new LetterboxdOptions { Mode = LetterboxdMode.Off }),
            Microsoft.Extensions.Options.Options.Create(new DeliveryOptions { Mode = DeliveryMode.Send }), clock);
        (await policy.CanDeliverAsync(Guild, Channel, LetterboxdPlanner.EntryKind, Ct)).Should().BeOfType<DeliveryDecision.Cancel>()
            .Which.Reason.Should().Be("letterboxd_mode_off");
        var dry = new LetterboxdDeliveryPolicy(null!, Microsoft.Extensions.Options.Options.Create(new LetterboxdOptions { Mode = LetterboxdMode.Live }),
            Microsoft.Extensions.Options.Options.Create(new DeliveryOptions { Mode = DeliveryMode.DryRun }), clock);
        (await dry.CanDeliverAsync(Guild, Channel, LetterboxdPlanner.EntryKind, Ct)).Should().BeOfType<DeliveryDecision.Cancel>()
            .Which.Reason.Should().Be("letterboxd_mode_dry_run", "Live on a host that does not send is a dry run");
        (await dry.CanDeliverAsync(Guild, Channel, "something-else", Ct)).Should().BeOfType<DeliveryDecision.Cancel>().Which.Reason.Should().Be("unknown_kind");
    }

    // ---------- 17-18: failures, partial answers, validators ----------

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, FeedOutcome.Blocked)]
    [InlineData(HttpStatusCode.TooManyRequests, FeedOutcome.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, FeedOutcome.NotFound)]
    [InlineData(HttpStatusCode.MovedPermanently, FeedOutcome.Redirected)]
    [InlineData(HttpStatusCode.BadGateway, FeedOutcome.ServerError)]
    public async Task A_failed_first_answer_establishes_no_baseline_and_backs_off(HttpStatusCode status, FeedOutcome expected)
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => LetterboxdFeeds.Status(status, status == HttpStatusCode.TooManyRequests ? TimeSpan.FromHours(2) : null);
        await PollAsync(rig);

        var state = (await StateAsync(rig.Host))!;
        ((FeedOutcome)state.LastOutcome).Should().Be(expected);
        state.BaselineAt.Should().BeNull();
        state.ConsecutiveFailures.Should().Be(1);
        var wait = state.NextPollAt!.Value - Now(rig);
        if (expected is FeedOutcome.Blocked or FeedOutcome.NotFound)
            wait.Should().Be(LetterboxdPoller.BlockedBackoff, "a refusal waits hours and is never worked around");
        else if (expected == FeedOutcome.RateLimited)
            wait.Should().Be(TimeSpan.FromHours(2), "Retry-After is honoured");
        else
            wait.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMinutes(15));
        (await PollAsync(rig, after: TimeSpan.Zero)).Should().BeTrue();
        rig.Host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await rig.Host.Services.GetRequiredService<LetterboxdPoller>().TickAsync(Ct)).Should().BeFalse("not due: the backoff is stored, no extra request");
    }

    [Fact]
    public async Task A_200_html_page_and_a_broken_answer_after_the_baseline_change_nothing_and_delete_nothing()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(800, "Posted", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        var before = await EntriesAsync(rig.Host);

        rig.Feed.Respond = _ => LetterboxdFeeds.Ok("<html><title>Just a moment...</title></html>", "text/html");
        await PollAsync(rig);
        ((FeedOutcome)(await StateAsync(rig.Host))!.LastOutcome).Should().Be(FeedOutcome.WrongContentType);
        rig.Feed.Respond = _ => LetterboxdFeeds.Ok("<rss><channel><link>https://letterboxd.com/lordtoro/</link><item>", "application/rss+xml");
        await PollAsync(rig);
        ((FeedOutcome)(await StateAsync(rig.Host))!.LastOutcome).Should().Be(FeedOutcome.Malformed);
        rig.Feed.Serve([]); // a well-formed empty feed: not "everything was deleted"
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        (await EntriesAsync(rig.Host)).Select(e => (e.ViewingId, e.ContentHash, e.RatingHalves)).Should().Equal(before.Select(e => (e.ViewingId, e.ContentHash, e.RatingHalves)));
        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Edits.Should().BeEmpty("an entry missing from the feed is not deleted or 'unrated'");
    }

    [Fact]
    public async Task An_entry_that_leaves_the_feed_window_is_kept_and_its_card_is_never_touched()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(810, "Will Scroll Away", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Feed.Serve([new LbItem(811, "Newer", Now(rig).AddMinutes(20), Rating: "2.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().HaveCount(2);
        rig.Host.Transport.Messages[0].Edits.Should().BeEmpty();
        (await EntriesAsync(rig.Host)).Should().Contain(e => e.ViewingId == 810);
    }

    [Fact]
    public async Task Not_modified_before_a_baseline_proves_nothing_and_after_it_keeps_the_state()
    {
        await using var rig = await RigAsync();
        rig.Feed.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        await PollAsync(rig);
        var state = (await StateAsync(rig.Host))!;
        ((FeedOutcome)state.LastOutcome).Should().Be(FeedOutcome.Malformed, "a 304 without stored content is not a baseline");
        state.BaselineAt.Should().BeNull();

        rig.Feed.Respond = _ => LetterboxdFeeds.Ok(LetterboxdFeeds.Rss(History), etag: "\"v1\"");
        await PollAsync(rig);
        (await StateAsync(rig.Host))!.ETag.Should().Be("\"v1\"");
        rig.Feed.Respond = request =>
        {
            request.Headers.GetValues("If-None-Match").Should().Equal("\"v1\"");
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        };
        await PollAsync(rig);
        state = (await StateAsync(rig.Host))!;
        ((FeedOutcome)state.LastOutcome).Should().Be(FeedOutcome.NotModified);
        state.ConsecutiveFailures.Should().Be(0);
        (await EntriesAsync(rig.Host)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_round_that_fails_while_storing_keeps_nothing_and_the_next_round_posts_the_entry_once()
    {
        var failOnce = new FailOnceOutbox();
        await using var rig = await BaselinedAsync(replace: s => s.AddScoped<INotificationOutbox>(sp => failOnce.Wrap(ActivatorUtilities.CreateInstance<NotificationOutbox>(sp))));
        rig.Feed.Respond = _ => LetterboxdFeeds.Ok(LetterboxdFeeds.Rss([new LbItem(900, "Atomic", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]), etag: "\"new\"");
        failOnce.Armed = true;
        await FluentActions.Awaiting(() => PollAsync(rig)).Should().ThrowAsync<InvalidOperationException>();

        (await EntriesAsync(rig.Host)).Should().NotContain(e => e.ViewingId == 900, "the entry, validators and outbox row are one transaction");
        (await StateAsync(rig.Host))!.ETag.Should().BeNull("validators never advance past content that was not stored");
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_send_is_retried_and_still_yields_one_message()
    {
        await using var rig = await BaselinedAsync();
        rig.Host.Transport.ScriptSend(() => new SendOutcome.Transient("boom"));
        rig.Feed.Serve([new LbItem(910, "Retry", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty();
        rig.Host.Clock.Advance(TimeSpan.FromHours(1));
        await DeliverAsync(rig.Host);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();
    }

    // ---------- 19-20: dates and coverage ----------

    [Fact]
    public async Task An_entry_logged_today_for_an_old_watched_date_is_posted()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(1000, "Watched Years Ago", Now(rig).AddMinutes(5), Rating: "4.0", Watched: new DateOnly(2019, 5, 1)), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle().Which.Message.Embed!.Fields.Single(f => f.Name == "İzleme tarihi").Value.Should().Be("1 Mayıs 2019");
    }

    [Fact]
    public async Task Entries_published_before_the_catch_up_window_are_not_posted_after_an_outage()
    {
        await using var rig = await BaselinedAsync();
        rig.Host.Clock.Advance(TimeSpan.FromHours(30));
        rig.Feed.Serve([
            new LbItem(1100, "During Outage", Now(rig).AddHours(-28), Rating: "3.0"),
            new LbItem(1101, "Recent", Now(rig).AddHours(-1), Rating: "3.0"),
            .. History,
        ]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle().Which.Message.Embed!.Description.Should().StartWith("**Recent**");
    }

    [Fact]
    public async Task A_full_window_of_unknown_entries_is_reported_as_a_coverage_gap()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(1200, "New A", Now(rig).AddMinutes(5), Rating: "3.0"), new LbItem(1201, "New B", Now(rig).AddMinutes(6), Rating: "3.0")]);
        await PollAsync(rig);

        var state = (await StateAsync(rig.Host))!;
        state.CoverageGapAt.Should().NotBeNull("2 items (the observed window) and none known: entries in between may be lost");
        var (_, checks) = await rig.Host.InScopeAsync(sp => sp.GetRequiredService<LetterboxdConfigService>().DoctorAsync(TestHost.Admin(Guild), Ct));
        checks.Should().Contain(c => c.DetailKey == "letterboxd.doctor.coverage_gap_value" && c.State == LetterboxdCheckState.Warning);
        checks.Should().NotContain(c => c.LabelKey == "letterboxd.doctor.stale", "a potential gap is not a stale source");
    }

    // ---------- 21-22: safety, pause, channel and account ----------

    [Fact]
    public async Task Hostile_review_text_neither_pings_nor_escapes_the_spoiler()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(1300, "@everyone **bold**", Now(rig).AddMinutes(5), Review: "<p>||leak|| @here &lt;@&amp;1&gt; [x](https://evil.example)</p>"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        message.Pinged.Should().BeFalse();
        var description = message.Message.Embed!.Description!;
        DiscordText.RawMentionPattern().IsMatch(description).Should().BeFalse();
        description.Should().NotContain("https://evil.example");
        var spoiler = description[description.IndexOf("||", StringComparison.Ordinal)..];
        spoiler[2..^2].Should().NotContain("||");
    }

    [Fact]
    public async Task Pause_cancels_queued_cards_and_entries_of_the_paused_period_are_skipped_after_resume()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(1400, "Queued Then Paused", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        (await rig.Host.InScopeAsync(sp => sp.GetRequiredService<LetterboxdConfigService>().SetPausedAsync(TestHost.Admin(Guild), true, Ct))).Succeeded.Should().BeTrue();
        await DeliverAsync(rig.Host);
        (await OutboxAsync(rig.Host)).Should().ContainSingle().Which.Status.Should().Be(OutboxStatus.Cancelled);

        var requests = FeedRequests(rig);
        await PollAsync(rig, after: TimeSpan.FromHours(1));
        FeedRequests(rig).Should().Be(requests, "no guild receives cards: no request");

        rig.Feed.Serve([new LbItem(1401, "During Pause", Now(rig).AddMinutes(-5), Rating: "3.0"), new LbItem(1400, "Queued Then Paused", Start, Rating: "3.0"), .. History]);
        (await rig.Host.InScopeAsync(sp => sp.GetRequiredService<LetterboxdConfigService>().SetPausedAsync(TestHost.Admin(Guild), false, Ct))).Succeeded.Should().BeTrue();
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty("the paused period is skipped, the cancelled card stays cancelled");
    }

    [Fact]
    public async Task A_channel_change_never_resends_and_cancels_cards_queued_for_the_old_channel()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(1500, "Sent", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Feed.Serve([new LbItem(1501, "Queued", Now(rig).AddMinutes(20), Rating: "3.0"), new LbItem(1500, "Sent", Now(rig).AddMinutes(-10), Rating: "3.0"), .. History]);
        await PollAsync(rig);

        rig.Host.Guilds.SetChannel(Guild, Channel2, new BotChannelAccess(true, true, GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks));
        (await rig.Host.InScopeAsync(sp => sp.GetRequiredService<LetterboxdConfigService>().SetChannelAsync(TestHost.Admin(Guild), Channel2.Value, Ct))).Succeeded.Should().BeTrue();
        await DeliverAsync(rig.Host);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().ContainSingle("nothing is re-sent to the new channel").Which.Channel.Should().Be(Channel);
        (await OutboxAsync(rig.Host)).Single(o => o.Status != OutboxStatus.Sent).Status.Should().Be(OutboxStatus.Cancelled);
    }

    [Fact]
    public async Task Another_followed_account_stops_delivery_of_queued_cards()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Serve([new LbItem(1600, "Queued", Now(rig).AddMinutes(5), Rating: "3.0"), .. History]);
        await PollAsync(rig);
        await UpdateConfigAsync(rig.Host, c => c.Account = "someoneelse");
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().BeEmpty();
        var row = (await OutboxAsync(rig.Host)).Single();
        row.Status.Should().Be(OutboxStatus.Cancelled);
        (await rig.Host.Services.GetRequiredService<LetterboxdPoller>().TickAsync(Ct)).Should().BeFalse("no guild follows the configured account");
    }

    [Fact]
    public async Task A_disabled_module_requests_nothing_and_re_enabling_starts_a_new_window()
    {
        await using var rig = await RigAsync(configure: false);
        await ConfigureGuildAsync(rig.Host, Guild, Channel, enable: false);
        rig.Feed.Serve(History);
        (await PollAsync(rig)).Should().BeFalse();
        FeedRequests(rig).Should().Be(0);

        (await rig.Host.InScopeAsync(sp => sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "letterboxd", true, Ct))).Succeeded.Should().BeTrue();
        await PollAsync(rig);
        FeedRequests(rig).Should().Be(1);
        (await OutboxAsync(rig.Host)).Should().BeEmpty("the first read is the baseline");
    }

    // ---------- retention ----------

    [Fact]
    public async Task Excerpts_expire_ids_are_pruned_behind_a_watermark_and_a_pruned_entry_that_reappears_is_never_sent()
    {
        await using var rig = await BaselinedAsync(extra: new() { ["Letterboxd:DedupRetentionDays"] = "60", ["Letterboxd:TextRetentionDays"] = "30" });
        rig.Feed.Serve([new LbItem(1700, "Later", Now(rig).AddMinutes(5), Rating: "3.0")]);
        rig.Host.Clock.Advance(TimeSpan.FromDays(40));
        await PollAsync(rig);
        (await EntriesAsync(rig.Host)).Single(e => e.ViewingId == 101).ReviewExcerpt.Should().BeNull("text retention");
        rig.Host.Clock.Advance(TimeSpan.FromDays(30));
        await PollAsync(rig);
        (await EntriesAsync(rig.Host)).Should().NotContain(e => e.ViewingId == 100 || e.ViewingId == 101);
        (await StateAsync(rig.Host))!.PrunedBelowViewingId.Should().Be(101);

        rig.Feed.Serve([new LbItem(101, "Old Review", Now(rig), Rating: "4.0", Review: "<p>Old words.</p>"), new LbItem(1700, "Later", Now(rig).AddMinutes(-5), Rating: "3.0")]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        (await EntriesAsync(rig.Host)).Single(e => e.ViewingId == 101).Baseline.Should().BeTrue();
        rig.Host.Transport.Messages.Should().NotContain(m => m.Message.Embed!.Description!.StartsWith("**Old Review**", StringComparison.Ordinal));
    }

    // ---------- an entry without a card becoming worth one ----------

    public enum Change
    {
        ReviewAdded,
        RatingAdded,
        RewatchSet,
        MetadataOnly,
    }

    [Theory]
    [InlineData(Change.ReviewAdded, false)]
    [InlineData(Change.RatingAdded, false)]
    [InlineData(Change.RewatchSet, false)]
    [InlineData(Change.MetadataOnly, false)]
    [InlineData(Change.ReviewAdded, true)]
    [InlineData(Change.RatingAdded, true)]
    [InlineData(Change.RewatchSet, true)]
    [InlineData(Change.MetadataOnly, true)]
    public async Task A_plain_watch_seen_during_live_gets_its_first_card_only_when_the_member_makes_it_worth_one(Change change, bool restart)
    {
        await using var rig = await BaselinedAsync();
        // Seen for the first time during Live, with an OLD watched and published date: no rating, no review, no rewatch.
        var plain = new LbItem(2000, "Plain", Start.AddDays(-3), Rating: null, Rewatch: false, Watched: new DateOnly(2026, 10, 1));
        rig.Feed.Serve([plain, .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().BeEmpty("an unrated first watch without review gets no card");
        (await EntriesAsync(rig.Host)).Single(e => e.ViewingId == 2000).Should().Match<LetterboxdEntryEntity>(e => !e.Baseline && e.EligibleAt == null);

        var poller = restart ? ActivatorUtilities.CreateInstance<LetterboxdPoller>(rig.Host.Services) : null;
        var next = change switch
        {
            Change.ReviewAdded => plain with { Review = "<p>Added later.</p>" },
            Change.RatingAdded => plain with { Rating = "3.5" },
            Change.RewatchSet => plain with { Rewatch = true },
            _ => plain with { Film = "Plain (Renamed)", Year = 2021 },
        };
        rig.Feed.Serve([next, .. History]);
        await PollAsync(rig, poller: poller);
        await DeliverAsync(rig.Host);

        if (change == Change.MetadataOnly)
        {
            rig.Host.Transport.Messages.Should().BeEmpty("a title/year/poster change is not a member action");
            return;
        }

        var message = rig.Host.Transport.Messages.Should().ContainSingle("the FIRST card is created — there is no message to edit").Subject;
        message.Edits.Should().BeEmpty();
        rig.Host.Transport.EditCalls.Should().Be(0);
        message.Message.Embed!.Title.Should().Be(change switch
        {
            Change.ReviewAdded => "🎬 LordToro değerlendirdi",
            Change.RatingAdded => "⭐ LordToro puan verdi",
            _ => "🔁 LordToro yeniden izledi",
        });

        // The same state read again (also after another restart) creates nothing more.
        await PollAsync(rig, poller: ActivatorUtilities.CreateInstance<LetterboxdPoller>(rig.Host.Services));
        await DeliverAsync(rig.Host);
        rig.Host.Transport.Messages.Should().ContainSingle();
    }

    // ---------- identity ----------

    [Fact]
    public async Task A_known_viewing_id_naming_another_film_is_ignored_never_merged_into_the_posted_card()
    {
        await using var rig = await BaselinedAsync();
        var entry = new LbItem(3000, "Film A", Now(rig).AddMinutes(5), Rating: "3.0", Slug: "film-a");
        rig.Feed.Serve([entry, .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        // Same viewing id, another film (other TMDB id and slug): a contradiction, not an edit.
        rig.Feed.Respond = _ => LetterboxdFeeds.Ok(LetterboxdFeeds.Rss([entry with { Film = "Film B", Slug = "film-b", Review = "<p>x</p>" }, .. History])
            .Replace("<tmdb:movieId>3000</tmdb:movieId>", "<tmdb:movieId>3999</tmdb:movieId>", StringComparison.Ordinal));
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Host.Transport.Messages.Should().ContainSingle().Which.Edits.Should().BeEmpty();
        (await EntriesAsync(rig.Host)).Single(e => e.ViewingId == 3000).FilmTitle.Should().Be("Film A");
        (await StateAsync(rig.Host))!.LastConflictCount.Should().Be(1);
        var (_, checks) = await rig.Host.InScopeAsync(sp => sp.GetRequiredService<LetterboxdConfigService>().DoctorAsync(TestHost.Admin(Guild), Ct));
        checks.Should().Contain(c => c.DetailKey == "letterboxd.doctor.conflicts_value");
    }

    [Fact]
    public async Task A_renamed_link_of_the_same_entry_and_film_edits_the_card_and_is_not_a_new_entry()
    {
        await using var rig = await BaselinedAsync();
        var entry = new LbItem(3100, "Old Title", Now(rig).AddMinutes(5), Rating: "3.0", Slug: "old-slug");
        rig.Feed.Serve([entry, .. History]);
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        rig.Feed.Serve([entry with { Film = "New Title", Slug = "new-slug" }, .. History]); // same TMDB id (= the synthetic id)
        await PollAsync(rig);
        await DeliverAsync(rig.Host);

        var message = rig.Host.Transport.Messages.Should().ContainSingle("a new link or title is not a new entry").Subject;
        message.Edits.Should().ContainSingle().Which.Embed!.Url.Should().Be("https://letterboxd.com/lordtoro/film/new-slug/");
        (await EntriesAsync(rig.Host)).Count(e => e.ViewingId == 3100).Should().Be(1);
    }

    // ---------- coverage wording ----------

    [Fact]
    public async Task A_stale_source_is_reported_as_uncertain_coverage_not_as_a_gap_or_a_loss()
    {
        await using var rig = await BaselinedAsync();
        rig.Feed.Respond = _ => LetterboxdFeeds.Status(HttpStatusCode.ServiceUnavailable);
        rig.Host.Clock.Advance(TimeSpan.FromHours(30));
        await PollAsync(rig);

        await rig.Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<LetterboxdConfigService>();
            var (_, status) = await config.StatusAsync(TestHost.Admin(Guild), Ct);
            status!.Stale.Should().BeTrue();
            status.Feed!.CoverageGapAt.Should().BeNull();
            var (_, checks) = await config.DoctorAsync(TestHost.Admin(Guild), Ct);
            checks.Should().ContainSingle(c => c.LabelKey == "letterboxd.doctor.stale" && c.State == LetterboxdCheckState.Warning);
            checks.Should().NotContain(c => c.LabelKey == "letterboxd.doctor.coverage_gap");
            checks.Should().Contain(c => c.DetailKey == "letterboxd.capability.standalone_ratings", "the missing capability is its own line");
        });
    }

    // ---------- admin ----------

    [Fact]
    public async Task Status_doctor_and_preview_require_manage_server_and_show_the_partial_coverage()
    {
        await using var rig = await BaselinedAsync();
        await rig.Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<LetterboxdConfigService>();
            (await config.StatusAsync(TestHost.Member(Guild), Ct)).Status.Should().BeNull();
            (await config.SetChannelAsync(TestHost.Member(Guild), Channel2.Value, Ct)).Succeeded.Should().BeFalse();

            var (_, status) = await config.StatusAsync(TestHost.Admin(Guild), Ct);
            status!.Account.Should().Be("lordtoro");
            status.ChannelId.Should().Be(Channel.Value);
            status.Feed!.BaselineAt.Should().NotBeNull();
            status.Capabilities.Single(c => c.Key == "StandaloneRatings").State.Should().Be(CapabilityState.Blocked);

            var (_, checks) = await config.DoctorAsync(TestHost.Admin(Guild), Ct);
            checks.Should().Contain(c => c.DetailKey == "letterboxd.doctor.coverage_partial" && c.State == LetterboxdCheckState.Warning);
            checks.Should().Contain(c => c.DetailKey == "letterboxd.capability.standalone_ratings" && c.State == LetterboxdCheckState.Warning);
            checks.Should().Contain(c => c.DetailKey == "letterboxd.doctor.channel_ok");
            checks.Should().NotContain(c => c.State == LetterboxdCheckState.Problem);

            var (_, preview) = await config.PreviewAsync(TestHost.Admin(Guild), "tr", Ct);
            preview!.Synthetic.Should().BeFalse("a real stored entry is shown");
            preview.Message.Mentions.Should().Be(MentionPolicy.None);
        });
        rig.Host.Transport.Messages.Should().BeEmpty("admin operations never post");
    }

    [Fact]
    public async Task Configure_reports_missing_channel_permissions_and_doctor_flags_them()
    {
        await using var rig = await RigAsync(configure: false);
        rig.Host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild));
        rig.Host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, GuildPermission.ViewChannel | GuildPermission.SendMessages));
        await rig.Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<LetterboxdConfigService>();
            var saved = await config.SetChannelAsync(TestHost.Admin(Guild), Channel.Value, Ct);
            saved.Succeeded.Should().BeTrue();
            saved.MessageKey.Should().Be("letterboxd.config.saved_missing_permissions");
            var (_, checks) = await config.DoctorAsync(TestHost.Admin(Guild), Ct);
            checks.Should().Contain(c => c.DetailKey == "letterboxd.doctor.channel_permissions" && c.State == LetterboxdCheckState.Problem);
        });
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
                if (owner.Armed && request.Module.Value == "letterboxd")
                {
                    owner.Armed = false;
                    throw new InvalidOperationException("simulated failure while storing the round");
                }

                return inner.StageAsync(request, cancellationToken);
            }
        }
    }
}
