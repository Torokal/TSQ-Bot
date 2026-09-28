using Discord;
using Discord.Interactions;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Commands;

/// <summary>
/// /dolar, /euro, /altın — no options, only in the currency channel (<see cref="CurrencyOptions.ChannelId"/>). The flow
/// (<see cref="CurrencyCommandFlow"/>) decides: elsewhere a private pointer to that channel and nothing else; there a public
/// card. A cached price answers at once; otherwise the command is acknowledged publicly first and the card — or the
/// "unavailable" notice with its trace code — replaces the acknowledgement. Never pings.
/// </summary>
[ToroModule(CurrencyModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class CurrencyCommands(InteractionServices services, CurrencyCommandFlow flow)
    : ToroInteractionModule(services), ICurrencyResponder
{
    [SlashCommand("dolar", "Current US dollar buy and sell rate in Turkish lira")]
    public Task DollarAsync() => ShowAsync(MarketInstrument.Usd);

    [SlashCommand("euro", "Current euro buy and sell rate in Turkish lira")]
    public Task EuroAsync() => ShowAsync(MarketInstrument.Eur);

    [SlashCommand("altın", "Current gram gold buy and sell price in Turkish lira")]
    public Task GoldAsync() => ShowAsync(MarketInstrument.GramGold);

    private async Task ShowAsync(MarketInstrument instrument) => await flow.RunAsync(instrument, await LangAsync(), this);

    ulong? ICurrencyResponder.ChannelId => Context.Interaction.ChannelId;

    Task ICurrencyResponder.DeferPublicAsync() => Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync(ephemeral: false);

    Task ICurrencyResponder.ReplyPrivateAsync(string text) => SendEphemeralAsync(text, null, null);

    Task ICurrencyResponder.ReplyPublicAsync(CurrencyReply reply) =>
        SendAsync(reply.Text, reply.Embed is null ? null : DiscordConversions.ToEmbed(reply.Embed), null, reply.Ephemeral);
}
