using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.News.Domain;

/// <summary>
/// One feed item as TSQ keeps it. <see cref="ArticleId"/> (the number in the HLTV news URL) is the identity — never the
/// headline. <see cref="MatchText"/> is the RSS description as plain text, bounded, used for matching only and never
/// stored or shown.
/// </summary>
public sealed record NewsArticle(
    long ArticleId,
    string CanonicalUrl,
    string Title,
    DateTimeOffset? PublishedAt,
    string MatchText)
{
    public const string Source = "hltv";

    /// <summary>What the card shows: a change of any of these (not of the description) edits a posted card.</summary>
    public string ContentHash
    {
        get
        {
            var text = string.Join('\u001F', Title, CanonicalUrl,
                PublishedAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "", MatchText);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        }
    }
}

/// <summary>
/// The only news links TSQ accepts and renders: https://www.hltv.org/news/&lt;id&gt;/&lt;slug&gt; exactly (no look-alike
/// hosts, no credentials, no port, no query or fragment kept). Nothing here fetches the page.
/// </summary>
public static partial class NewsUrl
{
    public const string Host = "www.hltv.org";

    public static bool TryParse(string? value, out long articleId, out string canonical)
    {
        articleId = 0;
        canonical = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 400)
            return false;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo))
            return false;
        if (!string.Equals(uri.IdnHost, Host, StringComparison.OrdinalIgnoreCase))
            return false;
        var match = PathPattern().Match(uri.AbsolutePath);
        if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out articleId) || articleId <= 0)
            return false;
        var slug = match.Groups[2].Value.ToLowerInvariant();
        canonical = string.Create(CultureInfo.InvariantCulture, $"https://{Host}/news/{articleId}/{slug}");
        return true;
    }

    [GeneratedRegex(@"^/news/([0-9]{1,10})/([A-Za-z0-9-]{1,200})/?$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}

/// <summary>Plain-text handling of feed strings: entities, markup and whitespace, deterministic and bounded.</summary>
public static partial class NewsText
{
    public const int TitleMax = 300;
    public const int MatchTextMax = 1000;

    /// <summary>HTML/markup removed (images, scripts, links are text only — nothing is fetched), entities decoded.</summary>
    public static string Plain(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var text = ScriptOrStyle().Replace(value, " ");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        var result = sb.ToString();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    /// <summary>For matching: compatibility-normalized, accents kept as letters, invariant lower case, one space.</summary>
    public static string ForMatching(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var text = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var c = ch switch
            {
                '’' or '‘' or 'ʼ' => '\'',
                '‐' or '‑' or '‒' or '–' or '—' => '-',
                _ => ch,
            };
            sb.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
        }

        return MultiSpace().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"<(script|style|iframe)\b[^>]*>.*?</\1\s*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpace();
}
