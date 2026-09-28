using ToroSquad.Modules.Currency;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// /dolar, /euro, /altın answer only in the currency channel: there a public card, anywhere else a private pointer — and
/// then nothing at all (no quote, no provider request, no acknowledgement).
/// </summary>
public sealed class CurrencyChannelGateTests
{
    private const ulong CurrencyChannel = 1242464361855848459;
    private const ulong OtherChannel = 689812743242514449;

    /// <summary>Records what the command would have sent to Discord.</summary>
    private sealed class FakeResponder(ulong? channel) : ICurrencyResponder
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

    private static (CurrencyCommandFlow Flow, MarketQuoteService Service, CurrencyHttpStub Http) Create()
    {
        var (service, http, _) = CurrencyTestKit.OverHttp();
        var localizer = CurrencyTestKit.Localizer();
        return (new CurrencyCommandFlow(service, new CurrencyCardRenderer(localizer), localizer, CurrencyTestKit.Options()), service, http);
    }

    [Fact]
    public void The_production_channel_is_the_configured_default()
    {
        new CurrencyOptions().ChannelId.Should().Be(CurrencyChannel);
        CurrencyCommandFlow.ChannelMention(CurrencyChannel).Should().Be("<#1242464361855848459>");
    }

    [Theory]
    [InlineData(MarketInstrument.Usd, "💵 Amerikan Doları")]
    [InlineData(MarketInstrument.Eur, "💶 Euro")]
    [InlineData(MarketInstrument.GramGold, "🪙 Gram Altın")]
    public async Task In_the_currency_channel_the_answer_is_a_public_card(MarketInstrument instrument, string title)
    {
        var (flow, _, http) = Create();
        var responder = new FakeResponder(CurrencyChannel);
        await flow.RunAsync(instrument, "tr", responder);

        responder.Private.Should().BeEmpty();
        var reply = responder.Public.Should().ContainSingle().Subject;
        reply.Ephemeral.Should().BeFalse("everyone in the channel sees it");
        reply.Embed!.Title.Should().Be(title);
        responder.Defers.Should().Be(0, "the answer was ready at once");
        http.TotalCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_slow_provider_is_acknowledged_publicly_before_the_card()
    {
        var http = CurrencyHttpStub.Healthy();
        http.Hang(CurrencyHttpStub.AltinkaynakCurrencyPath);
        var (service, _, clock) = CurrencyTestKit.OverHttp(http);
        var localizer = CurrencyTestKit.Localizer();
        var flow = new CurrencyCommandFlow(service, new CurrencyCardRenderer(localizer), localizer, CurrencyTestKit.Options());
        var responder = new FakeResponder(CurrencyChannel);

        var running = flow.RunAsync(MarketInstrument.Usd, "tr", responder);
        responder.Defers.Should().Be(1, "public, like the answer");
        responder.Public.Should().BeEmpty();
        responder.Private.Should().BeEmpty();

        clock.Advance(TimeSpan.FromSeconds(5)); // Altınkaynak times out → TCMB
        await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        responder.Public.Should().ContainSingle().Which.Embed!.Footer.Should().Be("Kaynak: TCMB — Gösterge Kuru");
    }

    [Fact]
    public async Task A_cached_price_answers_without_an_acknowledgement()
    {
        var (flow, _, _) = Create();
        await flow.RunAsync(MarketInstrument.Usd, "tr", new FakeResponder(CurrencyChannel));
        var second = new FakeResponder(CurrencyChannel);
        await flow.RunAsync(MarketInstrument.Eur, "tr", second);
        second.Defers.Should().Be(0);
        second.Public.Should().ContainSingle();
    }

    [Theory]
    [InlineData(MarketInstrument.Usd, OtherChannel)]
    [InlineData(MarketInstrument.Eur, OtherChannel)]
    [InlineData(MarketInstrument.GramGold, OtherChannel)]
    [InlineData(MarketInstrument.Usd, 1UL)]
    [InlineData(MarketInstrument.Usd, null)]
    public async Task Elsewhere_only_a_private_pointer_and_no_fetch_at_all(MarketInstrument instrument, ulong? channel)
    {
        var (flow, service, http) = Create();
        var responder = new FakeResponder(channel);
        await flow.RunAsync(instrument, "tr", responder);

        responder.Private.Should().Equal("Bu komutu yalnızca <#1242464361855848459> kanalında kullanabilirsiniz.");
        responder.Public.Should().BeEmpty("no public answer");
        responder.Defers.Should().Be(0, "no public acknowledgement either");
        http.TotalCalls.Should().Be(0, "no provider request");
        service.LastStatus.Should().BeNull("the quote service was not even asked");
    }

    [Fact]
    public async Task English_guilds_get_the_english_pointer()
    {
        var (flow, _, _) = Create();
        var responder = new FakeResponder(OtherChannel);
        await flow.RunAsync(MarketInstrument.Usd, "en", responder);
        responder.Private.Should().Equal("You can only use this command in <#1242464361855848459>.");
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(4194303UL)]
    [InlineData(9223372036854775808UL)]
    public void An_invalid_channel_id_fails_startup_validation(ulong channel) =>
        new CurrencyOptions { ChannelId = channel }.Validate().Should().ContainSingle(e => e.Contains("Currency:ChannelId", StringComparison.Ordinal));
}
