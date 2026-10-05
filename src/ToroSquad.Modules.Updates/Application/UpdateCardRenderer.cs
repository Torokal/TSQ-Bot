using System.Text;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>
/// The update card: "🛠️ CS2 Güncellemesi" (a link to the notes), the provider's original title (never translated or
/// summarized), one fixed sentence, a link to the full notes at the source, the footer "Kaynak: Steam" and the provider's
/// publication time as the embed timestamp (Discord renders it natively; without a provider time there is none). No image,
/// no ids, no mentions. The link is also inside the description so the content fingerprint of two posts with the same
/// title differs (MessageFingerprint does not hash the URL).
/// <para>When a post comes with <see cref="UpdateHighlights"/>, the card also shows version and build ("1.60.1 · Build
/// 69977") and a short excerpt of the change list — the first sections with their first lines, cut deterministically, and
/// how many changes are left for the source. Never the whole post: the link is always there for that. Without highlights
/// the card is exactly title, sentence and link.</para>
/// Every provider string (title, headings, lines, version) is defused before it is rendered.
/// </summary>
public sealed class UpdateCardRenderer(ILocalizer localizer)
{
    public const uint Color = 0xE08A1E;
    public const int TitleMax = 256;

    /// <summary>Sections of the excerpt shown on a card.</summary>
    public const int MaxSections = 3;

    /// <summary>Lines shown per section.</summary>
    public const int MaxItemsPerSection = 3;

    /// <summary>A longer line is cut (with "…") before it is rendered.</summary>
    public const int MaxItemLength = 160;

    /// <summary>The excerpt stops before it grows beyond this many characters.</summary>
    public const int MaxExcerptLength = 1100;

    /// <summary>A real update. Only a link the game's provider itself recognizes is ever rendered.</summary>
    public OutgoingMessage Render(GameUpdateDefinition game, IGameUpdateProvider provider, string url, string title, DateTimeOffset? publishedAt, string language,
        UpdateHighlights? highlights = null)
    {
        if (!provider.IsCanonicalUrl(url))
            throw new ArgumentException("not a link of the update's provider", nameof(url));
        return Build(game, provider, url, title, publishedAt, language, highlights);
    }

    /// <summary>The admin preview's made-up card: the same layout with a sample title and no link (there is nothing to link to).</summary>
    public OutgoingMessage RenderSample(GameUpdateDefinition game, IGameUpdateProvider provider, DateTimeOffset now, string language) =>
        Build(game, provider, null, game.DisplayName + " Update", now, language, null);

    private OutgoingMessage Build(GameUpdateDefinition game, IGameUpdateProvider provider, string? url, string title, DateTimeOffset? publishedAt, string language,
        UpdateHighlights? highlights)
    {
        var read = localizer.Get(language, provider.ReadLinkKey);
        var description = "**" + DiscordText.Untrusted(title, TitleMax) + "**" + Excerpt(highlights, language) + "\n\n" +
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

    /// <summary>
    /// "\n1.60.1 · Build 69977\n\n**Bug Fixes**\n• …" — empty without highlights. Deterministic: the same highlights always
    /// give the same text (the outbox compares payloads, so an unchanged excerpt is never an edit).
    /// </summary>
    private string Excerpt(UpdateHighlights? highlights, string language)
    {
        if (highlights is null || highlights.IsEmpty)
            return "";
        var text = new StringBuilder();
        var version = string.Join(" · ", new[] { highlights.Version, highlights.Build is null ? null : "Build " + highlights.Build }.Where(p => p is not null));
        if (version.Length > 0)
            text.Append('\n').Append(Plain(version, UpdateHighlights.MaxVersionLength * 2 + 16));

        var shown = 0;
        foreach (var section in highlights.Sections.Take(MaxSections))
        {
            var block = new StringBuilder("\n");
            if (section.Heading is not null)
                block.Append("\n**").Append(Plain(section.Heading, UpdateHighlights.MaxHeadingLength)).Append("**");
            var lines = 0;
            foreach (var item in section.Items.Take(MaxItemsPerSection))
            {
                var line = "\n• " + Plain(item, MaxItemLength);
                if (text.Length + block.Length + line.Length > MaxExcerptLength)
                    break;
                block.Append(line);
                lines++;
            }

            if (lines == 0)
                break; // no room for even one line of this section: the excerpt ends here
            text.Append(block);
            shown += lines;
        }

        var more = highlights.ChangeCount - shown;
        if (shown > 0 && more > 0)
            text.Append("\n\n_").Append(localizer.Get(language, "updates.card.more", more)).Append('_');
        return text.ToString();
    }

    /// <summary>Provider text for the card: cut to <paramref name="max"/> characters first, then defused (so a cut never lands inside an escape).</summary>
    private static string Plain(string value, int max)
    {
        if (value.Length > max)
        {
            var cut = value[..(max - 1)];
            if (char.IsHighSurrogate(cut[^1]))
                cut = cut[..^1];
            value = cut.TrimEnd() + "…";
        }

        return DiscordText.Untrusted(value, max * 3);
    }
}
