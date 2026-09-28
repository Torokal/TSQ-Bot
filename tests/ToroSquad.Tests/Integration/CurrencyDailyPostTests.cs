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
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The daily 09:00 card through the production wiring (real SQLite outbox, fake clock, scripted providers — no real
/// waiting, no network): Türkiye-time schedule, bounded catch-up, one card per Türkiye day across passes, restarts and
/// overlapping passes, module gate, provider outages, one Currency + one Gold fetch.
/// </summary>
public sealed class CurrencyDailyPostTests
{
    private static readonly GuildId Guild = new(42);
    private static readonly ChannelId Channel = new(1242464361855848459);

    /// <summary>09:00 in Türkiye on 2026-09-29 is 06:00 UTC.</summary>
    private static readonly DateTimeOffset NineTr = new(2026, 9, 29, 6, 0, 0, TimeSpan.Zero);

    private static async Task<(TestHost Host, FakeMarketDataSource Source)> CreateAsync(DateTimeOffset start, bool enable = true)
    {
        var host = await TestHost.CreateAsync(start: start, replace: s => s.AddSingleton<IMarketDataSource>(sp =>
        {
            var source = new FakeMarketDataSource { Clock = sp.GetRequiredService<TimeProvider>() };
            source.Succeed(MarketDataset.AltinkaynakCurrency, 48.900m, 49.090m);
            source.Succeed(MarketDataset.AltinkaynakGold, 6493.80m, 6623.54m);
            source.Succeed(MarketDataset.Tcmb, 48.7901m, 48.8780m);
            source.Succeed(MarketDataset.Truncgil, 6539.59m, 6540.36m);
            return source;
        }));
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild));
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, BotChannelAccess.RequiredForNotifications));
        if (enable)
            await SetEnabledAsync(host, true);
        return (host, (FakeMarketDataSource)host.Services.GetRequiredService<IMarketDataSource>());
    }

    private static Task SetEnabledAsync(TestHost host, bool enabled) => host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "currency", enabled, CancellationToken.None))
        .Succeeded.Should().BeTrue());

    private static CurrencyDailyPoster Poster(TestHost host) => host.Services.GetRequiredService<CurrencyDailyPoster>();

    /// <summary>A fresh instance with no memory of earlier passes — what a restart or redeploy looks like.</summary>
    private static CurrencyDailyPoster Restarted(TestHost host) => ActivatorUtilities.CreateInstance<CurrencyDailyPoster>(host.Services);

    private static Task<List<OutboxMessageEntity>> RowsAsync(TestHost host) =>
        host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(x => x.ModuleId == "currency").OrderBy(x => x.Id).ToListAsync());

    private static Task<DailyPassOutcome> PassAsync(CurrencyDailyPoster poster, string reason = "scheduled") => poster.RunAsync(reason, CancellationToken.None);

    [Fact]
    public async Task Before_nine_nothing_at_nine_exactly_one_card()
    {
        var (host, source) = await CreateAsync(NineTr.AddMinutes(-1));
        await using var _ = host;
        var poster = Poster(host);

        (await PassAsync(poster)).Should().Be(DailyPassOutcome.NotDue);
        (await RowsAsync(host)).Should().BeEmpty();
        source.TotalCalls.Should().Be(0, "no price is fetched before it is needed");

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.Queued);
        var row = (await RowsAsync(host)).Should().ContainSingle().Subject;
        row.LogicalKey.Should().Be("live|42|currency|day:2026-09-29|1242464361855848459|currency-daily");
        row.Kind.Should().Be(CurrencyDailySchedule.Kind);
        row.ChannelId.Should().Be(Channel.Value);
        row.Status.Should().Be(OutboxStatus.Pending, "the outbox delivers it; the poster never talks to Discord");

        var message = PayloadSerializer.Deserialize(row.PayloadJson);
        message.Mentions.PingsAnything.Should().BeFalse();
        message.Content.Should().BeNull();
        message.Embed!.Title.Should().Be("💱 Günlük Döviz & Altın");
        message.Embed.Fields.Select(f => f.Name).Should().Equal("💵 Amerikan Doları", "💶 Euro", "🪙 Gram Altın");
        message.Embed.Fields[0].Value.Should().StartWith("Alış: **48,900 ₺** · Satış: **49,090 ₺**");
        message.Embed.Fields[2].Value.Should().StartWith("Alış: **6.493,80 ₺** · Satış: **6.623,54 ₺**");

        // USD and EUR share one Altınkaynak Currency response; gold is one more; no fallback was needed.
        source.Calls(MarketDataset.AltinkaynakCurrency).Should().Be(1);
        source.Calls(MarketDataset.AltinkaynakGold).Should().Be(1);
        source.Calls(MarketDataset.Tcmb).Should().Be(0);
        source.Calls(MarketDataset.Truncgil).Should().Be(0);
    }

    [Fact]
    public async Task Later_passes_restarts_and_overlapping_passes_never_add_a_second_card()
    {
        var (host, source) = await CreateAsync(NineTr);
        await using var _ = host;
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.Queued);
        var calls = source.TotalCalls;

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.AlreadyQueued);

        host.Clock.Advance(TimeSpan.FromMinutes(2)); // 09:03 deploy: a new process remembers nothing
        (await PassAsync(Restarted(host), "startup")).Should().Be(DailyPassOutcome.AlreadyQueued);

        (await RowsAsync(host)).Should().ContainSingle();
        source.TotalCalls.Should().Be(calls, "an already queued day fetches nothing");
    }

    [Fact]
    public async Task A_sent_card_is_never_edited_or_resent_after_a_restart()
    {
        var (host, _) = await CreateAsync(NineTr);
        await using var _h = host;
        await PassAsync(Poster(host));
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            var row = await db.Outbox.SingleAsync(x => x.ModuleId == "currency");
            row.Status = OutboxStatus.Sent;
            row.DeliveredPayloadHash = row.PayloadHash;
            await db.SaveChangesAsync();
        });

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        (await PassAsync(Restarted(host), "startup")).Should().Be(DailyPassOutcome.AlreadyQueued);
        var row = (await RowsAsync(host)).Should().ContainSingle().Subject;
        row.EditPending.Should().BeFalse("the morning card is not rewritten with newer prices");
    }

    [Fact]
    public async Task Two_overlapping_passes_produce_one_card()
    {
        var (host, _) = await CreateAsync(NineTr);
        await using var _h = host;
        var outcomes = await Task.WhenAll(
            Task.Run(() => PassAsync(Restarted(host)), TestContext.Current.CancellationToken),
            Task.Run(() => PassAsync(Restarted(host)), TestContext.Current.CancellationToken));
        outcomes.Should().Contain(DailyPassOutcome.Queued);
        (await RowsAsync(host)).Should().ContainSingle();
    }

    [Theory]
    [InlineData(5, DailyPassOutcome.Queued)]
    [InlineData(28, DailyPassOutcome.Queued)]
    [InlineData(29, DailyPassOutcome.Queued)]
    [InlineData(31, DailyPassOutcome.WindowPassed)]
    [InlineData(300, DailyPassOutcome.WindowPassed)] // 14:00
    public async Task A_bot_started_after_nine_catches_up_only_inside_the_window(int minutesAfterNine, DailyPassOutcome expected)
    {
        var (host, source) = await CreateAsync(NineTr.AddMinutes(minutesAfterNine));
        await using var _ = host;
        (await PassAsync(Poster(host), "startup")).Should().Be(expected);
        (await RowsAsync(host)).Should().HaveCount(expected == DailyPassOutcome.Queued ? 1 : 0);
        if (expected == DailyPassOutcome.WindowPassed)
            source.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_next_day_gets_its_own_card()
    {
        var (host, _) = await CreateAsync(NineTr);
        await using var _h = host;
        var poster = Poster(host);
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.Queued);
        host.Clock.Advance(TimeSpan.FromHours(12));
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.WindowPassed);
        host.Clock.Advance(TimeSpan.FromHours(12)); // 2026-09-30 09:00 Türkiye
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.Queued);
        (await RowsAsync(host)).Select(r => r.SourceKey).Should().Equal("day:2026-09-29", "day:2026-09-30");
    }

    [Fact]
    public async Task A_disabled_module_gets_no_card_and_fetches_nothing()
    {
        var (host, source) = await CreateAsync(NineTr, enable: false);
        await using var _ = host;
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.NoTarget);
        (await RowsAsync(host)).Should().BeEmpty();
        source.TotalCalls.Should().Be(0);

        await SetEnabledAsync(host, true);
        await SetEnabledAsync(host, false);
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.NoTarget, "disabled again");
        (await RowsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Enabling_inside_the_window_posts_today_s_card()
    {
        var (host, _) = await CreateAsync(NineTr, enable: false);
        await using var _h = host;
        var poster = Poster(host);
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.NoTarget);
        host.Clock.Advance(TimeSpan.FromMinutes(15));
        await SetEnabledAsync(host, true);
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.Queued);
        (await RowsAsync(host)).Should().ContainSingle();
    }

    [Fact]
    public async Task Enabling_after_the_window_does_not_post_the_missed_morning_card()
    {
        var (host, _) = await CreateAsync(NineTr.AddHours(1), enable: false);
        await using var _h = host;
        await SetEnabledAsync(host, true);
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.WindowPassed);
        (await RowsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task One_unavailable_instrument_is_shown_as_such_while_the_others_are_posted()
    {
        var (host, source) = await CreateAsync(NineTr);
        await using var _ = host;
        source.Fail(MarketDataset.AltinkaynakGold, ProviderFailure.Timeout);
        source.Fail(MarketDataset.Truncgil, ProviderFailure.MalformedPayload);

        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.Queued);
        var card = PayloadSerializer.Deserialize((await RowsAsync(host)).Single().PayloadJson).Embed!;
        card.Fields[0].Value.Should().Contain("48,900 ₺");
        card.Fields[1].Value.Should().Contain("Altınkaynak");
        card.Fields[2].Value.Should().Be("Şu anda alınamadı");
    }

    [Fact]
    public async Task No_usable_price_waits_for_recovery_inside_the_window()
    {
        var (host, source) = await CreateAsync(NineTr);
        await using var _ = host;
        foreach (var dataset in Enum.GetValues<MarketDataset>())
            source.Fail(dataset, ProviderFailure.Network);
        var poster = Poster(host);

        (await PassAsync(poster)).Should().Be(DailyPassOutcome.NoData);
        (await RowsAsync(host)).Should().BeEmpty("no empty card");

        source.Succeed(MarketDataset.AltinkaynakCurrency, 48.910m, 49.100m);
        source.Succeed(MarketDataset.AltinkaynakGold, 6495.00m, 6625.00m);
        host.Clock.Advance(TimeSpan.FromMinutes(10)); // 09:10: the failures are no longer cached
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.Queued);
        PayloadSerializer.Deserialize((await RowsAsync(host)).Single().PayloadJson).Embed!.Fields[0].Value.Should().Contain("48,910 ₺");
    }

    [Fact]
    public async Task No_usable_price_for_the_whole_window_skips_the_day_and_the_next_day_is_normal()
    {
        var (host, source) = await CreateAsync(NineTr);
        await using var _ = host;
        foreach (var dataset in Enum.GetValues<MarketDataset>())
            source.Fail(dataset, ProviderFailure.HttpStatus);
        var poster = Poster(host);

        for (var minute = 0; minute <= 30; minute += 10)
        {
            (await PassAsync(poster)).Should().Be(DailyPassOutcome.NoData, $"09:{minute:00}");
            host.Clock.Advance(TimeSpan.FromMinutes(10));
        }

        (await PassAsync(poster)).Should().Be(DailyPassOutcome.WindowPassed); // 09:40
        (await RowsAsync(host)).Should().BeEmpty();

        source.Succeed(MarketDataset.AltinkaynakCurrency, 48.900m, 49.090m);
        source.Succeed(MarketDataset.AltinkaynakGold, 6493.80m, 6623.54m);
        host.Clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(20)); // 2026-09-30 09:00
        (await PassAsync(poster)).Should().Be(DailyPassOutcome.Queued);
        (await RowsAsync(host)).Should().ContainSingle().Which.SourceKey.Should().Be("day:2026-09-30");
    }

    [Fact]
    public async Task A_channel_the_gateway_does_not_show_in_the_guild_is_retried_not_posted()
    {
        var (host, source) = await CreateAsync(NineTr);
        await using var _ = host;
        host.Guilds.SetChannel(Guild, Channel, BotChannelAccess.Missing);
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.NoTarget);
        (await RowsAsync(host)).Should().BeEmpty();
        source.TotalCalls.Should().Be(0);

        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, BotChannelAccess.RequiredForNotifications));
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.Queued);
    }

    // ---------------------------------------------------------------- delivery deadline (outbox expiry)

    private static DateTimeOffset TurkeyTime(DateOnly day, int hour, int minute)
    {
        var local = day.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, CurrencyTestKit.Turkey.GetUtcOffset(local));
    }

    [Theory]
    [InlineData(0)] // 09:00
    [InlineData(29)] // 09:29
    [InlineData(30)] // 09:30: the last moment a new card may be staged
    public async Task Every_card_of_the_day_expires_at_the_same_absolute_deadline_0935(int minutesAfterNine)
    {
        var (host, _) = await CreateAsync(NineTr.AddMinutes(minutesAfterNine));
        await using var _h = host;
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.Queued);

        var day = new DateOnly(2026, 9, 29);
        var turkey = CurrencyTestKit.Turkey;
        CurrencyDailySchedule.DueAt(day, turkey).Should().Be(TurkeyTime(day, 9, 0));
        (CurrencyDailySchedule.DueAt(day, turkey) + new ToroSquad.Modules.Currency.CurrencyOptions().DailyCatchUp).Should().Be(TurkeyTime(day, 9, 30));
        CurrencyDailyPoster.DeliveryGrace.Should().Be(TimeSpan.FromMinutes(5));
        CurrencyDailySchedule.DeliveryDeadline(day, turkey, TimeSpan.FromMinutes(30)).Should().Be(TurkeyTime(day, 9, 35));

        var row = (await RowsAsync(host)).Should().ContainSingle().Subject;
        row.ExpiresAt.Should().Be(TurkeyTime(day, 9, 35), "an absolute daily deadline, not staged time + 5 minutes");
        if (minutesAfterNine < 30) // at 09:30 exactly, staged + 5 min happens to be the deadline too
            row.ExpiresAt.Should().NotBe(host.Clock.GetUtcNow() + CurrencyDailyPoster.DeliveryGrace);
    }

    [Fact]
    public async Task After_0930_no_new_card_is_staged()
    {
        var (host, _) = await CreateAsync(NineTr.AddMinutes(31));
        await using var _h = host;
        (await PassAsync(Poster(host))).Should().Be(DailyPassOutcome.WindowPassed);
        (await RowsAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_card_staged_at_0930_is_still_delivered_before_0935()
    {
        var (host, _) = await CreateAsync(NineTr.AddMinutes(30));
        await using var _h = host;
        await PassAsync(Poster(host));
        host.Clock.Advance(TimeSpan.FromMinutes(4)); // 09:34
        await host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

        (await RowsAsync(host)).Single().Status.Should().Be(OutboxStatus.Sent);
        host.Transport.Messages.Should().ContainSingle(m => m.Channel == Channel);
    }

    [Fact]
    public async Task A_pending_card_is_never_delivered_after_0935()
    {
        var (host, _) = await CreateAsync(NineTr.AddMinutes(29));
        await using var _h = host;
        await PassAsync(Poster(host));
        host.Clock.Advance(TimeSpan.FromMinutes(7)); // 09:36: Discord never took it in time (outage, restart)
        await host.Services.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(CancellationToken.None);

        var row = (await RowsAsync(host)).Single();
        row.Status.Should().Be(OutboxStatus.Expired, "the existing outbox expiry: no late morning card");
        host.Transport.Messages.Should().BeEmpty();

        // And the next pass that day does not stage a replacement.
        (await PassAsync(Restarted(host))).Should().Be(DailyPassOutcome.WindowPassed);
        (await RowsAsync(host)).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- schedule arithmetic (Türkiye time, not UTC)

    [Fact]
    public void The_day_is_the_turkey_calendar_date_and_nine_is_turkey_time()
    {
        var turkey = CurrencyTestKit.Turkey;
        CurrencyDailySchedule.LocalDate(new DateTimeOffset(2026, 9, 29, 21, 30, 0, TimeSpan.Zero), turkey).Should().Be(new DateOnly(2026, 9, 30),
            "00:30 in Türkiye is still the 29th in UTC");
        CurrencyDailySchedule.LocalDate(new DateTimeOffset(2026, 9, 29, 20, 59, 0, TimeSpan.Zero), turkey).Should().Be(new DateOnly(2026, 9, 29));
        CurrencyDailySchedule.DueAt(new DateOnly(2026, 9, 29), turkey).Should().Be(NineTr);
        CurrencyDailySchedule.DueAt(new DateOnly(2026, 12, 29), turkey).UtcDateTime.Hour.Should().Be(6, "Türkiye keeps UTC+3 all year");
        CurrencyDailySchedule.SourceKey(new DateOnly(2026, 9, 30)).Should().Be("day:2026-09-30");
        NotificationRequest.BuildLogicalKey(Guild, new ModuleId("currency"), "day:2026-09-30", Channel, CurrencyDailySchedule.Kind, false)
            .Should().Be("live|42|currency|day:2026-09-30|1242464361855848459|currency-daily");
    }

    [Theory]
    [InlineData(-70, 60)] // 07:50 → capped at an hour, recomputed then
    [InlineData(-10, 10)] // 08:50 → until 09:00
    [InlineData(0, 1)] // inside the window: every minute
    [InlineData(29, 1)]
    [InlineData(40, 60)] // after the window: until tomorrow 09:00, at most an hour per sleep
    public async Task The_next_pass_is_computed_from_the_clock_each_time(int minutesFromNine, int expectedMinutes)
    {
        var (host, _) = await CreateAsync(NineTr.AddMinutes(minutesFromNine));
        await using var _h = host;
        Poster(host).NextDelay().Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }
}
