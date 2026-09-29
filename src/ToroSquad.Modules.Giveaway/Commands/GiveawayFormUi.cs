using Discord;
using Discord.Interactions;
using ToroSquad.Modules.Giveaway.Domain;

namespace ToroSquad.Modules.Giveaway.Commands;

/// <summary>
/// The /giveaway create form: four text fields (prize, duration, winners, optional description), localized, with Discord's
/// own length limits matching <see cref="GiveawayRules"/>. Stateless: the custom id carries nothing, the channel is the one
/// the form is submitted in, and the submit is validated again server-side (<see cref="GiveawayForm"/>).
/// </summary>
public static class GiveawayFormUi
{
    public const string ModalId = "tsq:giveaway:create";
    public const string PrizeField = "prize";
    public const string DurationField = "duration";
    public const string WinnersField = "winners";
    public const string DescriptionField = "description";

    /// <summary>Discord: modal titles and labels at most 45 characters, label descriptions at most 100.</summary>
    public const int MaxTitleLength = 45;
    public const int MaxHintLength = 100;

    public static Modal Modal(Func<string, string> L) => new ModalBuilder()
        .WithTitle(Cut(L("giveaway.form.title"), MaxTitleLength))
        .WithCustomId(ModalId)
        .AddLabel(Cut(L("giveaway.form.prize"), MaxTitleLength),
            Input(PrizeField, TextInputStyle.Short, L("giveaway.form.prize_placeholder"), 1, GiveawayRules.PrizeMaxLength, required: true, null),
            Cut(L("giveaway.form.prize_hint"), MaxHintLength))
        .AddLabel(Cut(L("giveaway.form.duration"), MaxTitleLength),
            Input(DurationField, TextInputStyle.Short, L("giveaway.form.duration_placeholder"), 2, GiveawayRules.DurationInputMaxLength, required: true, null),
            Cut(L("giveaway.form.duration_hint"), MaxHintLength))
        .AddLabel(Cut(L("giveaway.form.winners"), MaxTitleLength),
            Input(WinnersField, TextInputStyle.Short, "1", null, GiveawayRules.WinnersInputMaxLength, required: false, "1"),
            Cut(L("giveaway.form.winners_hint"), MaxHintLength))
        .AddLabel(Cut(L("giveaway.form.description"), MaxTitleLength),
            Input(DescriptionField, TextInputStyle.Paragraph, L("giveaway.form.description_placeholder"), null, GiveawayRules.DescriptionMaxLength, required: false, null),
            Cut(L("giveaway.form.description_hint"), MaxHintLength))
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
        if (value is not null)
            input.WithValue(value);
        return input;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>The form's fields as the Interaction Framework binds them (by field custom id).</summary>
public sealed class GiveawayModal : IModal
{
    public string Title => "TSQ Giveaway";

    [ModalTextInput(GiveawayFormUi.PrizeField)]
    public string? Prize { get; set; }

    [ModalTextInput(GiveawayFormUi.DurationField)]
    public string? Duration { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(GiveawayFormUi.WinnersField)]
    public string? Winners { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(GiveawayFormUi.DescriptionField, TextInputStyle.Paragraph)]
    public string? Description { get; set; }
}
