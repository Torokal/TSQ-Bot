using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Application;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Support.LiveBed;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Live session games: the categories observed during a creator session are stored per session and listed, first seen
/// first and each once, on the ENDED card — by editing the announcement message, never with a new message or a ping.
/// </summary>
public sealed class LiveSessionGamesTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);
    private const string Heading = "🎮 **Yayında Oynananlar**";

    private static async Task<LiveBed> WatchingAsync(Dictionary<string, string?>? overrides = null)
    {
        var bed = await CreateAsync(overrides);
        await bed.PollAsync();
        return bed;
    }

    private static Task<List<SessionCategory>> RowsAsync(LiveBed bed, string creator = Toro) => bed.Host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<SessionCategory>().AsNoTracking()
            .Where(c => c.CreatorKey == creator).OrderBy(c => c.SessionNumber).ThenBy(c => c.FirstSeenAt).ThenBy(c => c.Sequence).ToListAsync());

    /// <summary>One poll per category on one platform (same stream, so the session continues).</summary>
    private static async Task PlayAsync(LiveBed bed, LiveFakeProvider provider, string streamId, DateTimeOffset start, params (string Name, string? Id)[] categories)
    {
        foreach (var (name, id) in categories)
        {
            provider.GoLive(Toro, Stream(streamId, start, "Yayın", name, id));
            await bed.StepAsync(Poll);
        }
    }

    private static async Task EndAsync(LiveBed bed)
    {
        bed.Twitch.GoOffline(Toro);
        bed.Kick.GoOffline(Toro);
        for (var i = 0; i < 6; i++)
            await bed.StepAsync(Poll);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Offline);
    }

    /// <summary>The numbered game lines of an ended card, in order (markdown escapes removed).</summary>
    private static string[] Games(OutgoingMessage card)
    {
        var lines = card.Embed!.Description!.Split('\n');
        var at = Array.IndexOf(lines, Heading);
        return at < 0 ? [] : lines[(at + 1)..].Select(l => l.Replace("\\", "", StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public async Task The_starting_category_is_recorded_by_the_observation_that_opens_the_session()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın", "Minecraft", "27471"));
        await bed.StepAsync(Poll);

        var row = (await RowsAsync(bed)).Should().ContainSingle().Subject;
        row.Should().Match<SessionCategory>(c => c.Name == "Minecraft" && c.SessionNumber == 1 && c.FirstPlatform == LivePlatform.Twitch && c.TwitchCategoryId == "27471");
        bed.Messages.Should().ContainSingle().Which.Message.Embed!.Description.Should().NotContain(Heading, "the live card is not filled with history");
    }

    [Fact]
    public async Task Ended_card_lists_the_session_categories_once_in_first_seen_order_on_the_same_message_without_any_mention()
    {
        await using var bed = await WatchingAsync();
        var start = bed.Now;
        await PlayAsync(bed, bed.Kick, "k1", start,
            ("Minecraft", "15"), ("Counter-Strike 2", "101"), ("Grand Theft Auto V", "20"), ("Counter-Strike 2", "101"), ("Minecraft", "15"), ("Phasmophobia", "33"));
        var announced = bed.Messages.Should().ContainSingle().Subject;
        await EndAsync(bed);

        var message = bed.Messages.Should().ContainSingle("the ended card is an edit of the announcement").Subject;
        message.Id.Should().Be(announced.Id, "same Discord message id");
        var ended = message.Edits[^1];
        ended.Content.Should().Be("⚫ **LORDTORO** yayını sona erdi.");
        ended.Mentions.PingsAnything.Should().BeFalse("allowed_mentions stays empty on the end edit");
        ended.Buttons.Should().BeNullOrEmpty();
        ended.Embed!.Color.Should().Be(LiveCardRenderer.EndedColor);
        ended.Embed.Description.Should().Contain("Süre:").And.Contain("📺 Kick");
        Games(ended).Should().Equal("1. Minecraft", "2. Counter-Strike 2", "3. Grand Theft Auto V", "4. Phasmophobia");
        bed.EveryonePings.Should().Be(1, "only the original announcement ever pinged");
        (await RowsAsync(bed)).Select(c => c.Name).Should().Equal("Minecraft", "Counter-Strike 2", "Grand Theft Auto V", "Phasmophobia");

        // The end is rendered once: later rounds neither edit again nor post anything.
        var edits = bed.Transport.EditCalls;
        for (var i = 0; i < 5; i++)
            await bed.StepAsync(Poll);
        bed.Transport.EditCalls.Should().Be(edits);
        bed.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Multistream_categories_of_both_platforms_merge_into_one_creator_history()
    {
        await using var bed = await WatchingAsync();
        var start = bed.Now;
        bed.Twitch.GoLive(Toro, Stream("t1", start, "Yayın", "Minecraft", "27471"));
        bed.Kick.GoLive(Toro, Stream("k1", start, "Yayın", "Minecraft", "15"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoLive(Toro, Stream("t1", start, "Yayın", "Counter-Strike 2", "32399"));
        await bed.StepAsync(Poll);
        bed.Kick.GoLive(Toro, Stream("k1", start, "Yayın", "Grand Theft Auto V", "20"));
        await bed.StepAsync(Poll);
        bed.Twitch.GoLive(Toro, Stream("t1", start, "Yayın", "Minecraft", "27471"));
        await bed.StepAsync(Poll);
        await EndAsync(bed);

        var message = bed.Messages.Should().ContainSingle().Subject;
        Games(message.Edits[^1]).Should().Equal("1. Minecraft", "2. Counter-Strike 2", "3. Grand Theft Auto V");
        message.Edits[^1].Embed!.Description.Should().Contain("📺 Twitch + Kick");
        var minecraft = (await RowsAsync(bed)).First();
        minecraft.Should().Match<SessionCategory>(c => c.TwitchCategoryId == "27471" && c.KickCategoryId == "15", "one entry, both source ids kept");
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task Repeated_polling_adds_no_rows_and_causes_no_edits()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Yayın", "Minecraft", "27471"));
        for (var i = 0; i < 12; i++)
            await bed.StepAsync(Poll);

        (await RowsAsync(bed)).Should().ContainSingle();
        bed.Transport.EditCalls.Should().Be(0);
        bed.Messages.Should().ContainSingle().Which.Edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Provider_failures_and_malformed_answers_never_erase_the_history()
    {
        await using var bed = await WatchingAsync();
        var start = bed.Now;
        await PlayAsync(bed, bed.Twitch, "t1", start, ("Minecraft", "27471"));
        bed.Twitch.Failure = LiveProviderOutcome.Timeout;
        bed.Kick.Undescribed.Add(Toro); // malformed: the channel is not described
        for (var i = 0; i < 12; i++)
        {
            await bed.StepAsync(Poll);
            (await RowsAsync(bed)).Should().ContainSingle("unknown never deletes history");
        }

        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.Live, "and never closes the session");
        bed.Twitch.Failure = null;
        bed.Kick.Undescribed.Clear();
        bed.Host.Clock.Advance(TimeSpan.FromMinutes(16)); // past any backoff
        await PlayAsync(bed, bed.Twitch, "t1", start, ("Counter-Strike 2", "32399"));
        await EndAsync(bed);

        Games(bed.Messages.Single().Edits[^1]).Should().Equal("1. Minecraft", "2. Counter-Strike 2");
    }

    [Fact]
    public async Task A_reconnect_inside_the_grace_keeps_the_session_and_its_history()
    {
        await using var bed = await WatchingAsync();
        await PlayAsync(bed, bed.Twitch, "t1", bed.Now, ("Minecraft", "27471"));
        bed.Twitch.GoOffline(Toro); // 30-60 s cut
        await bed.StepAsync(Poll);
        (await bed.CreatorAsync(Toro)).Phase.Should().Be(CreatorPhase.ReconnectGrace);
        await PlayAsync(bed, bed.Twitch, "t2", bed.Now + TimeSpan.FromSeconds(10), ("Counter-Strike 2", "32399"));
        (await bed.CreatorAsync(Toro)).SessionNumber.Should().Be(1);
        await EndAsync(bed);

        Games(bed.Messages.Should().ContainSingle().Subject.Edits[^1]).Should().Equal("1. Minecraft", "2. Counter-Strike 2");
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task History_survives_a_restart_and_continues_where_it_left_off()
    {
        await using var first = await WatchingAsync();
        var start = first.Now;
        await PlayAsync(first, first.Twitch, "t1", start, ("Minecraft", "27471"), ("Counter-Strike 2", "32399"));

        await using var second = await CreateAsync(restartOf: first, start: first.Now + TimeSpan.FromMinutes(2));
        await second.PollAsync();
        (await RowsAsync(second)).Select(c => c.Name).Should().Equal("Minecraft", "Counter-Strike 2");
        await PlayAsync(second, second.Twitch, "t1", start, ("Grand Theft Auto V", "32982"), ("Minecraft", "27471"));
        await EndAsync(second);

        var message = second.Messages.Should().ContainSingle("no new message after the restart").Subject;
        Games(message.Edits[^1]).Should().Equal("1. Minecraft", "2. Counter-Strike 2", "3. Grand Theft Auto V");
        second.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task A_new_session_starts_a_new_history_and_each_card_shows_its_own_games()
    {
        await using var bed = await WatchingAsync();
        await PlayAsync(bed, bed.Twitch, "t1", bed.Now, ("Minecraft", "27471"), ("Counter-Strike 2", "32399"));
        await EndAsync(bed);
        bed.Host.Clock.Advance(TimeSpan.FromHours(3));
        await bed.PollAsync();
        await PlayAsync(bed, bed.Kick, "k9", bed.Now, ("Phasmophobia", "33"), ("Minecraft", "15"));
        await EndAsync(bed);

        bed.Messages.Should().HaveCount(2);
        Games(bed.Messages[0].Edits[^1]).Should().Equal("1. Minecraft", "2. Counter-Strike 2");
        Games(bed.Messages[1].Edits[^1]).Should().Equal("1. Phasmophobia", "2. Minecraft");
        var rows = await RowsAsync(bed);
        rows.Where(c => c.SessionNumber == 1).Select(c => c.Name).Should().Equal("Minecraft", "Counter-Strike 2");
        rows.Where(c => c.SessionNumber == 2).Select(c => c.Name).Should().Equal("Phasmophobia", "Minecraft");
        bed.EveryonePings.Should().Be(2);
    }

    [Fact]
    public async Task A_reopened_session_of_the_same_stream_keeps_its_history()
    {
        await using var bed = await WatchingAsync();
        var start = bed.Now;
        await PlayAsync(bed, bed.Twitch, "t1", start, ("Minecraft", "27471"));
        await EndAsync(bed);
        Games(bed.Messages.Single().Edits[^1]).Should().Equal("1. Minecraft");

        // The provider reports the very same stream again (it never really ended): the session is reopened.
        await PlayAsync(bed, bed.Twitch, "t1", start, ("Counter-Strike 2", "32399"));
        (await bed.CreatorAsync(Toro)).Should().Match<CreatorState>(s => s.Phase == CreatorPhase.Live && s.SessionNumber == 1);
        await EndAsync(bed);

        var message = bed.Messages.Should().ContainSingle().Subject;
        Games(message.Edits[^1]).Should().Equal("1. Minecraft", "2. Counter-Strike 2");
        bed.EveryonePings.Should().Be(1);
    }

    [Fact]
    public async Task A_long_history_stays_inside_the_embed_limits_and_says_how_many_more_there_are()
    {
        await using var bed = await WatchingAsync();
        var start = bed.Now;
        var games = Enumerable.Range(1, 25).Select(i => (Name: "Oyun " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Id: (string?)(1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        await PlayAsync(bed, bed.Twitch, "t1", start, games);
        await EndAsync(bed);

        var ended = bed.Messages.Should().ContainSingle().Subject.Edits[^1];
        var lines = Games(ended);
        lines.Take(LiveCardRenderer.MaxGamesShown).Should().Equal(Enumerable.Range(1, 15).Select(i => $"{i}. Oyun {i}"));
        lines[^1].Should().Be("… ve 10 kategori daha");
        lines.Should().HaveCount(16);
        DiscordLimits.Validate(ended).Should().BeEmpty();
        ended.Embed!.Description!.Length.Should().BeLessThan(DiscordLimits.EmbedDescriptionMax / 2);
        (await RowsAsync(bed)).Should().HaveCount(25, "the whole history stays in the database");
    }

    [Fact]
    public async Task Very_long_category_names_are_cut_by_the_character_budget()
    {
        await using var bed = await WatchingAsync();
        var renderer = bed.Host.Services.GetRequiredService<LiveCardRenderer>();
        var creator = new TrackedCreator(Toro, "LORDTORO", [new TrackedChannel(LivePlatform.Twitch, Toro)]);
        var state = new CreatorState { CreatorKey = Toro, SessionNumber = 1, CategoryTracking = true, SessionPlatforms = "twitch", SessionStartedAt = bed.Now, SessionEndedAt = bed.Now.AddHours(4) };
        var rows = new List<SessionCategory>();
        for (var i = 0; i < 60; i++)
            SessionCategories.Record(rows, Toro, 1, LivePlatform.Twitch, null, new string((char)('A' + (i % 26)), 90) + i, bed.Now.AddMinutes(i));

        var card = renderer.Ended(creator, state, [], "tr", rows);
        DiscordLimits.Validate(card).Should().BeEmpty();
        card.Embed!.Description!.Length.Should().BeLessThan(1400);
        var lines = Games(card);
        lines.Length.Should().BeLessThan(LiveCardRenderer.MaxGamesShown, "the character budget ends the list first");
        lines[^1].Should().MatchRegex(@"^… ve \d+ kategori daha$");
    }

    [Fact]
    public async Task Without_any_recorded_category_the_card_says_so_and_claims_nothing()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Kategorisiz yayın")); // the provider states no category
        await bed.StepAsync(Poll);
        await EndAsync(bed);

        var ended = bed.Messages.Should().ContainSingle().Subject.Edits[^1];
        Games(ended).Should().Equal("Oyun/kategori bilgisi kaydedilemedi.");
        (await RowsAsync(bed)).Should().BeEmpty("no invented game");
        ended.Mentions.PingsAnything.Should().BeFalse();
    }

    [Fact]
    public async Task The_games_section_is_localized()
    {
        await using var bed = await WatchingAsync();
        await bed.Host.InScopeAsync(async sp =>
            (await sp.GetRequiredService<GuildSettingsService>().UpdateAsync(TestHost.Admin(Guild), "en", null, CancellationToken.None)).Succeeded.Should().BeTrue());
        await PlayAsync(bed, bed.Twitch, "t1", bed.Now, ("Minecraft", "27471"), ("Counter-Strike 2", "32399"));
        await EndAsync(bed);

        var description = bed.Messages.Should().ContainSingle().Subject.Edits[^1].Embed!.Description!;
        description.Should().Contain("🎮 **Played during the stream**\n1. Minecraft\n2. Counter\\-Strike 2");

        var renderer = bed.Host.Services.GetRequiredService<LiveCardRenderer>();
        var creator = new TrackedCreator(Toro, "LORDTORO", [new TrackedChannel(LivePlatform.Twitch, Toro)]);
        var state = new CreatorState { CreatorKey = Toro, SessionNumber = 1, CategoryTracking = true, SessionPlatforms = "twitch" };
        renderer.Ended(creator, state, [], "en", []).Embed!.Description.Should().Contain("No game/category information could be recorded.");
        renderer.Ended(creator, state, [], "tr", []).Embed!.Description.Should().Contain("Oyun/kategori bilgisi kaydedilemedi.");
        var many = new List<SessionCategory>();
        for (var i = 0; i < 20; i++)
            SessionCategories.Record(many, Toro, 1, LivePlatform.Twitch, null, "G" + i, bed.Now.AddMinutes(i));
        renderer.Ended(creator, state, [], "en", many).Embed!.Description.Should().EndWith("… and 5 more");
    }

    [Fact]
    public async Task A_silent_bootstrap_session_tracks_categories_but_never_posts_or_pings()
    {
        await using var bed = await CreateAsync();
        var start = bed.Now - TimeSpan.FromHours(2);
        bed.Twitch.GoLive(Toro, Stream("t1", start, "Zaten yayında", "Minecraft", "27471"));
        await bed.PollAsync();
        await PlayAsync(bed, bed.Twitch, "t1", start, ("Counter-Strike 2", "32399"));
        (await bed.CreatorAsync(Toro)).NotAnnouncedReason.Should().Be("bootstrap");
        (await RowsAsync(bed)).Select(c => c.Name).Should().Equal("Minecraft", "Counter-Strike 2");
        await EndAsync(bed);

        bed.Messages.Should().BeEmpty("an unannounced session has no card: nothing is posted at its end either");
        bed.Transport.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Cards_of_sessions_from_before_category_tracking_are_left_untouched()
    {
        await using var bed = await WatchingAsync();
        bed.Twitch.GoLive(Toro, Stream("t1", bed.Now, "Eski oturum"));
        await bed.StepAsync(Poll);
        // As deployed before this feature: the session exists without the tracking flag and without category rows.
        await bed.Host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            (await db.Set<CreatorState>().SingleAsync(s => s.CreatorKey == Toro)).CategoryTracking = false;
            await db.SaveChangesAsync();
        });
        await EndAsync(bed);

        var ended = bed.Messages.Should().ContainSingle().Subject.Edits[^1];
        ended.Embed!.Description.Should().NotContain("Oynananlar").And.NotContain("kaydedilemedi");
        ended.Embed.Description.Should().Contain("Yayın sona erdi");
    }

    [Fact]
    public async Task Old_history_is_pruned_when_a_session_starts_and_recent_sessions_are_kept()
    {
        await using var bed = await WatchingAsync();
        await bed.Host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            (await db.Set<CreatorState>().SingleAsync(s => s.CreatorKey == Toro)).SessionNumber = 60;
            db.Add(new SessionCategory { CreatorKey = Toro, SessionNumber = 5, Sequence = 1, Name = "Çok eski", NameKey = "ÇOK ESKI", FirstSeenAt = bed.Now });
            db.Add(new SessionCategory { CreatorKey = Toro, SessionNumber = 40, Sequence = 1, Name = "Yakın", NameKey = "YAKIN", FirstSeenAt = bed.Now });
            await db.SaveChangesAsync();
        });
        await PlayAsync(bed, bed.Twitch, "t1", bed.Now, ("Minecraft", "27471"));

        (await RowsAsync(bed)).Select(c => (c.SessionNumber, c.Name)).Should().Equal((40, "Yakın"), (61, "Minecraft"));
    }
}
