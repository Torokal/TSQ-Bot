using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core.Localization;
using ToroSquad.Modules.Currency;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;

namespace ToroSquad.Tests.Support;

/// <summary>
/// Provider payloads in the shape of the live responses (contract checked 2026-09-28) with fixed test values. Fixtures,
/// not market data: tests never touch the network.
/// </summary>
public static class CurrencyPayloads
{
    public const string AltinkaynakCurrency = """
        [{"Alis":"48,820","Satis":"49,030","Kod":"USD","Aciklama":"Amerikan Doları","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"55,508","Satis":"55,819","Kod":"EUR","Aciklama":"Avrupa Para Birimi","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"58,170","Satis":"58,968","Kod":"CHF","Aciklama":"İsviçre Frangı","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"0,3072","Satis":"0,3132","Kod":"JPY","Aciklama":"Japon Yeni","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"147,349","Satis":"162,038","Kod":"KWD","Aciklama":"Kuveyt Dinarı","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":false}]
        """;

    /// <summary>GA and PGA share the description "Gram Altın" (GA with a trailing space, as live) but not the sell price.</summary>
    public const string AltinkaynakGold = """
        [{"Alis":"6.502,19","Satis":"6.566,45","Kod":"HH_T","Aciklama":"Has","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"6.469,68","Satis":"6.533,62","Kod":"CH_T","Aciklama":"Külçe","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"6.439,47","Satis":"6.574,16","Kod":"GA","Aciklama":"Gram Altın ","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"6.439,47","Satis":"6.630,00","Kod":"PGA","Aciklama":"Gram Altın","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true},
         {"Alis":"42.760,34","Satis":"46.325,00","Kod":"PA","Aciklama":"Ata Cumhuriyet","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":false},
         {"Alis":"87,22","Satis":"97,26","Kod":"AG_T","Aciklama":"Gümüş","GuncellenmeZamani":"28.09.2026 11:51:42","IsMobile":true}]
        """;

    public const string Tcmb = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?xml-stylesheet type="text/xsl" href="isokur.xsl"?>
        <Tarih_Date Tarih="25.09.2026" Date="09/25/2026" Bulten_No="2026/181">
          <Currency CrossOrder="0" Kod="USD" CurrencyCode="USD">
            <Unit>1</Unit><Isim>ABD DOLARI</Isim><CurrencyName>US DOLLAR</CurrencyName>
            <ForexBuying>48.7901</ForexBuying><ForexSelling>48.8780</ForexSelling>
            <BanknoteBuying>48.7560</BanknoteBuying><BanknoteSelling>48.9514</BanknoteSelling>
          </Currency>
          <Currency CrossOrder="9" Kod="EUR" CurrencyCode="EUR">
            <Unit>1</Unit><Isim>EURO</Isim><CurrencyName>EURO</CurrencyName>
            <ForexBuying>55.5878</ForexBuying><ForexSelling>55.6880</ForexSelling>
            <BanknoteBuying>55.5489</BanknoteBuying><BanknoteSelling>55.7715</BanknoteSelling>
          </Currency>
          <Currency CrossOrder="12" Kod="JPY" CurrencyCode="JPY">
            <Unit>100</Unit><Isim>JAPON YENİ</Isim><CurrencyName>JAPENESE YEN</CurrencyName>
            <ForexBuying>32.8766</ForexBuying><ForexSelling>33.0942</ForexSelling>
          </Currency>
        </Tarih_Date>
        """;

    public const string Truncgil = """
        {"Update_Date":"2026-09-28 11:51:01",
         "USD":{"Buying":48.9776,"Type":"Currency","Selling":48.987,"Change":0.11},
         "GRA":{"Selling":6540.36,"Type":"Gold","Name":"GRAMALTIN","Change":-2.97,"Buying":6539.59},
         "CEYREKALTIN":{"Selling":10915.95,"Type":"Gold","Name":"CEYREKALTIN","Change":-2.96,"Buying":10604.5}}
        """;

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}

/// <summary>Routes provider requests by path; counts calls per path (thread-safe). Unrouted paths answer 404.</summary>
public sealed class CurrencyHttpStub : HttpMessageHandler
{
    public const string AltinkaynakCurrencyPath = "/public/Currency";
    public const string AltinkaynakGoldPath = "/public/Gold";
    public const string TcmbPath = "/kurlar/today.xml";
    public const string TruncgilPath = "/v4/today.json";

    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> Routes { get; } = new(StringComparer.Ordinal);

    public ConcurrentBag<string> UserAgents { get; } = [];

    public int Calls(string path) => _calls.TryGetValue(path, out var n) ? n : 0;

    public int TotalCalls => _calls.Values.Sum();

    /// <summary>Every provider answers with the standard payloads.</summary>
    public static CurrencyHttpStub Healthy()
    {
        var stub = new CurrencyHttpStub();
        stub.Ok(AltinkaynakCurrencyPath, CurrencyPayloads.AltinkaynakCurrency);
        stub.Ok(AltinkaynakGoldPath, CurrencyPayloads.AltinkaynakGold);
        stub.Ok(TcmbPath, CurrencyPayloads.Tcmb, "application/xml");
        stub.Ok(TruncgilPath, CurrencyPayloads.Truncgil);
        return stub;
    }

    public void Ok(string path, string body, string mediaType = "application/json") =>
        Routes[path] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    public void Status(string path, HttpStatusCode status) =>
        Routes[path] = _ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("error") });

    /// <summary>Never answers: only the client's timeout (on the fake clock) ends the request.</summary>
    public void Hang(string path) => Routes[path] = async ct =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new HttpResponseMessage(HttpStatusCode.OK);
    };

    public void Throw(string path) => Routes[path] = _ => throw new HttpRequestException("Name or service not known", null, null);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        _calls.AddOrUpdate(path, 1, (_, n) => n + 1);
        UserAgents.Add(request.Headers.UserAgent.ToString());
        return Routes.TryGetValue(path, out var respond)
            ? respond(cancellationToken)
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>The production named clients' base addresses over a stub handler (what the module registers, minus the socket handler).</summary>
public sealed class CurrencyHttpFactory(HttpMessageHandler handler, CurrencyOptions options) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(name switch
            {
                MarketDataClient.AltinkaynakHttpClient => options.AltinkaynakBaseUrl,
                MarketDataClient.TcmbHttpClient => options.TcmbBaseUrl,
                MarketDataClient.TruncgilHttpClient => options.TruncgilBaseUrl,
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
            }),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CurrencyModule.UserAgent);
        return client;
    }
}

/// <summary>A scriptable dataset source with call counting — for cache, single-flight and fallback-order tests.</summary>
public sealed class FakeMarketDataSource : IMarketDataSource
{
    private readonly ConcurrentDictionary<MarketDataset, int> _calls = new();

    public ConcurrentDictionary<MarketDataset, Func<DateTimeOffset, Task<DatasetResult>>> Responses { get; } = new();

    public required TimeProvider Clock { get; init; }

    public int Calls(MarketDataset dataset) => _calls.TryGetValue(dataset, out var n) ? n : 0;

    public int TotalCalls => _calls.Values.Sum();

    public void Fail(MarketDataset dataset, ProviderFailure failure = ProviderFailure.HttpStatus) =>
        Responses[dataset] = at => Task.FromResult(DatasetResult.Failed(dataset, failure, at, failure == ProviderFailure.HttpStatus ? 500 : null));

    public void Succeed(MarketDataset dataset, decimal buy, decimal sell) =>
        Responses[dataset] = at => Task.FromResult(DatasetResult.From(dataset,
            MarketDatasets.Covers(dataset).ToDictionary(i => i, _ => ParsedPrice.Ok(new MarketPrice(buy, sell, at.AddMinutes(-1)))), at));

    public Task<DatasetResult> FetchAsync(MarketDataset dataset, CancellationToken cancellationToken)
    {
        _calls.AddOrUpdate(dataset, 1, (_, n) => n + 1);
        return Responses.TryGetValue(dataset, out var respond)
            ? respond(Clock.GetUtcNow())
            : Task.FromResult(DatasetResult.Failed(dataset, ProviderFailure.HttpStatus, Clock.GetUtcNow(), 404));
    }
}

public static class CurrencyTestKit
{
    /// <summary>2026-09-28 12:00 in Türkiye.</summary>
    public static readonly DateTimeOffset T0 = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    public static TimeZoneInfo Turkey => ProviderFormats.TryTurkeyZone(out var zone) ? zone : throw new InvalidOperationException("no Europe/Istanbul");

    public static IOptions<CurrencyOptions> Options(CurrencyOptions? options = null) => Microsoft.Extensions.Options.Options.Create(options ?? new CurrencyOptions());

    /// <summary>The production client and service over a stub HTTP handler and a fake clock.</summary>
    public static (MarketQuoteService Service, CurrencyHttpStub Http, FakeTimeProvider Clock) OverHttp(CurrencyHttpStub? http = null)
    {
        var clock = new FakeTimeProvider(T0);
        http ??= CurrencyHttpStub.Healthy();
        var options = Options();
        var client = new MarketDataClient(new CurrencyHttpFactory(http, options.Value), options, clock);
        return (new MarketQuoteService(client, options, clock, NullLogger<MarketQuoteService>.Instance), http, clock);
    }

    /// <summary>The service over a scriptable source (all providers healthy by default).</summary>
    public static (MarketQuoteService Service, FakeMarketDataSource Source, FakeTimeProvider Clock) OverFake()
    {
        var clock = new FakeTimeProvider(T0);
        var source = new FakeMarketDataSource { Clock = clock };
        source.Succeed(MarketDataset.AltinkaynakCurrency, 48.820m, 49.030m);
        source.Succeed(MarketDataset.AltinkaynakGold, 6439.47m, 6574.16m);
        source.Succeed(MarketDataset.Tcmb, 48.7901m, 48.8780m);
        source.Succeed(MarketDataset.Truncgil, 6539.59m, 6540.36m);
        return (new MarketQuoteService(source, Options(), clock, NullLogger<MarketQuoteService>.Instance), source, clock);
    }

    public static LocalizationCatalog Localizer() => new(
    [
        new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization"),
        new LocalizationSource(typeof(CurrencyModule).Assembly, "ToroSquad.Modules.Currency.Localization"),
    ]);
}
