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

    /// <summary>The answer, a field or a list is larger than the safety bound allows, or the rendered summary is too long.</summary>
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

/// <summary>
/// The outcome: the Markdown to post, or the failure category; plus counts for the log. <paramref name="EvidenceCount"/>
/// covers every candidate that was checked; <paramref name="SpoilerClaimCount"/> the shown points and plans published as
/// spoilers; the candidate counts are what the model wrote, the shown counts what was rendered.
/// </summary>
public sealed record SummaryGroundedResult(
    SummaryGroundedFailure Failure,
    string? Markdown,
    int EvidenceCount,
    int SpoilerClaimCount,
    int CandidatePoints = 0,
    int CandidatePlans = 0,
    int ShownPoints = 0,
    int ShownPlans = 0)
{
    public bool Succeeded => Failure == SummaryGroundedFailure.None && Markdown is not null;

    public static SummaryGroundedResult Failed(SummaryGroundedFailure failure) => new(failure, null, 0, 0);
}

/// <summary>
/// Reads the grounded mode's JSON answer and renders the usual Markdown from it — or refuses it as a whole. Two things are
/// kept apart here. SOURCE SAFETY is strict and covers every point and plan the model wrote, shown or not: one complete
/// JSON object of the agreed flat shape, every evidence reference among the records that were sent, every quote really
/// inside the record it names (only whitespace runs are collapsed — no punctuation, suffix or negation is ever dropped to
/// make a quote fit), no text resting on context-only records alone, no record reference in a visible text, and spoiler
/// protection decided here from where the quotes come from (a point quoting a hidden span is rendered as
/// <c>**Spoiler (konu):** ||…||</c> whatever the model said; hidden content in an open text is refused). One failure
/// refuses the whole answer; a point is never dropped to hide its broken source. The DISPLAY TARGET is not a safety rule:
/// when everything checked out, the first <see cref="ShownPoints"/> points and <see cref="ShownPlans"/> plans are shown, in
/// the model's order, as complete items — a few more than that is not a failure, only the candidate bound is. Selection
/// never rewrites, shortens or merges a text; only records that are exactly the same (text, evidence, spoiler nature) are
/// shown once. This proves that sources exist and quotes are intact. It does NOT prove that the model understood those
/// sources, that each point is as self-contained as the prompt asks, or that a paraphrased spoiler was caught. A refused
/// answer is never repaired, never retried and never replaced by the legacy path.
/// </summary>
public static partial class SummaryGroundedAnswer
{
    /// <summary>Display target: how many points and plans are shown. More than this is trimmed by selection, not refused.</summary>
    public const int ShownPoints = 6;
    public const int ShownPlans = 2;

    /// <summary>Safety bound: how many candidates an answer may carry at all. More than this is refused.</summary>
    public const int MaxCandidatePoints = 12;
    public const int MaxCandidatePlans = 4;

    /// <summary>One quote is the norm; up to three stay possible for a correction, a reply, a disagreement or a who-about-whom relation.</summary>
    public const int MaxEvidence = 3;
    public const int MaxAnswerChars = 16000;
    public const int MaxTextChars = 500;
    public const int MaxTopicChars = 80;
    public const int MinQuoteChars = 3;
    public const int MaxQuoteChars = 300;
    public const int MaxRenderedChars = 3900;
    public const string UnknownSpoilerTopic = "konu belirtilmemiş";

    /// <summary>A point (with a topic) or a plan (without). <paramref name="Sources"/> identifies its evidence, order-independent.</summary>
    private sealed record Item(string Topic, string Text, bool Protected, string Label, string Sources)
    {
        public bool SameRecordAs(Item other) =>
            Protected == other.Protected && string.Equals(Text, other.Text, StringComparison.Ordinal) && string.Equals(Sources, other.Sources, StringComparison.Ordinal);
    }

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
        if (body.Length > MaxAnswerChars)
            return SummaryGroundedResult.Failed(SummaryGroundedFailure.Limit);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.NotJson);
            // Only the current flat contract is read: an answer in an earlier shape (v1 long names, v2 nested claims) is refused.
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != SummaryGroundedPrompt.ContractVersion)
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.Contract);

            // 1) Every candidate inside the safety bound is checked in full — also the ones that will not be shown.
            var reader = new Reader(input);
            var main = reader.OpenText(Required(root, "main", JsonValueKind.Object));
            var points = Items(Required(root, "points", JsonValueKind.Array), 1, MaxCandidatePoints).Select(p => reader.Item(p, withTopic: true)).ToList();
            var plans = root.TryGetProperty("plans", out var planList) && planList.ValueKind != JsonValueKind.Null
                ? Items(planList.ValueKind == JsonValueKind.Array ? planList : throw new Refused(SummaryGroundedFailure.Contract), 0, MaxCandidatePlans)
                    .Select(p => reader.Item(p, withTopic: false)).ToList()
                : [];
            var atmosphere = reader.OpenText(Required(root, "atmosphere", JsonValueKind.Object));

            // Nothing that would be shown openly may repeat hidden content or carry a technical reference — in any candidate.
            var all = points.Concat(plans).ToList();
            var open = new[] { main, atmosphere }.Concat(points.Select(p => p.Topic)).Concat(all.Select(i => i.Protected ? i.Label : i.Text)).ToList();
            if (open.Any(reader.RepeatsHiddenContent))
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.SpoilerInOpenText);
            if (open.Concat(all.Where(i => i.Protected).Select(i => i.Text)).Any(reader.MentionsRecordReference))
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.TechnicalLeak);

            // 2) Selection among complete, checked items: exact copies once, then the first ones in the model's order.
            var (candidatePoints, candidatePlans) = (points.Count, plans.Count);
            (points, plans) = WithoutExactCopies(points, plans);
            var shownPoints = points.Take(ShownPoints).ToList();
            var shownPlans = plans.Take(ShownPlans).ToList();

            var markdown = SummaryOutput.Normalize(Render(main, shownPoints, shownPlans, atmosphere));
            if (markdown is null || markdown.Length > MaxRenderedChars)
                return SummaryGroundedResult.Failed(SummaryGroundedFailure.Limit);
            return new SummaryGroundedResult(SummaryGroundedFailure.None, markdown, reader.EvidenceCount,
                shownPoints.Concat(shownPlans).Count(i => i.Protected), candidatePoints, candidatePlans, shownPoints.Count, shownPlans.Count);
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
    /// Removes records that are exactly the same — same text, same evidence, same spoiler nature — keeping the first of each
    /// list; a point that is exactly a plan gives way to the plan. Nothing similar-but-different is ever merged: sharing a
    /// source does not make two texts the same information.
    /// </summary>
    private static (List<Item> Points, List<Item> Plans) WithoutExactCopies(List<Item> points, List<Item> plans)
    {
        points = Distinct(points);
        plans = Distinct(plans);
        var notPlans = points.Where(point => !plans.Any(point.SameRecordAs)).ToList();
        // The points section is never left empty: if every point is also a plan, the points stay and those plans go.
        return notPlans.Count > 0 ? (notPlans, plans) : (points, plans.Where(plan => !points.Any(plan.SameRecordAs)).ToList());

        static List<Item> Distinct(List<Item> items)
        {
            var kept = new List<Item>(items.Count);
            foreach (var item in items)
            {
                if (!kept.Any(item.SameRecordAs))
                    kept.Add(item);
            }

            return kept;
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

    private static string Render(string main, List<Item> points, List<Item> plans, string atmosphere)
    {
        var sb = new StringBuilder();
        sb.Append(SummaryPrompt.Title).Append("\n\n");
        sb.Append(SummaryPrompt.MainTopicHeading).Append('\n').Append(main).Append("\n\n");
        sb.Append(SummaryPrompt.KeyPointsHeading).Append('\n');
        foreach (var point in points)
            sb.Append("- **").Append(point.Topic).Append(":** ").Append(Shown(point)).Append('\n');
        if (plans.Count > 0)
        {
            sb.Append('\n').Append(SummaryPrompt.PlansHeading).Append('\n');
            foreach (var plan in plans)
                sb.Append("- ").Append(Shown(plan)).Append('\n');
        }

        sb.Append('\n').Append(SummaryPrompt.AtmosphereHeading).Append('\n').Append(atmosphere);
        return sb.ToString();
    }

    /// <summary>A whole item or nothing: a protected one is wrapped as a whole, never cut inside.</summary>
    private static string Shown(Item item) => item.Protected ? "**Spoiler (" + item.Label + "):** ||" + item.Text + "||" : item.Text;

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

    /// <summary>
    /// The element's optional spoiler topic <c>s</c> as a label: one line, no brackets or markup; "" when absent. Its absence
    /// never means "not a spoiler": protection is decided from where the quotes come from.
    /// </summary>
    private static string Label(JsonElement element)
    {
        if (!element.TryGetProperty("s", out var topic) || topic.ValueKind == JsonValueKind.Null)
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
            var text = Text(element, "t", MaxTextChars);
            return Evidence(element).FromHidden ? throw new Refused(SummaryGroundedFailure.SpoilerInOpenText) : text;
        }

        /// <summary>
        /// One point (<c>topic</c>, <c>t</c>, <c>e</c>, optional <c>s</c>) or one plan (no topic): protected when a quote comes
        /// from a hidden span, or when the model names a spoiler topic for it.
        /// </summary>
        public Item Item(JsonElement element, bool withTopic)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw new Refused(SummaryGroundedFailure.Contract);
            var topic = withTopic ? Topic(Text(element, "topic", MaxTopicChars)) : "";
            var text = Text(element, "t", MaxTextChars);
            var (fromHidden, sources) = Evidence(element);
            var label = Label(element);
            var isProtected = fromHidden || label.Length > 0;
            if (isProtected && label.Length == 0)
                label = UnknownSpoilerTopic;
            return new Item(topic, text, isProtected, label, sources);
        }

        /// <summary>
        /// Checks the element's evidence list <c>e</c> — pairs of [record reference, verbatim quote]. FromHidden: any quote lies
        /// (partly) inside a spoiler span. Sources: the checked pairs in a fixed order, to recognise an exact copy.
        /// </summary>
        private (bool FromHidden, string Sources) Evidence(JsonElement element)
        {
            var fromHidden = false;
            var onlyContext = true;
            var pairs = new List<string>(MaxEvidence);
            foreach (var item in Items(Required(element, "e", JsonValueKind.Array), 1, MaxEvidence))
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2 ||
                    item[0].ValueKind != JsonValueKind.String || item[1].ValueKind != JsonValueKind.String)
                    throw new Refused(SummaryGroundedFailure.Contract);
                var reference = item[0].GetString()!.Trim();
                if (!input.Records.TryGetValue(reference, out var record))
                    throw new Refused(SummaryGroundedFailure.UnknownSource);

                var raw = item[1].GetString()!;
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
                pairs.Add(reference + "\t" + quote);
                EvidenceCount++;
            }

            if (onlyContext)
                throw new Refused(SummaryGroundedFailure.ContextOnly);
            pairs.Sort(StringComparer.Ordinal);
            return (fromHidden, string.Join('\n', pairs));
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
