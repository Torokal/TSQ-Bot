using System.Text.Json;

namespace ToroSquad.Modules.Sor.Domain;

/// <summary>Why a model answer cannot be shown.</summary>
public enum SorAnswerProblem
{
    None = 0,

    /// <summary>Nothing to show before the memory separator.</summary>
    Empty = 1,

    /// <summary>The provider cut the text (output limit) before the answer was complete.</summary>
    Truncated = 2,
}

/// <summary>
/// The parsed answer: the text for the member, the memory proposals (at most <see cref="SorAnswerFormat.MaxCandidates"/>), and
/// whether proposals were present but unusable (dropped — the answer is still valid).
/// </summary>
public sealed record SorParsedAnswer(SorAnswerProblem Problem, string Answer, IReadOnlyList<SorMemoryCandidate> Candidates, bool CandidatesDropped)
{
    public bool IsValid => Problem == SorAnswerProblem.None;
}

/// <summary>
/// The small wire contract of /sor's ONE request: the answer as plain text, then — only when the member said something
/// lasting about themselves — <see cref="Separator"/> and one JSON object <c>{"memory":[…]}</c>. Chosen over a JSON envelope
/// around the whole answer (no provider JSON/schema mode is verified for this gateway): a broken or cut proposal can never
/// cost a complete answer, and neither the separator nor the memory JSON is ever shown. No repair request is ever made.
/// <para>Grammar:</para>
/// <list type="bullet">
/// <item>The PROTOCOL separator is the first occurrence outside a ``` code block that is followed only by whitespace, a
/// <c>{</c> or a ``` fence. It ends the answer; everything after it is the memory block and is never shown.</item>
/// <item>Any other occurrence (in prose, a quote or a code example) is answer text and is shown defused
/// (<see cref="Defused"/>), so the protocol marker never reaches Discord.</item>
/// <item>The block is at most <see cref="MaxBlockLength"/> characters and exactly one JSON object (optionally fenced) with a
/// <c>memory</c> array; unknown fields are ignored, only the first <see cref="MaxCandidates"/> valid items are kept, and
/// anything else — a second separator, trailing text, invalid or cut JSON — yields no proposals.</item>
/// <item>Cut output (<c>finish_reason=length</c>): with a protocol separator the answer before it is complete and the
/// block is dropped; without one the answer itself may be cut and the result is <see cref="SorAnswerProblem.Truncated"/>.</item>
/// <item>No text before the protocol separator: <see cref="SorAnswerProblem.Empty"/>.</item>
/// </list>
/// </summary>
public static class SorAnswerFormat
{
    public const string Separator = "<<<HAFIZA>>>";
    public const string Defused = "‹‹‹HAFIZA›››";
    public const int MaxCandidates = 3;
    public const int MaxBlockLength = 4000;

    public static SorParsedAnswer Parse(string text, bool truncated)
    {
        var index = ProtocolSeparator(text);
        if (index < 0)
        {
            var whole = Defuse(StripPartialSeparator(text)).Trim();
            if (whole.Length == 0)
                return new SorParsedAnswer(SorAnswerProblem.Empty, "", [], false);
            return new SorParsedAnswer(truncated ? SorAnswerProblem.Truncated : SorAnswerProblem.None, whole, [], false);
        }

        var answer = Defuse(text[..index]).Trim();
        if (answer.Length == 0)
            return new SorParsedAnswer(SorAnswerProblem.Empty, "", [], true);
        var (candidates, dropped) = ParseCandidates(text[(index + Separator.Length)..], truncated);
        return new SorParsedAnswer(SorAnswerProblem.None, answer, candidates, dropped);
    }

    /// <summary>The first separator outside a code block that is followed only by whitespace, a JSON object or a fence; -1 if none.</summary>
    private static int ProtocolSeparator(string text)
    {
        for (var i = text.IndexOf(Separator, StringComparison.Ordinal); i >= 0; i = text.IndexOf(Separator, i + Separator.Length, StringComparison.Ordinal))
        {
            if (CountFences(text.AsSpan(0, i)) % 2 == 1)
                continue; // inside a code example
            var rest = text.AsSpan(i + Separator.Length).TrimStart();
            if (rest.IsEmpty || rest[0] == '{' || rest.StartsWith("```", StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static int CountFences(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (var i = text.IndexOf("```", StringComparison.Ordinal); i >= 0;)
        {
            count++;
            var next = text[(i + 3)..].IndexOf("```", StringComparison.Ordinal);
            i = next < 0 ? -1 : i + 3 + next;
        }

        return count;
    }

    private static string Defuse(string text) => text.Replace(Separator, Defused, StringComparison.Ordinal);

    /// <summary>A text cut right inside the separator ends with "&lt;&lt;&lt;HAF"…: that tail is not part of the answer.</summary>
    private static string StripPartialSeparator(string text)
    {
        for (var length = Math.Min(Separator.Length - 1, text.Length); length >= 3; length--)
        {
            if (text.EndsWith(Separator[..length], StringComparison.Ordinal))
                return text[..^length];
        }

        return text;
    }

    private static (IReadOnlyList<SorMemoryCandidate> Candidates, bool Dropped) ParseCandidates(string tail, bool truncated)
    {
        if (truncated || tail.Length > MaxBlockLength || tail.Contains(Separator, StringComparison.Ordinal))
            return ([], true);
        var json = tail.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = json.IndexOf('\n');
            json = firstLine < 0 ? "" : json[(firstLine + 1)..];
            var fence = json.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
                json = json[..fence];
            json = json.Trim();
        }

        if (json.Length == 0)
            return ([], false);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("memory", out var memory) || memory.ValueKind != JsonValueKind.Array)
                return ([], true);
            var list = new List<SorMemoryCandidate>();
            var dropped = false;
            foreach (var item in memory.EnumerateArray())
            {
                if (list.Count == MaxCandidates)
                {
                    dropped = true;
                    break;
                }

                if (Candidate(item) is { } candidate)
                    list.Add(candidate);
                else
                    dropped = true;
            }

            return (list, dropped);
        }
        catch (JsonException)
        {
            return ([], true);
        }
    }

    private static SorMemoryCandidate? Candidate(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;
        var op = String(item, "op") switch
        {
            "add" => SorMemoryOp.Add,
            "update" => SorMemoryOp.Update,
            "forget" => SorMemoryOp.Forget,
            _ => (SorMemoryOp?)null,
        };
        if (op is null || !SorMemoryKinds.TryParse(String(item, "kind"), out var kind) && op != SorMemoryOp.Forget)
            return null;
        var text = String(item, "text") ?? "";
        var evidence = String(item, "evidence");
        if (string.IsNullOrWhiteSpace(evidence))
            return null;
        var target = String(item, "target");
        if (op == SorMemoryOp.Forget && string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(text))
            return null;
        return new SorMemoryCandidate(op.Value, kind, text, evidence, string.IsNullOrWhiteSpace(target) ? null : target);
    }

    private static string? String(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
