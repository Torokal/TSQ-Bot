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
/// lasting about themselves — the line <see cref="Separator"/> and one JSON object <c>{"memory":[…]}</c>. Chosen over a JSON
/// envelope around the whole answer (no provider JSON/schema mode is verified for this gateway): a broken or cut proposal can
/// never cost the answer, and the raw JSON is never shown. No repair request is ever made.
/// </summary>
public static class SorAnswerFormat
{
    public const string Separator = "<<<HAFIZA>>>";
    public const int MaxCandidates = 3;

    public static SorParsedAnswer Parse(string text, bool truncated)
    {
        var index = text.IndexOf(Separator, StringComparison.Ordinal);
        var answer = (index >= 0 ? text[..index] : StripPartialSeparator(text)).Trim();
        if (answer.Length == 0)
            return new SorParsedAnswer(SorAnswerProblem.Empty, "", [], index >= 0);
        if (index < 0)
            return truncated ? new SorParsedAnswer(SorAnswerProblem.Truncated, answer, [], false) : new SorParsedAnswer(SorAnswerProblem.None, answer, [], false);

        var (candidates, dropped) = ParseCandidates(text[(index + Separator.Length)..], truncated);
        return new SorParsedAnswer(SorAnswerProblem.None, answer, candidates, dropped);
    }

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
        if (truncated)
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
