using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ToroSquad.Core.Messaging;

/// <summary>
/// Which mentions may actually ping. Default is none. @here and user pings are never allowed for automated messages;
/// only explicitly configured and permitted role IDs can be listed. <see cref="Everyone"/> is a separate, explicit
/// opt-in for exactly one case — the first message of a new TSQ Live stream announcement — and is never set by any
/// other module (architecture test). Edits and retries after a proven non-delivery reuse the same payload; edits always
/// go out with <see cref="None"/> (outbox + transport).
/// Wire mapping: allowed_mentions = { "parse": Everyone ? ["everyone"] : [], "roles": [..Roles] }.
/// <see cref="Everyone"/> is left out of the stored payload while false, so existing payload hashes are unchanged.
/// </summary>
public sealed record MentionPolicy(
    IReadOnlyList<RoleId> Roles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Everyone = false)
{
    public static MentionPolicy None { get; } = new(Array.Empty<RoleId>());

    /// <summary>@everyone only (no roles). Only for the first send of a new live-stream announcement.</summary>
    public static MentionPolicy EveryoneOnly { get; } = new(Array.Empty<RoleId>(), Everyone: true);

    public bool PingsAnything => Roles.Count > 0 || Everyone;
}

public sealed record EmbedField(string Name, string Value, bool Inline = false);

/// <summary>
/// <see cref="ThumbnailUrl"/> is a small image in the embed's corner (e.g. a team logo). It is omitted from the stored
/// payload when absent, so messages without a thumbnail keep their previous payload hash, and it is not part of
/// <see cref="MessageFingerprint"/> (like <see cref="Url"/>).
/// </summary>
public sealed record MessageEmbed(
    string? Title,
    string? Description,
    string? Url,
    IReadOnlyList<EmbedField> Fields,
    string? Footer,
    DateTimeOffset? Timestamp,
    uint? Color,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ThumbnailUrl = null);

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
    public const int EmbedUrlMax = 2048;
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
            if (e.ThumbnailUrl is { } thumbnail &&
                (thumbnail.Length > EmbedUrlMax || !Uri.TryCreate(thumbnail, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
                errors.Add("embed thumbnail url invalid");
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
    private const char ZeroWidthSpace = '\u200B';

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

        var result = DefuseLinks(sb.ToString());
        return result.Length <= maxLength ? result : result[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// Discord turns any "scheme://" text into a clickable link; untrusted text must not bypass the URL allow-list.
    /// </summary>
    private static string DefuseLinks(string text) => text.Replace("://", ":" + ZeroWidthSpace + "//", StringComparison.Ordinal);

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

        var result = DefuseLinks(sb.ToString().Trim());
        return result.Length <= maxLength ? result : result[..(maxLength - 1)] + "…";
    }

    /// <summary>Discord timestamp markup — rendered in each viewer's own client time zone.</summary>
    public static string Timestamp(DateTimeOffset instant, char style = 'F') =>
        string.Create(CultureInfo.InvariantCulture, $"<t:{instant.ToUnixTimeSeconds()}:{style}>");

    public static string Spoiler(string text)
    {
        // Repeat until no "||" remains: a single pass turns "|||" into "| ||", which would still close the spoiler.
        while (text.Contains("||", StringComparison.Ordinal))
            text = text.Replace("||", "| |", StringComparison.Ordinal);
        return "||" + text + "||";
    }

    public static string RoleMention(RoleId role) => string.Create(CultureInfo.InvariantCulture, $"<@&{role.Value}>");

    /// <summary>Only http(s) URLs on an explicit host allow-list are rendered as links.</summary>
    public static string? SafeUrl(string? url, IReadOnlyCollection<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512)
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return allowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal)) ? uri.AbsoluteUri : null;
    }

    [GeneratedRegex(@"@(everyone|here)|<@[!&]?\d+>", RegexOptions.CultureInvariant)]
    public static partial Regex RawMentionPattern();
}
