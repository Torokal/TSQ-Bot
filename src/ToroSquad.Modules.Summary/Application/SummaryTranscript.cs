using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>Who wrote a message, as far as the summary cares: only <see cref="Member"/> messages are summarized.</summary>
public enum SummaryAuthorKind
{
    /// <summary>A person's own message (default or reply).</summary>
    Member = 0,

    /// <summary>Any bot — TSQ Bot's own earlier summaries included.</summary>
    Bot = 1,

    /// <summary>A webhook (integrations, cross-posts).</summary>
    Webhook = 2,

    /// <summary>Joins, pins, boosts, thread notices and every other system event.</summary>
    System = 3,
}

/// <summary>An attachment as metadata only: nothing is downloaded, only the kind (or a file name) reaches the transcript.</summary>
public sealed record SummaryAttachment(string FileName, string? ContentType);

/// <summary>
/// One message as Discord returned it (SDK-free). <paramref name="Content"/> is the raw text with Discord markup
/// (mentions, custom emoji, timestamps); <see cref="SummaryTranscript"/> makes it readable.
/// </summary>
public sealed record SummarySourceMessage(
    ulong Id,
    DateTimeOffset Timestamp,
    SummaryAuthorKind Kind,
    string AuthorName,
    string Content,
    IReadOnlyList<SummaryAttachment> Attachments,
    IReadOnlyList<string> Stickers,
    bool IsForward = false,
    bool HasPoll = false,
    bool HasEmbeds = false);

/// <summary>Display names for the ids that appear in message markup (users, roles, channels).</summary>
public sealed record SummaryMentionNames(
    IReadOnlyDictionary<ulong, string> Users,
    IReadOnlyDictionary<ulong, string> Roles,
    IReadOnlyDictionary<ulong, string> Channels)
{
    public static SummaryMentionNames Empty { get; } = new(new Dictionary<ulong, string>(), new Dictionary<ulong, string>(), new Dictionary<ulong, string>());
}

/// <summary>
/// The text sent to the model and the counts logged about it (never the text itself).
/// </summary>
/// <param name="Text">One "Name: message" line per message, oldest → newest.</param>
/// <param name="MessageCount">Lines in <paramref name="Text"/>.</param>
/// <param name="TruncatedMessageCount">Messages shortened to <see cref="SummaryTranscript.MaxMessageChars"/>.</param>
/// <param name="DroppedForSizeCount">Oldest messages left out to stay under <see cref="SummaryTranscript.MaxTranscriptChars"/>.</param>
/// <param name="EmptyMessageCount">Member messages with nothing to show (no text, no attachment, no sticker) — withheld text looks like this.</param>
public sealed record SummaryTranscriptResult(string Text, int MessageCount, int TruncatedMessageCount, int DroppedForSizeCount, int EmptyMessageCount);

/// <summary>
/// Builds the transcript the model reads: member messages only (bots — TSQ Bot's own summaries included —, webhooks and system
/// events are dropped), the newest <c>maxMessages</c>, sent oldest → newest as "DisplayName: text". No ids, no timestamps per
/// line, no reactions. Discord markup becomes readable (<c>&lt;@id&gt;</c> → @Name, <c>&lt;#id&gt;</c> → #kanal, custom emoji
/// → :name:, <c>&lt;t:…&gt;</c> → a local date), links become <c>[link: host]</c> (no path, no tracking query), attachments
/// become placeholders by kind. One message is capped at <see cref="MaxMessageChars"/> and the whole transcript at
/// <see cref="MaxTranscriptChars"/> (the oldest lines go first). Pure and deterministic.
/// </summary>
public static partial class SummaryTranscript
{
    public const int MaxMessageChars = 1500;
    public const int MaxTranscriptChars = 40_000;
    public const int MaxNameChars = 32;
    public const string TruncatedMarker = "[uzun mesaj kısaltıldı]";
    public const string FallbackName = "Kullanıcı";

    public static SummaryTranscriptResult Build(
        IEnumerable<SummarySourceMessage> messages, SummaryMentionNames names, TimeZoneInfo zone, int maxMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);

        // Newest member messages first to pick them, then oldest → newest for reading.
        var picked = messages
            .Where(m => m.Kind == SummaryAuthorKind.Member)
            .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id)
            .Take(maxMessages)
            .Reverse()
            .ToList();

        var lines = new List<string>(picked.Count);
        int truncated = 0, empty = 0;
        foreach (var message in picked)
        {
            var (text, wasTruncated) = Normalize(message, names, zone);
            if (text.Length == 0)
            {
                empty++;
                continue;
            }

            if (wasTruncated)
                truncated++;
            lines.Add(Name(message.AuthorName) + ": " + text);
        }

        // Hard size limit: keep the newest lines.
        var dropped = 0;
        var total = lines.Sum(l => l.Length + 1);
        while (total > MaxTranscriptChars && lines.Count > 0)
        {
            total -= lines[0].Length + 1;
            lines.RemoveAt(0);
            dropped++;
        }

        return new SummaryTranscriptResult(string.Join("\n", lines), lines.Count, truncated, dropped, empty);
    }

    /// <summary>One message as a single transcript line body (without the name); empty when there is nothing to show.</summary>
    public static (string Text, bool Truncated) Normalize(SummarySourceMessage message, SummaryMentionNames names, TimeZoneInfo zone)
    {
        var text = ReadableText(message.Content, names, zone);
        var truncated = false;
        if (text.Length > MaxMessageChars)
        {
            var cut = text.LastIndexOf(' ', MaxMessageChars);
            text = text[..(cut > MaxMessageChars / 2 ? cut : MaxMessageChars)].TrimEnd() + " " + TruncatedMarker;
            truncated = true;
        }

        var parts = new List<string>(4);
        if (text.Length > 0)
            parts.Add(text);
        if (message.IsForward)
            parts.Add("[iletilen mesaj]");
        if (message.HasPoll)
            parts.Add("[anket]");
        parts.AddRange(message.Attachments.Take(10).Select(Placeholder));
        parts.AddRange(message.Stickers.Take(3).Select(s => "[sticker: " + Plain(s, 40, "sticker") + "]"));
        return (string.Join(" ", parts), truncated);
    }

    /// <summary>Discord markup → readable text on one line, with links reduced to their host.</summary>
    public static string ReadableText(string? content, SummaryMentionNames names, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var text = UserMention().Replace(content, m => "@" + Lookup(names.Users, m.Groups[1].Value, "kullanıcı"));
        text = RoleMention().Replace(text, m => "@" + Lookup(names.Roles, m.Groups[1].Value, "rol"));
        text = ChannelMention().Replace(text, m => "#" + Lookup(names.Channels, m.Groups[1].Value, "kanal"));
        text = SlashMention().Replace(text, m => "/" + m.Groups[1].Value);
        text = CustomEmoji().Replace(text, m => ":" + m.Groups[1].Value + ":");
        text = TimestampMarkup().Replace(text, m => LocalTime(m.Groups[1].Value, zone));
        text = SuppressedLink().Replace(text, m => m.Groups[1].Value);
        text = Link().Replace(text, m => LinkPlaceholder(m.Value));
        // The transcript delimiters must not be closable from inside a message.
        text = Delimiter().Replace(text, "‹$1transcript");
        return OneLine(text);
    }

    /// <summary>[link: host] — no path, no query (tracking parameters and tokens never reach the model).</summary>
    public static string LinkPlaceholder(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return "[link]";
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return "[link: " + host.ToLowerInvariant() + "]";
    }

    public static string Placeholder(SummaryAttachment attachment)
    {
        var type = attachment.ContentType ?? GuessType(attachment.FileName);
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return "[görsel]";
        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return "[video]";
        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return "[ses]";
        return "[dosya: " + Plain(attachment.FileName, 60, "dosya") + "]";
    }

    /// <summary>A display name on one line, without markup that could fake a new transcript line or a mention.</summary>
    public static string Name(string? name)
    {
        var plain = Plain(name, MaxNameChars, FallbackName).Replace(":", "", StringComparison.Ordinal).Replace("@", "", StringComparison.Ordinal).Trim();
        return plain.Length == 0 ? FallbackName : plain;
    }

    private static string Plain(string? value, int max, string fallback)
    {
        var text = OneLine(value ?? "");
        if (text.Length > max)
            text = text[..max].TrimEnd();
        return text.Length == 0 ? fallback : text;
    }

    /// <summary>Line breaks become " / " (a message never starts a new transcript line); control characters are removed.</summary>
    private static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingBreak = false;
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (c is '\n' or '\r' or '\u2028' or '\u2029' or '\u0085')
            {
                pendingBreak = sb.Length > 0;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue;
            if (pendingBreak)
                sb.Append(" / ");
            else if (pendingSpace)
                sb.Append(' ');
            pendingBreak = pendingSpace = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Lookup(IReadOnlyDictionary<ulong, string> names, string id, string fallback) =>
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var key) && names.TryGetValue(key, out var name)
            ? Plain(name, MaxNameChars, fallback)
            : fallback;

    private static string LocalTime(string unix, TimeZoneInfo zone)
    {
        if (!long.TryParse(unix, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds) || seconds is < -62135596800 or > 253402300799)
            return "[tarih]";
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), zone);
        return local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }

    private static string GuessType(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".heic" or ".avif" => "image/",
            ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" => "video/",
            ".mp3" or ".ogg" or ".wav" or ".m4a" or ".flac" or ".opus" => "audio/",
            _ => "",
        };
    }

    [GeneratedRegex(@"<@!?(\d{1,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex UserMention();

    [GeneratedRegex(@"<@&(\d{1,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex RoleMention();

    [GeneratedRegex(@"<#(\d{1,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelMention();

    [GeneratedRegex(@"</([\p{L}\p{N}_\- ]{1,100}):\d{1,20}>", RegexOptions.CultureInvariant)]
    private static partial Regex SlashMention();

    [GeneratedRegex(@"<a?:(\w{1,32}):\d{1,20}>", RegexOptions.CultureInvariant)]
    private static partial Regex CustomEmoji();

    [GeneratedRegex(@"<t:(-?\d{1,13})(?::[tTdDfFR])?>", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampMarkup();

    [GeneratedRegex(@"<(https?://[^\s<>]+)>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SuppressedLink();

    [GeneratedRegex(@"https?://[^\s<>()\[\]]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    [GeneratedRegex(@"<(/?)\s*transcript", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Delimiter();
}
