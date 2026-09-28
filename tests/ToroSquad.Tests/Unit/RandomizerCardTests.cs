using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Randomizer;
using ToroSquad.Modules.Randomizer.Application;
using ToroSquad.Modules.Randomizer.Commands;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Randomizer draws and cards. The draws run on a scripted <see cref="IRandomSource"/> (deterministic: which bounds were
/// asked for, which value maps to which result); the real <see cref="SecureRandomSource"/> is only checked for properties
/// that hold on every call (range, count) — never for a distribution.
/// </summary>
public sealed class RandomizerCardTests
{
    /// <summary>Returns the scripted values in order and records every (from, to) it was asked for.</summary>
    private sealed class ScriptedRandom(params int[] values) : IRandomSource
    {
        private int _next;

        public List<(int From, int To)> Calls { get; } = [];

        public int NextInt32(int fromInclusive, int toExclusive)
        {
            Calls.Add((fromInclusive, toExclusive));
            var value = values[_next++ % values.Length];
            value.Should().BeInRange(fromInclusive, toExclusive - 1, "the script must stay inside the requested bounds");
            return value;
        }
    }

    private static LocalizationCatalog Localizer() => new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(RandomizerModule).Assembly, "ToroSquad.Modules.Randomizer.Localization"),
    ]);

    private static readonly string Zwsp = char.ConvertFromUtf32(0x200B); // what DiscordText inserts to defuse a mention

    private static RandomizerCards Cards(IRandomSource random) => new(new RandomizerService(random), Localizer());

    private static MessageEmbed Card(RandomizerReply reply)
    {
        reply.Refusal.Should().BeNull();
        reply.Card.Should().NotBeNull();
        DiscordLimits.Validate(new OutgoingMessage(null, reply.Card, MentionPolicy.None)).Should().BeEmpty();
        return reply.Card!;
    }

    // ---- shared RNG service ----

    [Fact]
    public void Dice_ask_for_one_to_sides_inclusive_once_per_die()
    {
        var random = new ScriptedRandom(4, 6);
        var roll = new RandomizerService(random).Roll(new DiceSpec(2, 6));
        roll.Dice.Should().Equal(4, 6);
        roll.Total.Should().Be(10);
        random.Calls.Should().Equal((1, 7), (1, 7));
    }

    [Fact]
    public void Dice_at_the_limits_ask_for_the_right_bounds()
    {
        var random = new ScriptedRandom(10_000);
        var roll = new RandomizerService(random).Roll(new DiceSpec(20, 10_000));
        roll.Dice.Should().HaveCount(20).And.OnlyContain(d => d == 10_000);
        roll.Total.Should().Be(200_000);
        random.Calls.Should().HaveCount(20).And.AllBeEquivalentTo((1, 10_001));
    }

    [Fact]
    public void Number_asks_for_minimum_to_maximum_plus_one()
    {
        var random = new ScriptedRandom(100);
        new RandomizerService(random).Between(new NumberRange(1, 100)).Should().Be(100, "the maximum itself can come up");
        random.Calls.Should().Equal((1, 101));

        random = new ScriptedRandom(-1_000_000_000);
        new RandomizerService(random).Between(new NumberRange(-1_000_000_000, 1_000_000_000)).Should().Be(-1_000_000_000);
        random.Calls.Should().Equal((-1_000_000_000, 1_000_000_001));

        random = new ScriptedRandom(10);
        new RandomizerService(random).Between(new NumberRange(10, 10)).Should().Be(10);
        random.Calls.Should().Equal((10, 11));
    }

    [Theory]
    [InlineData(0, "CS2")]
    [InlineData(1, "Valheim")]
    [InlineData(2, "WoW")]
    public void Choice_takes_the_drawn_index(int index, string expected)
    {
        var random = new ScriptedRandom(index);
        new RandomizerService(random).Choose(["CS2", "Valheim", "WoW"]).Should().Be(expected);
        random.Calls.Should().Equal((0, 3));
    }

    [Theory]
    [InlineData(0, CoinFace.Yazi)]
    [InlineData(1, CoinFace.Tura)]
    public void Coin_maps_zero_to_yazi_and_one_to_tura(int drawn, CoinFace expected)
    {
        var random = new ScriptedRandom(drawn);
        new RandomizerService(random).Flip().Should().Be(expected);
        random.Calls.Should().Equal((0, 2));
    }

    [Fact]
    public void Draws_are_independent_and_never_adjusted()
    {
        // The same face five times in a row stays five times the same face: no streak prevention.
        var random = new ScriptedRandom(1, 1, 1, 1, 1);
        var service = new RandomizerService(random);
        Enumerable.Range(0, 5).Select(_ => service.Flip()).Should().OnlyContain(f => f == CoinFace.Tura);
        random.Calls.Should().HaveCount(5).And.AllBeEquivalentTo((0, 2));
    }

    // ---- the real secure source: properties that hold on every call ----

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, 6)]
    [InlineData(20, 6)]
    [InlineData(20, 20)]
    [InlineData(20, 10_000)]
    public void Secure_dice_are_always_between_one_and_sides_and_have_the_right_count(int count, int sides)
    {
        var service = new RandomizerService(new SecureRandomSource());
        for (var i = 0; i < 200; i++)
        {
            var roll = service.Roll(new DiceSpec(count, sides));
            roll.Dice.Should().HaveCount(count);
            roll.Dice.Should().OnlyContain(d => d >= 1 && d <= sides);
            roll.Total.Should().BeInRange(count, count * sides);
        }
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(-100, 100)]
    [InlineData(-10, -5)]
    [InlineData(10, 10)]
    [InlineData(-1_000_000_000, 1_000_000_000)]
    [InlineData(999_999_999, 1_000_000_000)]
    public void Secure_numbers_stay_inside_the_inclusive_range(int minimum, int maximum)
    {
        var service = new RandomizerService(new SecureRandomSource());
        for (var i = 0; i < 200; i++)
            service.Between(new NumberRange(minimum, maximum)).Should().BeInRange(minimum, maximum);
    }

    [Fact]
    public void Secure_choice_and_coin_only_return_allowed_values()
    {
        var service = new RandomizerService(new SecureRandomSource());
        string[] options = ["CS2", "Valheim", "WoW"];
        for (var i = 0; i < 200; i++)
        {
            options.Should().Contain(service.Choose(options));
            service.Flip().Should().BeOneOf(CoinFace.Yazi, CoinFace.Tura);
        }
    }

    // ---- /zarat card ----

    [Fact]
    public void Single_die_card()
    {
        var card = Card(Cards(new ScriptedRandom(17)).Dice("tr", "1-20", "Toro"));
        card.Title.Should().Be("🎲 1d20 atıldı");
        card.Description.Should().Be("Sonuç: **17**");
        card.Footer.Should().Be("Toro tarafından atıldı");
        card.Color.Should().Be(RandomizerCards.Color);
        card.Fields.Should().BeEmpty();
    }

    [Fact]
    public void Several_dice_card_lists_every_die_and_the_total()
    {
        var card = Card(Cards(new ScriptedRandom(4, 6)).Dice("tr", "2D6", "Toro"));
        card.Title.Should().Be("🎲 2d6 atıldı");
        card.Description.Should().Be("Zarlar: `4` `6`\nToplam: **10**");
        card.Footer.Should().Be("Toro tarafından atıldı");
    }

    [Fact]
    public void Twenty_dice_of_ten_thousand_sides_fit_one_card()
    {
        var card = Card(Cards(new ScriptedRandom(10_000)).Dice("tr", "20d10000", "Toro"));
        card.Description.Should().Be("Zarlar: " + string.Join(" ", Enumerable.Repeat("`10000`", 20)) + "\nToplam: **200000**");
    }

    [Theory]
    [InlineData("abc", "Zar formatı geçersiz. Örnek: 1-20, 2-6 veya 2d6.")]
    [InlineData("21d6", "Tek seferde en fazla 20 zar atabilirsin.")]
    [InlineData("0d6", "En az 1 zar atmalısın.")]
    [InlineData("2d1", "Bir zar en az 2 yüzlü olmalı.")]
    [InlineData("2d10001", "Bir zar en fazla 10.000 yüzlü olabilir.")]
    public void Invalid_dice_get_a_turkish_refusal_and_draw_nothing(string input, string expected)
    {
        var random = new ScriptedRandom(1);
        var reply = Cards(random).Dice("tr", input, "Toro");
        reply.Card.Should().BeNull();
        reply.Refusal.Should().Be(expected);
        random.Calls.Should().BeEmpty();
    }

    // ---- /randomsayi card ----

    [Fact]
    public void Number_card()
    {
        var card = Card(Cards(new ScriptedRandom(73)).RandomNumber("tr", 100, null, "Toro"));
        card.Title.Should().Be("🔢 Rastgele sayı");
        card.Description.Should().Be("Aralık: `1 – 100`\nSonuç: **73**");
        card.Footer.Should().Be("Toro için seçildi");
    }

    [Fact]
    public void Number_card_with_negative_and_equal_bounds()
    {
        Card(Cards(new ScriptedRandom(-42)).RandomNumber("tr", 100, -100, "Toro")).Description.Should().Be("Aralık: `-100 – 100`\nSonuç: **-42**");
        Card(Cards(new ScriptedRandom(10)).RandomNumber("tr", 10, 10, "Toro")).Description.Should().Be("Aralık: `10 – 10`\nSonuç: **10**");
    }

    [Fact]
    public void Invalid_numbers_get_a_turkish_refusal_and_draw_nothing()
    {
        var random = new ScriptedRandom(1);
        Cards(random).RandomNumber("tr", 10, 50, "Toro").Refusal.Should().Be("Minimum değer maksimum değerden büyük olamaz.");
        Cards(random).RandomNumber("tr", 2_000_000_000, 1, "Toro").Refusal.Should().Be("Sayılar -1.000.000.000 ile 1.000.000.000 arasında olmalı.");
        random.Calls.Should().BeEmpty();
    }

    // ---- /sec card ----

    [Fact]
    public void Choice_card_lists_the_options_and_the_pick()
    {
        var card = Card(Cards(new ScriptedRandom(1)).Choose("tr", "CS2, Valheim, WoW", "Toro"));
        card.Title.Should().Be("🎯 Seçim yapıldı");
        card.Description.Should().Be("Seçenekler: CS2 · Valheim · WoW\nSeçilen: **Valheim**");
        card.Footer.Should().Be("Toro için seçildi");
    }

    [Fact]
    public void Choice_card_with_duplicates_draws_among_distinct_options_only()
    {
        var random = new ScriptedRandom(1);
        var card = Card(Cards(random).Choose("tr", "CS2, cs2, Valheim", "Toro"));
        card.Description.Should().Be("Seçenekler: CS2 · Valheim\nSeçilen: **Valheim**");
        random.Calls.Should().Equal((0, 2));
    }

    [Fact]
    public void Long_choice_lists_show_only_the_count()
    {
        var input = string.Join(", ", Enumerable.Range(1, 25).Select(i => "Oyun" + i));
        var card = Card(Cards(new ScriptedRandom(24)).Choose("tr", input, "Toro"));
        card.Description.Should().Be("25 seçenek arasından:\n**Oyun25**");

        var ten = string.Join(", ", Enumerable.Range(1, RandomizerCards.ListedOptionsMax).Select(i => "Oyun" + i));
        Card(Cards(new ScriptedRandom(0)).Choose("tr", ten, "Toro")).Description.Should().StartWith("Seçenekler: Oyun1 · Oyun2");

        // Few options that grow when defused (every '-' is escaped): counted too, the card stays small.
        var longOnes = string.Join(", ", Enumerable.Range(1, 10).Select(i => i + new string('-', 89)));
        Card(Cards(new ScriptedRandom(0)).Choose("tr", longOnes, "Toro")).Description.Should().StartWith("10 seçenek arasından:");
    }

    [Theory]
    [InlineData("CS2", "En az 2 farklı seçenek girmelisin.")]
    [InlineData("CS2, cs2", "En az 2 farklı seçenek girmelisin.")]
    [InlineData(",,,", "En az 2 farklı seçenek girmelisin.")]
    public void Invalid_choices_get_a_turkish_refusal_and_draw_nothing(string input, string expected)
    {
        var random = new ScriptedRandom(0);
        var reply = Cards(random).Choose("tr", input, "Toro");
        reply.Card.Should().BeNull();
        reply.Refusal.Should().Be(expected);
        random.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Choice_refusals_for_limits()
    {
        var cards = Cards(new ScriptedRandom(0));
        cards.Choose("tr", string.Join(",", Enumerable.Range(1, 26)), "Toro").Refusal.Should().Be("En fazla 25 seçenek kullanabilirsin.");
        cards.Choose("tr", new string('a', 101) + ", b", "Toro").Refusal.Should().Be("Bir seçenek en fazla 100 karakter olabilir.");
        cards.Choose("tr", "a, b" + new string(' ', 1000), "Toro").Refusal.Should().Be("Seçenekler toplamda en fazla 1000 karakter olabilir.");
    }

    [Fact]
    public void Mentions_and_markdown_in_options_never_render()
    {
        var card = Card(Cards(new ScriptedRandom(0)).Choose("tr", "@everyone, @here, <@&123>, <@456>, **kalın**, `kod`, ~~çizili~~", "Toro"));
        DiscordText.RawMentionPattern().IsMatch(card.Description!).Should().BeFalse(card.Description);
        card.Description.Should().NotContain("**kalın**").And.NotContain("`kod`");
        card.Description.Should().Contain("@" + Zwsp + "everyone").And.Contain("<" + Zwsp + "@" + Zwsp + "&123\\>");
        card.Description.Should().EndWith("Seçilen: **@" + Zwsp + "everyone**");
    }

    [Fact]
    public void A_pick_that_looks_like_a_catalog_key_is_shown_as_typed()
    {
        var card = Card(Cards(new ScriptedRandom(0)).Choose("tr", "randomizer.coin.tura, help.title", "Toro"));
        card.Description.Should().Contain("Seçilen: **randomizer.coin.tura**").And.NotContain("TURA");
    }

    // ---- /yazitura card ----

    [Theory]
    [InlineData(0, "**YAZI**")]
    [InlineData(1, "**TURA**")]
    public void Coin_card(int drawn, string expected)
    {
        var card = Card(Cards(new ScriptedRandom(drawn)).CoinFlip("tr", "Toro"));
        card.Title.Should().Be("🪙 Yazı Tura");
        card.Description.Should().Be(expected);
        card.Footer.Should().Be("Toro tarafından atıldı");
    }

    [Fact]
    public void Coin_card_only_ever_shows_one_of_the_two_faces()
    {
        var cards = Cards(new SecureRandomSource());
        for (var i = 0; i < 100; i++)
            Card(cards.CoinFlip("tr", "Toro")).Description.Should().BeOneOf("**YAZI**", "**TURA**");
    }

    // ---- shared ----

    [Fact]
    public void Display_names_are_defused_and_bounded_in_the_footer()
    {
        var card = Card(Cards(new ScriptedRandom(0)).CoinFlip("tr", "@everyone\n" + new string('x', 200)));
        card.Footer.Should().StartWith("@" + Zwsp + "everyone").And.EndWith("… tarafından atıldı");
        card.Footer.Should().NotContain("\n");
        card.Footer!.Length.Should().BeLessThan(100);
    }

    [Theory]
    [InlineData("Hasom")]
    [InlineData("Oykeli")]
    [InlineData("Çağrı Ş.")]
    public void Every_card_names_whoever_ran_the_command(string name)
    {
        var cards = Cards(new ScriptedRandom(1));
        Card(cards.Dice("tr", "2d6", name)).Footer.Should().Be(name + " tarafından atıldı");
        Card(cards.CoinFlip("tr", name)).Footer.Should().Be(name + " tarafından atıldı");
        Card(cards.RandomNumber("tr", 100, null, name)).Footer.Should().Be(name + " için seçildi");
        Card(cards.Choose("tr", "CS2, Valheim", name)).Footer.Should().Be(name + " için seçildi");
    }

    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@123456789012345678>")]
    [InlineData("<@!123456789012345678>")]
    [InlineData("<@&123456789012345678>")]
    [InlineData("**Hasom** __x__ https://example.com")]
    public void A_mention_like_display_name_never_becomes_a_mention_or_a_link(string name)
    {
        var cards = Cards(new ScriptedRandom(1));
        foreach (var card in new[]
                 {
                     Card(cards.Dice("tr", "2d6", name)), Card(cards.CoinFlip("tr", name)),
                     Card(cards.RandomNumber("tr", 100, null, name)), Card(cards.Choose("tr", "CS2, Valheim", name)),
                 })
        {
            foreach (var text in new[] { card.Title, card.Description, card.Footer })
                DiscordText.RawMentionPattern().IsMatch(text ?? "").Should().BeFalse(text);
            card.Footer.Should().NotContain("://", "links are defused");
        }
    }

    [Fact]
    public void Display_name_source_is_the_member_name_then_global_name_then_username()
    {
        // IGuildUser.DisplayName already is Discord.Net's nickname → global name → username for a member (TSQ Quote uses the same).
        var member = InterfaceFake.Create<global::Discord.IGuildUser>(new()
        {
            ["DisplayName"] = "Hasom (sunucu)",
            ["GlobalName"] = "Hasom",
            ["Username"] = "hasom",
        });
        RandomizerCommands.DisplayNameOf(member).Should().Be("Hasom (sunucu)");

        var user = InterfaceFake.Create<global::Discord.IUser>(new() { ["GlobalName"] = "Oykeli", ["Username"] = "oykeli" });
        RandomizerCommands.DisplayNameOf(user).Should().Be("Oykeli");

        var plain = InterfaceFake.Create<global::Discord.IUser>(new() { ["Username"] = "oykeli" });
        RandomizerCommands.DisplayNameOf(plain).Should().Be("oykeli");
    }

    [Fact]
    public void English_cards()
    {
        Card(Cards(new ScriptedRandom(4, 6)).Dice("en", "2d6", "Toro")).Should().Match<MessageEmbed>(c =>
            c.Title == "🎲 Rolled 2d6" && c.Description == "Dice: `4` `6`\nTotal: **10**" && c.Footer == "Rolled by Toro");
        Card(Cards(new ScriptedRandom(1)).CoinFlip("en", "Toro")).Description.Should().Be("**HEADS**");
        Cards(new ScriptedRandom(0)).Choose("en", "a", "Toro").Refusal.Should().Be("Enter at least 2 different options.");
    }
}
