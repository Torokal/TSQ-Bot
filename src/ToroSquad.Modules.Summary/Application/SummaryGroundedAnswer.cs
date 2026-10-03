using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>Why a grounded answer was not published. Logged as a category only — never with the answer's content.</summary>
public enum SummaryGroundedFailure
{
    None = 0,

    /// <summary>The model hit the token limit (finish_reason "length"): an incomplete answer is never published.</summary>
    Truncated = 1,

    /// <summary>Not one complete JSON object.</summary>
    NotJson = 2,

    /// <summary>Wrong version, a missing field or a wrong type.</summary>
    Contract = 3,

    /// <summary>A field, list or the rendered summary is larger than allowed.</summary>
    Limit = 4,

    /// <summary>An evidence reference that was not in this request's records.</summary>
    UnknownSource = 5,

    /// <summary>A quote that is not in the record it names.</summary>
    QuoteNotFound = 6,

    /// <summary>A text supported only by context-only records.</summary>
    ContextOnly = 7,

    /// <summary>Spoiler-protected content relied on (or repeated) by a text that is shown openly.</summary>
    SpoilerInOpenText = 8,

    /// <summary>A record reference in a visible text.</summary>
    TechnicalLeak = 9,
}

/// <summary>The outcome: the Markdown to post, or the failure category; plus counts for the log.</summary>
public sealed record SummaryGroundedResult(SummaryGroundedFailure Failure, string? Markdown, int EvidenceCount, int SpoilerClaimCount)
{
    public bool Succeeded => Failure == SummaryGroundedFailure.None && Markdown is not null;

    public static SummaryGroundedResult Failed(SummaryGroundedFailure failure) => new(failure, null, 0, 0);
}

/// <summary>
/// Reads the grounded mode's JSON answer and renders the usual Markdown from it — or refuses it as a whole. What is checked
/// is STRUCTURAL: one complete JSON object of the agreed shape and sizes, every evidence reference among the records that
/// were sent, every quote really inside the record it names (only whitespace runs are collapsed — no punctuation, suffix or
/// negation is ever dropped to make a quote fit), no text resting on context-only records alone, no record reference in a
/// visible text, and spoiler protection decided here from where the quotes come from (a claim quoting a hidden span is
/// rendered as <c>**Spoiler (konu):** ||…||</c> whatever the model said; hidden content in an open text is refused). This
/// proves that sources exist and quotes are intact. It does NOT prove that the model understood those sources: a correct
/// quote can still be misread, and a paraphrased spoiler cannot be detected deterministically. A refused answer is never
/// repaired, never retried and never replaced by the legacy path.
/// </summary>
public static partial class SummaryGroundedAnswer
{
    public const int MaxPoints = 7;
    public const int MaxClaimsPerPoint = 3;
    public const int MaxPlans = 5;
    public const int MaxEvidence = 3;
    public const int MaxTextChars = 500;
    public const int MaxTopicChars = 80;
    public const int MinQuoteChars = 3;
    public const int MaxQuoteChars = 300;
    public const int MaxRenderedChars = 3900;
    public const string UnknownSpoilerTopic = "konu belirtilmemiş";

    private sealed record Claim(string Text, bool Protected, string Label);

    private sealed class Refused(SummaryGroundedFailure failure) : Exception
    {
        public SummaryGroundedFailure Failure { get; } = failure;
    }

    public static SummaryGroundedResult Read(string? raw, string? finishReason, SummaryGroundedInput input)
    {
        if (finishReason == "length")
            return SummaryGroundedResult.Failed(SummaryGroundedFailure.Truncated);
        var body = Unwrap(raw);
        if (body is null)
            return SummaryGroundedResult.Failed(SummaryGroundedFailure.NotJson);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.NotJson);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != SummaryGroundedPrompt.ContractVersion)
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.Contract);

            var reader = new Reader(input);
            var main = reader.OpenText(Required(root, "main", JsonValueKind.Object));
            var points = new List<(string Topic, List<Claim> Claims)>();
            foreach (var point in Items(Required(root, "points", JsonValueKind.Array), 1, MaxPoints))
            {
                if (point.ValueKind != JsonValueKind.Object)
                    throw new Refused(SummaryGroundedFailure.Contract);
                var topic = Topic(Text(point, "topic", MaxTopicChars));
                // A spoiler topic given for the whole point only LABELS claims that are protected anyway; it hides nothing itself.
                var pointLabel = Label(point);
                var claims = Items(Required(point, "claims", JsonValueKind.Array), 1, MaxClaimsPerPoint).Select(c => reader.Claim(c, pointLabel)).ToList();
                points.Add((topic, claims));
            }

            var plans = root.TryGetProperty("plans", out var planList) && planList.ValueKind != JsonValueKind.Null
                ? Items(planList.ValueKind == JsonValueKind.Array ? planList : throw new Refused(SummaryGroundedFailure.Contract), 0, MaxPlans)
                    .Select(c => reader.Claim(c, "")).ToList()
                : [];
            var atmosphere = reader.OpenText(Required(root, "atmosphere", JsonValueKind.Object));

            // Nothing shown openly may repeat hidden content or carry a technical reference.
            var open = new[] { main, atmosphere }.Concat(points.Select(p => p.Topic))
                .Concat(points.SelectMany(p => p.Claims).Concat(plans).SelectMany(c => c.Protected ? [c.Label] : new[] { c.Text })).ToList();
            if (open.Any(reader.RepeatsHiddenContent))
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.SpoilerInOpenText);
            var visible = open.Concat(points.SelectMany(p => p.Claims).Concat(plans).Where(c => c.Protected).Select(c => c.Text));
            if (visible.Any(reader.MentionsRecordReference))
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.TechnicalLeak);

            var markdown = SummaryOutput.Normalize(Render(main, points, plans, atmosphere));
            if (markdown is null || markdown.Length > MaxRenderedChars)
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.Limit);
            return new SummaryGroundedResult(SummaryGroundedFailure.None, markdown, reader.EvidenceCount,
                points.SelectMany(p => p.Claims).Concat(plans).Count(c => c.Protected));
        }
        catch (JsonException)
        {
            return SummaryGroundedResult.Failed(SummaryGroundedFailure.NotJson); // the exception text may quote the answer: never logged
        }
        catch (Refused refused)
        {
            return SummaryGroundedResult.Failed(refused.Failure);
        }
    }

    /// <summary>
    /// The answer as one JSON object: surrounding whitespace and one enclosing code fence are removed, nothing else — text
    /// before or after the object is a refusal, not something to cut away.
    /// </summary>
    private static string? Unwrap(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.Length > 6)
        {
            var firstLineEnd = text.IndexOf('\n');
            if (firstLineEnd < 0)
                return null;
            text = text[(firstLineEnd + 1)..^3].Trim();
        }

        return text.StartsWith('{') && text.EndsWith('}') ? text : null;
    }

    private static string Render(string main, List<(string Topic, List<Claim> Claims)> points, List<Claim> plans, string atmosphere)
    {
        var sb = new StringBuilder();
        sb.Append(SummaryPrompt.Title).Append("\n\n");
        sb.Append(SummaryPrompt.MainTopicHeading).Append('\n').Append(main).Append("\n\n");
        sb.Append(SummaryPrompt.KeyPointsHeading).Append('\n');
        foreach (var (topic, claims) in points)
            sb.Append("- **").Append(topic).Append(":** ").AppendJoin(' ', claims.Select(Shown)).Append('\n');
        if (plans.Count > 0)
        {
            sb.Append('\n').Append(SummaryPrompt.PlansHeading).Append('\n');
            foreach (var plan in plans)
                sb.Append("- ").Append(Shown(plan)).Append('\n');
        }

        sb.Append('\n').Append(SummaryPrompt.AtmosphereHeading).Append('\n').Append(atmosphere);
        return sb.ToString();
    }

    private static string Shown(Claim claim) => claim.Protected ? "**Spoiler (" + claim.Label + "):** ||" + claim.Text + "||" : claim.Text;

    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : throw new Refused(SummaryGroundedFailure.Contract);

    private static List<JsonElement> Items(JsonElement array, int min, int max)
    {
        var count = array.GetArrayLength();
        if (count < min)
            throw new Refused(SummaryGroundedFailure.Contract);
        return count > max ? throw new Refused(SummaryGroundedFailure.Limit) : array.EnumerateArray().ToList();
    }

    /// <summary>A required visible string: one line, without spoiler marks or heading markup, within <paramref name="max"/>.</summary>
    private static string Text(JsonElement parent, string name, int max)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new Refused(SummaryGroundedFailure.Contract);
        var raw = value.GetString()!;
        if (raw.Length > max * 2)
            throw new Refused(SummaryGroundedFailure.Limit);
        var text = Clean(raw);
        if (text.Length == 0)
            throw new Refused(SummaryGroundedFailure.Contract);
        return text.Length > max ? throw new Refused(SummaryGroundedFailure.Limit) : text;
    }

    private static string Clean(string text)
    {
        text = SpoilerTag().Replace(text, " ").Replace("||", " ", StringComparison.Ordinal);
        return SummaryGrounded.CollapseWhitespace(text).TrimStart('#', '-', ' ');
    }

    /// <summary>The element's optional <c>spoiler_topic</c> as a label: one line, no brackets or markup; "" when absent.</summary>
    private static string Label(JsonElement element)
    {
        if (!element.TryGetProperty("spoiler_topic", out var topic) || topic.ValueKind == JsonValueKind.Null)
            return "";
        if (topic.ValueKind != JsonValueKind.String)
            throw new Refused(SummaryGroundedFailure.Contract);
        var given = topic.GetString()!;
        if (given.Length > MaxTopicChars * 2)
            throw new Refused(SummaryGroundedFailure.Limit);
        var label = Clean(given).Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal).Trim();
        return label.Length > MaxTopicChars ? throw new Refused(SummaryGroundedFailure.Limit) : label;
    }

    private static string Topic(string topic) => topic.Replace("*", "", StringComparison.Ordinal).Trim().TrimEnd(':').Trim() is { Length: > 0 } clean
        ? clean
        : throw new Refused(SummaryGroundedFailure.Contract);

    private sealed class Reader(SummaryGroundedInput input)
    {
        private readonly List<string> _hidden = input.Records.Values
            .SelectMany(r => r.SpoilerRanges.Select(s => r.Plain[s.Start..s.End].Trim()))
            .Where(s => s.Length >= 8).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public int EvidenceCount { get; private set; }

        /// <summary>main / atmosphere: shown openly, so their evidence must not come from a hidden span.</summary>
        public string OpenText(JsonElement element)
        {
            var text = Text(element, "text", MaxTextChars);
            return Evidence(element) ? throw new Refused(SummaryGroundedFailure.SpoilerInOpenText) : text;
        }

        /// <summary>
        /// A claim or plan: protected when a quote comes from a hidden span, or when the model names a spoiler topic for it.
        /// <paramref name="pointLabel"/> (a topic the model gave for the whole point) is only a label for a protected claim.
        /// </summary>
        public Claim Claim(JsonElement element, string pointLabel)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new Refused(SummaryGroundedFailure.Contract);
            var text = Text(element, "text", MaxTextChars);
            var fromHidden = Evidence(element);
            var label = Label(element);
            var isProtected = fromHidden || label.Length > 0;
            if (isProtected && label.Length == 0)
                label = pointLabel.Length > 0 ? pointLabel : UnknownSpoilerTopic;
            return new Claim(text, isProtected, label);
        }

        /// <summary>Checks the element's evidence list; true when any quote lies (partly) inside a spoiler span.</summary>
        private bool Evidence(JsonElement element)
        {
            var fromHidden = false;
            var onlyContext = true;
            foreach (var item in Items(Required(element, "evidence", JsonValueKind.Array), 1, MaxEvidence))
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("message", out var reference) || reference.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("quote", out var quoted) || quoted.ValueKind != JsonValueKind.String)
                    throw new Refused(SummaryGroundedFailure.Contract);
                if (!input.Records.TryGetValue(reference.GetString()!.Trim(), out var record))
                    throw new Refused(SummaryGroundedFailure.UnknownSource);

                var raw = quoted.GetString()!;
                if (raw.Length > MaxQuoteChars * 2)
                    throw new Refused(SummaryGroundedFailure.Limit);
                // The only tolerance: spoiler tags (not part of the member's text) and whitespace runs.
                var quote = SummaryGrounded.CollapseWhitespace(raw.Replace(SummaryTranscript.SpoilerOpen, " ", StringComparison.Ordinal)
                    .Replace(SummaryTranscript.SpoilerClose, " ", StringComparison.Ordinal));
                if (quote.Length > MaxQuoteChars)
                    throw new Refused(SummaryGroundedFailure.Limit);
                if (quote.Length < MinQuoteChars)
                    throw new Refused(SummaryGroundedFailure.QuoteNotFound);

                var found = false;
                for (var at = record.Plain.IndexOf(quote, StringComparison.Ordinal); at >= 0; at = record.Plain.IndexOf(quote, at + 1, StringComparison.Ordinal))
                {
                    found = true;
                    fromHidden |= record.SpoilerRanges.Any(s => at < s.End && at + quote.Length > s.Start);
                }

                if (!found)
                    throw new Refused(SummaryGroundedFailure.QuoteNotFound);
                onlyContext &= record.ContextOnly;
                EvidenceCount++;
            }

            return onlyContext ? throw new Refused(SummaryGroundedFailure.ContextOnly) : fromHidden;
        }

        public bool RepeatsHiddenContent(string text) => _hidden.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase));

        public bool MentionsRecordReference(string text)
        {
            foreach (Match match in RecordReference().Matches(text))
            {
                if (input.Records.ContainsKey(match.Value))
                    return true;
            }

            return false;
        }
    }

    [GeneratedRegex(@"<\s*/?\s*spoiler\s*>", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SpoilerTag();

    [GeneratedRegex(@"\bm\d{3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex RecordReference();
}
