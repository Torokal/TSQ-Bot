using System.Net;
using ToroSquad.Modules.Currency;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Support.CurrencyHttpStub;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Döviz &amp; Altın quote pipeline, offline (stub HTTP / scripted source, fake clock — no real waiting): provider order,
/// fallback labelling, every provider failure category, dataset cache and its TTLs, single flight, last-known-good.
/// </summary>
public sealed class CurrencyQuoteServiceTests
{
    private static readonly CurrencyCardRenderer Cards = new(CurrencyTestKit.Localizer());

    private static async Task<MarketQuote> QuoteAsync(MarketQuoteService service, MarketInstrument instrument)
    {
        var result = await service.GetQuoteAsync(instrument, CancellationToken.None);
        result.Quote.Should().NotBeNull($"a price for {instrument}");
        return result.Quote!;
    }

    // ---------------------------------------------------------------- provider order

    [Fact]
    public async Task Primary_success_never_calls_a_fallback()
    {
        var (service, http, _) = CurrencyTestKit.OverHttp();
        var usd = await QuoteAsync(service, MarketInstrument.Usd);
        var gold = await QuoteAsync(service, MarketInstrument.GramGold);

        usd.Source.Should().Be(MarketSource.Altinkaynak);
        usd.IsFallback.Should().BeFalse();
        usd.IsStale.Should().BeFalse();
        usd.Buy.Should().Be(48.820m);
        usd.Sell.Should().Be(49.030m);
        usd.RetrievedAt.Should().Be(CurrencyTestKit.T0);
        usd.SourceTimestamp.Should().Be(new DateTimeOffset(2026, 9, 28, 11, 51, 42, TimeSpan.FromHours(3)), "the provider's time, not the request time");
        gold.Source.Should().Be(MarketSource.Altinkaynak);
        gold.Sell.Should().Be(6574.16m);

        http.Calls(TcmbPath).Should().Be(0);
        http.Calls(TruncgilPath).Should().Be(0);
        http.UserAgents.Should().OnlyContain(ua => ua.StartsWith("TSQBot", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(MarketInstrument.Usd, 48.7901, 48.8780)]
    [InlineData(MarketInstrument.Eur, 55.5878, 55.6880)]
    public async Task Fx_primary_failure_falls_back_to_the_tcmb_indicative_rate(MarketInstrument instrument, double buy, double sell)
    {
        var http = Healthy();
        http.Status(AltinkaynakCurrencyPath, HttpStatusCode.InternalServerError);
        var (service, _, _) = CurrencyTestKit.OverHttp(http);

        var quote = await QuoteAsync(service, instrument);
        quote.Source.Should().Be(MarketSource.Tcmb);
        quote.IsFallback.Should().BeTrue();
        quote.IsStale.Should().BeFalse();
        quote.Buy.Should().Be((decimal)buy);
        quote.Sell.Should().Be((decimal)sell);
        quote.Price.SourceDateOnly.Should().BeTrue();
        http.Calls(AltinkaynakCurrencyPath).Should().Be(1);
        http.Calls(TcmbPath).Should().Be(1);

        var card = Cards.Render("tr", MarketQuoteResult.Ok(quote)).Embed!;
        card.Footer.Should().Be("Kaynak: TCMB — Gösterge Kuru");
        card.Description.Should().Contain("gösterge kuru").And.Contain("anlık piyasa fiyatı değildir");
    }

    [Fact]
    public async Task Gold_primary_failure_falls_back_to_truncgil_gra()
    {
        var http = Healthy();
        http.Status(AltinkaynakGoldPath, HttpStatusCode.BadGateway);
        var (service, _, _) = CurrencyTestKit.OverHttp(http);

        var gold = await QuoteAsync(service, MarketInstrument.GramGold);
        gold.Source.Should().Be(MarketSource.Truncgil);
        gold.IsFallback.Should().BeTrue();
        gold.Buy.Should().Be(6539.59m);
        gold.Sell.Should().Be(6540.36m);
        http.Calls(TruncgilPath).Should().Be(1);
        http.Calls(TcmbPath).Should().Be(0, "TCMB has no gold");
        Cards.Render("tr", MarketQuoteResult.Ok(gold)).Embed!.Footer.Should().Be("Kaynak: Trunçgil");
    }

    // ---------------------------------------------------------------- every failure category → fallback, never a crash

    public static TheoryData<string, MarketInstrument> BrokenPrimaries()
    {
        var data = new TheoryData<string, MarketInstrument>();
        foreach (var broken in new[] { "http500", "http404", "timeout", "network", "empty", "malformed-json", "missing", "zero", "negative", "invalid-price" })
        {
            data.Add(broken, MarketInstrument.Usd);
            data.Add(broken, MarketInstrument.Eur);
            data.Add(broken, MarketInstrument.GramGold);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BrokenPrimaries))]
    public async Task A_broken_primary_answer_uses_the_fallback_and_still_renders_a_card(string broken, MarketInstrument instrument)
    {
        var http = Healthy();
        var gold = instrument == MarketInstrument.GramGold;
        var path = gold ? AltinkaynakGoldPath : AltinkaynakCurrencyPath;
        var healthy = gold ? CurrencyPayloads.AltinkaynakGold : CurrencyPayloads.AltinkaynakCurrency;
        var code = instrument switch { MarketInstrument.Usd => "USD", MarketInstrument.Eur => "EUR", _ => "GA" };
        var prices = instrument switch
        {
            MarketInstrument.Usd => "\"Alis\":\"48,820\",\"Satis\":\"49,030\"",
            MarketInstrument.Eur => "\"Alis\":\"55,508\",\"Satis\":\"55,819\"",
            _ => "\"Alis\":\"6.439,47\",\"Satis\":\"6.574,16\"",
        };
        Break(http, path, healthy, broken, code, prices);
        var (service, _, clock) = CurrencyTestKit.OverHttp(http);

        var pending = service.GetQuoteAsync(instrument, CancellationToken.None);
        if (broken == "timeout")
        {
            pending.IsCompleted.Should().BeFalse();
            clock.Advance(TimeSpan.FromSeconds(5)); // the provider timeout, on the fake clock
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        result.Quote!.IsFallback.Should().BeTrue(broken);
        result.Quote.Source.Should().Be(gold ? MarketSource.Truncgil : MarketSource.Tcmb);
        var reply = Cards.Render("tr", result);
        reply.Embed.Should().NotBeNull();
        reply.Ephemeral.Should().BeFalse();
        reply.Embed!.Description.Should().Contain("Birincil veri kaynağına ulaşılamadı.");
    }

    private static void Break(CurrencyHttpStub http, string path, string healthy, string broken, string code, string prices)
    {
        switch (broken)
        {
            case "http500": http.Status(path, HttpStatusCode.InternalServerError); break;
            case "http404": http.Status(path, HttpStatusCode.NotFound); break;
            case "timeout": http.Hang(path); break;
            case "network": http.Throw(path); break;
            case "empty": http.Ok(path, "  \n"); break;
            case "malformed-json": http.Ok(path, healthy[..(healthy.Length / 2)]); break;
            case "missing": http.Ok(path, healthy.Replace("\"Kod\":\"" + code + "\"", "\"Kod\":\"ZZZ\"", StringComparison.Ordinal)); break;
            case "zero": http.Ok(path, healthy.Replace(prices, "\"Alis\":\"0,00\",\"Satis\":\"0,00\"", StringComparison.Ordinal)); break;
            case "negative": http.Ok(path, healthy.Replace(prices, "\"Alis\":\"-1,00\",\"Satis\":\"2,00\"", StringComparison.Ordinal)); break;
            case "invalid-price": http.Ok(path, healthy.Replace(prices, "\"Alis\":\"n/a\",\"Satis\":\"n/a\"", StringComparison.Ordinal)); break;
            default: throw new ArgumentOutOfRangeException(nameof(broken), broken, null);
        }
    }

    [Fact]
    public async Task Broken_fallbacks_too_end_in_a_controlled_unavailable_answer_with_a_trace_code()
    {
        var http = Healthy();
        http.Status(AltinkaynakCurrencyPath, HttpStatusCode.ServiceUnavailable);
        http.Ok(TcmbPath, "<Tarih_Date Tarih=\"25.09.2026\"><Curr"); // malformed XML
        http.Throw(AltinkaynakGoldPath);
        http.Ok(TruncgilPath, CurrencyPayloads.Truncgil.Replace("\"GRA\":", "\"GRX\":", StringComparison.Ordinal)); // missing GRA
        var (service, _, _) = CurrencyTestKit.OverHttp(http);

        foreach (var instrument in new[] { MarketInstrument.Usd, MarketInstrument.Eur, MarketInstrument.GramGold })
        {
            var result = await service.GetQuoteAsync(instrument, CancellationToken.None);
            result.Quote.Should().BeNull();
            result.TraceCode.Should().MatchRegex("^TS-[A-Z2-9]{8}$");
            var reply = Cards.Render("tr", result);
            reply.Embed.Should().BeNull();
            reply.Text.Should().Be("Döviz/altın verisine şu anda ulaşılamıyor. Lütfen kısa süre sonra tekrar deneyin.\nTakip kodu: `" + result.TraceCode + "`");
            reply.Text.Should().NotContain("Exception").And.NotContain("HTTP");
        }
    }

    [Fact]
    public async Task A_crashing_source_is_a_provider_failure_not_an_exception()
    {
        var (service, source, _) = CurrencyTestKit.OverFake();
        source.Responses[MarketDataset.AltinkaynakCurrency] = _ => throw new InvalidOperationException("boom");
        var quote = await QuoteAsync(service, MarketInstrument.Usd);
        quote.Source.Should().Be(MarketSource.Tcmb);
    }

    // ---------------------------------------------------------------- cache

    [Fact]
    public async Task Two_requests_within_the_fresh_ttl_fetch_once_and_the_second_answers_synchronously()
    {
        var (service, http, clock) = CurrencyTestKit.OverHttp();
        await QuoteAsync(service, MarketInstrument.Usd);
        clock.Advance(TimeSpan.FromSeconds(59));
        var second = service.GetQuoteAsync(MarketInstrument.Usd, CancellationToken.None);
        second.IsCompletedSuccessfully.Should().BeTrue("a cached price needs no deferred interaction");
        (await second).Quote!.RetrievedAt.Should().Be(CurrencyTestKit.T0);
        http.Calls(AltinkaynakCurrencyPath).Should().Be(1);
    }

    [Fact]
    public async Task Dollar_then_euro_share_one_altinkaynak_currency_response()
    {
        var (service, http, clock) = CurrencyTestKit.OverHttp();
        var usd = await QuoteAsync(service, MarketInstrument.Usd);
        clock.Advance(TimeSpan.FromSeconds(10));
        var eur = await QuoteAsync(service, MarketInstrument.Eur);
        http.Calls(AltinkaynakCurrencyPath).Should().Be(1);
        usd.Buy.Should().Be(48.820m);
        eur.Buy.Should().Be(55.508m);
        http.Calls(AltinkaynakGoldPath).Should().Be(0, "gold is a separate response, fetched only when asked");
    }

    [Fact]
    public async Task After_the_fresh_ttl_the_provider_is_asked_again()
    {
        var (service, source, clock) = CurrencyTestKit.OverFake();
        await QuoteAsync(service, MarketInstrument.Usd);
        clock.Advance(TimeSpan.FromSeconds(61));
        var again = await QuoteAsync(service, MarketInstrument.Usd);
        source.Calls(MarketDataset.AltinkaynakCurrency).Should().Be(2);
        again.RetrievedAt.Should().Be(CurrencyTestKit.T0.AddSeconds(61));
    }

    [Fact]
    public async Task Concurrent_cache_misses_share_a_single_upstream_fetch()
    {
        var (service, source, _) = CurrencyTestKit.OverFake();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthy = source.Responses[MarketDataset.AltinkaynakCurrency];
        source.Responses[MarketDataset.AltinkaynakCurrency] = async at =>
        {
            await release.Task;
            return await healthy(at);
        };

        var requests = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => service.GetQuoteAsync(i % 2 == 0 ? MarketInstrument.Usd : MarketInstrument.Eur, CancellationToken.None),
                TestContext.Current.CancellationToken))
            .ToList();
        await Task.Delay(50, TestContext.Current.CancellationToken); // let every request reach the in-flight fetch
        release.SetResult();
        var results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        source.Calls(MarketDataset.AltinkaynakCurrency).Should().Be(1);
        results.Should().OnlyContain(r => r.Quote != null && r.Quote.Source == MarketSource.Altinkaynak);
        source.Calls(MarketDataset.Tcmb).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_primary_is_skipped_for_the_fallback_ttl_and_retried_afterwards()
    {
        var (service, source, clock) = CurrencyTestKit.OverFake();
        source.Fail(MarketDataset.AltinkaynakCurrency, ProviderFailure.Timeout);

        (await QuoteAsync(service, MarketInstrument.Usd)).Source.Should().Be(MarketSource.Tcmb);
        clock.Advance(TimeSpan.FromSeconds(20));
        var cached = service.GetQuoteAsync(MarketInstrument.Eur, CancellationToken.None);
        cached.IsCompletedSuccessfully.Should().BeTrue("the known failure and the fallback answer are both cached");
        (await cached).Quote!.Source.Should().Be(MarketSource.Tcmb);
        source.Calls(MarketDataset.AltinkaynakCurrency).Should().Be(1);
        source.Calls(MarketDataset.Tcmb).Should().Be(1);

        // The primary recovers: after the (shorter) fallback TTL it is asked again and wins.
        source.Succeed(MarketDataset.AltinkaynakCurrency, 48.900m, 49.100m);
        clock.Advance(TimeSpan.FromSeconds(11));
        var recovered = await QuoteAsync(service, MarketInstrument.Usd);
        recovered.Source.Should().Be(MarketSource.Altinkaynak);
        recovered.IsFallback.Should().BeFalse();
        source.Calls(MarketDataset.AltinkaynakCurrency).Should().Be(2);
    }

    [Fact]
    public async Task A_fallback_answer_is_cached_shorter_than_a_primary_answer()
    {
        var (service, source, clock) = CurrencyTestKit.OverFake();
        source.Fail(MarketDataset.AltinkaynakGold);
        await QuoteAsync(service, MarketInstrument.GramGold);
        clock.Advance(TimeSpan.FromSeconds(31));
        await QuoteAsync(service, MarketInstrument.GramGold);
        source.Calls(MarketDataset.Truncgil).Should().Be(2, "30 s for a fallback, not 60 s");
        source.Calls(MarketDataset.AltinkaynakGold).Should().Be(2);
    }

    // ---------------------------------------------------------------- last-known-good

    [Fact]
    public async Task When_every_provider_fails_a_recent_last_good_price_is_served_as_stale()
    {
        var (service, source, clock) = CurrencyTestKit.OverFake();
        var good = await QuoteAsync(service, MarketInstrument.Usd);
        source.Fail(MarketDataset.AltinkaynakCurrency, ProviderFailure.Network);
        source.Fail(MarketDataset.Tcmb, ProviderFailure.MalformedPayload);
        clock.Advance(TimeSpan.FromMinutes(14));

        var stale = await QuoteAsync(service, MarketInstrument.Usd);
        stale.IsStale.Should().BeTrue();
        stale.Buy.Should().Be(good.Buy);
        stale.RetrievedAt.Should().Be(CurrencyTestKit.T0, "the age is not hidden");
        stale.SourceTimestamp.Should().Be(good.SourceTimestamp);
        service.LastStatus!.Outcome.Should().Be(QuoteOutcome.Stale);

        var card = Cards.Render("tr", MarketQuoteResult.Ok(stale)).Embed!;
        card.Description.Should().StartWith("⚠️ Veri kaynağına şu anda ulaşılamıyor. Son başarılı fiyat gösteriliyor.");
    }

    [Fact]
    public async Task A_last_good_price_older_than_fifteen_minutes_is_never_shown()
    {
        var (service, source, clock) = CurrencyTestKit.OverFake();
        await QuoteAsync(service, MarketInstrument.GramGold);
        source.Fail(MarketDataset.AltinkaynakGold);
        source.Fail(MarketDataset.Truncgil);
        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        var result = await service.GetQuoteAsync(MarketInstrument.GramGold, CancellationToken.None);
        result.Quote.Should().BeNull();
        result.TraceCode.Should().StartWith("TS-");
        service.LastStatus!.Outcome.Should().Be(QuoteOutcome.Unavailable);
    }

    [Fact]
    public async Task Last_good_prices_are_kept_per_instrument()
    {
        var (service, source, clock) = CurrencyTestKit.OverFake();
        await QuoteAsync(service, MarketInstrument.Usd);
        source.Fail(MarketDataset.AltinkaynakCurrency);
        source.Fail(MarketDataset.Tcmb);
        clock.Advance(TimeSpan.FromMinutes(2));

        (await QuoteAsync(service, MarketInstrument.Usd)).IsStale.Should().BeTrue();
        (await service.GetQuoteAsync(MarketInstrument.Eur, CancellationToken.None)).Quote.Should().BeNull("EUR never had a good price");
    }

    [Fact]
    public void Default_options_match_the_documented_ttls_and_validate()
    {
        var o = new CurrencyOptions();
        (o.Fresh, o.FallbackFresh, o.StaleMax, o.Timeout).Should().Be((TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5)));
        o.Validate().Should().BeEmpty();
        new CurrencyOptions { TcmbBaseUrl = "http://www.tcmb.gov.tr/kurlar/" }.Validate().Should().ContainSingle(e => e.Contains("TcmbBaseUrl", StringComparison.Ordinal));
        new CurrencyOptions { AltinkaynakBaseUrl = "https://static.altinkaynak.com/public" }.Validate().Should().ContainSingle(); // no trailing slash
        new CurrencyOptions { TruncgilBaseUrl = "finans.truncgil.com/v4/" }.Validate().Should().ContainSingle();
        new CurrencyOptions { FallbackFreshSeconds = 90 }.Validate().Should().ContainSingle();
        new CurrencyOptions { TimeoutSeconds = 0 }.Validate().Should().ContainSingle();
    }
}
