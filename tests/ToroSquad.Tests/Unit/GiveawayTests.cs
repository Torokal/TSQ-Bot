using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Roles;
using ToroSquad.Modules.Giveaway;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Giveaway without Discord or a database: the form (duration, winners, prize, description), the target parser, the
/// draw (uniform, unique, members only, never partial on an unanswered lookup) and the card / announcement rendering.
/// Random draws use a scripted source, so nothing here is flaky.
/// </summary>
public sealed class GiveawayTests
{
    /// <summary>Always picks the lowest index still available (the pool order), or the scripted offsets.</summary>
    public sealed class ScriptedRandom(params int[] offsets) : IGiveawayRandom
    {
        private int _next;

        public List<(int From, int To)> Calls { get; } = [];

        public int NextInt32(int fromInclusive, int toExclusive)
        {
            Calls.Add((fromInclusive, toExclusive));
            var offset = _next < offsets.Length ? offsets[_next++] : 0;
            return fromInclusive + (offset % (toExclusive - fromInclusive));
        }
    }

    private static ILocalizer Localizer() => new LocalizationCatalog(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(GiveawayModule).Assembly, "ToroSquad.Modules.Giveaway.Localization"),
    ]);

    private static IReadOnlyList<UserId> Users(int count, ulong first = 100) =>
        Enumerable.Range(0, count).Select(i => new UserId(first + (ulong)i)).ToList();

    private static Func<UserId, Task<MemberLookupOutcome>> Everyone => _ => Task.FromResult(MemberLookupOutcome.Found);

    // ---- duration ----

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("2h", 120)]
    [InlineData("1d", 1440)]
    [InlineData("3d", 4320)]
    [InlineData("1d 12h", 2160)]
    [InlineData("1d12h", 2160)]
    [InlineData(" 1 d  12 h ", 2160)]
    [InlineData("30dk", 30)]
    [InlineData("2s", 120)]
    [InlineData("2 saat", 120)]
    [InlineData("1g", 1440)]
    [InlineData("1 GÜN", 1440)]
    [InlineData("45 DAKİKA", 45)]
    [InlineData("1w", 10080)]
    [InlineData("2H30M", 150)]
    public void Durations_are_read_as_typed(string input, int minutes) =>
        GiveawayDuration.Parse(input).Should().Be(TimeSpan.FromMinutes(minutes));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("30")]
    [InlineData("abc")]
    [InlineData("10x")]
    [InlineData("1d x")]
    [InlineData("x 1d")]
    [InlineData("-5m")]
    [InlineData("1.5h")]
    [InlineData("30sec")]
    [InlineData("1d 12h 30m 5m 5m 5m 5m")] // longer than the input limit
    public void Anything_else_is_not_a_duration(string? input) => GiveawayDuration.Parse(input).Should().BeNull();

    // ---- the form ----

    [Fact]
    public void A_valid_form_is_normalized()
    {
        var check = GiveawayForm.Parse("  Discord Nitro ", "1d 12h", " 3 ", "  Kazanana Steam üzerinden gönderilecektir. ");
        check.ErrorKey.Should().BeNull();
        check.Input.Should().Be(new GiveawayInput("Discord Nitro", TimeSpan.FromHours(36), 3, "Kazanana Steam üzerinden gönderilecektir."));
        GiveawayForm.Parse("Nitro", "30m", "", "   ").Input.Should().Be(new GiveawayInput("Nitro", TimeSpan.FromMinutes(30), 1, null), "empty winners: 1, blank description: none");
        GiveawayForm.Parse("Nitro", "30m", null, null).Input!.Winners.Should().Be(GiveawayRules.DefaultWinners);
    }

    [Theory]
    [InlineData(null, "30m", "1", "giveaway.form.prize_required")]
    [InlineData("   ", "30m", "1", "giveaway.form.prize_required")]
    [InlineData("Nitro", "yarın", "1", "giveaway.form.duration_invalid")]
    [InlineData("Nitro", "30", "1", "giveaway.form.duration_invalid")]
    [InlineData("Nitro", "0m", "1", "giveaway.form.duration_too_short")]
    [InlineData("Nitro", "31d", "1", "giveaway.form.duration_too_long")]
    [InlineData("Nitro", "5w", "1", "giveaway.form.duration_too_long")]
    [InlineData("Nitro", "30m", "0", "giveaway.form.winners_invalid")]
    [InlineData("Nitro", "30m", "11", "giveaway.form.winners_invalid")]
    [InlineData("Nitro", "30m", "-1", "giveaway.form.winners_invalid")]
    [InlineData("Nitro", "30m", "iki", "giveaway.form.winners_invalid")]
    public void Invalid_fields_are_refused_with_the_field_error(string? prize, string duration, string winners, string key)
    {
        var check = GiveawayForm.Parse(prize, duration, winners, null);
        check.Input.Should().BeNull();
        check.ErrorKey.Should().Be(key);
    }

    [Fact]
    public void Too_long_prize_and_description_are_refused_at_the_modal_limits()
    {
        GiveawayForm.Parse(new string('x', GiveawayRules.PrizeMaxLength), "1h", "1", new string('y', GiveawayRules.DescriptionMaxLength)).Input.Should().NotBeNull();
        GiveawayForm.Parse(new string('x', GiveawayRules.PrizeMaxLength + 1), "1h", "1", null).ErrorKey.Should().Be("giveaway.form.prize_too_long");
        GiveawayForm.Parse("Nitro", "1h", "1", new string('y', GiveawayRules.DescriptionMaxLength + 1)).ErrorKey.Should().Be("giveaway.form.description_too_long");
        GiveawayForm.Parse("Nitro", "1m", "10", null).Input!.Duration.Should().Be(GiveawayRules.MinDuration, "the bounds themselves are allowed");
        GiveawayForm.Parse("Nitro", "30d", "10", null).Input!.Duration.Should().Be(GiveawayRules.MaxDuration);
    }

    // ---- target ----

    [Theory]
    [InlineData("12", 12L, null, null)]
    [InlineData("#12", 12L, null, null)]
    [InlineData(" #7 ", 7L, null, null)]
    [InlineData("1234567890123456789", null, 1234567890123456789UL, null)]
    [InlineData("https://discord.com/channels/777/7001/1234567890123456789", null, 1234567890123456789UL, 777UL)]
    [InlineData("https://ptb.discord.com/channels/777/7001/1234567890123456789/", null, 1234567890123456789UL, 777UL)]
    [InlineData("https://discordapp.com/channels/777/7001/42", null, 42UL, 777UL)]
    public void Targets_are_a_number_a_message_link_or_a_message_id(string input, long? id, ulong? message, ulong? guild) =>
        GiveawayTarget.Parse(input).Should().Be(new GiveawayTarget(id, message, guild));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("#1234567890123456789")]
    [InlineData("https://evil.example/channels/777/7001/42")]
    [InlineData("http://discord.com/channels/777/7001/42")]
    [InlineData("12 13")]
    public void Anything_else_is_no_target(string? input) => GiveawayTarget.Parse(input).Should().BeNull();

    // ---- entrants and the draw ----

    [Fact]
    public void Entrants_are_unique_users_without_bots()
    {
        var users = new[]
        {
            new ReactionUser(new UserId(1), IsBot: true), // the bot's own 🎉
            new ReactionUser(new UserId(10), false),
            new ReactionUser(new UserId(11), false),
            new ReactionUser(new UserId(10), false), // listed twice (page boundary)
            new ReactionUser(new UserId(99), IsBot: true),
        };
        GiveawayEntrants.Valid(users).Should().Equal(new UserId(10), new UserId(11));
    }

    [Fact]
    public async Task One_entrant_one_winner()
    {
        var draw = await WinnerDraw.DrawAsync(Users(1), 1, new ScriptedRandom(), Everyone);
        draw.Should().Be(new DrawResult(draw.Winners, false));
        draw.Winners.Should().Equal(new UserId(100));
    }

    [Fact]
    public async Task Ten_entrants_one_winner_is_the_scripted_pick()
    {
        var random = new ScriptedRandom(7);
        var draw = await WinnerDraw.DrawAsync(Users(10), 1, random, Everyone);
        draw.Winners.Should().Equal(new UserId(107));
        random.Calls.Should().Equal((0, 10)); // one uniform pick over all ten, nothing more
    }

    [Fact]
    public async Task Ten_entrants_three_winners_are_unique_and_in_draw_order()
    {
        var random = new ScriptedRandom(9, 0, 8);
        var draw = await WinnerDraw.DrawAsync(Users(10), 3, random, Everyone);
        // step 0: [0..10) → index 9 (109); swap → pool[9] = 100. step 1: [1..10) → index 1 (101). step 2: [2..10) → index 2+8%8=2 (102)
        draw.Winners.Should().Equal(new UserId(109), new UserId(101), new UserId(102));
        random.Calls.Should().Equal([(0, 10), (1, 10), (2, 10)]); // each pick is uniform over the users not drawn yet
    }

    [Fact]
    public async Task Winners_never_repeat_whatever_the_random_source_answers()
    {
        foreach (var offsets in new[] { new[] { 0, 0, 0, 0, 0 }, [9, 9, 9, 9, 9], [5, 3, 5, 3, 5], [1, 2, 3, 4, 5] })
        {
            var draw = await WinnerDraw.DrawAsync(Users(10), 5, new ScriptedRandom(offsets), Everyone);
            draw.Winners.Should().HaveCount(5).And.OnlyHaveUniqueItems();
            draw.Winners.Should().OnlyContain(u => u.Value >= 100 && u.Value < 110);
        }

        // Duplicated candidates (never passed by the service, but still): one person, one chance.
        (await WinnerDraw.DrawAsync([new UserId(5), new UserId(5), new UserId(5)], 3, new ScriptedRandom(), Everyone)).Winners.Should().Equal(new UserId(5));
    }

    [Fact]
    public async Task Fewer_entrants_than_winners_all_win_and_none_is_no_winner()
    {
        (await WinnerDraw.DrawAsync(Users(2), 5, new ScriptedRandom(), Everyone)).Winners.Should().BeEquivalentTo(Users(2));
        var none = await WinnerDraw.DrawAsync([], 3, new ScriptedRandom(), Everyone);
        none.Winners.Should().BeEmpty();
        none.Unavailable.Should().BeFalse();
    }

    [Fact]
    public async Task Members_who_left_are_skipped_and_the_next_pick_is_uniform_over_the_rest()
    {
        var left = new UserId(100);
        var random = new ScriptedRandom(0, 0);
        var draw = await WinnerDraw.DrawAsync(Users(4), 1, random,
            u => Task.FromResult(u == left ? MemberLookupOutcome.NotMember : MemberLookupOutcome.Found));
        draw.Winners.Should().Equal(new UserId(101));
        random.Calls.Should().Equal((0, 4), (1, 4));

        var everyoneLeft = await WinnerDraw.DrawAsync(Users(3), 2, new ScriptedRandom(), _ => Task.FromResult(MemberLookupOutcome.NotMember));
        everyoneLeft.Winners.Should().BeEmpty();
        everyoneLeft.Unavailable.Should().BeFalse();
    }

    [Fact]
    public async Task An_unanswered_member_lookup_stops_the_draw_instead_of_skipping_someone()
    {
        var draw = await WinnerDraw.DrawAsync(Users(5), 2, new ScriptedRandom(),
            u => Task.FromResult(u.Value == 101 ? MemberLookupOutcome.Unavailable : MemberLookupOutcome.Found));
        draw.Unavailable.Should().BeTrue();
        draw.Winners.Should().BeEmpty("no half-drawn result is ever used");
    }

    [Fact]
    public async Task The_secure_source_draws_every_entrant_over_many_draws()
    {
        // Not a statistical test (no flaky thresholds): with 600 draws of 1 out of 3 every entrant is picked at least once
        // unless the source is broken (probability of a false failure ≈ 3 · (2/3)^600).
        var seen = new HashSet<UserId>();
        for (var i = 0; i < 600; i++)
            seen.UnionWith((await WinnerDraw.DrawAsync(Users(3), 1, new SecureGiveawayRandom(), Everyone)).Winners);
        seen.Should().BeEquivalentTo(Users(3));
    }

    // ---- rendering ----

    private static GiveawayView View(GiveawayStatus status, IReadOnlyList<UserId>? winners = null, string prize = "Discord Nitro", string? description = null,
        int winnerCount = 1, int? entrants = null, int rerolls = 0) => new(
        12, new GuildId(777), new ChannelId(7001), new MessageId(4242), new UserId(10), "Kaan", prize, description, winnerCount, status,
        TestHost.T0, TestHost.T0.AddHours(2), status == GiveawayStatus.Active ? null : TestHost.T0.AddHours(2), entrants, rerolls, winners ?? []);

    [Fact]
    public void The_active_card_shows_prize_winners_end_and_how_to_enter_and_never_pings()
    {
        var card = new GiveawayCards(Localizer()).Render(View(GiveawayStatus.Active, description: "Kazanana Steam üzerinden gönderilecektir."), "tr");
        card.Mentions.Should().Be(MentionPolicy.None);
        card.Buttons.Should().BeNull("entry is Discord's own 🎉 reaction, not a button");
        var embed = card.Embed!;
        embed.Title.Should().Be("🎉 ÇEKİLİŞ");
        embed.Description.Should().Be("Kazanana Steam üzerinden gönderilecektir.\n\n🎉 Katılmak için aşağıdaki 🎉 reaksiyonuna bas.");
        embed.Fields.Select(f => (f.Name, f.Value)).Should().Equal(
            ("🎁 Ödül", "Discord Nitro"),
            ("🏆 Kazanan Sayısı", "1"),
            ("⏰ Bitiş", DiscordText.Timestamp(TestHost.T0.AddHours(2), 'F') + "\n" + DiscordText.Timestamp(TestHost.T0.AddHours(2), 'R')));
        embed.Footer.Should().Be("Çekilişi başlatan: Kaan · #12");
        embed.Color.Should().Be(GiveawayCards.ActiveColor);
        DiscordLimits.Validate(card).Should().BeEmpty();
    }

    [Fact]
    public void The_result_card_lists_the_winners_with_medals_and_the_entrant_count()
    {
        var winners = Users(5);
        var card = new GiveawayCards(Localizer()).Render(View(GiveawayStatus.Finished, winners, winnerCount: 5, entrants: 37, rerolls: 1), "tr");
        var embed = card.Embed!;
        embed.Title.Should().Be("🎉 ÇEKİLİŞ SONUÇLANDI");
        embed.Fields.Select(f => f.Name).Should().Equal("🎁 Ödül", "🏆 Kazananlar", "👥 Katılımcı", "⏰ Bitiş");
        embed.Fields[1].Value.Should().Be("🥇 <@100>\n🥈 <@101>\n🥉 <@102>\n4. <@103>\n5. <@104>");
        embed.Fields[2].Value.Should().Be("37");
        embed.Description.Should().Be("🔁 Kazananlar yeniden çekildi (1. kez).");
        card.Mentions.Should().Be(MentionPolicy.None, "the card edit never pings; the announcement does");

        var single = new GiveawayCards(Localizer()).Render(View(GiveawayStatus.Finished, Users(1), entrants: 1), "en").Embed!;
        single.Fields.Select(f => (f.Name, f.Value)).Should().Contain(("🏆 Winner", "🥇 <@100>"));
        single.Title.Should().Be("🎉 GIVEAWAY ENDED");
    }

    [Fact]
    public void No_entrants_and_cancelled_cards_say_so()
    {
        var empty = new GiveawayCards(Localizer()).Render(View(GiveawayStatus.Finished, [], entrants: 0), "tr").Embed!;
        empty.Description.Should().Be("Çekiliş sona erdi ancak geçerli katılımcı bulunamadı.");
        empty.Fields.Select(f => f.Name).Should().NotContain(n => n.Contains("Kazanan", StringComparison.Ordinal));

        var cancelled = new GiveawayCards(Localizer()).Render(View(GiveawayStatus.Cancelled), "tr").Embed!;
        cancelled.Title.Should().Be("🚫 ÇEKİLİŞ İPTAL EDİLDİ");
        cancelled.Description.Should().Be("Bu çekiliş iptal edildi.");
        cancelled.Color.Should().Be(GiveawayCards.EndedColor);
    }

    [Fact]
    public void Untrusted_prize_description_and_name_are_defused_and_catalog_keys_are_not_expanded()
    {
        var view = View(GiveawayStatus.Active, prize: "@everyone <@&1> **x**", description: "help.title https://evil.example") with { CreatorName = "@here" };
        var embed = new GiveawayCards(Localizer()).Render(view, "tr").Embed!;
        embed.Fields[0].Value.Should().NotContain("@everyone").And.NotContain("<@&1>").And.Contain("\\*\\*x\\*\\*");
        embed.Description.Should().StartWith("help.title https:​//evil.example", "a prize or description that looks like a key stays text; links are defused");
        embed.Footer.Should().NotContain("@here");
        (DiscordText.RawMentionPattern().IsMatch(embed.Fields[0].Value) || DiscordText.RawMentionPattern().IsMatch(embed.Footer!)).Should().BeFalse();
    }

    [Fact]
    public void The_announcement_pings_exactly_the_winners_and_links_the_card()
    {
        var renderer = new GiveawayAnnouncementRenderer(Localizer());
        var winners = Users(2);
        var message = renderer.Render(View(GiveawayStatus.Finished, winners), winners, 0, "tr");
        message.Content.Should().Be("🎉 Tebrikler <@100> <@101>! **Discord Nitro** çekilişini kazandınız.\nhttps://discord.com/channels/777/7001/4242");
        message.Mentions.Should().BeEquivalentTo(MentionPolicy.ExplicitUsers(winners));
        message.Mentions.Everyone.Should().BeFalse();
        message.Mentions.Roles.Should().BeEmpty();
        message.Embed.Should().BeNull();

        renderer.Render(View(GiveawayStatus.Finished, Users(1)), Users(1), 2, "tr").Content.Should().StartWith("🔁 Yeniden çekiliş: tebrikler <@100>! **Discord Nitro** çekilişini kazandın.");
    }
}
