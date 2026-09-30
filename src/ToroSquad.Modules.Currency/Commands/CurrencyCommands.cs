using Discord;
using Discord.Interactions;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Currency.Application;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Commands;

/// <summary>
/// /dolar, /euro, /altın (no options) and /çevir (amount, from, to) — only in the currency channel (<see cref="CurrencyOptions.ChannelId"/>). The flow
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

    [SlashCommand("çevir", "Convert between Turkish lira, US dollar, euro and gram gold")]
    public async Task ConvertAsync(
        [Summary("miktar", "Amount to convert"), MinValue(0), MaxValue(1_000_000_000)] double miktar,
        [Summary("kaynak", "What you have"),
         Choice("Turkish lira (TRY)", "TRY"), Choice("US dollar (USD)", "USD"), Choice("Euro (EUR)", "EUR"), Choice("Gram gold", "GRAM_GOLD")] string kaynak,
        [Summary("hedef", "What to convert it to"),
         Choice("Turkish lira (TRY)", "TRY"), Choice("US dollar (USD)", "USD"), Choice("Euro (EUR)", "EUR"), Choice("Gram gold", "GRAM_GOLD")] string hedef) =>
        await flow.ConvertAsync(ToAmount(miktar), kaynak, hedef, await LangAsync(), this);

    /// <summary>
    /// Discord's number option arrives as a binary floating-point value; it becomes a decimal right here and nowhere else. Non-finite or
    /// non-positive values map to 0 (refused as "not positive"), values beyond the limit just past it (refused as too large).
    /// </summary>
    private static decimal ToAmount(double value) =>
        !double.IsFinite(value) || value <= 0 ? 0m
        : value > (double)CurrencyConversionService.MaxAmount ? CurrencyConversionService.MaxAmount + 1
        : (decimal)value;

    private async Task ShowAsync(MarketInstrument instrument) => await flow.RunAsync(instrument, await LangAsync(), this);

    ulong? ICurrencyResponder.ChannelId => Context.Interaction.ChannelId;

    Task ICurrencyResponder.DeferPublicAsync() => Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync(ephemeral: false);

    Task ICurrencyResponder.ReplyPrivateAsync(string text) => SendEphemeralAsync(text, null, null);

    Task ICurrencyResponder.ReplyPublicAsync(CurrencyReply reply) =>
        SendAsync(reply.Text, reply.Embed is null ? null : DiscordConversions.ToEmbed(reply.Embed), null, reply.Ephemeral);
}
