using System.Globalization;
using System.Text;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Currency.Domain;
using ToroSquad.Modules.Currency.Providers;

namespace ToroSquad.Modules.Currency.Application;

/// <summary>
/// What /dolar, /euro and /altın answer: a card, or (nothing usable at all) a short text with a trace code. Never ephemeral:
/// everyone in the channel sees it — including the "unavailable" notice, which replaces the public acknowledgement.
/// </summary>
public sealed record CurrencyReply(MessageEmbed? Embed, string? Text, bool Ephemeral = false);

/// <summary>
/// The one card layout shared by every instrument (no Discord SDK — unit-tested): title, buy and sell in tr-TR lira format,
/// the provider's own update time as Discord timestamps (each viewer's time zone), and the source in the footer. A fallback
/// source is named as such (TCMB: indicative rate, date only); a stale price carries a warning and both its update time
/// and how long ago the bot last received it.
/// </summary>
public sealed class CurrencyCardRenderer(ILocalizer localizer)
{
    public const uint NormalColor = 0xE8590C;
    public const uint StaleColor = 0xF59F00;
    public const string Lira = "₺";

    public CurrencyReply Render(string lang, MarketQuoteResult result)
    {
        if (result.Quote is not { } quote)
            return new CurrencyReply(null, L(lang, "currency.unavailable") + "\n" + L(lang, "error.trace_code", result.TraceCode ?? "?"));

        var notes = new List<string>();
        if (quote.IsStale)
            notes.Add(L(lang, "currency.note.stale"));
        else if (quote.IsFallback)
            notes.Add(L(lang, "currency.note.fallback"));
        if (quote.Source == MarketSource.Tcmb)
            notes.Add(L(lang, "currency.note.tcmb_indicative"));

        var fields = new List<EmbedField>
        {
            new(L(lang, "currency.card.buy"), Price(quote.Buy), true),
            new(L(lang, "currency.card.sell"), Price(quote.Sell), true),
            Updated(lang, quote),
        };
        if (quote.IsStale)
            fields.Add(new EmbedField(L(lang, "currency.card.last_received"), DiscordText.Timestamp(quote.RetrievedAt, 'R')));

        return new CurrencyReply(new MessageEmbed(
            Emoji(quote.Instrument) + " " + L(lang, NameKey(quote.Instrument)),
            notes.Count == 0 ? null : string.Join("\n", notes),
            null,
            fields,
            L(lang, "currency.card.source", L(lang, SourceKey(quote.Source))),
            null,
            quote.IsStale ? StaleColor : NormalColor), null);
    }

    /// <summary>tr-TR lira: "48,820 ₺", "6.439,47 ₺". The provider's own precision is kept (2 to 4 decimals).</summary>
    public static string Price(decimal value)
    {
        var decimals = Math.Clamp((int)value.Scale, 2, 4);
        return value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), ProviderFormats.Turkish) + " " + Lira;
    }

    public static string Emoji(MarketInstrument instrument) => instrument switch
    {
        MarketInstrument.Usd => "💵",
        MarketInstrument.Eur => "💶",
        MarketInstrument.GramGold => "🪙",
        _ => "💱",
    };

    public static string NameKey(MarketInstrument instrument) => instrument switch
    {
        MarketInstrument.Usd => "currency.instrument.usd",
        MarketInstrument.Eur => "currency.instrument.eur",
        _ => "currency.instrument.gram_gold",
    };

    public static string SourceKey(MarketSource source) => source switch
    {
        MarketSource.Altinkaynak => "currency.source.altinkaynak",
        MarketSource.Tcmb => "currency.source.tcmb",
        _ => "currency.source.truncgil",
    };

    /// <summary>
    /// The provider's time as Discord timestamps (rendered in each viewer's own time zone). A TCMB bulletin only has a date:
    /// shown as that Türkiye calendar date in plain text, since a date-only Discord timestamp would shift a day for viewers
    /// west of Türkiye.
    /// </summary>
    private EmbedField Updated(string lang, MarketQuote quote)
    {
        if (quote.Price.SourceDateOnly && ProviderFormats.TryTurkeyZone(out var turkey))
        {
            var day = TimeZoneInfo.ConvertTime(quote.SourceTimestamp, turkey);
            return new EmbedField(L(lang, "currency.card.bulletin_date"), day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        }

        var text = new StringBuilder()
            .Append(DiscordText.Timestamp(quote.SourceTimestamp, 'R'))
            .Append(" · ")
            .Append(DiscordText.Timestamp(quote.SourceTimestamp, 'f'));
        return new EmbedField(L(lang, "currency.card.updated"), text.ToString());
    }

    private string L(string lang, string key, params object?[] args) => localizer.Get(lang, key, args);
}
