using System.Globalization;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Modules.Currency.Domain;

namespace ToroSquad.Modules.Currency.Application;

/// <summary>The three ways a currency command can answer (implemented by the Discord command class; faked in tests).</summary>
public interface ICurrencyResponder
{
    /// <summary>The channel the command was used in (null when Discord did not say).</summary>
    ulong? ChannelId { get; }

    /// <summary>Public "thinking…" acknowledgement; the public answer replaces it.</summary>
    Task DeferPublicAsync();

    /// <summary>Only the user who ran the command sees it; never pings.</summary>
    Task ReplyPrivateAsync(string text);

    /// <summary>Everyone in the channel sees it; never pings.</summary>
    Task ReplyPublicAsync(CurrencyReply reply);
}

/// <summary>
/// What /dolar, /euro and /altın do, in one place for all three: the channel guard first — outside
/// <see cref="CurrencyOptions.ChannelId"/> the user gets a private pointer to the right channel and nothing else happens
/// (no quote, no provider request, no acknowledgement) — then the quote and the public card.
/// </summary>
public sealed class CurrencyCommandFlow(MarketQuoteService quotes, CurrencyCardRenderer cards, ILocalizer localizer, IOptions<CurrencyOptions> options)
{
    public async Task RunAsync(MarketInstrument instrument, string lang, ICurrencyResponder responder)
    {
        var allowed = options.Value.ChannelId;
        if (responder.ChannelId != allowed)
        {
            await responder.ReplyPrivateAsync(localizer.Get(lang, "currency.wrong_channel", ChannelMention(allowed)));
            return;
        }

        var pending = quotes.GetQuoteAsync(instrument, CancellationToken.None);
        if (!pending.IsCompleted)
            await responder.DeferPublicAsync(); // a provider round-trip plus a fallback can exceed Discord's 3-second window

        await responder.ReplyPublicAsync(cards.Render(lang, await pending));
    }

    /// <summary>Discord channel mention markup: clickable, and a channel mention never pings anyone.</summary>
    public static string ChannelMention(ulong channel) => string.Create(CultureInfo.InvariantCulture, $"<#{channel}>");
}
