using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Commands;

/// <summary>
/// The two forms of TSQ Öngörü. Creation: title, outcomes (one "label | odds" per line), optional lock time and optional
/// rules — prefilled with the draft's values when reopened (Düzenle), so a correction never loses the input. Entry: the
/// stake. Custom ids carry only the draft id or the prediction and outcome ids; everything is validated again server-side.
/// Discord: modal titles and labels at most 45 characters, label descriptions and placeholders at most 100.
/// </summary>
public static class PredictionFormUi
{
    public const string TitleField = "title";
    public const string OutcomesField = "outcomes";
    public const string LockField = "lock";
    public const string RulesField = "rules";
    public const string AmountField = "amount";

    public const int MaxTitleLength = 45;
    public const int MaxHintLength = 100;

    /// <summary>
    /// The inputs allow a little more than the rules (Discord counts UTF-16 units, the rules count characters): the server
    /// applies the real limit and names the field, instead of Discord silently refusing a paste.
    /// </summary>
    public const int InputSlack = 2;

    public static Modal CreateModal(string draftId, PredictionFormValues values, Func<string, string> L) => new ModalBuilder()
        .WithTitle(Cut(L("predictions.form.title"), MaxTitleLength))
        .WithCustomId(PredictionMessages.FormModalPrefix + draftId)
        .AddLabel(Cut(L("predictions.form.question"), MaxTitleLength),
            Input(TitleField, TextInputStyle.Short, L("predictions.form.question_placeholder"), 1, PredictionRules.TitleMaxLength * InputSlack, true, values.Title),
            Cut(L("predictions.form.question_hint"), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.outcomes"), MaxTitleLength),
            Input(OutcomesField, TextInputStyle.Paragraph, L("predictions.form.outcomes_placeholder"), 1, PredictionRules.OutcomesInputMaxLength, true, values.Outcomes),
            Cut(L("predictions.form.outcomes_hint"), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.lock"), MaxTitleLength),
            Input(LockField, TextInputStyle.Short, L("predictions.form.lock_placeholder"), null, PredictionRules.LockInputMaxLength, false, values.LockAt),
            Cut(L("predictions.form.lock_hint"), MaxHintLength))
        .AddLabel(Cut(L("predictions.form.rules"), MaxTitleLength),
            Input(RulesField, TextInputStyle.Paragraph, L("predictions.form.rules_placeholder"), null, PredictionRules.RulesMaxLength * InputSlack, false, values.Rules),
            Cut(L("predictions.form.rules_hint"), MaxHintLength))
        .Build();

    public static Modal StakeModal(EntryFormInfo info, string balance, Func<string, string> L) => new ModalBuilder()
        .WithTitle(Cut(L("predictions.stake.title"), MaxTitleLength))
        .WithCustomId(PredictionMessages.StakeModalPrefix + info.PredictionId.ToString(CultureInfo.InvariantCulture) + ":" +
                      info.OutcomeId.ToString(CultureInfo.InvariantCulture))
        .AddLabel(Cut(L("predictions.stake.amount"), MaxTitleLength),
            Input(AmountField, TextInputStyle.Short, L("predictions.stake.placeholder"), 1, PredictionRules.AmountInputMaxLength, true, null),
            Cut(string.Format(CultureInfo.InvariantCulture, L("predictions.stake.hint"), Plain(info.OutcomeLabel, 40), Odds.Format(info.OddsX100), balance), MaxHintLength))
        .Build();

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
    [ModalTextInput(PredictionFormUi.LockField)]
    public string? LockAt { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(PredictionFormUi.RulesField, TextInputStyle.Paragraph)]
    public string? Rules { get; set; }

    public PredictionFormValues ToValues() => new(Question, Outcomes, LockAt, Rules);
}

public sealed class PredictionStakeModal : IModal
{
    public string Title => "TSQ Öngörü";

    [ModalTextInput(PredictionFormUi.AmountField)]
    public string? Amount { get; set; }
}
