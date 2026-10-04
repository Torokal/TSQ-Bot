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

    /// <summary>Wrong version, a missing field, a wrong type or a field of an earlier contract.</summary>
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

    /// <summary>A window record with a hidden part is not quoted (from that hidden part) by any item of <c>spoilers</c>.</summary>
    MissingRequiredSpoiler = 10,
}

/// <summary>
/// Which safety bound refused an answer with <see cref="SummaryGroundedFailure.Limit"/>: the first one the reader met, in
/// its fixed reading order. Diagnostic only — the bounds and what is refused are the same; <see cref="None"/> for every
/// other outcome. Logged as a name, never with the answer's content.
/// </summary>
public enum SummaryGroundedLimitReason
{
    None = 0,

    /// <summary>The whole answer is longer than <see cref="SummaryGroundedAnswer.MaxAnswerChars"/>.</summary>
    AnswerChars = 1,

    /// <summary>More items in <c>points</c> than <see cref="SummaryGroundedAnswer.MaxCandidatePoints"/>.</summary>
    CandidatePoints = 2,

    /// <summary>More items in <c>spoilers</c> than <see cref="SummaryGroundedAnswer.MaxSpoilers"/>.</summary>
    CandidateSpoilers = 3,

    /// <summary>More items in <c>plans</c> than <see cref="SummaryGroundedAnswer.MaxCandidatePlans"/>.</summary>
    CandidatePlans = 4,

    /// <summary>More quotes on one text than <see cref="SummaryGroundedAnswer.MaxEvidence"/>.</summary>
    EvidencePerText = 5,

    /// <summary>A text <c>t</c> longer than <see cref="SummaryGroundedAnswer.MaxTextChars"/>.</summary>
    TextChars = 6,

    /// <summary>A <c>topic</c> longer than <see cref="SummaryGroundedAnswer.MaxTopicChars"/>.</summary>
    TopicChars = 7,

    /// <summary>A quote longer than <see cref="SummaryGroundedAnswer.MaxQuoteChars"/>.</summary>
    QuoteChars = 8,

    /// <summary>The rendered summary is longer than <see cref="SummaryGroundedAnswer.MaxRenderedChars"/>.</summary>
    RenderedChars = 9,
}

/// <summary>
/// The outcome: the Markdown to post, or the failure category; plus numbers for the log. <paramref name="EvidenceCount"/>
/// covers every candidate that was checked; <paramref name="SpoilerClaimCount"/> the items published in the hidden section;
/// the candidate counts are what the model wrote, the shown counts what was rendered;
/// <paramref name="RequiredSpoilers"/> the window records that had to be covered; <paramref name="AnswerChars"/> is the
/// length of the answer as it was received, <paramref name="RenderedChars"/> of the Markdown made from it. A number the
/// reader did not reach before it refused the answer is <c>null</c> (logged as unknown) — never a 0 that would read as "the
/// model wrote none". The published counts (<paramref name="SpoilerClaimCount"/>, the shown counts) are really 0 for a
/// refused answer.
/// <paramref name="LimitReason"/> names the bound behind a <see cref="SummaryGroundedFailure.Limit"/> and
/// <paramref name="LimitValue"/> the size that broke it (a count or a length, never content).
/// </summary>
public sealed record SummaryGroundedResult(
    SummaryGroundedFailure Failure,
    string? Markdown,
    int? EvidenceCount,
    int SpoilerClaimCount,
    int? CandidatePoints = null,
    int? CandidatePlans = null,
    int ShownPoints = 0,
    int ShownPlans = 0,
    int RequiredSpoilers = 0,
    int? CandidateSpoilers = null,
    int? AnswerChars = null,
    int? RenderedChars = null,
    SummaryGroundedLimitReason LimitReason = SummaryGroundedLimitReason.None,
    int? LimitValue = null)
{
    public bool Succeeded => Failure == SummaryGroundedFailure.None && Markdown is not null;
}

/// <summary>
/// Reads the grounded mode's JSON answer (contract v4) and renders the usual Markdown from it — or refuses it as a whole.
/// The contract keeps open and hidden information in different places: <c>main</c>, <c>points</c>, <c>plans</c> and
/// <c>atmosphere</c> are shown openly, <c>spoilers</c> is a list of its own whose topics are shown and whose texts are
/// rendered as <c>||…||</c> under a separate heading. SOURCE SAFETY is strict and covers every item the model wrote, shown
/// or not: one complete JSON object of the agreed shape, every evidence reference among the records that were sent, every
/// quote really inside the record it names (only whitespace runs are collapsed — no punctuation, suffix or negation is
/// ever dropped to make a quote fit), no text resting on context-only records alone, no record reference in a visible
/// text. OPEN / HIDDEN: an open text may not quote a hidden span or repeat hidden content; hidden evidence is valid only
/// inside <c>spoilers</c>. SPOILER COVERAGE is required, not left to the model's choice of topics: every window record with
/// a hidden part (<see cref="SummaryGroundedInput.RequiredSpoilerSources"/>) must be quoted, from that hidden part, by an
/// item of <c>spoilers</c>. One failure refuses the whole answer; nothing is dropped to hide a broken source. The DISPLAY
/// TARGET is not a safety rule: the first <see cref="ShownPoints"/> points and <see cref="ShownPlans"/> plans are shown, in
/// the model's order, as complete items; every spoiler item is shown — points and spoilers do not compete for places.
/// Nothing is rewritten, shortened or merged; only records that are exactly the same (text and evidence) are shown once.
/// This proves that sources exist, quotes are intact and hidden quotes stay in the hidden section. It does NOT prove that
/// the model understood those sources, that a spoiler text really summarises the hidden event (a sentence that only says
/// "a spoiler was shared" passes with the right quote), or that a paraphrased spoiler in an open text or a topic was
/// caught. A refused answer is never repaired, never retried and never replaced by the legacy path.
/// </summary>
public static partial class SummaryGroundedAnswer
{
    /// <summary>Display target: how many points and plans are shown. More than this is trimmed by selection, not refused.</summary>
    public const int ShownPoints = 6;
    public const int ShownPlans = 2;

    /// <summary>Safety bound: how many items an answer may carry at all. More than this is refused.</summary>
    public const int MaxCandidatePoints = 8;
    public const int MaxCandidatePlans = 4;
    public const int MaxSpoilers = 4;

    /// <summary>
    /// Safety ceiling for the quotes of one text — NOT the target: the prompt asks for one, two or three when a correction,
    /// a reply or a disagreement needs them, up to five when one spoiler item covers several sources. Every quote is checked.
    /// </summary>
    public const int MaxEvidence = 5;
    public const int MaxAnswerChars = 16000;
    public const int MaxTextChars = 500;
    public const int MaxTopicChars = 80;
    public const int MinQuoteChars = 3;
    public const int MaxQuoteChars = 300;
    public const int MaxRenderedChars = 3900;

    /// <summary>The heading of the hidden section; written only when there is a spoiler item.</summary>
    public const string SpoilersHeading = "## Spoilerlar";

    /// <summary>
    /// One checked item. <paramref name="Sources"/> identifies its evidence, order-independent; <paramref name="HiddenSources"/>
    /// are the window records it quotes from their hidden part (what counts for the required coverage);
    /// <paramref name="FromHidden"/> tells whether any quote touches a hidden span at all (context records included).
    /// </summary>
    private sealed record Item(string Topic, string Text, string Sources, bool FromHidden, IReadOnlyList<string> HiddenSources)
    {
        public bool SameRecordAs(Item other) =>
            string.Equals(Text, other.Text, StringComparison.Ordinal) && string.Equals(Sources, other.Sources, StringComparison.Ordinal);
    }

    private sealed class Refused(SummaryGroundedFailure failure, SummaryGroundedLimitReason limitReason = SummaryGroundedLimitReason.None, int? limitValue = null) : Exception
    {
        public SummaryGroundedFailure Failure { get; } = failure;

        public SummaryGroundedLimitReason LimitReason { get; } = limitReason;

        public int? LimitValue { get; } = limitValue;
    }

    /// <summary>A safety bound was exceeded: which one, and the size (a count or a length) that broke it.</summary>
    private static Refused TooLarge(SummaryGroundedLimitReason reason, int value) => new(SummaryGroundedFailure.Limit, reason, value);

    public static SummaryGroundedResult Read(string? raw, string? finishReason, SummaryGroundedInput input)
    {
        // What a refusal can still say in numbers. Each stays null until the reader has really measured it: an answer refused
        // early has unknown counts, not zero ones.
        int? answerChars = raw?.Length, candidatePoints = null, candidateSpoilers = null, candidatePlans = null, evidence = null, renderedChars = null;
        SummaryGroundedResult Refuse(SummaryGroundedFailure failure, SummaryGroundedLimitReason limitReason = SummaryGroundedLimitReason.None, int? limitValue = null) =>
            new(failure, null, evidence, 0, candidatePoints, candidatePlans, CandidateSpoilers: candidateSpoilers, AnswerChars: answerChars,
                RenderedChars: renderedChars, LimitReason: limitReason, LimitValue: limitValue);

        if (finishReason == "length")
            return Refuse(SummaryGroundedFailure.Truncated);
        var body = Unwrap(raw);
        if (body is null)
            return Refuse(SummaryGroundedFailure.NotJson);
        if (body.Length > MaxAnswerChars)
            return Refuse(SummaryGroundedFailure.Limit, SummaryGroundedLimitReason.AnswerChars, body.Length);

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Refuse(SummaryGroundedFailure.NotJson);
            // The three list sizes as plain numbers, before any item is read: a refusal further down can still say how much
            // the model wrote. Nothing is checked or decided here.
            (candidatePoints, candidateSpoilers, candidatePlans) = (Length(root, "points"), Length(root, "spoilers"), Length(root, "plans"));
            // Only the current contract is read: an answer in an earlier shape (v1 long names, v2 nested claims, v3 spoilers
            // as labelled points) is refused, never converted.
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != SummaryGroundedPrompt.ContractVersion)
                return Refuse(SummaryGroundedFailure.Contract);

            // 1) Every item inside the safety bound is checked in full — also the ones that will not be shown. Open texts
            //    may not quote a hidden span; hidden evidence is valid only inside "spoilers".
            var reader = new Reader(input);
            var main = reader.Open(Required(root, "main", JsonValueKind.Object), withTopic: false).Text;
            var points = Items(Required(root, "points", JsonValueKind.Array), 1, MaxCandidatePoints, SummaryGroundedLimitReason.CandidatePoints)
                .Select(p => reader.Open(p, withTopic: true)).ToList();
            var spoilers = Items(Required(root, "spoilers", JsonValueKind.Array), 0, MaxSpoilers, SummaryGroundedLimitReason.CandidateSpoilers)
                .Select(reader.Hidden).ToList();
            var plans = root.TryGetProperty("plans", out var planList) && planList.ValueKind != JsonValueKind.Null
                ? Items(planList.ValueKind == JsonValueKind.Array ? planList : throw new Refused(SummaryGroundedFailure.Contract), 0, MaxCandidatePlans,
                        SummaryGroundedLimitReason.CandidatePlans)
                    .Select(p => reader.Open(p, withTopic: false)).ToList()
                : [];
            var atmosphere = reader.Open(Required(root, "atmosphere", JsonValueKind.Object), withTopic: false).Text;
            // Every item has been read: the quote count is complete, and a missing "plans" list is really no plans.
            (evidence, candidatePlans) = (reader.EvidenceCount, plans.Count);

            // Nothing shown openly may repeat hidden content — in any candidate; the topic of a spoiler item is shown openly too.
            var open = new[] { main, atmosphere }.Concat(points.Select(p => p.Topic)).Concat(points.Concat(plans).Select(i => i.Text))
                .Concat(spoilers.Select(s => s.Topic)).ToList();
            if (open.Any(reader.RepeatsHiddenContent))
                return Refuse(SummaryGroundedFailure.SpoilerInOpenText);
            if (open.Concat(spoilers.Select(s => s.Text)).Any(reader.MentionsRecordReference))
                return Refuse(SummaryGroundedFailure.TechnicalLeak);

            // 2) Required spoiler coverage: every window record with a hidden part is quoted, from that part, inside "spoilers".
            var required = input.RequiredSpoilerSources;
            var covered = spoilers.SelectMany(s => s.HiddenSources).ToHashSet(StringComparer.Ordinal);
            if (required.Any(r => !covered.Contains(r)))
                return Refuse(SummaryGroundedFailure.MissingRequiredSpoiler);

            // 3) What is shown: exact copies once; the first points and plans in the model's order; every spoiler item.
            (points, plans) = WithoutExactCopies(points, plans);
            spoilers = Distinct(spoilers);
            var shownPoints = points.Take(ShownPoints).ToList();
            var shownPlans = plans.Take(ShownPlans).ToList();

            var markdown = SummaryOutput.Normalize(Render(main, shownPoints, spoilers, shownPlans, atmosphere));
            renderedChars = markdown?.Length;
            if (markdown is null || markdown.Length > MaxRenderedChars)
                return Refuse(SummaryGroundedFailure.Limit, SummaryGroundedLimitReason.RenderedChars, renderedChars);
            return new SummaryGroundedResult(SummaryGroundedFailure.None, markdown, evidence, spoilers.Count,
                candidatePoints, candidatePlans, shownPoints.Count, shownPlans.Count, required.Count, candidateSpoilers, answerChars, renderedChars);
        }
        catch (JsonException)
        {
            return Refuse(SummaryGroundedFailure.NotJson); // the exception text may quote the answer: never logged
        }
        catch (Refused refused)
        {
            return Refuse(refused.Failure, refused.LimitReason, refused.LimitValue);
        }
    }

    /// <summary>The size of a list of the answer, or null when it is not there as a list. A number only; no item is read.</summary>
    private static int? Length(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;

    /// <summary>
    /// Removes records that are exactly the same — same text, same evidence — keeping the first of each list; a point that is
    /// exactly a plan gives way to the plan. Nothing similar-but-different is ever merged: sharing a source does not make two
    /// texts the same information.
    /// </summary>
    private static (List<Item> Points, List<Item> Plans) WithoutExactCopies(List<Item> points, List<Item> plans)
    {
        points = Distinct(points);
        plans = Distinct(plans);
        var notPlans = points.Where(point => !plans.Any(point.SameRecordAs)).ToList();
        // The points section is never left empty: if every point is also a plan, the points stay and those plans go.
        return notPlans.Count > 0 ? (notPlans, plans) : (points, plans.Where(plan => !points.Any(plan.SameRecordAs)).ToList());
    }

    private static List<Item> Distinct(List<Item> items)
    {
        var kept = new List<Item>(items.Count);
        foreach (var item in items)
        {
            if (!kept.Any(item.SameRecordAs))
                kept.Add(item);
        }

        return kept;
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

    private static string Render(string main, List<Item> points, List<Item> spoilers, List<Item> plans, string atmosphere)
    {
        var sb = new StringBuilder();
        sb.Append(SummaryPrompt.Title).Append("\n\n");
        sb.Append(SummaryPrompt.MainTopicHeading).Append('\n').Append(main).Append("\n\n");
        sb.Append(SummaryPrompt.KeyPointsHeading).Append('\n');
        foreach (var point in points)
            sb.Append("- **").Append(point.Topic).Append(":** ").Append(point.Text).Append('\n');
        if (spoilers.Count > 0)
        {
            // The topic stays outside, the whole text inside the native spoiler: a whole item or nothing, never cut inside.
            sb.Append('\n').Append(SpoilersHeading).Append('\n');
            foreach (var spoiler in spoilers)
                sb.Append("- **").Append(spoiler.Topic).Append(":** ||").Append(spoiler.Text).Append("||\n");
        }

        if (plans.Count > 0)
        {
            sb.Append('\n').Append(SummaryPrompt.PlansHeading).Append('\n');
            foreach (var plan in plans)
                sb.Append("- ").Append(plan.Text).Append('\n');
        }

        sb.Append('\n').Append(SummaryPrompt.AtmosphereHeading).Append('\n').Append(atmosphere);
        return sb.ToString();
    }

    private static JsonElement Required(JsonElement parent, string name, JsonValueKind kind) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == kind ? value : throw new Refused(SummaryGroundedFailure.Contract);

    private static List<JsonElement> Items(JsonElement array, int min, int max, SummaryGroundedLimitReason bound)
    {
        var count = array.GetArrayLength();
        if (count < min)
            throw new Refused(SummaryGroundedFailure.Contract);
        return count > max ? throw TooLarge(bound, count) : array.EnumerateArray().ToList();
    }

    /// <summary>
    /// A required visible string: one line, without spoiler marks or heading markup, within <paramref name="max"/>;
    /// <paramref name="bound"/> names that bound when the string is too long.
    /// </summary>
    private static string Text(JsonElement parent, string name, int max, SummaryGroundedLimitReason bound)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new Refused(SummaryGroundedFailure.Contract);
        var raw = value.GetString()!;
        if (raw.Length > max * 2)
            throw TooLarge(bound, raw.Length);
        var text = Clean(raw);
        if (text.Length == 0)
            throw new Refused(SummaryGroundedFailure.Contract);
        return text.Length > max ? throw TooLarge(bound, text.Length) : text;
    }

    private static string Clean(string text)
    {
        text = SpoilerTag().Replace(text, " ").Replace("||", " ", StringComparison.Ordinal);
        return SummaryGrounded.CollapseWhitespace(text).TrimStart('#', '-', ' ');
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

        /// <summary>main, a point, a plan or atmosphere: shown openly, so its evidence must not come from a hidden span.</summary>
        public Item Open(JsonElement element, bool withTopic)
        {
            var item = Read(element, withTopic);
            return item.FromHidden ? throw new Refused(SummaryGroundedFailure.SpoilerInOpenText) : item;
        }

        /// <summary>An item of <c>spoilers</c>: a safe topic shown openly, a text that is hidden, evidence that may be hidden.</summary>
        public Item Hidden(JsonElement element) => Read(element, withTopic: true);

        private Item Read(JsonElement element, bool withTopic)
        {
            // The spoiler topic field of the earlier contract has no place here: such an answer is not converted silently.
            if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty("s", out _))
                throw new Refused(SummaryGroundedFailure.Contract);
            var topic = withTopic ? Topic(Text(element, "topic", MaxTopicChars, SummaryGroundedLimitReason.TopicChars)) : "";
            var text = Text(element, "t", MaxTextChars, SummaryGroundedLimitReason.TextChars);
            var (fromHidden, sources, hiddenSources) = Evidence(element);
            return new Item(topic, text, sources, fromHidden, hiddenSources);
        }

        /// <summary>
        /// Checks the element's evidence list <c>e</c> — pairs of [record reference, verbatim quote]. FromHidden: any quote lies
        /// (partly) inside a spoiler span. Sources: the checked pairs in a fixed order, to recognise an exact copy.
        /// HiddenSources: the window (not context-only) records quoted from their hidden part.
        /// </summary>
        private (bool FromHidden, string Sources, IReadOnlyList<string> HiddenSources) Evidence(JsonElement element)
        {
            var fromHidden = false;
            var onlyContext = true;
            var pairs = new List<string>(MaxEvidence);
            var hiddenSources = new List<string>();
            foreach (var item in Items(Required(element, "e", JsonValueKind.Array), 1, MaxEvidence, SummaryGroundedLimitReason.EvidencePerText))
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2 ||
                    item[0].ValueKind != JsonValueKind.String || item[1].ValueKind != JsonValueKind.String)
                    throw new Refused(SummaryGroundedFailure.Contract);
                var reference = item[0].GetString()!.Trim();
                if (!input.Records.TryGetValue(reference, out var record))
                    throw new Refused(SummaryGroundedFailure.UnknownSource);

                var raw = item[1].GetString()!;
                if (raw.Length > MaxQuoteChars * 2)
                    throw TooLarge(SummaryGroundedLimitReason.QuoteChars, raw.Length);
                // The only tolerance: spoiler tags (not part of the member's text) and whitespace runs.
                var quote = SummaryGrounded.CollapseWhitespace(raw.Replace(SummaryTranscript.SpoilerOpen, " ", StringComparison.Ordinal)
                    .Replace(SummaryTranscript.SpoilerClose, " ", StringComparison.Ordinal));
                if (quote.Length > MaxQuoteChars)
                    throw TooLarge(SummaryGroundedLimitReason.QuoteChars, quote.Length);
                if (quote.Length < MinQuoteChars)
                    throw new Refused(SummaryGroundedFailure.QuoteNotFound);

                var found = false;
                var hidden = false;
                for (var at = record.Plain.IndexOf(quote, StringComparison.Ordinal); at >= 0; at = record.Plain.IndexOf(quote, at + 1, StringComparison.Ordinal))
                {
                    found = true;
                    hidden |= record.SpoilerRanges.Any(s => at < s.End && at + quote.Length > s.Start);
                }

                if (!found)
                    throw new Refused(SummaryGroundedFailure.QuoteNotFound);
                fromHidden |= hidden;
                if (hidden && !record.ContextOnly && !hiddenSources.Contains(record.Ref))
                    hiddenSources.Add(record.Ref);
                onlyContext &= record.ContextOnly;
                pairs.Add(reference + "\t" + quote);
                EvidenceCount++;
            }

            if (onlyContext)
                throw new Refused(SummaryGroundedFailure.ContextOnly);
            pairs.Sort(StringComparer.Ordinal);
            return (fromHidden, string.Join('\n', pairs), hiddenSources);
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
