using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Core.Messaging;

/// <summary>
/// Which mentions may actually ping. Default is none. @everyone/@here and user pings are never allowed for
/// automated messages; only explicitly configured and permitted role IDs can be listed.
/// Wire mapping: allowed_mentions = { "parse": [], "roles": [..Roles] }.
/// </summary>
public sealed record MentionPolicy(IReadOnlyList<RoleId> Roles)
{
    public static MentionPolicy None { get; } = new(Array.Empty<RoleId>());
    public bool PingsAnything => Roles.Count > 0;
}

public sealed record EmbedField(string Name, string Value, bool Inline = false);

public sealed record MessageEmbed(
    string? Title,
    string? Description,
    string? Url,
    IReadOnlyList<EmbedField> Fields,
    string? Footer,
    DateTimeOffset? Timestamp,
    uint? Color);

public sealed record MessageButton(string Label, string? CustomId, string? Url, bool Disabled = false);

/// <summary>SDK-agnostic outgoing message. Rendered by modules, delivered by an <see cref="IMessageTransport"/>.</summary>
public sealed record OutgoingMessage(
    string? Content,
    MessageEmbed? Embed,
    MentionPolicy Mentions,
    IReadOnlyList<MessageButton>? Buttons = null)
{
    /// <summary>Same visible content, but guaranteed not to ping anyone (previews, edits, retries).</summary>
    public OutgoingMessage WithoutPings() => this with { Mentions = MentionPolicy.None };
}

public static class DiscordLimits
{
    public const int ContentMax = 2000;
    public const int EmbedTitleMax = 256;
    public const int EmbedDescriptionMax = 4096;
    public const int EmbedFieldsMax = 25;
    public const int EmbedFieldNameMax = 256;
    public const int EmbedFieldValueMax = 1024;
    public const int EmbedFooterMax = 2048;
    public const int EmbedTotalMax = 6000;
    public const int AutocompleteChoicesMax = 25;

    public static IReadOnlyList<string> Validate(OutgoingMessage message)
    {
        var errors = new List<string>();
        if (message.Content is { Length: > ContentMax })
            errors.Add("content too long");
        if (message.Embed is { } e)
        {
            if (e.Title is { Length: > EmbedTitleMax }) errors.Add("embed title too long");
            if (e.Description is { Length: > EmbedDescriptionMax }) errors.Add("embed description too long");
            if (e.Fields.Count > EmbedFieldsMax) errors.Add("too many embed fields");
            if (e.Fields.Any(f => f.Name.Length is 0 or > EmbedFieldNameMax || f.Value.Length is 0 or > EmbedFieldValueMax))
                errors.Add("embed field size invalid");
            if (e.Footer is { Length: > EmbedFooterMax }) errors.Add("embed footer too long");
            var total = (e.Title?.Length ?? 0) + (e.Description?.Length ?? 0) + (e.Footer?.Length ?? 0) +
                        e.Fields.Sum(f => f.Name.Length + f.Value.Length);
            if (total > EmbedTotalMax) errors.Add("embed total too long");
        }

        return errors;
    }
}

/// <summary>Text helpers for untrusted (provider / user supplied) strings rendered into Discord messages.</summary>
public static partial class DiscordText
{
    private const char ZeroWidthSpace = '​';

    /// <summary>
    /// Neutralizes mention syntax and markdown in untrusted text. Even though allowed_mentions blocks pings,
    /// we also defuse the text so @everyone / &lt;@&amp;id&gt; never even render as mentions, and data cannot
    /// break out of spoiler tags or formatting.
    /// </summary>
    public static string Untrusted(string? value, int maxLength = 256)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                continue;
            switch (ch)
            {
                case '@':
                    sb.Append('@').Append(ZeroWidthSpace);
                    break;
                case '<':
                    sb.Append('<').Append(ZeroWidthSpace);
                    break;
                case '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '[' or ']' or '(' or ')' or '#' or '-':
                    sb.Append('\\').Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        var result = sb.ToString();
        return result.Length <= maxLength ? result : result[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// For places Discord renders without markdown (embed titles, button labels, autocomplete choices):
    /// strips control characters and defuses mention syntax, without adding visible escape backslashes.
    /// </summary>
    public static string UntrustedPlain(string? value, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var sb = new StringBuilder(value.Length + 4);
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                continue;
            sb.Append(ch);
            if (ch is '@' or '<')
                sb.Append(ZeroWidthSpace);
        }

        var result = sb.ToString().Trim();
        return result.Length <= maxLength ? result : result[..(maxLength - 1)] + "…";
    }

    /// <summary>Discord timestamp markup — rendered in each viewer's own client time zone.</summary>
    public static string Timestamp(DateTimeOffset instant, char style = 'F') =>
        string.Create(CultureInfo.InvariantCulture, $"<t:{instant.ToUnixTimeSeconds()}:{style}>");

    public static string Spoiler(string text) => "||" + text.Replace("||", "| |", StringComparison.Ordinal) + "||";

    public static string RoleMention(RoleId role) => string.Create(CultureInfo.InvariantCulture, $"<@&{role.Value}>");

    /// <summary>Only http(s) URLs on an explicit host allow-list are rendered as links.</summary>
    public static string? SafeUrl(string? url, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512)
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return allowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? uri.AbsoluteUri : null;
    }

    [GeneratedRegex(@"@(everyone|here)|<@[!&]?\d+>", RegexOptions.CultureInvariant)]
    public static partial Regex RawMentionPattern();
}
