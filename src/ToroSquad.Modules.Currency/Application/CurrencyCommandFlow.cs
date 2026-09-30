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
/// What /dolar, /euro, /altın and /çevir do, in one place: the channel guard first — outside
/// <see cref="CurrencyOptions.ChannelId"/> the user gets a private pointer to the right channel and nothing else happens
/// (no quote, no provider request, no acknowledgement) — then the quote from <see cref="MarketQuoteService"/> (same
/// providers, fallbacks and cache for every command) and the public card. /çevir additionally refuses a bad amount or an
/// unsupported pair privately, before any quote is asked for.
/// </summary>
public sealed class CurrencyCommandFlow(MarketQuoteService quotes, CurrencyCardRenderer cards, ILocalizer localizer, IOptions<CurrencyOptions> options)
{
    public async Task RunAsync(MarketInstrument instrument, string lang, ICurrencyResponder responder)
    {
        if (!await EnsureAllowedChannelAsync(lang, responder))
            return;

        var pending = quotes.GetQuoteAsync(instrument, CancellationToken.None);
        if (!pending.IsCompleted)
            await responder.DeferPublicAsync(); // a provider round-trip plus a fallback can exceed Discord's 3-second window

        await responder.ReplyPublicAsync(cards.Render(lang, await pending));
    }

    /// <summary>
    /// /çevir. <paramref name="from"/> / <paramref name="to"/> are the option's choice values (unknown values count as an
    /// unsupported pair). Success and "no price" are public like the other commands; refusals are private.
    /// </summary>
    public async Task ConvertAsync(decimal amount, string from, string to, string lang, ICurrencyResponder responder)
    {
        if (!await EnsureAllowedChannelAsync(lang, responder))
            return;

        var refusal = CurrencyConversionService.ChoiceValues.TryGetValue(from, out var source) &&
                      CurrencyConversionService.ChoiceValues.TryGetValue(to, out var target)
            ? CurrencyConversionService.Validate(amount, source, target, out var request)
            : Refuse(out request);
        if (refusal != ConversionRefusal.None)
        {
            await responder.ReplyPrivateAsync(cards.RenderRefusal(lang, refusal));
            return;
        }

        var pending = quotes.GetQuoteAsync(request!.Instrument, CancellationToken.None);
        if (!pending.IsCompleted)
            await responder.DeferPublicAsync();

        var result = await pending;
        await responder.ReplyPublicAsync(result.Quote is { } quote
            ? cards.RenderConversion(lang, CurrencyConversionService.Convert(request, quote))
            : cards.Render(lang, result)); // the controlled "unavailable" notice with its trace code, as /dolar answers it

        static ConversionRefusal Refuse(out ConversionRequest? request)
        {
            request = null;
            return ConversionRefusal.UnsupportedPair;
        }
    }

    /// <summary>The one channel guard of every currency command.</summary>
    private async Task<bool> EnsureAllowedChannelAsync(string lang, ICurrencyResponder responder)
    {
        var allowed = options.Value.ChannelId;
        if (responder.ChannelId == allowed)
            return true;
        await responder.ReplyPrivateAsync(localizer.Get(lang, "currency.wrong_channel", ChannelMention(allowed)));
        return false;
    }

    /// <summary>Discord channel mention markup: clickable, and a channel mention never pings anyone.</summary>
    public static string ChannelMention(ulong channel) => string.Create(CultureInfo.InvariantCulture, $"<#{channel}>");
}
