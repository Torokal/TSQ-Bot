using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Öngörü without Discord or a database: coin and odds arithmetic (integer units, rounding down, overflow), the creation
/// form (every field, every line, never a silent repair or cut), lock dates in Türkiye time, the daily day boundary, the
/// access rules, the card layout and buttons (2/3/5/25 outcomes within Discord's limits, no pings) and the announcement.
/// </summary>
public sealed class PredictionDomainTests
{
    private static readonly TimeZoneInfo Turkey = GuildTime.TryResolve("Europe/Istanbul", out var zone) ? zone : throw new InvalidOperationException();
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero); // 15:00 in Türkiye

    public static ILocalizer Localizer() => new LocalizationCatalog(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(PredictionsModule).Assembly, "ToroSquad.Modules.Predictions.Localization"),
    ]);

    // ---- coins ----

    [Theory]
    [InlineData("100", 10_000)]
    [InlineData("1", 100)]
    [InlineData("12.5", 1_250)]
    [InlineData("12,50", 1_250)]
    [InlineData(" 7,05 ", 705)]
    [InlineData("1000000", 100_000_000)]
    public void Amounts_are_parsed_to_units_exactly(string text, long minor)
    {
        var parsed = Coins.ParseAmount(text);
        parsed.Ok.Should().BeTrue();
        parsed.Minor.Should().Be(minor);
    }

    [Theory]
    [InlineData("", AmountError.Empty)]
    [InlineData("  ", AmountError.Empty)]
    [InlineData("-5", AmountError.Format)]
    [InlineData("+5", AmountError.Format)]
    [InlineData("1e3", AmountError.Format)]
    [InlineData("NaN", AmountError.Format)]
    [InlineData("Infinity", AmountError.Format)]
    [InlineData("1 000", AmountError.Format)]
    [InlineData("1.000,00", AmountError.Format)]
    [InlineData("12.345", AmountError.TooManyDecimals)]
    [InlineData("0", AmountError.BelowMinimum)]
    [InlineData("0.99", AmountError.BelowMinimum)]
    public void Bad_amounts_are_refused_never_guessed(string text, AmountError error) => Coins.ParseAmount(text).Error.Should().Be(error);

    [Theory]
    [InlineData(10_000, 110, 11_000)] // 100 × 1.10 = 110
    [InlineData(10_000, 310, 31_000)]
    [InlineData(101, 101, 102)] // 1.01 × 1.01 = 1.0201 → 1.02 (rounded down)
    [InlineData(333, 333, 1_108)] // 3.33 × 3.33 = 11.0889 → 11.08
    [InlineData(199, 150, 298)] // 1.99 × 1.5 = 2.985 → 2.98
    public void Payout_is_stake_times_fixed_odds_rounded_down(long stake, int odds, long payout)
    {
        Coins.Payout(stake, odds).Should().Be(payout);
        Coins.NetGain(stake, odds).Should().Be(payout - stake);
    }

    [Fact]
    public void Payout_and_sums_throw_instead_of_overflowing()
    {
        FluentActions.Invoking(() => Coins.Payout(long.MaxValue / 10, Odds.MaxX100)).Should().Throw<OverflowException>();
        FluentActions.Invoking(() => Coins.Add(long.MaxValue, 1)).Should().Throw<OverflowException>();
        FluentActions.Invoking(() => Coins.Subtract(long.MinValue, 1)).Should().Throw<OverflowException>();
        Coins.Payout(Coins.WalletCeilingMinor / Odds.MaxX100 * 100, Odds.MaxX100).Should().BeLessThanOrEqualTo(Coins.WalletCeilingMinor);
    }

    [Theory]
    [InlineData(104_700, "tr", "1047")]
    [InlineData(104_750, "tr", "1047,50")]
    [InlineData(104_705, "en", "1047.05")]
    [InlineData(1_250_000, "tr", "12.500")]
    [InlineData(1_250_000, "en", "12,500")]
    [InlineData(123_456_789_00, "tr", "123.456.789")]
    [InlineData(0, "tr", "0")]
    public void Amounts_are_shown_in_whole_coins_with_two_decimals_only_when_needed(long minor, string language, string text) =>
        Coins.Format(minor, language).Should().Be(text);

    // ---- odds ----

    [Theory]
    [InlineData("1.10", 110)]
    [InlineData("1,10", 110)]
    [InlineData("2.3", 230)]
    [InlineData("3", 300)]
    [InlineData("1.01", 101)]
    [InlineData("1000", 100_000)]
    [InlineData("1000.00", 100_000)]
    public void Odds_accept_dot_or_comma_with_at_most_two_decimals(string text, int x100)
    {
        var odds = Odds.Parse(text);
        odds.Ok.Should().BeTrue(text);
        odds.X100.Should().Be(x100);
    }

    [Theory]
    [InlineData("1.00", OddsError.OutOfRange)]
    [InlineData("1", OddsError.OutOfRange)]
    [InlineData("0", OddsError.OutOfRange)]
    [InlineData("1000.01", OddsError.OutOfRange)]
    [InlineData("-2", OddsError.Format)]
    [InlineData("NaN", OddsError.Format)]
    [InlineData("Infinity", OddsError.Format)]
    [InlineData("∞", OddsError.Format)]
    [InlineData("1e2", OddsError.Format)]
    [InlineData("2,005", OddsError.TooManyDecimals)]
    [InlineData("x2", OddsError.Format)]
    [InlineData("", OddsError.Empty)]
    public void Invalid_odds_are_refused_never_replaced_by_the_default(string text, OddsError error) => Odds.Parse(text).Error.Should().Be(error);

    // ---- the form ----

    private static PredictionFormCheck Parse(string? title = "Galatasaray - Fenerbahçe Maç Sonucu Ne Olur?", string? outcomes = null, string? lockAt = null,
        string? rules = null, int max = 25) =>
        PredictionForm.Parse(new PredictionFormValues(title, outcomes ?? "Galatasaray Kazanır | 1.10\nBeraberlik | 2.30\nFenerbahçe Kazanır | 3.10", lockAt, rules),
            200, max, Turkey, Now);

    private static string Lines(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => $"Sonuç {i} | {1 + i / 10.0:0.00}".Replace(',', '.')));

    [Fact]
    public void The_example_form_is_valid_with_its_fixed_odds_in_order()
    {
        var check = Parse(rules: "Normal sürenin sonucu esas alınır; uzatmalar dahil değildir.");
        check.Ok.Should().BeTrue(string.Join(", ", check.Errors.Select(e => e.Key)));
        check.Input!.Outcomes.Select(o => (o.Label, o.OddsX100)).Should().Equal(("Galatasaray Kazanır", 110), ("Beraberlik", 230), ("Fenerbahçe Kazanır", 310));
        check.Input.LockAt.Should().BeNull("an empty lock time means a manual lock");
        check.Input.UsesDefaultOdds.Should().BeFalse();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(25)]
    public void Two_to_twenty_five_outcomes_are_accepted(int count) => Parse(outcomes: Lines(count)).Input!.Outcomes.Should().HaveCount(count);

    [Fact]
    public void A_26th_outcome_is_refused_not_cut_off()
    {
        var check = Parse(outcomes: Lines(26));
        check.Ok.Should().BeFalse();
        check.Errors.Should().ContainSingle(e => e.Key == "predictions.form.error.outcomes_too_many").Which.Args.Should().Equal(25, 26);
        Parse(outcomes: Lines(1)).Errors.Should().ContainSingle(e => e.Key == "predictions.form.error.outcomes_too_few");
        Parse(outcomes: Lines(4), max: 3).Errors.Should().ContainSingle(e => e.Key == "predictions.form.error.outcomes_too_many");
    }

    [Fact]
    public void Blank_lines_are_ignored_and_a_line_without_odds_uses_the_default_which_the_preview_mentions()
    {
        var check = Parse(outcomes: "\n  Evet | 1,50\r\n\r\n   \nHayır\n\n");
        check.Input!.Outcomes.Select(o => (o.Label, o.OddsX100, o.DefaultOdds)).Should().Equal(("Evet", 150, false), ("Hayır", 200, true));
        check.Input.UsesDefaultOdds.Should().BeTrue();
    }

    [Fact]
    public void Every_bad_line_is_reported_with_its_line_number_and_nothing_is_repaired()
    {
        var check = Parse(outcomes: "A | 1.10\n\nA | 2\nB | abc\nC | 1.005\nD | 0.5\n | 2\nE | F | 2\nG |\n" + new string('x', 81) + " | 2");
        check.Ok.Should().BeFalse();
        check.Errors.Select(e => (e.Key, (int)e.Args[0])).Should().BeEquivalentTo(new[]
        {
            ("predictions.form.error.outcome_duplicate", 3),
            ("predictions.form.error.odds_format", 4),
            ("predictions.form.error.odds_decimals", 5),
            ("predictions.form.error.odds_range", 6),
            ("predictions.form.error.outcome_empty", 7),
            ("predictions.form.error.outcome_separator", 8),
            ("predictions.form.error.odds_empty", 9),
            ("predictions.form.error.outcome_length", 10),
        });
        check.Errors.Single(e => e.Key == "predictions.form.error.outcome_duplicate").Args.Should().Equal(3, 1);
    }

    [Theory]
    [InlineData("İstanbul Kazanır", "istanbul kazanır")]
    [InlineData("Beraberlik", "  BERABERLİK ")]
    [InlineData("Maç   uzarsa", "maç uzarsa")]
    public void Duplicates_are_found_after_trimming_collapsing_spaces_and_turkish_lowercasing(string first, string second) =>
        Parse(outcomes: first + " | 2\n" + second + " | 3").Errors.Should().ContainSingle(e => e.Key == "predictions.form.error.outcome_duplicate");

    [Theory]
    [InlineData("", "predictions.form.error.title_required")]
    [InlineData("Kim?", "predictions.form.error.title_length")]
    public void The_title_is_required_and_bounded(string title, string key)
    {
        Parse(title: title).Errors.Should().ContainSingle(e => e.Key == key);
        Parse(title: new string('a', 201)).Errors.Should().ContainSingle(e => e.Key == "predictions.form.error.title_length");
        Parse(title: new string('a', 200)).Ok.Should().BeTrue();
        Parse(title: "Kimİ?").Ok.Should().BeTrue("five characters");
    }

    [Fact]
    public void The_rules_are_optional_and_at_most_1000_characters()
    {
        Parse(rules: new string('k', 1000)).Input!.Rules.Should().HaveLength(1000);
        Parse(rules: new string('k', 1001)).Errors.Should().ContainSingle(e => e.Key == "predictions.form.error.rules_length");
        Parse(rules: "   ").Input!.Rules.Should().BeNull();
    }

    [Fact]
    public void All_field_problems_are_reported_together()
    {
        var check = Parse(title: "", outcomes: "Tek", lockAt: "yarın", rules: new string('r', 1200));
        check.Errors.Select(e => e.Key).Should().BeEquivalentTo(
            "predictions.form.error.title_required", "predictions.form.error.outcomes_too_few", "predictions.form.error.lock_format", "predictions.form.error.rules_length");
    }

    // ---- lock dates ----

    [Theory]
    [InlineData("05.10.2026 20:00", 2026, 10, 5, 17, 0)] // Türkiye is UTC+3 all year
    [InlineData("5.10.2026 8:05", 2026, 10, 5, 5, 5)]
    [InlineData("2026-10-05 20:00", 2026, 10, 5, 17, 0)]
    [InlineData("29.09.2026 15:01", 2026, 9, 29, 12, 1)]
    public void Lock_times_are_turkiye_wall_clock_converted_to_utc(string text, int y, int m, int d, int h, int min)
    {
        var (at, error) = PredictionLockDate.Resolve(text, Turkey, Now);
        error.Should().Be(LockDateError.None);
        at.Should().Be(new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("29.09.2026 15:00", LockDateError.NotInFuture)] // now
    [InlineData("28.09.2026 20:00", LockDateError.NotInFuture)]
    [InlineData("01.10.2027 20:00", LockDateError.TooFar)]
    [InlineData("31.02.2026 20:00", LockDateError.Format)]
    [InlineData("05.10.26 20:00", LockDateError.Format)] // no two-digit years
    [InlineData("yarın 20:00", LockDateError.Format)]
    [InlineData("05/10/2026 20:00", LockDateError.Format)]
    [InlineData("05.10.2026 24:00", LockDateError.Format)]
    public void Past_far_or_unreadable_lock_times_are_refused(string text, LockDateError error) =>
        PredictionLockDate.Resolve(text, Turkey, Now).Error.Should().Be(error);

    [Fact]
    public void Wall_clock_times_that_do_not_exist_or_exist_twice_are_refused_not_guessed()
    {
        GuildTime.TryResolve("Europe/Berlin", out var berlin).Should().BeTrue();
        var march = new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
        PredictionLockDate.Resolve("28.03.2027 02:30", berlin, march).Error.Should().Be(LockDateError.NotInTimeZone);
        PredictionLockDate.Resolve("31.10.2027 02:30", berlin, march).Error.Should().Be(LockDateError.Ambiguous);
    }

    [Fact]
    public void The_daily_day_starts_at_midnight_turkiye_time_not_after_24_hours()
    {
        var beforeMidnight = new DateTimeOffset(2026, 9, 29, 20, 59, 59, TimeSpan.Zero); // 23:59:59 in Türkiye
        var midnight = beforeMidnight.AddSeconds(1);
        TurkeyCalendar.DayKey(beforeMidnight, Turkey).Should().Be(20260929);
        TurkeyCalendar.DayKey(midnight, Turkey).Should().Be(20260930);
        TurkeyCalendar.NextDayStart(beforeMidnight, Turkey).Should().Be(midnight);
        TurkeyCalendar.NextDayStart(midnight, Turkey).Should().Be(midnight.AddDays(1));
    }

    // ---- access ----

    private static readonly RoleId Creator = new(PredictionsOptions.DefaultCreatorRoleId);

    private static ActorContext Actor(ulong user, GuildPermission permissions = GuildPermission.ViewChannel, bool owner = false, params RoleId[] roles) =>
        new(new GuildId(1), new UserId(user), permissions, roles, owner, 1);

    [Fact]
    public void Creating_needs_the_role_administrator_alone_is_not_enough()
    {
        PredictionAccess.CanCreate(Actor(1, roles: Creator), Creator).Should().BeTrue();
        PredictionAccess.CanCreate(Actor(2, GuildPermission.Administrator), Creator).Should().BeFalse();
        PredictionAccess.CanCreate(Actor(3, owner: true), Creator).Should().BeFalse();
        PredictionAccess.CanCreate(Actor(4, GuildPermission.ManageGuild), Creator).Should().BeFalse();
    }

    [Fact]
    public void Managing_is_the_creator_with_the_role_or_an_administrator_ending_the_tournament_is_administrators_only()
    {
        var creator = new UserId(1);
        PredictionAccess.CanManage(Actor(1, roles: Creator), Creator, creator).Should().BeTrue();
        PredictionAccess.CanManage(Actor(1), Creator, creator).Should().BeFalse("the role was removed");
        PredictionAccess.CanManage(Actor(2, roles: Creator), Creator, creator).Should().BeFalse("another creator's prediction");
        PredictionAccess.CanManage(Actor(3, GuildPermission.Administrator), Creator, creator).Should().BeTrue();
        PredictionAccess.CanManage(Actor(4, owner: true), Creator, creator).Should().BeTrue();
        PredictionAccess.CanManage(Actor(5, GuildPermission.ManageGuild | GuildPermission.ManageRoles), Creator, creator).Should().BeFalse("Manage Server is not Administrator");
        PredictionAccess.CanManage(Actor(0, roles: Creator), Creator, new UserId(0)).Should().BeFalse("a deleted creator id matches nobody");

        PredictionAccess.CanEndTournament(Actor(1, roles: Creator)).Should().BeFalse();
        PredictionAccess.CanEndTournament(Actor(3, GuildPermission.Administrator)).Should().BeTrue();
        PredictionAccess.CanEndTournament(Actor(4, owner: true)).Should().BeTrue();
    }

    // ---- the card ----

    private static readonly PredictionCards Cards = new(Localizer());

    public static PredictionView View(int outcomes, PredictionStatus status = PredictionStatus.Open, string? title = null, string? label = null, string? rules = null,
        DateTimeOffset? lockAt = null) => new(
        12, new GuildId(1), 1, 1, new ChannelId(PredictionsOptions.DefaultChannelId), new MessageId(99), new UserId(5), "Kaan",
        title ?? "Galatasaray - Fenerbahçe Maç Sonucu Ne Olur?", rules, lockAt, status, status == PredictionStatus.Locked ? PredictionLockReason.Manual : null,
        status == PredictionStatus.Locked ? Now : null,
        Enumerable.Range(1, outcomes).Select(i => new OutcomeView(100 + i, i, label ?? "Sonuç " + i, 100 + i * 7)).ToList(),
        3, 30_000, status == PredictionStatus.Settled ? 101 : null, status == PredictionStatus.Settled ? 1 : null, status == PredictionStatus.Settled ? 11_000 : null,
        status == PredictionStatus.Cancelled ? "Maç ertelendi" : null, status == PredictionStatus.Cancelled ? 30_000 : null, Now, Now, false, 1);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(25)]
    public void The_open_card_leads_with_the_question_lists_every_outcome_with_its_odds_and_offers_the_buttons(int outcomes)
    {
        var card = Cards.Render(View(outcomes), "tr");
        DiscordLimits.Validate(card).Should().BeEmpty();
        card.Mentions.Should().Be(MentionPolicy.None);
        card.Embed!.Description.Should().StartWith("## Galatasaray \\- Fenerbahçe Maç Sonucu Ne Olur?\n\n1️⃣ Sonuç 1\nOran: **1.07**\n\n2️⃣ Sonuç 2\nOran: **1.14**");
        if (outcomes == 25)
            card.Embed.Description.Should().Contain("🔟 Sonuç 10\nOran: **1.70**").And.Contain("**11.** Sonuç 11\nOran: **1.77**").And.Contain("**25.** Sonuç 25\nOran: **2.75**");
        card.Embed.Title.Should().Be("🟢 Katılım Açık");
        card.Embed.Footer.Should().Be("TSQ Öngörü #12 · Turnuva 1 · Oluşturan: Kaan");
        card.Select.Should().BeNull("no shared select state on the public card");
        card.Buttons!.Select(b => (b.Label, b.CustomId, b.Style, b.NewRow)).Should().Equal(
            ("🎯 Tahmin Yap", "tsq:pred:enter:12", MessageButtonStyle.Primary, false), ("🔒 Kilitle", "tsq:pred:lock:12", MessageButtonStyle.Secondary, true),
            ("✅ Sonuçlandır", "tsq:pred:settle:12", MessageButtonStyle.Success, false), ("↩️ İptal / İade", "tsq:pred:cancel:12", MessageButtonStyle.Danger, false));
        card.Embed.Fields.Should().Contain(f => f.Name == "⏳ Kilitlenme" && f.Value == "Manuel kilitlenecek");
        card.Embed.Fields.Should().Contain(f => f.Name == "👥 Katılım" && f.Value == "3 katılımcı\n🪙 300 TSQ Coin yatırıldı");
        Cards.Fits(View(outcomes)).Should().BeTrue();
        Cards.Render(View(outcomes), "tr", preview: true).Buttons.Should().BeNull("the private preview has its own buttons");
    }

    [Fact]
    public void The_lock_time_is_a_discord_timestamp_and_later_states_close_the_buttons()
    {
        var at = new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero);
        Cards.Render(View(3, lockAt: at), "tr").Embed!.Fields.Should().Contain(f => f.Value == "<t:1791219600:R>\n<t:1791219600:F>");

        var locked = Cards.Render(View(3, PredictionStatus.Locked), "tr");
        locked.Embed!.Title.Should().Be("🔒 Katılım Kapandı");
        locked.Buttons!.Select(b => (b.Label, b.Disabled, b.NewRow)).Should().Equal(("🔒 Katılım kapandı", true, false), ("✅ Sonuçlandır", false, true), ("↩️ İptal / İade", false, false));

        var settled = Cards.Render(View(3, PredictionStatus.Settled), "tr");
        settled.Buttons.Should().BeNull();
        settled.Embed!.Description.Should().Contain("🏆 **Sonuç 1**\nOran: **1.07**").And.Contain("2️⃣ Sonuç 2");
        settled.Embed.Fields.Single(f => f.Name == "🏆 Sonuç").Value.Should()
            .Be("🏆 **Sonuç 1** — 1.07\n1 kazanan · Toplam ödeme **110 TSQ Coin**\n👥 3 katılım · 🪙 300 TSQ Coin yatırılmıştı");

        var cancelled = Cards.Render(View(3, PredictionStatus.Cancelled), "tr");
        cancelled.Buttons.Should().BeNull();
        cancelled.Embed!.Title.Should().Be("⚠️ Öngörü İptal Edildi");
        cancelled.Embed.Fields.Single(f => f.Name == "Durum").Value.Should().Be("İptal nedeni: Maç ertelendi\nYatırılan **300 TSQ Coin** oyunculara tam olarak iade edildi.");
    }

    [Fact]
    public void Twenty_five_long_markdown_heavy_outcomes_still_fit_by_changing_the_layout_never_by_cutting()
    {
        var label = string.Concat(Enumerable.Repeat("*_|", 26)) + "ab"; // 80 characters, each escaped
        var view = View(25, title: new string('#', 200), label: null, rules: new string('*', 1000)) with
        {
            Outcomes = Enumerable.Range(1, 25).Select(i => new OutcomeView(100 + i, i, label[..^2] + (char)('A' + i % 26) + (char)('a' + i / 26), 100_000)).ToList(),
        };
        foreach (var language in new[] { "tr", "en" })
        {
            var card = Cards.Render(view, language);
            DiscordLimits.Validate(card).Should().BeEmpty();
            var text = card.Embed!.Description + string.Join("", card.Embed.Fields.Select(f => f.Value));
            foreach (var outcome in view.Outcomes)
                text.Should().Contain(outcome.Label[^2..], "every outcome is on the card");
        }

        Cards.Fits(view).Should().BeTrue();
    }

    [Fact]
    public void A_card_that_cannot_fit_in_every_state_is_reported_so_the_form_refuses_it()
    {
        var emoji = string.Concat(Enumerable.Repeat("😀", 80));
        var view = View(25, title: string.Concat(Enumerable.Repeat("😀", 200)), rules: string.Concat(Enumerable.Repeat("😀", 1000))) with
        {
            Outcomes = Enumerable.Range(1, 25).Select(i => new OutcomeView(100 + i, i, emoji + i, 100_000)).ToList(),
        };
        Cards.Fits(view).Should().BeFalse();
    }

    [Fact]
    public void Untrusted_text_cannot_ping_or_add_its_own_heading()
    {
        var card = Cards.Render(View(2, title: "@everyone <@&1> ## Büyük", label: "@here"), "tr");
        card.Embed!.Description.Should().NotContain("@everyone").And.NotContain("<@&1>").And.NotContain("\n## ").And.NotContain("@here");
        card.Mentions.PingsAnything.Should().BeFalse();
    }

    // ---- messages ----

    [Fact]
    public void The_entry_preview_shows_the_exact_payout_the_settlement_will_pay()
    {
        var messages = new PredictionMessages(Localizer(), Options.Create(new PredictionsOptions()));
        var payout = Coins.Payout(10_000, 110);
        var preview = messages.EntryPreview("Maç?", "Galatasaray Kazanır", 110, 10_000, payout, 90_000, "tok", "tr");
        preview.Embed!.Fields.Select(f => (f.Name, f.Value)).Should().Contain(new[]
        {
            ("Sabit oran", "1.10"), ("Yatırılacak", "100 TSQ Coin"), ("Kazanırsan toplam dönüş", "110 TSQ Coin"), ("Net kazanç", "10 TSQ Coin"),
            ("İşlem sonrası kullanılabilir", "900 TSQ Coin"),
        });
        preview.Buttons!.Select(b => (b.Label, b.CustomId)).Should().Equal(("✅ Onayla", "tsq:pred:entry-ok:tok"), ("✏️ Düzenle", "tsq:pred:entry-edit:tok"),
            ("Vazgeç", "tsq:pred:dismiss:tok"));
        preview.Mentions.Should().Be(MentionPolicy.None);
    }

    [Fact]
    public void The_closing_announcement_is_rendered_from_the_frozen_podiums_with_names_and_no_mention()
    {
        var messages = new PredictionMessages(Localizer(), Options.Create(new PredictionsOptions()));
        var coins = new[] { new StandingRow(1, new UserId(11), 284_000, 4, 5, "Toro"), new StandingRow(2, new UserId(12), 120_050, 2, 5, "@everyone") };
        var correct = new[] { new StandingRow(1, new UserId(11), 284_000, 12, 15, "Toro"), new StandingRow(2, new UserId(13), 100_000, 0, 0, null) };
        var message = messages.TournamentAnnouncement(new TournamentClosing(3, 4, 5, 7, coins, correct), "tr");
        message.Mentions.Should().Be(MentionPolicy.None);
        message.Embed!.Title.Should().Be("🏁 TSQ Öngörü · Turnuva 3 Sona Erdi");
        message.Embed.Fields.Select(f => f.Name).Should().Equal("💰 En Çok TSQ Coin", "🎯 En Çok Doğru Tahmin", "🔄 Yeni Turnuva Başladı");
        message.Embed.Fields[0].Value.Should().Be("🥇 **Toro** — **2840 TSQ Coin**\n🥈 **@​everyone** — **1200,50 TSQ Coin**");
        message.Embed.Fields[1].Value.Should().Be("🥇 **Toro** — **12** doğru / 15 sonuçlanan (%80)\n🥈 — — 0 doğru · Henüz sonuçlanmış tahmini yok");
        message.Embed.Fields[2].Value.Should().Be("Herkes yeni turnuvaya 1000 TSQ Coin ile başlar.");
        string.Join("", message.Embed.Fields.Select(f => f.Value)).Should().NotContain("<@");
        DiscordLimits.Validate(message).Should().BeEmpty();
    }

    [Fact]
    public void Options_default_to_the_production_values_and_are_validated()
    {
        var options = new PredictionsOptions();
        (options.ChannelId, options.CommandsChannelId, options.CreatorRoleId).Should().Be((1048525775919390840UL, 689814679056547857UL, 1233057768408350741UL));
        (options.InitialBalanceMinor, options.DailyMinCoins, options.DailyMaxCoins, options.DefaultOddsX100, options.MaxOutcomes).Should().Be((100_000L, 10, 100, 200, 25));
        options.Validate().Should().BeEmpty();
        new PredictionsOptions { DefaultOdds = 1.005m }.Validate().Should().ContainSingle();
        new PredictionsOptions { DefaultOdds = 1.00m }.Validate().Should().ContainSingle();
        new PredictionsOptions { MaxOutcomes = 26 }.Validate().Should().ContainSingle();
        new PredictionsOptions { DailyMinCoins = 50, DailyMaxCoins = 10 }.Validate().Should().ContainSingle();
        new PredictionsOptions { ChannelId = 5 }.Validate().Should().ContainSingle();
    }
}
