using System.Net;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// Deadlock end to end through the unchanged Updates pipeline (real SQLite, real outbox, the production wiring) against a
/// scripted Steam: first run without spam, the kinds of updates Valve posts (small patches, titled updates, named major
/// updates), hero reveals that never become cards, edits, long change lists, hostile text, outages and rate limits, one
/// request per round, and the three registered games side by side, each in its own channel. Nothing here touches the network.
/// </summary>
public sealed class UpdatesDeadlockTests
{
    private const string Deadlock = "deadlock";
    private static readonly GuildId Guild = UpdatesRig.Guild;
    private static readonly ChannelId Channel = UpdatesRig.Channel;
    private static readonly DateTimeOffset Start = UpdatesRig.Start;
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>What the feed holds on a first run: two earlier patches, a named update and a hero reveal.</summary>
    private static readonly SteamPost[] History =
    [
        DeadlockNews.Post(104, "Listen up, Crumbums! Your King is here.", Start.AddDays(-1), DeadlockNews.HeroReveal),
        DeadlockNews.Post(103, "City Never Sleeps", Start.AddDays(-4), DeadlockNews.Named("cityneversleeps")),
        DeadlockNews.Minor(102, Start.AddDays(-17)),
        DeadlockNews.Post(101, "Gameplay Update - 04-30-2026", Start.AddDays(-150), DeadlockNews.MinorNotes),
    ];

    private static void Serve(UpdatesRig rig, params SteamPost[] newer) => rig.Steam.Serve([.. newer.Reverse(), .. History], appId: DeadlockNews.AppId);

    private static async Task<UpdatesRig> BaselinedAsync(params string[] games)
    {
        var rig = await UpdatesRig.CreateAsync(configure: false);
        await rig.ConfigureGuildAsync(Guild, Channel, true, games.Length == 0 ? [Deadlock] : games);
        Serve(rig);
        (await rig.PollAsync()).Should().Be(1);
        (await rig.StateAsync(Deadlock))!.BaselineAt.Should().NotBeNull();
        return rig;
    }

    private static string Link(long gid) => SteamNews.CardUrl(gid.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ---------- first run, new updates ----------

    [Fact]
    public async Task The_first_run_posts_nothing_and_the_next_patch_is_one_card_with_its_first_changes()
    {
        await using var rig = await BaselinedAsync();
        (await rig.OutboxAsync()).Should().BeEmpty("what the feed already holds is never dumped into the channel");
        var seeded = await rig.ItemsAsync();
        seeded.Select(i => i.ExternalId).Should().BeEquivalentTo(["101", "102", "103", "104"]);
        seeded.Should().OnlyContain(i => i.Baseline && i.Provider == "steam" && i.GameKey == Deadlock);
        var state = await rig.StateAsync(Deadlock);
        (state!.Provider, state.ProviderGameId).Should().Be(("steam", "1422450"));
        rig.Steam.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri.Should()
            .Be("https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid=1422450&count=20&maxlength=0&feeds=steam_community_announcements&format=json");

        var published = rig.Now.AddMinutes(2);
        Serve(rig, DeadlockNews.Minor(110, published));
        await rig.PollAsync();
        await rig.DeliverAsync();

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        (message.Channel, message.Pinged, message.Message.Content).Should().Be((Channel, false, null));
        var embed = message.Message.Embed!;
        embed.Title.Should().Be("🛠️ Deadlock Güncellemesi");
        embed.Description.Should().Be(
            "**Minor Update \\- 10\\-01\\-2026**\n\n**General**\n• Synthetic change one\n• Synthetic change two\n\n" +
            "**Heroes**\n• Hero A: Synthetic ability damage increased from 10 to 12\n• Hero B: Synthetic cooldown reduced from 30s to 28s\n\n" +
            "Yeni Deadlock güncellemesi yayınlandı.\n\n[Steam'de Güncelleme Notlarını Gör](" + Link(110) + ")");
        (embed.Url, embed.Footer, embed.Timestamp, embed.ThumbnailUrl).Should().Be((Link(110), "Kaynak: Steam", published, null));
        var row = (await rig.OutboxAsync()).Should().ContainSingle().Subject;
        (row.SourceKey, row.Kind).Should().Be(("steam:1422450:110", "update:deadlock"));
        var item = (await rig.ItemsAsync()).Single(i => i.ExternalId == "110");
        ((UpdateClassification)item.Classification, item.ClassificationReason, item.Baseline).Should().Be((UpdateClassification.Update, "patch_notes", false));

        await rig.PollAsync();
        var restarted = ActivatorUtilities.CreateInstance<UpdatesPoller>(rig.Host.Services);
        await rig.PollAsync(TimeSpan.FromHours(2), restarted);
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "a second poll and a restart never post the same update again");
        restarted.Dispose();
    }

    [Fact]
    public async Task Named_and_titled_updates_are_cards_and_hero_reveals_are_not()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now.AddMinutes(1);
        Serve(rig,
            DeadlockNews.Post(120, "A New Synthetic Hero Arrives", t, DeadlockNews.HeroReveal),
            DeadlockNews.Post(121, "Old Gods, New Blood", t.AddMinutes(1),
                "[img]{STEAM_CLAN_IMAGE}/1/synthetic.png[/img]\n\nThe ritual takes form... Synthetic patrons, six new heroes, HUD updates and more.\n\n" +
                "View the [url=https://www.playdeadlock.com/oldgods]Old Gods, New Blood update page[/url]."),
            DeadlockNews.Post(122, "Matchmaking Update", t.AddMinutes(2), "This update includes a synthetic revamp of how matchmaking works, and a new mode."),
            DeadlockNews.Post(123, "Apollo - A Cut Above", t.AddMinutes(3), "A synthetic fencer.\n[list]\n[*] 100 votes\n[*] 200 votes\n[*] 300 votes\n[/list]"));
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.CardTexts.Should().HaveCount(2);
        rig.CardTexts[0].Should().Be(
            "**Old Gods, New Blood**\n\n• The ritual takes form... Synthetic patrons, six new heroes, HUD updates and more.\n\n" +
            "Yeni Deadlock güncellemesi yayınlandı.\n\n[Steam'de Güncelleme Notlarını Gör](" + Link(121) + ")",
            "a named update without \"update\" in its title, without the tag and without a change list");
        rig.CardTexts[1].Should().StartWith("**Matchmaking Update**\n\n• This update includes a synthetic revamp").And.EndWith("(" + Link(122) + ")");
        var items = (await rig.ItemsAsync()).ToDictionary(i => i.ExternalId, i => ((UpdateClassification)i.Classification, i.ClassificationReason));
        items["120"].Should().Be((UpdateClassification.NotUpdate, "no_update_signal"));
        items["121"].Should().Be((UpdateClassification.Update, "named_update"));
        items["122"].Should().Be((UpdateClassification.Update, "titled_update"));
        items["123"].Should().Be((UpdateClassification.Ambiguous, "list_only"));
        (await rig.StateAsync(Deadlock))!.LastAmbiguousCount.Should().BeGreaterThanOrEqualTo(1, "an inconclusive post is counted, not posted");
    }

    [Fact]
    public async Task A_rollout_that_ships_new_systems_and_a_roster_drop_are_cards_and_pure_hero_spotlights_are_not()
    {
        await using var rig = await BaselinedAsync();
        var t = rig.Now.AddMinutes(1);
        Serve(rig,
            DeadlockNews.Post(170, "Six New Heroes", t, DeadlockNews.SystemsRollout),
            DeadlockNews.Post(171, "Billy Comes in Swinging", t.AddMinutes(1), DeadlockNews.Spotlight("Billy")),
            DeadlockNews.Post(172, "Holliday, Vyper, Calico, and The Magnificent Sinclair", t.AddMinutes(2), DeadlockNews.RosterDrop),
            DeadlockNews.Post(173, "Apollo - A Cut Above", t.AddMinutes(3), DeadlockNews.Spotlight("Apollo")),
            DeadlockNews.Post(174, "Introducing The Dazzling Celeste", t.AddMinutes(4), DeadlockNews.Spotlight("Celeste")));
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.CardTexts.Should().HaveCount(2);
        rig.CardTexts[0].Should().StartWith("**Six New Heroes**\n\n• Six synthetic heroes have been spotted on the streets.").And.EndWith("(" + Link(170) + ")");
        rig.CardTexts[1].Should().StartWith("**Holliday, Vyper, Calico, and The Magnificent Sinclair**\n\n• Today's update adds four new heroes to matchmaking")
            .And.EndWith("(" + Link(172) + ")");
        var items = (await rig.ItemsAsync()).ToDictionary(i => i.ExternalId, i => ((UpdateClassification)i.Classification, i.ClassificationReason));
        items["170"].Should().Be((UpdateClassification.Update, "substantial_update"));
        items["172"].Should().Be((UpdateClassification.Update, "declared_update"));
        foreach (var spotlight in new[] { "171", "173", "174" })
            items[spotlight].Should().Be((UpdateClassification.NotUpdate, "no_update_signal"));
    }

    // ---------- edits, long lists, hostile text ----------

    [Fact]
    public async Task An_edited_post_edits_the_same_card_and_a_cosmetic_rewrite_of_the_markup_edits_nothing()
    {
        await using var rig = await BaselinedAsync();
        var published = rig.Now.AddMinutes(1);
        Serve(rig, DeadlockNews.Minor(130, published));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1);

        // The same lines in the older plain layout: the text Steam delivers differs, the card does not.
        Serve(rig, DeadlockNews.Minor(130, published, DeadlockNews.PlainNotes(DeadlockNews.SmallPatch)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        (await rig.ItemsAsync()).Single(i => i.ExternalId == "130").ContentChangedAt.Should().NotBeNull("the stored post follows the source");
        rig.Host.Transport.EditCalls.Should().Be(0, "an identical card is never an edit");

        // Valve corrects a line and adds one.
        var corrected = DeadlockNews.Notes(("General", ["Synthetic change one, corrected", "Synthetic change two", "A third change"]), DeadlockNews.SmallPatch[1]);
        Serve(rig, DeadlockNews.Minor(130, published, corrected));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "an edit is never a new card");
        rig.Host.Transport.EditCalls.Should().Be(1);
        rig.Host.Transport.Messages.Single().Edits.Single().Embed!.Description.Should().Contain("• Synthetic change one, corrected").And.Contain("• A third change");
        (await rig.OutboxAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task An_edit_far_down_a_very_long_patch_still_updates_the_number_of_changes_on_the_card()
    {
        await using var rig = await BaselinedAsync();
        var published = rig.Now.AddMinutes(1);
        string Patch(int lines) => DeadlockNews.Notes(("Heroes", Enumerable.Range(1, lines).Select(n => "Hero: synthetic change number " + n).ToArray()));
        Patch(1500).Length.Should().BeGreaterThan(GameUpdateCandidate.BodyMax * 2);
        Serve(rig, DeadlockNews.Post(135, "Gameplay Update - 10-01-2026", published, Patch(1500)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain("_… ve 1497 değişiklik daha_");

        // Valve adds three lines at the very end — beyond the part of the text the module keeps for classification.
        Serve(rig, DeadlockNews.Post(135, "Gameplay Update - 10-01-2026", published, Patch(1503)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1);
        rig.Host.Transport.EditCalls.Should().Be(1, "what the card shows changed, so the card is edited");
        rig.Host.Transport.Messages.Single().Edits.Single().Embed!.Description.Should().Contain("_… ve 1500 değişiklik daha_");

        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.EditCalls.Should().Be(1, "an unchanged post is never edited again");
    }

    [Fact]
    public async Task A_very_long_patch_is_a_short_excerpt_with_the_number_of_changes_left_and_hostile_text_is_defused()
    {
        await using var rig = await BaselinedAsync();
        var hostile = "@everyone <@&123456> [free skins](https://evil.example/x) **bold** `code` ||spoiler||";
        var notes = DeadlockNews.Notes(
            ("General", [hostile, .. Enumerable.Range(1, 60).Select(n => "General change " + n)]),
            ("Items", Enumerable.Range(1, 60).Select(n => "Item change " + n).ToArray()),
            ("Heroes", Enumerable.Range(1, 400).Select(n => "Hero change " + n).ToArray()),
            ("Misc", ["A misc change"]));
        Serve(rig, DeadlockNews.Post(140, "Gameplay Update - 10-01-2026", rig.Now.AddMinutes(1), notes));
        await rig.PollAsync();
        await rig.DeliverAsync();

        var message = rig.Host.Transport.Messages.Should().ContainSingle().Subject;
        var text = message.Message.Embed!.Description!;
        text.Should().Contain("**General**").And.Contain("**Items**").And.Contain("**Heroes**").And.NotContain("**Misc**", "three sections at most");
        text.Should().Contain("• General change 1\n• General change 2\n").And.NotContain("General change 3", "three lines per section at most");
        text.Should().Contain("_… ve 513 değişiklik daha_", "522 changes, 9 shown");
        text.Length.Should().BeLessThan(1500);
        message.Pinged.Should().BeFalse();
        text.Should().NotContain("@everyone").And.NotContain("<@&123456>").And.NotContain("](https://evil.example").And.NotContain("**bold**").And.NotContain("||spoiler||");
        System.Text.RegularExpressions.Regex.Matches(text, @"\]\(https://").Should().ContainSingle("the only link is the Steam one");
    }

    // ---------- outages, rate limits, request cost ----------

    [Fact]
    public async Task One_request_per_round_and_the_sources_own_cache_time_sets_the_pace()
    {
        await using var rig = await UpdatesRig.CreateAsync(configure: false);
        await rig.ConfigureGuildAsync(Guild, Channel, true, Deadlock);
        rig.Steam.Serve(History, expiresIn: TimeSpan.FromMinutes(60), appId: DeadlockNews.AppId);
        await rig.PollAsync();
        rig.Steam.RequestsFor(DeadlockNews.AppId).Should().Be(1, "a round is one request");
        (await rig.StateAsync(Deadlock))!.NextPollAt.Should().Be(rig.Now.AddMinutes(60), "Steam says the answer stays the same for an hour");

        rig.Host.Clock.Advance(TimeSpan.FromMinutes(59));
        await rig.Poller.TickAsync(Ct);
        rig.Steam.RequestsFor(DeadlockNews.AppId).Should().Be(1);
        await rig.PollAsync();
        rig.Steam.RequestsFor(DeadlockNews.AppId).Should().Be(2);
        rig.Steam.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Headers.Authorization == null && !r.Headers.Contains("Cookie") &&
                                                     r.RequestUri!.Host == "api.steampowered.com" && !r.RequestUri.Query.Contains("key=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_outage_is_a_failure_not_no_updates_and_what_was_published_meanwhile_is_posted_afterwards()
    {
        await using var rig = await BaselinedAsync();
        var complete = (await rig.StateAsync(Deadlock))!.LastSuccessAt;
        var during = rig.Now.AddMinutes(10);
        rig.Steam.Respond = _ => SteamNews.Status(HttpStatusCode.ServiceUnavailable);
        for (var failed = 1; failed <= 4; failed++)
        {
            await rig.PollAsync();
            var state = await rig.StateAsync(Deadlock);
            (state!.ConsecutiveFailures, state.LastSuccessAt, UpdatesConfigService.OutcomeName(state)).Should().Be((failed, complete, "ServerError"));
        }

        rig.Host.Transport.SendCalls.Should().Be(0);
        (await rig.ItemsAsync()).Should().HaveCount(4, "a failed round stores nothing");

        Serve(rig, DeadlockNews.Minor(150, during));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(150));
        (await rig.StateAsync(Deadlock))!.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task A_rate_limit_is_honoured_and_an_update_older_than_the_catch_up_window_is_not_posted_late()
    {
        await using var rig = await BaselinedAsync();
        rig.Steam.Respond = _ => SteamNews.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(45));
        await rig.PollAsync();
        var state = await rig.StateAsync(Deadlock);
        (UpdatesConfigService.OutcomeName(state!), state!.NextPollAt).Should().Be(("RateLimited", rig.Now.AddMinutes(45)));

        // Away for more than a day: an update published right after the last round is beyond catch-up, a recent one is not.
        var old = rig.Now.AddMinutes(5);
        rig.Host.Clock.Advance(TimeSpan.FromHours(30));
        Serve(rig, DeadlockNews.Minor(160, old), DeadlockNews.Minor(161, rig.Now.AddHours(-2)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.CardTexts.Should().ContainSingle().Which.Should().Contain(Link(161));
        (await rig.ItemsAsync()).Should().Contain(i => i.ExternalId == "160" && !i.Baseline, "seen and stored, but too old to post");
    }

    // ---------- next to the other games ----------

    [Fact]
    public async Task The_three_games_run_side_by_side_each_in_its_own_channel_and_counter_strike_2_is_unchanged()
    {
        var deadlockChannel = UpdatesRig.Channel2;
        var wowChannel = new ChannelId(9703);
        await using var rig = await UpdatesRig.CreateAsync(configure: false);
        await rig.ConfigureGuildAsync(Guild, Channel, true, "cs2", Deadlock, "wow-forever");
        rig.AllowChannel(Guild, deadlockChannel);
        rig.AllowChannel(Guild, wowChannel);
        var wowNotes = ForumHtmlSamples.BoldParagraphSections("Today we updated the beta.", ("Bug Fixes", ["Fixed one thing.", "Fixed another thing."]));
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes", new ForumPostSpec(1, wowNotes, Start.AddDays(-14)));
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3))]);
        Serve(rig);
        (await rig.PollAsync()).Should().Be(3, "one round per game");
        (await rig.StatesAsync()).Select(s => (s.Provider, s.GameKey, s.ProviderGameId)).Should().Equal(("steam", "cs2", "730"), ("steam", Deadlock, "1422450"), ("blizzard", "wow-forever", "349"));
        rig.Host.Transport.SendCalls.Should().Be(0);

        var status = (await rig.ConfigAsync(c => c.StatusAsync(TestHost.Admin(Guild), Ct))).Status!;
        status.Games.Select(g => (g.Game.DisplayName, g.ProviderName, g.Enabled)).Should().Equal(
            ("Counter-Strike 2", "Steam", true), ("Deadlock", "Steam", true), ("World of Warcraft: Forever", "Blizzard", true));
        (await rig.ConfigAsync(c => c.SetGameChannelAsync(TestHost.Admin(Guild), Deadlock, deadlockChannel.Value, Ct))).Succeeded.Should().BeTrue();
        (await rig.ConfigAsync(c => c.SetGameChannelAsync(TestHost.Admin(Guild), "wow-forever", wowChannel.Value, Ct))).Succeeded.Should().BeTrue();

        var t = rig.Now.AddMinutes(1);
        rig.Steam.Serve([SteamNews.Announcement(1, Start.AddDays(-3)), SteamNews.Update(202, t)]);
        Serve(rig, DeadlockNews.Minor(210, t));
        rig.Forum.SetDevNotes("WoW Forever Beta Development Notes", new ForumPostSpec(1, wowNotes, Start.AddDays(-14)), new ForumPostSpec(2, wowNotes, t));
        await rig.PollAsync();
        await rig.DeliverAsync();

        rig.Host.Transport.Messages.Select(m => (m.Channel, m.Message.Embed!.Title!)).Should().BeEquivalentTo(
            [(Channel, "🛠️ CS2 Güncellemesi"), (deadlockChannel, "🛠️ Deadlock Güncellemesi"), (wowChannel, "🛠️ WoW: Forever Güncellemesi")]);
        var cs2 = rig.Host.Transport.Messages.Single(m => m.Channel == Channel).Message.Embed!;
        cs2.Description.Should().Be("**Counter\\-Strike 2 Update**\n\nYeni Counter-Strike 2 güncellemesi yayınlandı.\n\n" +
                                    "[Steam'de Güncelleme Notlarını Gör](" + Link(202) + ")", "the Counter-Strike 2 card is byte for byte what it was");
        (await rig.ItemsAsync()).Where(i => i.GameKey == "cs2").Should().OnlyContain(i => i.Highlights == null);
        (await rig.OutboxAsync()).Select(o => o.SourceKey).Should().BeEquivalentTo(["steam:730:202", "steam:1422450:210", "blizzard:349:2360696:2"]);

        // One game's source failing leaves the two others working; the two Steam games are asked separately.
        rig.Steam.Respond = request => SteamNewsServer.AppIdOf(request) == DeadlockNews.AppId
            ? SteamNews.Status(HttpStatusCode.BadGateway)
            : SteamNews.Ok(SteamNews.Json([SteamNews.Announcement(1, Start.AddDays(-3)), SteamNews.Update(202, t), SteamNews.Update(203, rig.Now.AddMinutes(1))]));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Count(m => m.Channel == Channel).Should().Be(2);
        UpdatesConfigService.OutcomeName((await rig.StateAsync(Deadlock))!).Should().Be("ServerError");
        (await rig.StateAsync("cs2"))!.ConsecutiveFailures.Should().Be(0);

        // Turning Deadlock off stops only Deadlock; its channel moves with the game, not with the others.
        await rig.ConfigAsync(c => c.SetGameEnabledAsync(TestHost.Admin(Guild), Deadlock, false, Ct));
        var before = rig.Steam.RequestsFor(DeadlockNews.AppId);
        await rig.PollAsync(TimeSpan.FromHours(1));
        rig.Steam.RequestsFor(DeadlockNews.AppId).Should().Be(before);
        rig.Steam.RequestsFor(730).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Deadlock_falls_back_to_the_common_channel_and_a_channel_change_posts_nothing_again()
    {
        await using var rig = await BaselinedAsync();
        rig.AllowChannel(Guild, UpdatesRig.Channel2);
        Serve(rig, DeadlockNews.Minor(220, rig.Now.AddMinutes(1)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Should().ContainSingle().Which.Channel.Should().Be(Channel, "no channel of its own: the common Updates channel");

        (await rig.ConfigAsync(c => c.SetGameChannelAsync(TestHost.Admin(Guild), Deadlock, UpdatesRig.Channel2.Value, Ct))).Succeeded.Should().BeTrue();
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.SendCalls.Should().Be(1, "the card already posted is not posted again");
        Serve(rig, DeadlockNews.Minor(220, Start.AddMinutes(1)), DeadlockNews.Minor(221, rig.Now.AddMinutes(1)));
        await rig.PollAsync();
        await rig.DeliverAsync();
        rig.Host.Transport.Messages.Select(m => m.Channel).Should().Equal(Channel, UpdatesRig.Channel2);

        var preview = await rig.ConfigAsync(c => c.PreviewAsync(TestHost.Admin(Guild), Deadlock, "tr", Ct));
        preview.Preview!.Synthetic.Should().BeFalse();
        preview.Preview.Message.Embed!.Title.Should().Be("🛠️ Deadlock Güncellemesi");
        (await rig.ConfigAsync(c => c.DoctorAsync(TestHost.Admin(Guild), Ct))).Checks.Should().Contain(c => c.LabelKey == "updates.doctor.game_channel" && c.State == UpdatesCheckState.Ok);
    }
}
