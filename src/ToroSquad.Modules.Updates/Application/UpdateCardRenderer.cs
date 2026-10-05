using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>
/// The update card: "🛠️ CS2 Güncellemesi" (a link to the notes), the provider's original title (never translated or
/// summarized; the only provider text on the card, always defused), one fixed sentence, a link to the full notes at the
/// source, the footer "Kaynak: Steam" and the provider's publication time as the embed timestamp (Discord renders it
/// natively; without a provider time there is none). No patch text, no image, no ids, no mentions. The link is also inside
/// the description so the content fingerprint of two posts with the same title differs (MessageFingerprint does not hash
/// the URL).
/// </summary>
public sealed class UpdateCardRenderer(ILocalizer localizer)
{
    public const uint Color = 0xE08A1E;
    public const int TitleMax = 256;

    /// <summary>A real update. Only a link the game's provider itself recognizes is ever rendered.</summary>
    public OutgoingMessage Render(GameUpdateDefinition game, IGameUpdateProvider provider, string url, string title, DateTimeOffset? publishedAt, string language)
    {
        if (!provider.IsCanonicalUrl(url))
            throw new ArgumentException("not a link of the update's provider", nameof(url));
        return Build(game, provider, url, title, publishedAt, language);
    }

    /// <summary>The admin preview's made-up card: the same layout with a sample title and no link (there is nothing to link to).</summary>
    public OutgoingMessage RenderSample(GameUpdateDefinition game, IGameUpdateProvider provider, DateTimeOffset now, string language) =>
        Build(game, provider, null, game.DisplayName + " Update", now, language);

    private OutgoingMessage Build(GameUpdateDefinition game, IGameUpdateProvider provider, string? url, string title, DateTimeOffset? publishedAt, string language)
    {
        var read = localizer.Get(language, provider.ReadLinkKey);
        var description = "**" + DiscordText.Untrusted(title, TitleMax) + "**\n\n" +
                          localizer.Get(language, "updates.card.line", game.DisplayName) + "\n\n" +
                          (url is null ? read : "[" + read + "](" + url + ")");
        var embed = new MessageEmbed(
            localizer.Get(language, "updates.card.title", game.ShortName),
            description,
            url,
            [],
            localizer.Get(language, "updates.card.footer", provider.DisplayName),
            publishedAt,
            Color);
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }
}
