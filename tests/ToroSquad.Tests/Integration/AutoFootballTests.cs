using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Bot;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Application.Automation;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.PredictionTestKit;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The automatic football opener end to end on the real SQLite database with a SYNTHETIC provider (made-up matches and
/// prices — proof of the rules, not of the real provider's coverage) and the fake Discord transport: the three modes
/// (Disabled: no request; Observe: its own rows only; Live: real predictions through the normal pipeline), the Türkiye-day
/// schedule, one match = one prediction (derby, two processes, restarts, new tournaments, cancelled cards), the odds rules
/// (no default price, bounded attempts that survive a restart), the quota guard, uncertain delivery, late or stopped posts,
/// schedule changes after publishing, management rights, no wallet or leaderboard place for the automation, and the
/// additive migration.
/// </summary>
public sealed class AutoFootballTests : IAsyncLifetime
{
    /// <summary>Monday 5 October 2026, 08:00 in Türkiye.</summary>
    private static readonly DateTimeOffset Morning = new(2026, 10, 5, 5, 0, 0, TimeSpan.Zero);

    /// <summary>20:00 in Türkiye.</summary>
    private static readonly DateTimeOffset Kickoff = new(2026, 10, 5, 17, 0, 0, TimeSpan.Zero);

    /// <summary>09:00 in Türkiye.</summary>
    private static readonly DateTimeOffset NineTr = new(2026, 10, 5, 6, 0, 0, TimeSpan.Zero);

    private readonly FakeFootballOdds _odds = new();
    private PredictionTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await AutoFootballKit.CreateAsync(_odds, "Live", Morning);

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    private ProviderEvent Derby() => _odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff);

    private async Task<PredictionView> PublishDerbyAsync()
    {
        _odds.Price(Derby());
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        var prediction = (await _kit.AutoPredictionsAsync()).Should().ContainSingle().Subject;
        return (await _kit.Service(s => s.GetAsync(prediction.Id, Ct)))!;
    }

    // ---- modes ----

    [Fact]
    public async Task Disabled_makes_no_request_and_plans_nothing_while_manual_predictions_work()
    {
        var odds = new FakeFootballOdds();
        await using var kit = await AutoFootballKit.CreateAsync(odds, "Disabled", Morning);
        odds.Price(odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff));
        for (var i = 0; i < 3; i++)
        {
            (await kit.PassAsync()).Ran.Should().BeFalse();
            kit.Host.Clock.Advance(TimeSpan.FromHours(2));
        }

        odds.Calls.Should().BeEmpty("Disabled: zero HTTP");
        (await kit.AutoRowsAsync()).Should().BeEmpty();
        (await kit.CreatePredictionAsync()).Status.Should().Be(PredictionStatus.Open, "the manual predictions are untouched");
    }

    [Theory]
    [InlineData("no-key")]
    [InlineData("exchange")]
    [InlineData("typo")]
    [InlineData("two-guilds")]
    public async Task A_missing_key_a_bad_setting_or_no_single_guild_keeps_it_off_without_stopping_anything(string problem)
    {
        var odds = new FakeFootballOdds { IsConfigured = problem != "no-key" };
        var extra = problem switch
        {
            "exchange" => new Dictionary<string, string?> { ["Predictions:Automation:BookmakerPriority:0"] = "betfair_ex_eu" },
            "typo" => new Dictionary<string, string?> { ["Predictions:Automation:PublishLocalTime"] = "nine" },
            "two-guilds" => new Dictionary<string, string?> { ["Discord:AllowedGuildIds:1"] = "778" },
            _ => null,
        };
        await using var kit = await AutoFootballKit.CreateAsync(odds, "Live", NineTr, extra);
        odds.Price(odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff));
        (await kit.PassAsync()).Mode.Should().Be(AutomationMode.Disabled);
        odds.Calls.Should().BeEmpty();

        var health = await kit.Host.InScopeAsync(sp => sp.GetRequiredService<AutoFootballService>().HealthAsync(Ct));
        health[0].State.Should().Be(problem is "exchange" or "typo" ? HealthState.Degraded : HealthState.NotConfigured);
        string.Join(" ", health.SelectMany(h => (h.Args ?? []).Select(a => a?.ToString()))).Should().NotContain("synthetic", "never a secret value");
        (await kit.CreatePredictionAsync()).Status.Should().Be(PredictionStatus.Open);
    }

    [Fact]
    public async Task Observe_records_the_decision_in_its_own_rows_only_and_live_later_opens_the_same_match()
    {
        var odds = new FakeFootballOdds();
        await using var kit = await AutoFootballKit.CreateAsync(odds, "Observe", Morning);
        odds.Price(odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff));
        await kit.PassAsync();
        kit.Host.Clock.SetUtcNow(NineTr);
        (await kit.PassAsync()).Observed.Should().Be(1);

        var observed = (await kit.AutoRowsAsync()).Should().ContainSingle().Subject;
        (observed.Mode, observed.State, observed.PredictionId, observed.HomeOddsX100, observed.DrawOddsX100, observed.AwayOddsX100, observed.RawPrices)
            .Should().Be((AutomationMode.Observe, AutoEventState.Observed, (long?)null, 185, 340, 420, "1.85|3.40|4.20"));
        (await kit.CountAsync<PredictionEntity>()).Should().Be(0);
        (await kit.CountAsync<PredictionWalletEntity>()).Should().Be(0, "no wallet, no starting balance");
        (await kit.CountAsync<PredictionTournamentEntity>()).Should().Be(0, "no tournament opened");
        kit.Transport.Messages.Should().BeEmpty("nothing is sent to Discord");

        // Switching to Live (discovery runs at its next interval): the observed row does not count as published.
        await using var live = await AutoFootballKit.CreateAsync(odds, "Live", NineTr + TimeSpan.FromMinutes(20), directory: kit.Host.Directory, transport: kit.Transport);
        await live.PassAsync();
        (await live.AutoPredictionsAsync()).Should().ContainSingle();
        (await live.AutoRowsAsync()).Select(r => (r.Mode, r.State)).Should().BeEquivalentTo(new[]
            { (AutomationMode.Observe, AutoEventState.Observed), (AutomationMode.Live, AutoEventState.Published) });
    }

    // ---- live ----

    [Fact]
    public async Task Live_opens_one_card_at_nine_turkiye_time_with_the_fixed_odds_and_no_tournament_on_it()
    {
        _odds.Price(Derby());
        (await _kit.PassAsync()).Discovered.Should().Be(1);
        _odds.OddsCalls.Should().Be(0, "before its publish time no odds are fetched");
        _kit.Host.Clock.SetUtcNow(NineTr - TimeSpan.FromSeconds(1));
        await _kit.PassAsync();
        _kit.Transport.Messages.Should().BeEmpty();

        _kit.Host.Clock.SetUtcNow(NineTr);
        (await _kit.PassAsync()).Published.Should().Be(1);
        var row = (await _kit.AutoPredictionsAsync()).Should().ContainSingle().Subject;
        (row.Origin, row.CreatorUserId, row.CreatorName, row.Status, row.LockAt).Should()
            .Be((PredictionOrigin.AutoFootball, 0UL, "", PredictionStatus.Open, Kickoff - TimeSpan.FromMinutes(2)));
        row.Title.Should().Be("Galatasaray - Fenerbahçe maç sonucu ne olur?");
        var view = (await _kit.Service(s => s.GetAsync(row.Id, Ct)))!;
        view.Outcomes.Select(o => (o.Label, o.OddsX100)).Should().Equal(("Galatasaray kazanır", 185), ("Beraberlik", 340), ("Fenerbahçe kazanır", 420));
        view.Rules.Should().Contain("90 dakika").And.Contain("uzatma devreleri ve penaltı atışları dahil değildir").And.Contain("2 dakika önce kapanır");

        var card = Shown(_kit.Transport.Messages.Should().ContainSingle().Subject);
        card.Embed!.Footer.Should().Be($"TSQ Öngörü #{row.Id} · Otomatik · Sabit oran");
        card.Embed.Fields.Should().Contain(f => f.Name == "⚽ Planlanan başlama" && f.Value == "<t:1791219600:F>");
        card.Embed.Fields.Should().Contain(f => f.Name == "📈 Oran kaynağı" && f.Value.StartsWith("PINNACLE · <t:", StringComparison.Ordinal));
        card.Embed.Fields.Should().Contain(f => f.Name == "⏳ Kilitlenme" && f.Value == "<t:1791219480:R>\n<t:1791219480:F>", "entries lock 2 minutes before the planned kickoff");
        var text = string.Join("\n", new[] { card.Embed.Title, card.Embed.Description, card.Embed.Footer }.Concat(card.Embed.Fields.SelectMany(f => new[] { f.Name, f.Value })));
        text.Should().NotContain("Turnuva").And.NotContain("http").And.NotContain("bahis");
        card.Buttons!.Select(b => b.Label).Should().Equal("🎯 Tahmin Yap", "🔒 Kilitle", "✅ Sonuçlandır", "↩️ İptal / İade");
        card.Mentions.Should().Be(ToroSquad.Core.Messaging.MentionPolicy.None);

        (await _kit.CountAsync<PredictionWalletEntity>()).Should().Be(0, "the automation gets no wallet and no starting balance");
        (await _kit.CountAsync<PredictionTournamentEntity>()).Should().Be(1);
        (await _kit.AutoRowsAsync()).Single().State.Should().Be(AutoEventState.Published);

        // A real member plays exactly as on a manual card; the automation never appears on the boards.
        (await _kit.EnterAsync(Member(1), view, 3, "100")).Result.MessageKey.Should().Be("predictions.entry.done");
        (await _kit.WalletAsync(1))!.PendingMinor.Should().Be(10_000);
        var board = await _kit.Economy(e => e.LeaderboardAsync(Member(50), Commands, Ct));
        board.View!.Embed!.Fields[0].Value.Should().Contain("<@1>").And.NotContain("<@0>");
        (await _kit.Db(db => db.Set<PredictionWalletEntity>().CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_derby_of_two_followed_clubs_is_one_match_and_other_teams_are_ignored()
    {
        _odds.Price(Derby());
        _odds.Add(2, "Fenerbahce U19", "Galatasaray U19", Kickoff);
        _odds.Add(3, "Galatasaray W", "Trabzonspor", Kickoff);
        _odds.Add(4, "Trabzonspor", "Samsunspor", Kickoff);
        var bjk = _odds.Add(5, "Samsunspor", "Besiktas JK", Kickoff + TimeSpan.FromHours(-3));
        _odds.Price(bjk, 3.1m, 3.2m, 2.3m);
        await _kit.PassAsync();
        var rows = await _kit.AutoRowsAsync();
        rows.Select(r => (r.ExternalEventId, r.TrackedTeams)).Should().BeEquivalentTo(new[] { (FakeFootballOdds.Id(1), "GS,FB"), (FakeFootballOdds.Id(5), "BJK") });

        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        (await _kit.AutoPredictionsAsync()).Select(p => p.Title).Should().BeEquivalentTo("Galatasaray - Fenerbahçe maç sonucu ne olur?", "Samsunspor - Beşiktaş maç sonucu ne olur?");
        _odds.Count("odds").Should().Be(1, "the due matches of one competition share one call");
        await _kit.PassAsync();
        _kit.Host.Clock.Advance(TimeSpan.FromHours(1));
        await _kit.PassAsync();
        _odds.Count("odds").Should().Be(1, "a published match is never fetched again");
        (await _kit.AutoPredictionsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_late_start_opens_todays_missed_card_but_never_yesterdays_or_within_fifteen_minutes()
    {
        var odds = new FakeFootballOdds();
        var today = odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff);
        var soon = odds.Add(2, "Besiktas", "Konyaspor", new DateTimeOffset(2026, 10, 5, 7, 10, 0, TimeSpan.Zero));
        odds.Price(today);
        odds.Price(soon);
        // The bot was down at 09:00 and starts at 10:00 in Türkiye; the second match kicks off at 10:10.
        await using var kit = await AutoFootballKit.CreateAsync(odds, "Live", new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero));
        await kit.PassAsync();
        (await kit.AutoPredictionsAsync()).Should().ContainSingle().Which.Title.Should().StartWith("Galatasaray");
        (await kit.AutoRowsAsync()).Single(r => r.ExternalEventId == soon.Id).Should()
            .Match<PredictionAutoEventEntity>(r => r.State == AutoEventState.Skipped && r.Reason == AutoBlockReason.TooLateToPublish);

        // Next day: yesterday's match (it had no row yet in this database) is never planned.
        var odds2 = new FakeFootballOdds();
        odds2.Price(odds2.Add(9, "Galatasaray", "Rizespor", new DateTimeOffset(2026, 10, 5, 17, 0, 0, TimeSpan.Zero)));
        await using var nextDay = await AutoFootballKit.CreateAsync(odds2, "Live", new DateTimeOffset(2026, 10, 6, 7, 0, 0, TimeSpan.Zero));
        await nextDay.PassAsync();
        (await nextDay.AutoRowsAsync()).Should().BeEmpty();
        nextDay.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task An_early_kickoff_is_published_two_hours_before_it_on_the_same_day()
    {
        var early = _odds.Add(1, "Galatasaray", "Kasimpasa", new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero)); // 11:00 TR → 09:00 TR
        var morning = _odds.Add(2, "Fenerbahce", "Alanyaspor", new DateTimeOffset(2026, 10, 5, 7, 0, 0, TimeSpan.Zero)); // 10:00 TR → 08:00 TR
        _odds.Price(early);
        _odds.Price(morning);
        await _kit.PassAsync(); // 08:00 TR
        (await _kit.AutoPredictionsAsync()).Should().ContainSingle().Which.Title.Should().StartWith("Fenerbahçe", "10:00 kickoff: published at 08:00");
        (await _kit.AutoRowsAsync()).Single(r => r.ExternalEventId == early.Id).PublishAt.Should().Be(NineTr);
    }

    // ---- odds rules and attempts ----

    [Fact]
    public async Task Without_odds_attempts_are_limited_spread_restart_safe_and_end_in_a_skip_never_a_default_price()
    {
        Derby(); // no price at all
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        var first = (await _kit.AutoRowsAsync()).Single();
        (first.OddsAttempts, first.Reason, first.State).Should().Be((1, AutoBlockReason.NoOdds, AutoEventState.WaitingForOdds));
        first.NextAttemptAt.Should().BeAfter(NineTr + TimeSpan.FromMinutes(2), "never in the same second");

        // Restart: a new host on the same database continues the count.
        await using var restarted = await AutoFootballKit.CreateAsync(_odds, "Live", NineTr, directory: _kit.Host.Directory, transport: _kit.Transport);
        var calls = new List<DateTimeOffset>();
        for (var i = 0; i < 40 && (await restarted.AutoRowsAsync()).Single().State == AutoEventState.WaitingForOdds; i++)
        {
            var next = (await restarted.AutoRowsAsync()).Single().NextAttemptAt!.Value;
            restarted.Host.Clock.SetUtcNow(next);
            var before = _odds.OddsCalls;
            await restarted.PassAsync();
            if (_odds.OddsCalls > before)
                calls.Add(next);
        }

        var row = (await restarted.AutoRowsAsync()).Single();
        (row.OddsAttempts, row.State, row.Reason).Should().Be((4, AutoEventState.Skipped, AutoBlockReason.NoOdds), "4 attempts in total, across the restart");
        _odds.OddsCalls.Should().Be(4);
        calls.Should().OnlyContain(t => t < Kickoff - TimeSpan.FromMinutes(15), "every attempt within the safe window");
        (await restarted.AutoPredictionsAsync()).Should().BeEmpty("no card without real odds — never 2.00");
        restarted.Transport.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("stale", AutoBlockReason.StaleOdds)]
    [InlineData("exchange-only", AutoBlockReason.NoOdds)]
    [InlineData("invalid", AutoBlockReason.InvalidOdds)]
    public async Task Stale_exchange_only_or_invalid_odds_open_nothing_and_say_why(string kind, AutoBlockReason reason)
    {
        var e = Derby();
        switch (kind)
        {
            case "stale":
                _odds.Price(e, age: TimeSpan.FromMinutes(45));
                break;
            case "exchange-only":
                _odds.Price(e, bookmaker: "betfair_ex_eu");
                break;
            default:
                _odds.Price(e, home: 1.00m);
                break;
        }

        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        (await _kit.AutoRowsAsync()).Single().Reason.Should().Be(reason);
        (await _kit.AutoPredictionsAsync()).Should().BeEmpty();
    }

    // ---- quota and provider failures ----

    [Fact]
    public async Task The_credit_reserve_stops_paid_calls_but_not_free_discovery_or_manual_predictions()
    {
        _odds.Remaining = 51;
        var e = Derby();
        var other = _odds.Add(2, "Besiktas", "Goztepe", Kickoff, FakeFootballOdds.Europa);
        _odds.Price(e);
        _odds.Price(other);
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        _odds.OddsCalls.Should().Be(1, "51 left: one call (→ 50), then the reserve of 50 stops paid calls");
        (await _kit.AutoPredictionsAsync()).Should().ContainSingle();
        (await _kit.AutoRowsAsync()).Single(r => r.ExternalEventId == other.Id).Reason.Should().Be(AutoBlockReason.QuotaPaused);
        (await _kit.ProviderRowAsync())!.RemainingCredits.Should().Be(50);

        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        var free = _odds.FreeCalls;
        await _kit.PassAsync();
        _odds.OddsCalls.Should().Be(1);
        _odds.FreeCalls.Should().BeGreaterThan(free, "free discovery goes on");
        (await _kit.CreatePredictionAsync()).Status.Should().Be(PredictionStatus.Open);
    }

    [Fact]
    public async Task Without_usage_headers_only_one_blind_paid_call_is_made_and_the_count_survives_restarts_and_new_tournaments()
    {
        _odds.OmitUsageHeaders = true;
        var e = Derby();
        var other = _odds.Add(2, "Besiktas", "Goztepe", Kickoff, FakeFootballOdds.Europa);
        _odds.Price(e);
        _odds.Price(other);
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        _odds.OddsCalls.Should().Be(1, "usage unknown: one call to learn, not a series of blind calls");
        (await _kit.ProviderRowAsync())!.UnmeasuredCalls.Should().Be(1, "a call without headers is counted as spent");

        await using var restarted = await AutoFootballKit.CreateAsync(_odds, "Live", NineTr + TimeSpan.FromMinutes(30), directory: _kit.Host.Directory, transport: _kit.Transport);
        await restarted.PassAsync();
        _odds.OddsCalls.Should().Be(1, "the unmeasured call is remembered across a restart");
        _odds.OmitUsageHeaders = false;
        restarted.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await restarted.PassAsync(); // free discovery measures the usage again → the paid call is allowed
        _odds.OddsCalls.Should().Be(2);
    }

    [Fact]
    public async Task Rate_limits_pause_until_retry_after_and_auth_errors_stop_every_call_for_hours()
    {
        _odds.Price(Derby());
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        _odds.FailOdds = ProviderCallOutcome.RateLimited;
        _odds.RetryAfter = TimeSpan.FromMinutes(7);
        await _kit.PassAsync();
        var provider = (await _kit.ProviderRowAsync())!;
        (provider.PauseReason, provider.PausedUntil).Should().Be(("RATE_LIMITED", NineTr + TimeSpan.FromMinutes(7)));
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(3));
        var calls = _odds.Calls.Count;
        await _kit.PassAsync();
        _odds.Calls.Should().HaveCount(calls, "paused: no call before Retry-After");

        _odds.FailOdds = null;
        _odds.FailEverything = ProviderCallOutcome.AuthFailed;
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await _kit.PassAsync();
        (await _kit.ProviderRowAsync())!.PauseReason.Should().Be("AUTH_ERROR");
        calls = _odds.Calls.Count;
        for (var i = 0; i < 5; i++)
        {
            _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
            await _kit.PassAsync();
        }

        _odds.Calls.Should().HaveCount(calls, "401/403 is not retried in a loop");
        (await _kit.AutoPredictionsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProviderCallOutcome.Unavailable)]
    [InlineData(ProviderCallOutcome.Timeout)]
    [InlineData(ProviderCallOutcome.BadResponse)]
    public async Task Provider_errors_back_off_and_count_as_attempts(ProviderCallOutcome failure)
    {
        _odds.Price(Derby());
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        _odds.FailOdds = failure;
        await _kit.PassAsync();
        var row = (await _kit.AutoRowsAsync()).Single();
        (row.OddsAttempts, row.Reason).Should().Be((1, AutoBlockReason.ProviderUnavailable));
        (await _kit.ProviderRowAsync())!.Should().Match<PredictionAutoProviderEntity>(p => p.PauseReason == "PROVIDER_UNAVAILABLE" && p.ConsecutiveFailures == 1);
        if (failure == ProviderCallOutcome.Timeout)
            (await _kit.ProviderRowAsync())!.RemainingCredits.Should().Be(400, "the last measurement stays; the unknown call is counted separately");

        _odds.FailOdds = null;
        _kit.Host.Clock.SetUtcNow(row.NextAttemptAt!.Value);
        await _kit.PassAsync();
        (await _kit.AutoPredictionsAsync()).Should().ContainSingle("it recovers within the window");
    }

    // ---- one match, one prediction ----

    [Fact]
    public async Task Two_processes_on_one_database_open_a_match_once()
    {
        _odds.Price(Derby());
        await _kit.PassAsync();
        await using var second = await AutoFootballKit.CreateAsync(_odds, "Live", Morning, directory: _kit.Host.Directory, transport: _kit.Transport);
        _kit.Host.Clock.SetUtcNow(NineTr);
        second.Host.Clock.SetUtcNow(NineTr);
        await _kit.TogetherAsync(() => _kit.PassAsync(), () => second.PassAsync(), () => _kit.PassAsync());

        (await _kit.AutoPredictionsAsync()).Should().ContainSingle();
        (await _kit.AutoRowsAsync()).Should().ContainSingle();
        _kit.Transport.Messages.Should().ContainSingle();
        _odds.OddsCalls.Should().Be(1, "the attempt is claimed in the database before the call");
    }

    [Fact]
    public async Task A_restart_or_a_deploy_never_opens_the_published_match_again()
    {
        await PublishDerbyAsync();
        for (var i = 0; i < 2; i++)
        {
            await using var restarted = await AutoFootballKit.CreateAsync(_odds, "Live", NineTr + TimeSpan.FromMinutes(20 * (i + 1)), directory: _kit.Host.Directory,
                transport: _kit.Transport);
            await restarted.PassAsync();
            await restarted.TickAsync();
        }

        (await _kit.AutoPredictionsAsync()).Should().ContainSingle();
        _kit.Transport.Messages.Should().ContainSingle();
        _odds.OddsCalls.Should().Be(1);
    }

    [Fact]
    public async Task Without_a_followed_club_playing_no_paid_request_is_ever_made()
    {
        _odds.Add(1, "Trabzonspor", "Samsunspor", Kickoff);
        _odds.Add(2, "Galatasaray U19", "Fenerbahce U19", Kickoff);
        for (var hour = 0; hour < 12; hour++)
        {
            await _kit.PassAsync();
            _kit.Host.Clock.Advance(TimeSpan.FromHours(1));
        }

        _odds.OddsCalls.Should().Be(0);
        _odds.FreeCalls.Should().BeLessThanOrEqualTo(12 * 6, "discovery at most every 15 minutes, catalog rarely");
        (await _kit.AutoRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task The_read_only_check_spends_at_most_its_budget_writes_nothing_and_never_prints_the_key()
    {
        var odds = new FakeFootballOdds { Clock = _kit.Host.Clock };
        odds.Price(odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff));
        odds.Price(odds.Add(2, "Besiktas", "Goztepe", Kickoff, FakeFootballOdds.Europa));
        odds.Add(3, "Galatasaray Istanbul FK", "Rizespor", Kickoff);
        var options = new ToroSquad.Modules.Predictions.AutoFootballOptions();

        var check = new AutoFootballVerification(odds, options, _kit.Host.Clock);
        (await check.RunAsync(withOdds: true, budget: 1, Ct)).Should().BeTrue();
        check.CreditsSpent.Should().Be(1, "the budget");
        odds.OddsCalls.Should().Be(1);
        check.TrackedMatches.Should().Be(2);
        check.Lines.Should().Contain(l => l.Contains("would open with pinnacle: 1.85 / 3.40 / 4.20", StringComparison.Ordinal));
        check.Lines.Should().Contain(l => l.Contains("REVIEW: similar name not matched: 'Galatasaray Istanbul FK'", StringComparison.Ordinal));
        check.Lines.Should().Contain(l => l.Contains("skipped (budget 1 reached)", StringComparison.Ordinal));
        (await _kit.AutoRowsAsync()).Should().BeEmpty("the check writes nothing");
        _kit.Transport.Messages.Should().BeEmpty();

        var noKey = new AutoFootballVerification(new FakeFootballOdds { IsConfigured = false }, options, _kit.Host.Clock);
        (await noKey.RunAsync(true, 25, Ct)).Should().BeFalse();
        noKey.Lines.Should().ContainSingle().Which.Should().StartWith("BLOCKED: no API key");
    }

    [Fact]
    public async Task A_new_tournament_or_a_cancelled_card_never_reopens_the_match()
    {
        await _kit.ParticipateAsync(1); // someone to end the tournament for
        var view = await PublishDerbyAsync();
        (await _kit.CancelAsync(Admin(), view)).Result.MessageKey.Should().Be("predictions.cancel.done");
        (await _kit.ConfirmEndAsync(Admin(), (await _kit.EndTokenAsync(Admin()))!)).Result.Succeeded.Should().BeTrue();
        var sent = _kit.Transport.Messages.Count;

        for (var i = 0; i < 3; i++)
        {
            _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
            await _kit.PassAsync();
        }

        (await _kit.AutoPredictionsAsync()).Should().ContainSingle("the match key has no tournament");
        _kit.Transport.Messages.Should().HaveCount(sent, "no second card");
        (await _kit.AutoRowsAsync()).Single().State.Should().Be(AutoEventState.Published);
    }

    [Fact]
    public async Task A_planned_match_does_not_block_ending_the_tournament_a_published_one_does()
    {
        await _kit.ParticipateAsync(1);
        Derby();
        _odds.Price(_odds.Events[FakeFootballOdds.SuperLig][0]);
        await _kit.PassAsync();
        (await _kit.AutoRowsAsync()).Single().State.Should().Be(AutoEventState.Planned);
        (await _kit.EndTokenAsync(Admin())).Should().NotBeNull("a discovered match is not a prediction");

        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        (await _kit.Economy(e => e.PreviewTournamentEndAsync(Admin(), Commands, Ct))).Result.MessageKey.Should().Be("predictions.tournament.unresolved");
    }

    [Fact]
    public async Task Opening_and_ending_the_tournament_at_the_same_time_never_writes_into_a_closed_tournament()
    {
        await _kit.ParticipateAsync(1);
        _odds.Price(Derby());
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        var token = (await _kit.EndTokenAsync(Admin()))!;
        var results = await _kit.TogetherAsync<object>(async () => await _kit.PassAsync(), async () => await _kit.ConfirmEndAsync(Admin(), token));

        var ended = ((PredictionReply)results[1]).Result;
        var prediction = (await _kit.AutoPredictionsAsync()).Should().ContainSingle().Subject;
        var tournament = await _kit.Db(db => db.Set<PredictionTournamentEntity>().AsNoTracking().SingleAsync(t => t.Id == prediction.TournamentId));
        if (ended.Succeeded)
            tournament.Status.Should().Be(PredictionTournamentStatus.Active, "the close came first: the prediction went into the new tournament");
        else
            ended.MessageKey.Should().Be("predictions.tournament.unresolved", "the prediction came first and blocks the close");
    }

    // ---- delivery ----

    [Fact]
    public async Task An_uncertain_post_that_reached_discord_is_found_and_never_sent_twice()
    {
        _kit.Transport.ScriptAcceptedButTimedOut();
        await PublishDerbyAsync();
        _kit.Transport.Messages.Should().ContainSingle();
        (await _kit.AutoRowsAsync()).Single().State.Should().Be(AutoEventState.Published);
    }

    [Fact]
    public async Task An_uncertain_post_that_cannot_be_found_is_abandoned_for_review_and_never_posted_again()
    {
        _kit.Transport.ScriptSend(() => new SendOutcome.Ambiguous("timeout"));
        _odds.Price(Derby());
        await _kit.PassAsync();
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        (await _kit.AutoRowsAsync()).Single().State.Should().Be(AutoEventState.Publishing);
        var sent = _kit.Transport.Messages.Count;

        await _kit.TickAsync(TimeSpan.FromMinutes(2));
        await _kit.TickAsync(TimeSpan.FromMinutes(10)); // the prediction worker abandons it after its grace
        await _kit.PassAsync();
        (await _kit.AutoPredictionsAsync()).Single().Status.Should().Be(PredictionStatus.Abandoned);
        (await _kit.AutoRowsAsync()).Single().Should().Match<PredictionAutoEventEntity>(r => r.State == AutoEventState.ReviewRequired && r.Reason == AutoBlockReason.DeliveryUnknown);
        for (var i = 0; i < 3; i++)
        {
            _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
            await _kit.PassAsync();
        }

        _kit.Transport.Messages.Should().HaveCount(sent, "never a second card for the match");
    }

    [Fact]
    public async Task The_last_gate_never_posts_late_or_after_the_automation_was_stopped()
    {
        var plan = (long id) => new AutoPublishPlan(id, Guild, "Galatasaray - Fenerbahçe maç sonucu ne olur?", "kural",
            [("Galatasaray kazanır", 185), ("Beraberlik", 340), ("Fenerbahçe kazanır", 420)], Kickoff - TimeSpan.FromMinutes(2), Kickoff - TimeSpan.FromMinutes(15),
            "auto:test" + id, new AutoCardInfo(Kickoff, "Pinnacle", NineTr));
        Derby();
        await _kit.PassAsync();
        var rowId = (await _kit.AutoRowsAsync()).Single().Id;
        _kit.Host.Clock.SetUtcNow(NineTr);
        var stopped = await _kit.Service(s => s.PublishAutomaticAsync(plan(rowId), () => false, Ct));
        stopped.Should().Be(AutoPublishOutcome.Blocked(AutoBlockReason.AutomationStopped));
        _kit.Transport.Messages.Should().BeEmpty("nothing was posted");
        (await _kit.AutoPredictionsAsync()).Single().Status.Should().Be(PredictionStatus.Abandoned, "the reserved prediction never opened");
        (await _kit.AutoRowsAsync()).Single().State.Should().Be(AutoEventState.Skipped);

        _kit.Host.Clock.SetUtcNow(Kickoff - TimeSpan.FromMinutes(15));
        (await _kit.Service(s => s.PublishAutomaticAsync(plan(rowId), () => true, Ct))).Should().Be(AutoPublishOutcome.Blocked(AutoBlockReason.TooLateToPublish));
        _kit.Transport.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task With_the_module_disabled_it_waits_without_a_card_and_opens_once_enabled_in_time()
    {
        _odds.Price(Derby());
        await _kit.PassAsync();
        await _kit.SetEnabledAsync(false);
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        _kit.Transport.Messages.Should().BeEmpty();
        _odds.OddsCalls.Should().Be(0, "no paid call for a card that could not be opened");
        var row = (await _kit.AutoRowsAsync()).Single();
        (row.State, row.Reason, row.OddsAttempts).Should().Be((AutoEventState.Planned, AutoBlockReason.ModuleDisabled, 0));

        await _kit.SetEnabledAsync(true);
        _kit.Host.Clock.SetUtcNow(row.NextAttemptAt!.Value);
        await _kit.PassAsync();
        (await _kit.AutoPredictionsAsync()).Should().ContainSingle();
        _odds.OddsCalls.Should().Be(1);
    }

    // ---- changes after publishing ----

    [Fact]
    public async Task A_changed_kickoff_before_publishing_is_planned_again()
    {
        var e = Derby();
        _odds.Price(e);
        await _kit.PassAsync();
        _odds.Add(1, e.HomeTeam, e.AwayTeam, Kickoff + TimeSpan.FromDays(1)); // moved to tomorrow
        _kit.Host.Clock.SetUtcNow(NineTr);
        await _kit.PassAsync();
        (await _kit.AutoPredictionsAsync()).Should().BeEmpty("not today any more");
        (await _kit.AutoRowsAsync()).Single().PublishAt.Should().Be(NineTr + TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task A_changed_kickoff_after_publishing_locks_the_card_for_review_and_keeps_stakes_odds_and_both_times()
    {
        var view = await PublishDerbyAsync();
        (await _kit.EnterAsync(Member(1), view, 1, "100")).Result.Succeeded.Should().BeTrue();
        _odds.Add(1, "Galatasaray", "Fenerbahce", Kickoff + TimeSpan.FromHours(24));
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await _kit.PassAsync();

        var prediction = await _kit.RowAsync(view.Id);
        (prediction.Status, prediction.LockReason, prediction.LockAt).Should().Be((PredictionStatus.Locked, PredictionLockReason.NeedsReview, Kickoff - TimeSpan.FromMinutes(2)));
        var row = (await _kit.AutoRowsAsync()).Single();
        (row.State, row.Reason, row.KickoffAt, row.LatestKickoffAt).Should().Be((AutoEventState.ReviewRequired, AutoBlockReason.ScheduleChanged, Kickoff, Kickoff + TimeSpan.FromHours(24)));
        (await _kit.WalletAsync(1))!.PendingMinor.Should().Be(10_000, "stakes stay");
        (await _kit.Service(s => s.GetAsync(view.Id, Ct)))!.Outcomes.Select(o => o.OddsX100).Should().Equal(185, 340, 420);
        (await _kit.EnterAsync(Member(2), view, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        await _kit.TickAsync(TimeSpan.FromSeconds(11));
        Shown(_kit.Card(view)).Embed!.Fields.Should().Contain(f => f.Value.Contains("yönetici inceleyecek", StringComparison.Ordinal));

        (await _kit.SettleAsync(Admin(), view, 1)).Result.MessageKey.Should().Be("predictions.settle.done", "an administrator decides");
    }

    [Fact]
    public async Task A_match_that_vanishes_is_never_taken_as_cancelled_but_an_open_card_is_locked_for_review()
    {
        var view = await PublishDerbyAsync();
        _odds.Events[FakeFootballOdds.SuperLig].Clear();
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await _kit.PassAsync();
        (await _kit.RowAsync(view.Id)).Status.Should().Be(PredictionStatus.Open, "missing once is not enough");
        _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
        await _kit.PassAsync();
        (await _kit.RowAsync(view.Id)).Should().Match<PredictionEntity>(p => p.Status == PredictionStatus.Locked && p.LockReason == PredictionLockReason.NeedsReview);
        (await _kit.AutoRowsAsync()).Single().Reason.Should().Be(AutoBlockReason.EventMissing);
        (await _kit.Db(db => db.Set<PredictionLedgerEntity>().CountAsync(l => l.Kind == PredictionLedgerKind.Refund))).Should().Be(0, "never an automatic refund");
    }

    [Fact]
    public async Task The_worker_never_reopens_a_manually_locked_or_terminal_card()
    {
        var view = await PublishDerbyAsync();
        (await _kit.LockAsync(Admin(), view)).MessageKey.Should().Be("predictions.lock.done");
        for (var i = 0; i < 3; i++)
        {
            _kit.Host.Clock.Advance(TimeSpan.FromMinutes(20));
            await _kit.PassAsync();
            await _kit.TickAsync();
        }

        (await _kit.RowAsync(view.Id)).Should().Match<PredictionEntity>(p => p.Status == PredictionStatus.Locked && p.LockReason == PredictionLockReason.Manual);
    }

    // ---- rights and timing on the card ----

    [Fact]
    public async Task Only_administrators_or_the_owner_manage_an_automatic_card_while_manual_rules_stay()
    {
        var view = await PublishDerbyAsync();
        (await _kit.Service(s => s.PromptLockAsync(Creator(), Predictions, view.Id, view.Message, Ct))).Result.MessageKey
            .Should().Be("predictions.not_manager", "the creator role alone never manages the automatic ones");
        (await _kit.Service(s => s.PromptLockAsync(Member(), Predictions, view.Id, view.Message, Ct))).Result.MessageKey.Should().Be("predictions.not_manager");
        (await _kit.Service(s => s.PromptLockAsync(Owner(), Predictions, view.Id, view.Message, Ct))).Result.Succeeded.Should().BeTrue();
        (await _kit.LockAsync(Admin(), view)).MessageKey.Should().Be("predictions.lock.done");

        var manual = await _kit.CreatePredictionAsync(title: "Elle açılan öngörü başlığı");
        (await _kit.LockAsync(Creator(), manual)).MessageKey.Should().Be("predictions.lock.done", "its creator with the role still manages a manual one");
        (await _kit.Service(s => s.OpenFormAsync(Member(), Predictions, Ct))).Refusal!.MessageKey.Should().Be("predictions.missing_role");
    }

    [Fact]
    public async Task At_the_lock_time_entries_changes_and_withdrawals_are_refused_before_any_worker_pass()
    {
        var view = await PublishDerbyAsync();
        (await _kit.EnterAsync(Member(1), view, 1, "100")).Result.Succeeded.Should().BeTrue();
        _kit.Host.Clock.SetUtcNow(Kickoff - TimeSpan.FromMinutes(2));
        (await _kit.RowAsync(view.Id)).Status.Should().Be(PredictionStatus.Open, "no worker pass has run");
        (await _kit.EnterAsync(Member(2), view, 1, "100")).Result.MessageKey.Should().Be("predictions.entry.closed");
        (await _kit.SubmitChangeAsync(Member(1), view, 2, "150")).Result.MessageKey.Should().Be("predictions.change.locked");
        (await _kit.WithdrawAsync(Member(1), view)).Result.MessageKey.Should().Be("predictions.change.locked");
    }

    [Fact]
    public async Task The_status_lines_show_mode_discovery_today_credits_and_never_the_key()
    {
        await PublishDerbyAsync();
        var lines = await _kit.Host.InScopeAsync(sp => sp.GetRequiredService<AutoFootballService>().HealthAsync(Ct));
        lines.Select(l => l.Component).Should().Contain(["predictions.health.auto", "predictions.health.auto_discovery", "predictions.health.auto_today", "predictions.health.auto_quota"]);
        lines.Single(l => l.Component == "predictions.health.auto_today").Args!.Should().Equal(1, 1, 0, 0, 0);
        lines.Single(l => l.Component == "predictions.health.auto_quota").Args![0].Should().Be("399");
    }

    // ---- schema ----

    [Fact]
    public async Task The_migration_is_additive_existing_predictions_become_manual()
    {
        var path = Path.Combine(_kit.Host.Directory, "upgrade.db");
        var options = new DbContextOptionsBuilder<ToroDbContext>()
            .UseSqlite(DatabaseMaintenance.ConnectionString(path), o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .Options;
        await using (var db = new ToroDbContext(options, DesignTimeDbContextFactory.AllContributors()))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260929192822_PredictionEntryLifecycle", Ct); // the production schema before this change
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO prediction_tournament (Id, GuildId, Number, Status, StartedAt) VALUES (1, 777, 1, 0, 0);" +
                "INSERT INTO prediction (Id, GuildId, TournamentId, ChannelId, CreatorUserId, CreatorName, Title, Status, PublishKey, CreatedAt, EntryCount, StakeTotalMinor, " +
                "CardStale, CardSyncAttempts, CardMissing, PublishChecks, Version) VALUES (1, 777, 1, 5, 10, 'Kaan', 'Eski öngörü', 1, 'k1', 0, 0, 0, 0, 0, 0, 0, 1);", Ct);
            await db.Database.MigrateAsync(Ct);
            (await db.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();
            (await db.Set<PredictionEntity>().AsNoTracking().SingleAsync(Ct)).Origin.Should().Be(PredictionOrigin.Manual);
            (await db.Set<PredictionAutoEventEntity>().CountAsync(Ct)).Should().Be(0);
            (await db.Set<PredictionAutoProviderEntity>().CountAsync(Ct)).Should().Be(0);
        }

        using var connection = new SqliteConnection(DatabaseMaintenance.ConnectionString(path));
        SqliteConnection.ClearPool(connection);
    }
}
