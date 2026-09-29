using System.Globalization;
using System.Text;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>The creation form exactly as typed (kept whole between the steps, so a correction never loses the input).</summary>
public sealed record PredictionFormValues(string? Title, string? Outcomes, string? LockDate, string? LockTime, string? Rules)
{
    public static PredictionFormValues Empty { get; } = new(null, null, null, null, null);
}

/// <summary>One validated outcome: its label and fixed odds (×100); <see cref="DefaultOdds"/> when the line had none.</summary>
public sealed record OutcomeInput(string Label, int OddsX100, bool DefaultOdds);

/// <summary>A validated form.</summary>
public sealed record PredictionInput(string Title, IReadOnlyList<OutcomeInput> Outcomes, DateTimeOffset? LockAt, string? Rules)
{
    public bool UsesDefaultOdds => Outcomes.Any(o => o.DefaultOdds);
}

/// <summary>One problem: a localization key and its arguments (a line number where it concerns one outcome line).</summary>
public sealed record FormError(string Key, IReadOnlyList<object> Args)
{
    public static FormError Of(string key, params object[] args) => new(key, args);
}

public sealed record PredictionFormCheck(PredictionInput? Input, IReadOnlyList<FormError> Errors)
{
    public bool Ok => Input is not null;
}

/// <summary>
/// The creation form's text → <see cref="PredictionInput"/>, reporting EVERY problem with its field and outcome line (the
/// user fixes them in one go). Outcome lines are "label | odds"; blank lines are ignored; a line without odds uses the
/// configured default (the preview says so). Refused, never repaired: more outcomes than allowed (no silent cut), an empty
/// or duplicate label (compared after trimming, collapsing spaces and Turkish lowercasing), a label containing the
/// separator, and odds that are not 1.01–1000.00 with at most two decimals.
/// </summary>
public static class PredictionForm
{
    public static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static PredictionFormCheck Parse(PredictionFormValues values, int defaultOddsX100, int maxOutcomes, TimeZoneInfo zone, DateTimeOffset now)
    {
        var errors = new List<FormError>();

        var title = Collapse(values.Title);
        var titleLength = Length(title);
        if (titleLength == 0)
            errors.Add(FormError.Of("predictions.form.error.title_required"));
        else if (titleLength < PredictionRules.TitleMinLength || titleLength > PredictionRules.TitleMaxLength)
            errors.Add(FormError.Of("predictions.form.error.title_length", PredictionRules.TitleMinLength, PredictionRules.TitleMaxLength));

        var outcomes = ParseOutcomes(values.Outcomes, defaultOddsX100, maxOutcomes, errors);

        var (lockAt, lockErrors) = PredictionLockDate.Resolve(values.LockDate, values.LockTime, zone, now);
        errors.AddRange(lockErrors.Select(e => FormError.Of("predictions.form.error.lock_" + LockErrorKey(e))));

        var rules = values.Rules?.Trim();
        if (rules is { Length: > 0 } && Length(rules) > PredictionRules.RulesMaxLength)
            errors.Add(FormError.Of("predictions.form.error.rules_length", PredictionRules.RulesMaxLength));

        return errors.Count > 0
            ? new PredictionFormCheck(null, errors)
            : new PredictionFormCheck(new PredictionInput(title, outcomes, lockAt, string.IsNullOrEmpty(rules) ? null : rules), []);
    }

    private static List<OutcomeInput> ParseOutcomes(string? text, int defaultOddsX100, int maxOutcomes, List<FormError> errors)
    {
        var outcomes = new List<OutcomeInput>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;
            count++;
            var number = i + 1;
            var parts = line.Split(PredictionRules.Separator);
            if (parts.Length > 2)
            {
                errors.Add(FormError.Of("predictions.form.error.outcome_separator", number, PredictionRules.Separator.ToString()));
                continue;
            }

            var label = Collapse(parts[0]);
            var oddsX100 = defaultOddsX100;
            var usedDefault = parts.Length == 1;
            if (parts.Length == 2)
            {
                var odds = Odds.Parse(parts[1]);
                if (!odds.Ok)
                {
                    errors.Add(FormError.Of("predictions.form.error.odds_" + OddsErrorKey(odds.Error), number, Odds.Format(Odds.MinX100), Odds.Format(Odds.MaxX100)));
                    continue;
                }

                oddsX100 = odds.X100;
            }

            if (label.Length == 0)
            {
                errors.Add(FormError.Of("predictions.form.error.outcome_empty", number));
                continue;
            }

            if (Length(label) > PredictionRules.OutcomeLabelMaxLength)
            {
                errors.Add(FormError.Of("predictions.form.error.outcome_length", number, PredictionRules.OutcomeLabelMaxLength));
                continue;
            }

            var key = Normalize(label);
            if (seen.TryGetValue(key, out var first))
            {
                errors.Add(FormError.Of("predictions.form.error.outcome_duplicate", number, first));
                continue;
            }

            seen[key] = number;
            outcomes.Add(new OutcomeInput(label, oddsX100, usedDefault));
        }

        if (count > maxOutcomes)
            errors.Add(FormError.Of("predictions.form.error.outcomes_too_many", maxOutcomes, count));
        else if (count < PredictionRules.MinOutcomes)
            errors.Add(FormError.Of("predictions.form.error.outcomes_too_few", PredictionRules.MinOutcomes));
        return outcomes;
    }

    /// <summary>The comparison key of a label: trimmed, spaces collapsed, Unicode-normalized and lowercased the Turkish way.</summary>
    public static string Normalize(string label) => Collapse(label).Normalize(NormalizationForm.FormKC).ToLower(Turkish);

    /// <summary>Trims, removes control characters and collapses runs of white space to one space.</summary>
    public static string Collapse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                space = true;
                continue;
            }

            if (char.IsControl(ch))
                continue;
            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Length in characters as people count them (runes; an emoji is one), like Discord's own limits.</summary>
    public static int Length(string text) => text.EnumerateRunes().Count();

    private static string LockErrorKey(LockDateError error) => error switch
    {
        LockDateError.NotInFuture => "past",
        LockDateError.TooFar => "too_far",
        LockDateError.NotInTimeZone => "not_in_zone",
        LockDateError.Ambiguous => "ambiguous",
        LockDateError.DateMissing => "date_missing",
        LockDateError.TimeMissing => "time_missing",
        LockDateError.TimeFormat => "time_format",
        _ => "date_format",
    };

    private static string OddsErrorKey(OddsError error) => error switch
    {
        OddsError.Empty => "empty",
        OddsError.TooManyDecimals => "decimals",
        OddsError.OutOfRange => "range",
        _ => "format",
    };
}
