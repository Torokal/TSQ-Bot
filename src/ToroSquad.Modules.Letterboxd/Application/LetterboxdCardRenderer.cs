using System.Globalization;
using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Letterboxd.Domain;

namespace ToroSquad.Modules.Letterboxd.Application;

/// <summary>
/// The entry card — one embed per entry, whatever it combines (rating + review + rewatch is still one card):
/// title by what the entry is ("🎬 LordToro değerlendirdi" / "🔁 … yeniden izledi" / "⭐ … puan verdi"), linked to the real
/// Letterboxd entry; the film title and year in bold; fields for THIS entry's rating (only when it has one — never 0/5), the
/// rewatch flag (only when the feed says Yes) and the watched date; the review excerpt (only when there is review text,
/// bounded, ALWAYS behind a spoiler tag whether or not the feed marks spoilers, with a link to the full review). Footer
/// "Kaynak: Letterboxd", the feed's publication time as the embed timestamp, the official poster as thumbnail when present.
/// No translation, no summary, no AI, no mentions; every feed string is escaped (markdown, mentions, links, "||").
/// </summary>
public sealed class LetterboxdCardRenderer(ILocalizer localizer, IOptions<LetterboxdOptions> options)
{
    public const uint Color = 0x00C030;
    public const int FilmTitleMax = 200;

    /// <summary><paramref name="previewNote"/> is shown above the film on admin previews only.</summary>
    public OutgoingMessage Render(EntryView entry, string language, string? previewNote = null)
    {
        var culture = Culture(language);
        var name = DiscordText.UntrustedPlain(options.Value.DisplayName, 40);
        var titleKey = entry.CardKind switch
        {
            EntryCardKind.Review => "letterboxd.card.title.review",
            EntryCardKind.Rewatch => "letterboxd.card.title.rewatch",
            EntryCardKind.Rating => "letterboxd.card.title.rating",
            _ => "letterboxd.card.title.watched",
        };

        var film = "**" + DiscordText.Untrusted(entry.FilmTitle, FilmTitleMax) + "**" +
                   (entry.FilmYear is { } year ? " (" + year.ToString(CultureInfo.InvariantCulture) + ")" : "");
        var description = (previewNote is null ? "" : "_" + DiscordText.Untrusted(previewNote, 200) + "_\n\n") + film;
        if (entry.HasReview)
        {
            var paragraphs = entry.Review!.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(p => string.Join("\n", p.Split('\n').Select(line => DiscordText.Untrusted(line, 2000))));
            var label = localizer.Get(language, entry.Spoilers == true ? "letterboxd.card.review_spoilers" : "letterboxd.card.review");
            var readMore = "[" + localizer.Get(language, "letterboxd.card.read_more") + "](" + entry.Url + ")";
            var review = "\n\n**" + label + "**\n" + DiscordText.Spoiler(string.Join("\n\n", paragraphs)) + (entry.ReviewTruncated ? "\n" + readMore : "");
            // Never cut inside the spoiler (an unclosed "||" would show the text): too long → only the link.
            description += description.Length + review.Length <= DiscordLimits.EmbedDescriptionMax ? review : "\n\n**" + label + "**\n" + readMore;
        }

        var fields = new List<EmbedField>();
        if (entry.RatingHalves is { } halves)
            fields.Add(new(localizer.Get(language, "letterboxd.card.rating"),
                EntryRating.Stars(halves) + " · " + EntryRating.Number(halves, culture) + "/5", true));
        if (entry.Rewatch == true)
            fields.Add(new(localizer.Get(language, "letterboxd.card.rewatch"), localizer.Get(language, "letterboxd.card.rewatch_yes"), true));
        if (entry.WatchedDate is { } watched)
            fields.Add(new(localizer.Get(language, "letterboxd.card.watched"), watched.ToString("d MMMM yyyy", culture), true));

        var embed = new MessageEmbed(
            localizer.Get(language, titleKey, name),
            description,
            entry.Url,
            fields,
            localizer.Get(language, "letterboxd.card.footer"),
            entry.PublishedAt,
            Color,
            ThumbnailPolicy.Check(entry.PosterUrl, LetterboxdUrl.PosterHosts, out _));
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }

    public static CultureInfo Culture(string language) =>
        CultureInfo.GetCultureInfo(language.StartsWith("tr", StringComparison.OrdinalIgnoreCase) ? "tr-TR" : "en-GB");
}
