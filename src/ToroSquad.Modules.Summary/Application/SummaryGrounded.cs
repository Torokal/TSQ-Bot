using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>
/// One message as the grounded request shows it to the model. <paramref name="Ref"/> is a reference valid for this request
/// only (m001 …); Discord ids never reach the model. <paramref name="Text"/> is the normalized text (spoilers as
/// <c>&lt;spoiler&gt;</c> tags); <paramref name="Plain"/> is the same text without the tags, in which
/// <paramref name="SpoilerRanges"/> mark the hidden parts — quotes are checked against it.
/// </summary>
public sealed record SummaryGroundedRecord(
    string Ref,
    ulong MessageId,
    ulong AuthorId,
    string Author,
    string? ReplyRef,
    bool ReplyUnavailable,
    bool ContextOnly,
    bool Truncated,
    string Text,
    string Plain,
    IReadOnlyList<(int Start, int End)> SpoilerRanges)
{
    /// <summary>The message really has a hidden (Discord spoiler) part — decided from the parsed text, never from what a member typed as data.</summary>
    public bool HasSpoiler => SpoilerRanges.Any(r => r.End > r.Start);
}

/// <summary>The grounded request's data and the counts logged about it (never the text).</summary>
/// <param name="Text">One JSON record per line, oldest → newest.</param>
/// <param name="Records">The reference → record map of exactly what was sent.</param>
/// <param name="MessageCount">Member messages of the window (context records not included).</param>
/// <param name="ContextCount">Older reply targets added only to make a reply understandable.</param>
/// <param name="ReplyCount">Replies whose target is among the records.</param>
/// <param name="UnavailableReplyCount">Replies whose target could not be shown.</param>
public sealed record SummaryGroundedInput(
    string Text,
    IReadOnlyDictionary<string, SummaryGroundedRecord> Records,
    int MessageCount,
    int TruncatedMessageCount,
    int DroppedForSizeCount,
    int EmptyMessageCount,
    int ContextCount,
    int ReplyCount,
    int UnavailableReplyCount,
    DateTimeOffset? From,
    DateTimeOffset? To)
{
    /// <summary>
    /// The window records with a hidden part, by reference: every one of them must be covered by a spoiler point of the
    /// answer. Derived from the record map of exactly what is sent; context-only records are never required.
    /// </summary>
    public IReadOnlyList<string> RequiredSpoilerSources { get; } =
        Records.Values.Where(r => !r.ContextOnly && r.HasSpoiler).Select(r => r.Ref).Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Builds what the grounded mode sends instead of "Name: text" lines: one safely serialized JSON record per message with a
/// bot-made reference, the author's display name, the real Discord reply link, and flags for context-only and cut records
/// and for records with a hidden (spoiler) part.
/// Everything a member typed stays inside the JSON string <c>t</c>, so text like "[m001] Toro: …" can never become a source
/// or a speaker. Reply targets outside the window are added as context from what this request already has (the same history
/// read, or the message Discord returned with the reply) — one level, member messages only, at most
/// <see cref="MaxContextRecords"/> records and <see cref="MaxContextChars"/> characters; they never count as window messages.
/// A reply's author is never treated as the person talked about. Pure and deterministic.
/// </summary>
public static partial class SummaryGrounded
{
    public const int MaxContextRecords = 10;
    public const int MaxContextChars = 2500;
    public const int MaxContextMessageChars = 300;
    public const string ReplyUnavailableText = "bağlam mevcut değil";

    private static readonly JsonWriterOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The older messages this window's replies point to, from the data already at hand: member messages only, never a bot,
    /// webhook, system event or earlier summary, never more than <see cref="MaxContextRecords"/>.
    /// </summary>
    public static IReadOnlyList<SummarySourceMessage> ContextCandidates(
        IReadOnlyList<SummarySourceMessage> selected, IReadOnlyDictionary<ulong, SummarySourceMessage> read)
    {
        var inWindow = selected.Select(m => m.Id).ToHashSet();
        var found = new Dictionary<ulong, SummarySourceMessage>();
        foreach (var message in selected.OrderBy(m => m.Timestamp).ThenBy(m => m.Id))
        {
            if (message.ReplyToId is not { } target || inWindow.Contains(target) || found.ContainsKey(target))
                continue;
            var candidate = read.GetValueOrDefault(target) ?? (message.ReplyTarget?.Id == target ? message.ReplyTarget : null);
            if (candidate is { Kind: SummaryAuthorKind.Member } && found.Count < MaxContextRecords)
                found[target] = candidate with { ReplyToId = null, ReplyTarget = null }; // one reply level only
        }

        return found.Values.ToList();
    }

    public static SummaryGroundedInput Build(
        IReadOnlyList<SummarySourceMessage> selected,
        IReadOnlyList<SummarySourceMessage> context,
        SummaryMentionNames names,
        TimeZoneInfo zone,
        int maxMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMessages, 1);

        // The window: the newest member messages, oldest → newest, with something to show.
        var window = new List<(SummarySourceMessage Message, string Text, bool Truncated)>();
        int truncated = 0, empty = 0;
        foreach (var message in selected.Where(m => m.Kind == SummaryAuthorKind.Member)
                     .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id).Take(maxMessages).Reverse())
        {
            var (text, cut) = SummaryTranscript.Normalize(message, names, zone, SummaryTranscript.MaxMessageChars, inlineMarker: false, preferSentenceEnd: true);
            if (text.Length == 0)
            {
                empty++;
                continue;
            }

            window.Add((message, text, cut));
        }

        // Hard size limit (metadata and the context budget included): the oldest window messages go first.
        var dropped = 0;
        var budget = SummaryTranscript.MaxTranscriptChars - MaxContextChars - (MaxContextRecords * RecordOverhead);
        var total = window.Sum(w => w.Text.Length + RecordOverhead);
        while (total > budget && window.Count > 0)
        {
            total -= window[0].Text.Length + RecordOverhead;
            window.RemoveAt(0);
            dropped++;
        }

        truncated = window.Count(w => w.Truncated);

        // Context: only targets a kept window message replies to, within the record and character limits.
        var kept = window.Select(w => w.Message.Id).ToHashSet();
        var wanted = window.Where(w => w.Message.ReplyToId is { } id && !kept.Contains(id)).Select(w => w.Message.ReplyToId!.Value).ToHashSet();
        var extra = new List<(SummarySourceMessage Message, string Text, bool Truncated)>();
        var contextChars = 0;
        foreach (var candidate in context.Where(c => c.Kind == SummaryAuthorKind.Member && wanted.Contains(c.Id) && !SummaryHistory.IsSummaryMarker(c))
                     .DistinctBy(c => c.Id).OrderBy(c => c.Timestamp).ThenBy(c => c.Id))
        {
            if (extra.Count >= MaxContextRecords)
                break;
            var (text, cut) = SummaryTranscript.Normalize(candidate, names, zone, MaxContextMessageChars, inlineMarker: false, preferSentenceEnd: true);
            if (text.Length == 0 || contextChars + text.Length > MaxContextChars)
                continue;
            contextChars += text.Length;
            extra.Add((candidate, text, cut));
        }

        // References in chronological order over everything that is actually sent.
        var ordered = window.Select(w => (w.Message, w.Text, w.Truncated, ContextOnly: false))
            .Concat(extra.Select(e => (e.Message, e.Text, e.Truncated, ContextOnly: true)))
            .OrderBy(r => r.Message.Timestamp).ThenBy(r => r.Message.Id).ToList();
        var refs = new Dictionary<ulong, string>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
            refs[ordered[i].Message.Id] = string.Create(CultureInfo.InvariantCulture, $"m{i + 1:000}");
        var authors = AuthorLabels(ordered.Select(r => r.Message));

        var records = new Dictionary<string, SummaryGroundedRecord>(ordered.Count, StringComparer.Ordinal);
        var lines = new List<string>(ordered.Count);
        int replies = 0, unavailable = 0;
        foreach (var (message, text, cut, contextOnly) in ordered)
        {
            string? replyRef = null;
            var replyUnavailable = false;
            if (!contextOnly && message.ReplyToId is { } target)
            {
                if (refs.TryGetValue(target, out var known))
                {
                    replyRef = known;
                    replies++;
                }
                else
                {
                    replyUnavailable = true;
                    unavailable++;
                }
            }

            var shown = RecordsDelimiter().Replace(text, "‹$1records");
            var (plain, ranges) = PlainText(shown);
            var record = new SummaryGroundedRecord(refs[message.Id], message.Id, message.AuthorId, authors[AuthorKey(message)],
                replyRef, replyUnavailable, contextOnly, cut, shown, plain, ranges);
            records[record.Ref] = record;
            lines.Add(Serialize(record));
        }

        var first = window.Count > 0 ? window[0].Message.Timestamp : (DateTimeOffset?)null;
        var last = window.Count > 0 ? window[^1].Message.Timestamp : (DateTimeOffset?)null;
        return new SummaryGroundedInput(string.Join("\n", lines), records, window.Count, truncated, dropped, empty, extra.Count, replies, unavailable, first, last);
    }

    /// <summary>
    /// The text without spoiler tags (whitespace collapsed) and the ranges of it that were inside a spoiler. The tags are the
    /// transcript's own: a tag a member typed was defused before.
    /// </summary>
    public static (string Plain, IReadOnlyList<(int Start, int End)> SpoilerRanges) PlainText(string text)
    {
        var sb = new StringBuilder(text.Length);
        var ranges = new List<(int, int)>();
        var position = 0;
        var inSpoiler = false;
        var spoilerStart = 0;
        while (position < text.Length)
        {
            var marker = inSpoiler ? SummaryTranscript.SpoilerClose : SummaryTranscript.SpoilerOpen;
            var next = text.IndexOf(marker, position, StringComparison.Ordinal);
            Append(sb, text.AsSpan(position, (next < 0 ? text.Length : next) - position));
            if (next < 0)
                break;
            if (inSpoiler)
                ranges.Add((spoilerStart, sb.Length));
            else
                spoilerStart = sb.Length;
            inSpoiler = !inSpoiler;
            position = next + marker.Length;
        }

        if (inSpoiler)
            ranges.Add((spoilerStart, sb.Length));
        return (sb.ToString(), ranges);
    }

    /// <summary>Collapses whitespace runs to one space and trims: the only normalization a quote comparison may use.</summary>
    public static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        Append(sb, text);
        return sb.ToString().Trim();
    }

    private static void Append(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }
    }

    /// <summary>
    /// Display names per author id. Two different people with the same display name are told apart ("Ad", "Ad (2)") —
    /// never merged.
    /// </summary>
    private static Dictionary<(ulong, string), string> AuthorLabels(IEnumerable<SummarySourceMessage> messages)
    {
        var labels = new Dictionary<(ulong, string), string>();
        var perName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            var key = AuthorKey(message);
            if (labels.ContainsKey(key))
                continue;
            var name = key.Item2;
            var seen = perName[name] = perName.GetValueOrDefault(name) + 1;
            labels[key] = seen == 1 ? name : string.Create(CultureInfo.InvariantCulture, $"{name} ({seen})");
        }

        return labels;
    }

    private static (ulong, string) AuthorKey(SummarySourceMessage message) => (message.AuthorId, SummaryTranscript.Name(message.AuthorName));

    private static string Serialize(SummaryGroundedRecord record)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Json))
        {
            json.WriteStartObject();
            json.WriteString("m", record.Ref);
            json.WriteString("u", record.Author);
            if (record.ReplyRef is not null)
                json.WriteString("re", record.ReplyRef);
            else if (record.ReplyUnavailable)
                json.WriteString("re", ReplyUnavailableText);
            if (record.ContextOnly)
                json.WriteBoolean("ctx", true);
            if (record.Truncated)
                json.WriteBoolean("cut", true);
            if (record.HasSpoiler)
                json.WriteBoolean("sp", true);
            json.WriteString("t", record.Text);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A generous estimate of one record's JSON metadata (keys, reference, name, reply).</summary>
    private const int RecordOverhead = 80;

    [GeneratedRegex(@"<(/?)\s*records", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RecordsDelimiter();
}
