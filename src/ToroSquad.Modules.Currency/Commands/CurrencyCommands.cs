using Discord;
using Discord.Interactions;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Commands;

/// <summary>
/// /dolar, /euro, /altın — no options. The answer is public in the channel. A cached price answers at once; otherwise the
/// command is acknowledged publicly first (a provider round-trip plus a fallback can exceed Discord's 3-second window) and
/// the card — or the "unavailable" notice with its trace code — replaces the acknowledgement. Never pings.
/// </summary>
[ToroModule(CurrencyModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class CurrencyCommands(InteractionServices services, MarketQuoteService quotes, CurrencyCardRenderer cards)
    : ToroInteractionModule(services)
{
    [SlashCommand("dolar", "Current US dollar buy and sell rate in Turkish lira")]
    public Task DollarAsync() => ShowAsync(MarketInstrument.Usd);

    [SlashCommand("euro", "Current euro buy and sell rate in Turkish lira")]
    public Task EuroAsync() => ShowAsync(MarketInstrument.Eur);

    [SlashCommand("altın", "Current gram gold buy and sell price in Turkish lira")]
    public Task GoldAsync() => ShowAsync(MarketInstrument.GramGold);

    private async Task ShowAsync(MarketInstrument instrument)
    {
        var pending = quotes.GetQuoteAsync(instrument, CancellationToken.None);
        if (!pending.IsCompleted && !Context.Interaction.HasResponded)
            await DeferAsync(ephemeral: false);

        var reply = cards.Render(await LangAsync(), await pending);
        await SendAsync(reply.Text, reply.Embed is null ? null : DiscordConversions.ToEmbed(reply.Embed), null, reply.Ephemeral);
    }
}
