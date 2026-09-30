using Microsoft.Extensions.Options;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.News.Domain;

namespace ToroSquad.Modules.News.Application;

/// <summary>
/// The news card: "📰 Aurora — HLTV" (a link to the article), the original headline (never translated or summarized),
/// a "HLTV’de oku" link, the footer "Kaynak: HLTV" and the feed's publication time as the embed timestamp (Discord renders
/// it natively; no bot-side "43 minutes ago"). No image, no article text, no ids, no mentions. The link is also inside the
/// description so the content fingerprint of two same-titled articles differs (MessageFingerprint does not hash the URL).
/// </summary>
public sealed class NewsCardRenderer(ILocalizer localizer, IOptions<NewsOptions> options)
{
    public const uint Color = 0x2D6DA3;
    public const int HeadlineMax = 256;

    public OutgoingMessage Render(NewsArticle article, string language) => Render(article.CanonicalUrl, article.Title, article.PublishedAt, language, null);

    /// <summary><paramref name="previewNote"/> is shown above the headline on admin previews only.</summary>
    public OutgoingMessage Render(string url, string title, DateTimeOffset? publishedAt, string language, string? previewNote)
    {
        if (!NewsUrl.TryParse(url, out _, out var canonical))
            throw new ArgumentException("not an HLTV news link", nameof(url));
        var headline = DiscordText.Untrusted(NewsText.Plain(title, NewsText.TitleMax), HeadlineMax);
        var read = localizer.Get(language, "news.card.read");
        var description = (previewNote is null ? "" : "_" + DiscordText.Untrusted(previewNote, 200) + "_\n\n") +
                          "**" + headline + "**\n\n[" + read + "](" + canonical + ")";
        var embed = new MessageEmbed(
            localizer.Get(language, "news.card.title", DiscordText.UntrustedPlain(options.Value.Team.DisplayName, 40)),
            description,
            canonical,
            [],
            localizer.Get(language, "news.card.footer"),
            publishedAt,
            Color);
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }
}
