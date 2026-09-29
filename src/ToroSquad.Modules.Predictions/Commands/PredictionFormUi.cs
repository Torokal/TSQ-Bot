using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Commands;

/// <summary>
/// The forms of TSQ Öngörü. Creation (🔮 Öngörü Oluştur): title, outcomes (one "label | odds" per line), optional lock
/// date and lock time (two fields, Türkiye time) and optional result rule — short end-user hints only; limits and formats
/// are named by the validation messages when something is wrong. Prefilled with the draft's values when reopened
/// (Düzenle), so a correction never loses the input. Entry
/// (🎯 Tahmin Yap): the outcome as a single-choice string select inside the modal (Discord.Net 3.20 label components, as
/// TSQ LFG's form) plus the stake — its submit IS the entry, no second confirmation; ✏️ Tahminimi Değiştir opens the same
/// form prefilled with the active entry's outcome and stake. Cancel: the reason. Custom ids carry only the draft id, the
/// prediction number and a random token; everything is validated again server-side. Discord: modal titles and labels at most 45 characters, label descriptions and placeholders at most 100.
/// </summary>
public static class PredictionFormUi
{
    public const string TitleField = "title";
    public const string OutcomesField = "outcomes";
    public const string LockDateField = "lock_date";
    public const string LockTimeField = "lock_time";
    public const string RulesField = "rules";
    public const string AmountField = "amount";
    public const string OutcomeField = "outcome";
    public const string ReasonField = "reason";

    public const int MaxTitleLength = 45;
    public const int MaxHintLength = 100;

    /// <summary>
    /// The inputs allow a little more than the rules (Discord counts UTF-16 units, the rules count characters): the server
    /// applies the real limit and names the field, instead of Discord silently refusing a paste.
    /// </summary>
    public const int InputSlack = 2;

    public static Modal CreateModal(string draftId, PredictionFormValues values, string defaultOdds, Func<string, string> L) => new ModalBuilder()
        .WithTitle(Cut(L("predictions.form.title"), MaxTitleLength))
        .WithCustomId(PredictionMessages.FormModalPrefix + draftId)
        .AddLabel(Cut(L("predictions.form.question"), MaxTitleLength),
            Input(TitleField, TextInputStyle.Short, L("predictions.form.question_placeholder"), 1, PredictionRules.TitleMaxLength * InputSlack, true, values.Title),
            Cut(L("predictions.form.question_hint"), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.outcomes"), MaxTitleLength),
            Input(OutcomesField, TextInputStyle.Paragraph, L("predictions.form.outcomes_placeholder"), 1, PredictionRules.OutcomesInputMaxLength, true, values.Outcomes),
            Cut(string.Format(CultureInfo.InvariantCulture, L("predictions.form.outcomes_hint"), defaultOdds), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.lock_date"), MaxTitleLength),
            Input(LockDateField, TextInputStyle.Short, L("predictions.form.lock_date_placeholder"), null, PredictionRules.LockDateInputMaxLength, false, values.LockDate),
            Cut(L("predictions.form.lock_date_hint"), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.lock_time"), MaxTitleLength),
            Input(LockTimeField, TextInputStyle.Short, L("predictions.form.lock_time_placeholder"), null, PredictionRules.LockTimeInputMaxLength, false, values.LockTime),
            Cut(L("predictions.form.lock_time_hint"), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.rules"), MaxTitleLength),
            Input(RulesField, TextInputStyle.Paragraph, L("predictions.form.rules_placeholder"), null, PredictionRules.RulesMaxLength * InputSlack, false, values.Rules),
            Cut(L("predictions.form.rules_hint"), MaxHintLength))
        .Build();

    /// <summary>The custom id of the entry form: the prediction number, as a new entry or as a change of the active one.</summary>
    public static string StakeModalId(EntryFormInfo info) =>
        (info.IsChange ? PredictionMessages.ChangeModalPrefix : PredictionMessages.StakeModalPrefix) + info.PredictionId.ToString(CultureInfo.InvariantCulture);

    /// <param name="balance">The spendable coins, formatted.</param>
    /// <param name="current">For a change: the stake of the active entry, formatted.</param>
    public static Modal StakeModal(EntryFormInfo info, string balance, string? current, Func<string, string> L)
    {
        var outcomes = new SelectMenuBuilder()
            .WithCustomId(OutcomeField)
            .WithPlaceholder(Cut(L("predictions.stake.outcome_placeholder"), MaxHintLength))
            .WithMinValues(1)
            .WithMaxValues(1)
            .WithRequired(true);
        foreach (var outcome in info.Outcomes.Take(PredictionRules.HardMaxOutcomes))
        {
            outcomes.AddOption(Cut(Plain(outcome.Label, 400) + " — " + Odds.Format(outcome.OddsX100), 100), outcome.Id.ToString(CultureInfo.InvariantCulture),
                isDefault: outcome.Id == info.SelectedOutcomeId);
        }

        var hint = info.IsChange
            ? string.Format(CultureInfo.InvariantCulture, L("predictions.change.hint"), current, balance)
            : string.Format(CultureInfo.InvariantCulture, L("predictions.stake.hint"), balance);
        return new ModalBuilder()
            .WithTitle(Cut(L(info.IsChange ? "predictions.change.title" : "predictions.stake.title"), MaxTitleLength))
            .WithCustomId(StakeModalId(info))
            .AddLabel(Cut(L("predictions.stake.outcome"), MaxTitleLength), outcomes, Cut(Plain(info.Title, MaxHintLength), MaxHintLength))
            .AddLabel(Cut(L("predictions.stake.amount"), MaxTitleLength),
                Input(AmountField, TextInputStyle.Short, L("predictions.stake.placeholder"), 1, PredictionRules.AmountInputMaxLength, true, info.Amount),
                Cut(hint, MaxHintLength))
            .Build();
    }

    public static Modal CancelModal(long predictionId, Func<string, string> L) => new ModalBuilder()
        .WithTitle(Cut(L("predictions.cancel.form_title"), MaxTitleLength))
        .WithCustomId(PredictionMessages.CancelReasonModalPrefix + predictionId.ToString(CultureInfo.InvariantCulture))
        .AddLabel(Cut(L("predictions.cancel.reason"), MaxTitleLength),
            Input(ReasonField, TextInputStyle.Paragraph, L("predictions.cancel.reason_placeholder"), PredictionRules.CancelReasonMinLength,
                PredictionRules.CancelReasonMaxLength, true, null),
            Cut(L("predictions.cancel.reason_hint"), MaxHintLength))
        .Build();

    /// <summary>The value chosen in a modal's select with this custom id (none when nothing is selected).</summary>
    public static string? ReadValue(IEnumerable<IComponentInteractionData> components, string customId) =>
        components.FirstOrDefault(c => c.CustomId == customId)?.Values?.FirstOrDefault();

    private static TextInputBuilder Input(string id, TextInputStyle style, string placeholder, int? minLength, int maxLength, bool required, string? value)
    {
        var input = new TextInputBuilder()
            .WithCustomId(id)
            .WithStyle(style)
            .WithPlaceholder(Cut(placeholder, MaxHintLength))
            .WithMaxLength(maxLength)
            .WithRequired(required);
        if (minLength is { } min)
            input.WithMinLength(min);
        if (!string.IsNullOrEmpty(value))
            input.WithValue(value.Length <= maxLength ? value : value[..maxLength]);
        return input;
    }

    private static string Plain(string text, int max) => ToroSquad.Core.Messaging.DiscordText.UntrustedPlain(text, max);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>The creation form's fields as the Interaction Framework binds them (by field custom id).</summary>
public sealed class PredictionFormModal : IModal
{
    public string Title => "TSQ Öngörü";

    [ModalTextInput(PredictionFormUi.TitleField)]
    public string? Question { get; set; }

    [ModalTextInput(PredictionFormUi.OutcomesField, TextInputStyle.Paragraph)]
    public string? Outcomes { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(PredictionFormUi.LockDateField)]
    public string? LockDate { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(PredictionFormUi.LockTimeField)]
    public string? LockTime { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(PredictionFormUi.RulesField, TextInputStyle.Paragraph)]
    public string? Rules { get; set; }

    public PredictionFormValues ToValues() => new(Question, Outcomes, LockDate, LockTime, Rules);
}

/// <summary>The entry form's text field (the outcome select is read from the submitted components).</summary>
public sealed class PredictionStakeModal : IModal
{
    public string Title => "TSQ Öngörü";

    [ModalTextInput(PredictionFormUi.AmountField)]
    public string? Amount { get; set; }
}

public sealed class PredictionCancelModal : IModal
{
    public string Title => "TSQ Öngörü";

    [ModalTextInput(PredictionFormUi.ReasonField, TextInputStyle.Paragraph)]
    public string? Reason { get; set; }
}
