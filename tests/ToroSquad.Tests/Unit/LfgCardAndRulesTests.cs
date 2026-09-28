using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ LFG input rules and the card: one generic card for every game (the details are the creator's own text, never
/// interpreted), safe against mention/markdown/link injection, never pinging, buttons following the stored state.
/// </summary>
public sealed class LfgCardAndRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);
    private static readonly GuildId Guild = new(4242);

    private static LfgCardRenderer Renderer() => new(new LocalizationCatalog(
        [new LocalizationSource(typeof(ToroSquad.Modules.Lfg.LfgModule).Assembly, "ToroSquad.Modules.Lfg.Localization")]));

    private static LfgListingView Listing(string game, string? details, int max, LfgStatus status = LfgStatus.Open, params ulong[] players) =>
        new(7, Guild, new ChannelId(55), new MessageId(99), new UserId(players.Length > 0 ? players[0] : 1), game, details, max, status, T0, T0.AddHours(2),
            status is LfgStatus.Open or LfgStatus.Full ? null : T0.AddHours(1), (players.Length > 0 ? players : [1UL]).Select(p => new UserId(p)).ToList());

    [Theory]
    [InlineData("Deadlock", "Casual oynayacağız. Rank fark etmez.", 6)]
    [InlineData("Counter-Strike 2", "Premier • 18.500 rating civarı • mikrofon gerekli", 5)]
    [InlineData("World of Warcraft", "+10 Mythic+ • Tank ve Heal lazım • EU", 5)]
    [InlineData("Valheim", "Yagluth keseceğiz. Yemek ve portal malzemeleri hazır.", 6)]
    [InlineData("Satranç Turnuvası", "Hızlı oyun, 10+0", 2)]
    public void Every_game_gets_the_same_generic_card(string game, string details, int max)
    {
        var card = Renderer().Render(Listing(game, details, max, LfgStatus.Open, 1, 2), "tr");

        card.Content.Should().BeNull();
        card.Mentions.Should().Be(MentionPolicy.None);
        card.Embed!.Title.Should().Be("🎮 " + game);
        var description = card.Embed.Description!;
        description.Should().StartWith("<@1> ekip arıyor\n👥 **2 / " + max + "**\n📝 ");
        description.Should().Contain("**Katılanlar**\n<@1> · <@2>").And.NotContain("Belki").And.NotContain("Ses Odası");
        description.Should().Contain("\n\n🕘 **Başlangıç:** Şimdi\n⏰ <t:", "a listing without a start date started when it was opened");
        description.Should().EndWith("⏰ <t:" + T0.AddHours(2).ToUnixTimeSeconds() + ":R> kapanır");
        card.Embed.Footer.Should().Be("TSQ LFG · Oyuncu Bul");
        card.Buttons!.Select(b => (b.Label, b.CustomId, b.Disabled, b.Style, b.NewRow)).Should().Equal(
            ("Katıl", "tsq:lfg:join:7", false, MessageButtonStyle.Success, false),
            ("Belki", "tsq:lfg:maybe:7", false, MessageButtonStyle.Secondary, false),
            ("Ayrıl", "tsq:lfg:leave:7", false, MessageButtonStyle.Secondary, false),
            ("✏️ Düzenle", "tsq:lfg:edit:7", false, MessageButtonStyle.Secondary, true),
            ("İlanı Kapat", "tsq:lfg:close:7", false, MessageButtonStyle.Danger, false));
        DiscordLimits.Validate(card).Should().BeEmpty();
    }

    [Fact]
    public void Full_card_keeps_every_button_and_offers_the_waitlist_instead_of_join()
    {
        var card = Renderer().Render(Listing("Deadlock", null, 2, LfgStatus.Full, 1, 2), "tr");

        card.Embed!.Description.Should().Contain("✅ **Ekip dolu**").And.Contain("kapanır").And.NotContain("📝").And.NotContain("Bekleme");
        card.Buttons!.Select(b => b.Disabled).Should().Equal(false, false, false, false, false);
        (card.Buttons![0].Label, card.Buttons[0].CustomId, card.Buttons[0].Style).Should().Be(("🎟️ Sıraya Gir", "tsq:lfg:join:7", MessageButtonStyle.Primary),
            "the same join button: the server decides slot or waitlist");
        card.Embed.Color.Should().Be(LfgCardRenderer.FullColor);
    }

    [Theory]
    [InlineData(LfgStatus.Closed, "🔒 **İlan kapatıldı**")]
    [InlineData(LfgStatus.Expired, "⏰ Bu ekip ilanının süresi doldu.")]
    [InlineData(LfgStatus.Orphaned, "🔒 **İlan kapatıldı**")]
    public void Ended_card_keeps_its_history_and_disables_every_button(LfgStatus status, string line)
    {
        var card = Renderer().Render(Listing("Valheim", "Yagluth", 4, status, 1, 2, 3), "tr");

        card.Embed!.Description.Should().EndWith(line).And.Contain("<@1> · <@2> · <@3>").And.NotContain("kapanır");
        card.Buttons!.Should().OnlyContain(b => b.Disabled);
        card.Embed.Color.Should().Be(LfgCardRenderer.EndedColor);
    }

    [Fact]
    public void Creator_text_cannot_ping_format_or_link()
    {
        var card = Renderer().Render(Listing("@everyone <@&1> Game", "**bold** @here <@123> https://evil.example ||x||", 5), "tr");

        var visible = card.Embed!.Title + card.Embed.Description;
        DiscordText.RawMentionPattern().Matches(visible).Select(m => m.Value).Should().OnlyContain(m => m == "<@1>", "only the owner's rendered mention");
        visible.Should().NotContain("https://").And.NotContain("**bold**").And.NotContain("||x||");
        card.Mentions.PingsAnything.Should().BeFalse();
    }

    [Theory]
    [InlineData("tr", "🕘 **Başlangıç:** Şimdi", "⏰ <t:1790539200:R> kapanır")]
    [InlineData("en", "🕘 **Start:** Now", "⏰ Closes <t:1790539200:R>")]
    public void A_listing_without_a_start_date_says_it_starts_now_and_still_counts_down(string language, string start, string expires)
    {
        var listing = Listing("Deadlock", null, 4) with { EventAt = null, ExpiresAt = T0.AddHours(2) };

        var lines = Renderer().Render(listing, language).Embed!.Description!.Split('\n');

        lines.Should().Contain(start).And.Contain(expires);
        Array.IndexOf(lines, start).Should().Be(Array.IndexOf(lines, expires) - 1, "the start line comes right before the countdown");
        lines.Should().NotContain(l => l.Contains("🗓️", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tr", "🗓️ **Başlangıç:** ")]
    [InlineData("en", "🗓️ **Starts:** ")]
    public void A_scheduled_listing_shows_its_start_date(string language, string prefix)
    {
        var at = T0.AddHours(3);
        var listing = Listing("Deadlock", null, 4) with { EventAt = at, ExpiresAt = at.AddHours(2) };

        var description = Renderer().Render(listing, language).Embed!.Description!;

        description.Should().Contain(prefix + "<t:" + at.ToUnixTimeSeconds() + ":F> • <t:" + at.ToUnixTimeSeconds() + ":R>").And.NotContain("🕘");
    }

    [Fact]
    public void The_waitlist_is_shown_in_line_order_between_the_players_and_the_maybes_without_pinging()
    {
        var listing = Listing("Deadlock", null, 2, LfgStatus.Full, 1, 2) with
        {
            Waitlist = [new UserId(9), new UserId(3), new UserId(7)],
            Maybe = [new UserId(5)],
        };

        var card = Renderer().Render(listing, "tr");

        card.Embed!.Description.Should().Contain(
            "**Katılanlar**\n<@1> · <@2>\n🎟️ **Bekleme Listesi (3)**\n`1.` <@9> · `2.` <@3> · `3.` <@7>\n🤔 **Belki**\n<@5>");
        card.Embed.Description.Should().Contain("👥 **2 / 2**", "only Joined players count").And.Contain("✅ **Ekip dolu**");
        card.Mentions.Should().Be(MentionPolicy.None, "an edit that lists the waitlist pings nobody");
        DiscordLimits.Validate(card).Should().BeEmpty();
    }

    [Fact]
    public void A_long_waitlist_shows_the_first_twenty_and_the_full_count()
    {
        var listing = Listing("Deadlock", null, 2, LfgStatus.Full, 1, 2) with
        {
            Waitlist = Enumerable.Range(100, 28).Select(i => new UserId((ulong)i)).ToList(),
        };

        var description = Renderer().Render(listing, "tr").Embed!.Description!;

        description.Should().Contain("🎟️ **Bekleme Listesi (28)**").And.Contain("`20.` <@119> (+8)").And.NotContain("<@120>").And.NotContain("`21.`");
    }

    [Theory]
    [InlineData(LfgStatus.Open, "tr", "Katıl", MessageButtonStyle.Success)]
    [InlineData(LfgStatus.Full, "tr", "🎟️ Sıraya Gir", MessageButtonStyle.Primary)]
    [InlineData(LfgStatus.Open, "en", "Join", MessageButtonStyle.Success)]
    [InlineData(LfgStatus.Full, "en", "🎟️ Join Waitlist", MessageButtonStyle.Primary)]
    public void The_first_button_joins_while_open_and_queues_while_full_and_is_the_same_action(LfgStatus status, string language, string label,
        MessageButtonStyle style)
    {
        var join = Renderer().Render(Listing("Deadlock", null, 2, status, 1, 2), language).Buttons![0];

        (join.Label, join.CustomId, join.Disabled, join.Style).Should().Be((label, "tsq:lfg:join:7", false, style));
    }

    [Fact]
    public void English_guilds_get_the_english_card()
    {
        var card = Renderer().Render(Listing("Minecraft", null, 4), "en");

        card.Embed!.Description.Should().StartWith("<@1> is looking for players").And.Contain("⏰ Closes <t:");
        card.Buttons!.Select(b => b.Label).Should().Equal("Join", "Maybe", "Leave", "✏️ Edit", "Close listing");
    }

    [Theory]
    [InlineData("  Deadlock  ", "Deadlock")]
    [InlineData("Baldur's\tGate   3", "Baldur's Gate 3")]
    [InlineData("GTA‮V", "GTAV")] // bidi override dropped
    [InlineData("Helldivers​ 2", "Helldivers 2")] // zero-width space dropped
    public void Game_names_are_normalized_but_never_restricted_to_a_list(string input, string expected)
    {
        var (draft, error) = LfgRules.Validate(input, null, 4, null, 20, 120);

        error.Should().Be(LfgDraftError.None);
        draft!.GameName.Should().Be(expected);
        draft.Duration.Should().Be(TimeSpan.FromHours(2));
    }

    [Theory]
    [InlineData(null, 4, null, LfgDraftError.GameMissing)]
    [InlineData("   ", 4, null, LfgDraftError.GameMissing)]
    [InlineData("X", 4, null, LfgDraftError.GameTooShort)]
    [InlineData("A game name that is definitely far too long for any card!", 4, null, LfgDraftError.GameTooLong)]
    [InlineData("CS2", 1, null, LfgDraftError.PlayersOutOfRange)]
    [InlineData("CS2", 21, null, LfgDraftError.PlayersOutOfRange)]
    [InlineData("CS2", 5000, null, LfgDraftError.PlayersOutOfRange)]
    [InlineData("CS2", 5, 45, LfgDraftError.DurationInvalid)]
    public void Invalid_input_is_refused(string? game, int players, int? minutes, LfgDraftError expected) =>
        LfgRules.Validate(game, null, players, minutes, 20, 120).Error.Should().Be(expected);

    [Fact]
    public void Details_are_bounded_in_characters_not_utf16_units()
    {
        LfgRules.Validate("Valheim", new string('x', LfgRules.DetailsMaxLength), 4, null, 20, 120).Error.Should().Be(LfgDraftError.None);
        LfgRules.Validate("Valheim", new string('x', LfgRules.DetailsMaxLength + 1), 4, null, 20, 120).Error.Should().Be(LfgDraftError.DetailsTooLong);
        var emoji = string.Concat(Enumerable.Repeat("🎮", LfgRules.GameNameMaxLength));
        LfgRules.Validate(emoji, null, 4, null, 20, 120).Error.Should().Be(LfgDraftError.None);
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(180, 180)]
    [InlineData(null, 90)]
    public void Duration_is_one_of_the_offered_choices_or_the_configured_default(int? chosen, int expectedMinutes) =>
        LfgRules.Validate("Minecraft", null, 3, chosen, 20, 90).Draft!.Duration.Should().Be(TimeSpan.FromMinutes(expectedMinutes));

    [Fact]
    public void Options_validation_rejects_unsafe_limits()
    {
        new LfgOptions().Validate().Should().BeEmpty();
        new LfgOptions { DefaultExpirationMinutes = 5 }.Validate().Should().ContainSingle();
        new LfgOptions { MaxActiveListingsPerUser = 0 }.Validate().Should().ContainSingle();
        new LfgOptions { MaxPlayersPerListing = LfgRules.HardMaxPlayers + 1 }.Validate().Should().ContainSingle();
    }

    [Fact]
    public void Button_style_is_left_out_of_stored_payloads_while_default_so_existing_hashes_are_unchanged()
    {
        var plain = new OutgoingMessage("x", null, MentionPolicy.None, [new MessageButton("Watch", null, "https://twitch.tv/x")]);
        ToroSquad.Infrastructure.Delivery.PayloadSerializer.Serialize(plain).Should().NotContain("style");
        var styled = plain with { Buttons = [new MessageButton("Join", "id", null, Style: MessageButtonStyle.Success)] };
        ToroSquad.Infrastructure.Delivery.PayloadSerializer.Serialize(styled).Should().Contain("\"style\":2");
        ToroSquad.Discord.Transport.DiscordConversions.ToComponents(styled.Buttons)!.Components.Should().ContainSingle();
    }
}
