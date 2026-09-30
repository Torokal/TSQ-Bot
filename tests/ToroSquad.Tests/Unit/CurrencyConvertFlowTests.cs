using System.Net;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Support.CurrencyHttpStub;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /çevir end to end below Discord (stub HTTP, fake clock): the shared channel guard, private refusals that fetch nothing,
/// the same MarketQuoteService as /dolar and /altın (cache shared, no extra provider request), fallbacks with the right
/// side of the fallback's quote, stale labelling, and the controlled "unavailable" answer.
/// </summary>
public sealed class CurrencyConvertFlowTests
{
    private const ulong CurrencyChannel = 1242464361855848459;

    private sealed class Responder(ulong? channel) : ICurrencyResponder
    {
        public ulong? ChannelId => channel;
        public int Defers { get; private set; }
        public List<string> Private { get; } = [];
        public List<CurrencyReply> Public { get; } = [];

        public Task DeferPublicAsync()
        {
            Defers++;
            return Task.CompletedTask;
        }

        public Task ReplyPrivateAsync(string text)
        {
            Private.Add(text);
            return Task.CompletedTask;
        }

        public Task ReplyPublicAsync(CurrencyReply reply)
        {
            Public.Add(reply);
            return Task.CompletedTask;
        }
    }

    private static (CurrencyCommandFlow Flow, MarketQuoteService Service, CurrencyHttpStub Http, Microsoft.Extensions.Time.Testing.FakeTimeProvider Clock) Create(CurrencyHttpStub? http = null)
    {
        var (service, stub, clock) = CurrencyTestKit.OverHttp(http);
        var localizer = CurrencyTestKit.Localizer();
        return (new CurrencyCommandFlow(service, new CurrencyCardRenderer(localizer), localizer, CurrencyTestKit.Options()), service, stub, clock);
    }

    private static async Task<Responder> ConvertAsync(CurrencyCommandFlow flow, decimal amount, string from, string to, ulong? channel = CurrencyChannel)
    {
        var responder = new Responder(channel);
        await flow.ConvertAsync(amount, from, to, "tr", responder);
        return responder;
    }

    [Theory]
    [InlineData("USD", "TRY", "**2.500,00 USD**\n≈ **122.050,00 ₺**")] // 2500 × Altınkaynak buy 48,820
    [InlineData("TRY", "USD", "**2.500,00 ₺**\n≈ **50,99 USD**")] // 2500 ÷ sell 49,030
    [InlineData("EUR", "TRY", "**2.500,00 EUR**\n≈ **138.770,00 ₺**")] // × buy 55,508
    [InlineData("TRY", "EUR", "**2.500,00 ₺**\n≈ **44,79 EUR**")] // ÷ sell 55,819
    [InlineData("GRAM_GOLD", "TRY", "**2.500,00 g**\n≈ **16.098.675,00 ₺**")] // × GA buy 6.439,47
    [InlineData("TRY", "GRAM_GOLD", "**2.500,00 ₺**\n≈ **0,3803 g**")] // ÷ GA sell 6.574,16
    public async Task In_the_currency_channel_every_supported_direction_answers_publicly(string from, string to, string description)
    {
        var (flow, _, _, _) = Create();
        var responder = await ConvertAsync(flow, 2500m, from, to);
        responder.Private.Should().BeEmpty();
        var reply = responder.Public.Should().ContainSingle().Subject;
        reply.Ephemeral.Should().BeFalse();
        reply.Embed!.Title.Should().Be("💱 Döviz Çevirici");
        reply.Embed.Description.Should().Be(description);
        reply.Embed.Footer.Should().Be("Kaynak: Altınkaynak");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(689812743242514449UL)]
    [InlineData(null)]
    public async Task Elsewhere_only_the_private_pointer_and_nothing_else(ulong? channel)
    {
        var (flow, service, http, _) = Create();
        var responder = await ConvertAsync(flow, 2500m, "USD", "TRY", channel);
        responder.Private.Should().Equal("Bu komutu yalnızca <#1242464361855848459> kanalında kullanabilirsiniz.");
        responder.Public.Should().BeEmpty();
        responder.Defers.Should().Be(0);
        http.TotalCalls.Should().Be(0);
        service.LastStatus.Should().BeNull("the quote service was not asked");
    }

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("TRY", "TRY")]
    [InlineData("USD", "EUR")]
    [InlineData("EUR", "USD")]
    [InlineData("GRAM_GOLD", "USD")]
    [InlineData("EUR", "GRAM_GOLD")]
    [InlineData("XAU", "TRY")] // not a choice value
    public async Task Same_cross_and_unknown_pairs_are_refused_privately_without_a_fetch(string from, string to)
    {
        var (flow, service, http, _) = Create();
        var responder = await ConvertAsync(flow, 2500m, from, to);
        responder.Private.Should().ContainSingle().Which.Should().EndWith("Desteklenen dönüşümler: TRY ↔ USD, TRY ↔ EUR, TRY ↔ Gram Altın.");
        responder.Public.Should().BeEmpty();
        responder.Defers.Should().Be(0);
        http.TotalCalls.Should().Be(0);
        service.LastStatus.Should().BeNull();
    }

    [Theory]
    [InlineData("0", "Miktar 0'dan büyük olmalıdır.")]
    [InlineData("-1", "Miktar 0'dan büyük olmalıdır.")]
    [InlineData("1000000001", "Miktar en fazla 1.000.000.000 olabilir.")]
    public async Task A_bad_amount_is_refused_privately_without_a_fetch(string amount, string message)
    {
        var (flow, _, http, _) = Create();
        var responder = await ConvertAsync(flow, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), "USD", "TRY");
        responder.Private.Should().Equal(message);
        responder.Public.Should().BeEmpty();
        responder.Defers.Should().Be(0);
        http.TotalCalls.Should().Be(0);
    }

    [Fact]
    public async Task Dollar_then_convert_shares_the_cached_quote()
    {
        var (flow, _, http, clock) = Create();
        await flow.RunAsync(MarketInstrument.Usd, "tr", new Responder(CurrencyChannel)); // /dolar
        clock.Advance(TimeSpan.FromSeconds(30));
        var responder = await ConvertAsync(flow, 2500m, "USD", "TRY");
        responder.Defers.Should().Be(0, "answered from the cache at once");
        responder.Public.Should().ContainSingle();
        http.Calls(AltinkaynakCurrencyPath).Should().Be(1, "no second provider request inside the fresh TTL");
        (await ConvertAsync(flow, 1000m, "TRY", "EUR")).Public.Should().ContainSingle();
        http.Calls(AltinkaynakCurrencyPath).Should().Be(1, "EUR comes from the same Currency response");
    }

    [Fact]
    public async Task A_tiny_positive_result_is_shown_as_less_than_the_smallest_unit_never_as_zero()
    {
        var (flow, _, _, _) = Create();
        var responder = await ConvertAsync(flow, 0.3m, "TRY", "GRAM_GOLD"); // 0.3 ÷ 6574.16 ≈ 0.0000456 g, 0,0000 at 4 decimals
        var card = responder.Public.Should().ContainSingle().Subject.Embed!;
        card.Description.Should().Be("**0,30 ₺**\n≈ **<0,0001 g**");
        card.Description.Should().NotContain("0,00 g").And.NotContain("0,0000 g");

        var usd = (await ConvertAsync(flow, 0.1m, "TRY", "USD")).Public.Single().Embed!; // 0.1 ÷ 49.030 ≈ 0.002 USD
        usd.Description.Should().Be("**0,10 ₺**\n≈ **<0,01 USD**");
    }

    [Fact]
    public async Task Gold_then_lira_to_gold_shares_the_cached_quote()
    {
        var (flow, _, http, clock) = Create();
        await flow.RunAsync(MarketInstrument.GramGold, "tr", new Responder(CurrencyChannel)); // /altın
        clock.Advance(TimeSpan.FromSeconds(30));
        (await ConvertAsync(flow, 50000m, "TRY", "GRAM_GOLD")).Public.Should().ContainSingle();
        http.Calls(AltinkaynakGoldPath).Should().Be(1);
        http.TotalCalls.Should().Be(1);
    }

    [Fact]
    public async Task Usd_primary_down_converts_with_the_tcmb_buy_rate_and_says_so()
    {
        var http = Healthy();
        http.Status(AltinkaynakCurrencyPath, HttpStatusCode.InternalServerError);
        var (flow, _, _, _) = Create(http);
        var card = (await ConvertAsync(flow, 2500m, "USD", "TRY")).Public.Single().Embed!;
        card.Description.Should().StartWith("**2.500,00 USD**\n≈ **121.975,25 ₺**"); // 2500 × TCMB ForexBuying 48,7901
        card.Fields.Select(f => (f.Name, f.Value)).Take(2).Should().Equal(("Kullanılan kur", "48,7901 ₺"), ("Kur türü", "Alış"));
        card.Footer.Should().Be("Kaynak: TCMB — Gösterge Kuru");
        card.Description.Should().Contain("Birincil veri kaynağına ulaşılamadı.").And.Contain("TCMB günlük gösterge kurudur; anlık piyasa fiyatı değildir.");
    }

    [Fact]
    public async Task Gold_primary_down_converts_with_the_truncgil_sell_rate()
    {
        var http = Healthy();
        http.Status(AltinkaynakGoldPath, HttpStatusCode.BadGateway);
        var (flow, _, _, _) = Create(http);
        var card = (await ConvertAsync(flow, 50000m, "TRY", "GRAM_GOLD")).Public.Single().Embed!;
        card.Fields.Select(f => (f.Name, f.Value)).Take(2).Should().Equal(("Gram fiyatı", "6.540,36 ₺"), ("Kur türü", "Satış")); // Trunçgil Selling
        card.Description.Should().StartWith("**50.000,00 ₺**\n≈ **7,6448 g**"); // 50000 ÷ 6540.36
        card.Footer.Should().Be("Kaynak: Trunçgil");
    }

    [Fact]
    public async Task A_stale_quote_is_used_and_shown_as_stale()
    {
        var (flow, _, http, clock) = Create();
        await ConvertAsync(flow, 2500m, "USD", "TRY");
        http.Status(AltinkaynakCurrencyPath, HttpStatusCode.ServiceUnavailable);
        http.Status(TcmbPath, HttpStatusCode.ServiceUnavailable);
        clock.Advance(TimeSpan.FromMinutes(5));
        var card = (await ConvertAsync(flow, 2500m, "USD", "TRY")).Public.Single().Embed!;
        card.Description.Should().StartWith("**2.500,00 USD**\n≈ **122.050,00 ₺**").And.EndWith("⚠️ Son başarılı fiyat kullanılıyor.");
        card.Fields.Should().Contain(f => f.Name == "Son başarılı sorgu");
    }

    [Fact]
    public async Task No_price_at_all_gives_the_controlled_notice_with_a_trace_code()
    {
        var http = Healthy();
        http.Status(AltinkaynakCurrencyPath, HttpStatusCode.InternalServerError);
        http.Status(TcmbPath, HttpStatusCode.InternalServerError);
        var (flow, _, _, _) = Create(http);
        var reply = (await ConvertAsync(flow, 2500m, "TRY", "USD")).Public.Single();
        reply.Embed.Should().BeNull();
        reply.Text.Should().StartWith("Döviz/altın verisine şu anda ulaşılamıyor.").And.MatchRegex("TS-[A-Z2-9]{8}").And.NotContain("Exception");
    }
}
