using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Commands;

/// <summary>
/// The listing form's Discord surface, shared by create and edit. Discord allows at most five top-level components in a
/// modal; the main modal holds game (text), team size (a select of the allowed sizes only), start date (text; there is no
/// public date picker component), voice channel (channel select) and the two notice opt-ins (checkbox group). The private
/// settings message holds the duration, the optional details (their own small modal, opened by a button — a modal cannot
/// answer a modal submit) and the actions. Every custom id carries only the random draft id; what a click may do is decided
/// server-side.
/// </summary>
public static class LfgFormUi
{
    public const string DurationPrefix = "tsq:lfg:draft:duration:";
    public const string DetailsPrefix = "tsq:lfg:draft:details:";
    public const string SavePrefix = "tsq:lfg:draft:save:";
    public const string BackPrefix = "tsq:lfg:draft:back:";
    public const string CancelPrefix = "tsq:lfg:draft:cancel:";

    /// <summary>The details modal (opened from the settings message); custom id = prefix + draft id.</summary>
    public const string DetailsModalPrefix = "tsq:lfg:details:";

    public const string NotifyBefore = "before";
    public const string NotifyStart = "start";

    /// <summary>Duration option for a configured default that is not one of the choices (create only).</summary>
    public const string DefaultDuration = "default";

    /// <summary>Discord: at most 5 top-level components in a modal; titles and labels at most 45 characters.</summary>
    public const int MaxModalComponents = 5;
    public const int MaxTitleLength = 45;
    public const int MaxLabelLength = 45;
    public const int MaxDescriptionLength = 100;

    /// <summary>Discord: a string select holds at most 25 options.</summary>
    public const int MaxSelectOptions = 25;

    /// <summary>
    /// The edit form's text inputs allow twice the rule's length: Discord counts UTF-16 units, the rules count characters (an
    /// emoji is two units), so a stored name is never cut when it is prefilled. The server applies the real limit. The
    /// create form keeps the rule's length (no round trip for plain text that is too long).
    /// </summary>
    public const int EditInputLengthFactor = 2;

    public delegate string Text(string key, params object?[] args);

    public static Modal Modal(LfgFormDraft draft, int maxPlayers, Text L)
    {
        var values = draft.Values;
        var factor = draft.Kind == LfgFormKind.Edit ? EditInputLengthFactor : 1;

        var players = new SelectMenuBuilder()
            .WithCustomId(LfgForm.PlayersField)
            .WithPlaceholder(L("lfg.form.players_placeholder"))
            .WithMinValues(1)
            .WithMaxValues(1)
            .WithRequired(true);
        foreach (var size in PlayerChoices(maxPlayers, values.Players))
        {
            var value = size.ToString(CultureInfo.InvariantCulture);
            players.AddOption(value, value, isDefault: value == LfgRules.Normalize(values.Players));
        }

        var voice = new SelectMenuBuilder()
            .WithType(ComponentType.ChannelSelect)
            .WithCustomId(LfgForm.VoiceField)
            .WithChannelTypes(ChannelType.Voice)
            .WithPlaceholder(L("lfg.form.voice_placeholder"))
            .WithMinValues(0)
            .WithMaxValues(1)
            .WithRequired(false);
        if (draft.VoiceChannel is { } channel)
            voice.WithDefaultValues(new SelectMenuDefaultValue(channel.Value, SelectDefaultValueType.Channel));

        var notices = new CheckboxGroupBuilder()
            .WithCustomId(LfgForm.NoticesField)
            .WithMinValues(0)
            .WithMaxValues(2)
            .WithRequired(false)
            .AddOption(L("lfg.form.notice_before"), NotifyBefore, null, draft.NotifyBeforeStart)
            .AddOption(L("lfg.form.notice_start"), NotifyStart, null, draft.NotifyAtStart);

        return new ModalBuilder()
            .WithTitle(L(draft.Kind == LfgFormKind.Create ? "lfg.form.title_create" : "lfg.form.title_edit"))
            .WithCustomId(LfgForm.ModalPrefix + draft.Id)
            .AddLabel(L("lfg.form.game"), Input(LfgForm.GameField, TextInputStyle.Short, L("lfg.form.game_placeholder"), LfgRules.GameNameMinLength,
                LfgRules.GameNameMaxLength * factor, required: true, values.Game), L("lfg.form.game_hint"))
            .AddLabel(L("lfg.form.players"), players, L("lfg.form.players_hint", LfgRules.MinPlayers, maxPlayers))
            .AddLabel(L("lfg.form.start"), Input(LfgForm.StartField, TextInputStyle.Short, L("lfg.form.start_placeholder"), null, LfgForm.StartMaxLength,
                required: false, values.Start), L("lfg.form.start_hint"))
            .AddLabel(L("lfg.form.voice"), voice, L("lfg.form.voice_hint"))
            .AddLabel(L("lfg.form.notices"), notices, L("lfg.form.notices_hint"))
            .Build();
    }

    /// <summary>The small details modal (opened from the settings message), prefilled with the draft's details.</summary>
    public static Modal DetailsModal(LfgFormDraft draft, Text L)
    {
        var factor = draft.Kind == LfgFormKind.Edit ? EditInputLengthFactor : 1;
        return new ModalBuilder()
            .WithTitle(L("lfg.form.details_title"))
            .WithCustomId(DetailsModalPrefix + draft.Id)
            .AddLabel(L("lfg.form.details"), Input(LfgForm.DetailsField, TextInputStyle.Paragraph, L("lfg.form.details_placeholder"), null,
                LfgRules.DetailsMaxLength * factor, required: false, draft.Values.Details), L("lfg.form.details_hint"))
            .Build();
    }

    /// <summary>
    /// The team sizes offered: every allowed size (<see cref="LfgRules.MinPlayers"/> … the configured maximum; the options
    /// validation keeps that within <see cref="MaxSelectOptions"/>), plus an edited listing's own size if it lies outside.
    /// </summary>
    public static IReadOnlyList<int> PlayerChoices(int maxPlayers, string? current)
    {
        var choices = Enumerable.Range(LfgRules.MinPlayers, Math.Max(0, maxPlayers - LfgRules.MinPlayers + 1)).ToList();
        if (LfgFormText.Players(current) is var size and > 0 && !choices.Contains(size))
            choices = [.. choices.Append(size).Order()];
        return choices;
    }

    /// <summary>The value chosen in the modal's select or channel select with this custom id (none when nothing is selected).</summary>
    public static string? ReadValue(IEnumerable<IComponentInteractionData> components, string customId) =>
        components.FirstOrDefault(c => c.CustomId == customId)?.Values?.FirstOrDefault();

    /// <summary>The voice channel chosen in the modal's channel select (none when nothing is selected).</summary>
    public static ChannelId? ReadVoice(IEnumerable<IComponentInteractionData> components) =>
        ReadValue(components, LfgForm.VoiceField) is { } id && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var channel)
            ? new ChannelId(channel)
            : null;

    /// <summary>The notice opt-ins ticked in the modal's checkbox group.</summary>
    public static (bool Before, bool AtStart) ReadNotices(IEnumerable<IComponentInteractionData> components)
    {
        var values = components.FirstOrDefault(c => c.CustomId == LfgForm.NoticesField)?.Values ?? [];
        return (values.Contains(NotifyBefore), values.Contains(NotifyStart));
    }

    /// <summary>The second step: what the form will open (or change to), the listing duration, the details and the actions.</summary>
    public static (string Content, MessageComponent Components) Settings(LfgFormDraft draft, DateTimeOffset now, int defaultMinutes, Text L)
    {
        var preview = draft.Preview ?? throw new ArgumentException("A checked form is required.", nameof(draft));
        var lines = new List<string>
        {
            L(draft.Kind == LfgFormKind.Create ? "lfg.form.settings_title_create" : "lfg.form.settings_title_edit"),
            L("lfg.form.summary_game", DiscordText.Untrusted(preview.GameName, 120), preview.MaxPlayers),
            L("lfg.form.summary_start", StartText(preview, L)),
            L("lfg.form.summary_duration", DurationText(preview.Duration, L)),
        };
        if (draft.VoiceChannel is { } voice)
            lines.Add(L("lfg.card.voice", LfgCardRenderer.ChannelMention(voice)));
        var notices = new List<string>();
        if (draft.NotifyBeforeStart)
            notices.Add(L("lfg.form.notice_before"));
        if (draft.NotifyAtStart)
            notices.Add(L("lfg.form.notice_start"));
        lines.Add(L("lfg.form.summary_notices", notices.Count == 0 ? L("lfg.form.notices_none") : string.Join(" · ", notices)));
        lines.Add(string.IsNullOrEmpty(preview.Details)
            ? L("lfg.form.summary_no_details")
            : L("lfg.form.summary_details", DiscordText.Untrusted(preview.Details, 300)));
        lines.Add("");
        lines.Add(L(draft.Kind == LfgFormKind.Create ? "lfg.form.settings_hint_create" : "lfg.form.settings_hint_edit"));

        var builder = new ComponentBuilder()
            .WithSelectMenu(DurationSelect(draft, defaultMinutes, L), 0)
            .WithButton(L(string.IsNullOrEmpty(draft.Values.Details) ? "lfg.form.details_add" : "lfg.form.details_edit"), DetailsPrefix + draft.Id,
                ButtonStyle.Secondary, row: 1)
            .WithButton(L(draft.Kind == LfgFormKind.Create ? "lfg.form.create" : "lfg.form.save"), SavePrefix + draft.Id, ButtonStyle.Success, row: 2)
            .WithButton(L("lfg.form.back_main"), BackPrefix + draft.Id, ButtonStyle.Secondary, row: 2)
            .WithButton(L("lfg.form.cancel"), CancelPrefix + draft.Id, ButtonStyle.Secondary, row: 2);
        return (string.Join("\n", lines), builder.Build());
    }

    /// <summary>The duration value stored in the draft for a chosen option (the default option stores none = default).</summary>
    public static string? DurationValue(string option) => option == DefaultDuration ? null : option;

    // The draft transitions of the steps (used by the handlers; pure, so a whole modal ↔ settings round trip is testable).

    /// <summary>
    /// The main modal was submitted: game, start, team size, voice channel and notice opt-ins; the details and the duration
    /// (settings step) are kept.
    /// </summary>
    public static LfgFormDraft WithModal(LfgFormDraft draft, LfgFormModal modal, string? players, ChannelId? voice, bool notifyBeforeStart, bool notifyAtStart) =>
        draft with
        {
            Values = new LfgFormValues(modal.Game, players, draft.Values.Details, modal.Start, draft.Values.Duration),
            VoiceChannel = voice,
            NotifyBeforeStart = notifyBeforeStart,
            NotifyAtStart = notifyAtStart,
            Preview = null,
        };

    /// <summary>The details modal was submitted (empty = no details); everything else is kept.</summary>
    public static LfgFormDraft WithDetails(LfgFormDraft draft, string? details) =>
        draft with { Values = draft.Values with { Details = LfgRules.Normalize(details) is null ? null : details }, Preview = null };

    /// <summary>The form was checked: its preview.</summary>
    public static LfgFormDraft WithCheck(LfgFormDraft draft, LfgFormPreview preview) => draft with { Preview = preview };

    /// <summary>A duration option was chosen: stored in the draft and shown at once (an unreadable option changes nothing).</summary>
    public static LfgFormDraft WithDuration(LfgFormDraft draft, IReadOnlyCollection<string> values, int defaultMinutes)
    {
        if (values.FirstOrDefault() is not { } option)
            return draft;
        var duration = DurationValue(option);
        var minutes = LfgFormText.DurationMinutes(duration) ?? defaultMinutes;
        return minutes <= 0
            ? draft
            : draft with { Values = draft.Values with { Duration = duration }, Preview = draft.Preview is { } p ? p with { Duration = TimeSpan.FromMinutes(minutes) } : null };
    }

    /// <summary>A refused form: the reason and the ways back (main form, details) or out; the draft keeps everything.</summary>
    public static MessageComponent Retry(LfgFormDraft draft, Text L) => new ComponentBuilder()
        .WithButton(L("lfg.form.back"), BackPrefix + draft.Id, ButtonStyle.Primary)
        .WithButton(L(string.IsNullOrEmpty(draft.Values.Details) ? "lfg.form.details_add" : "lfg.form.details_edit"), DetailsPrefix + draft.Id, ButtonStyle.Secondary)
        .WithButton(L("lfg.form.cancel"), CancelPrefix + draft.Id, ButtonStyle.Secondary)
        .Build();

    public static string StartText(LfgFormPreview preview, Text L) =>
        preview.EventAt is { } at && !preview.Start.IsNow
            ? DiscordText.Timestamp(at, 'F') + " • " + DiscordText.Timestamp(at, 'R')
            : L("lfg.form.start_now");

    public static string DurationText(TimeSpan span, Text L)
    {
        var total = (int)Math.Round(span.TotalMinutes);
        int hours = total / 60, minutes = total % 60;
        if (hours == 0)
            return L("lfg.form.minutes", minutes);
        return minutes == 0 ? L("lfg.form.hours", hours) : L("lfg.form.hours", hours) + " " + L("lfg.form.minutes", minutes);
    }

    private static SelectMenuBuilder DurationSelect(LfgFormDraft draft, int defaultMinutes, Text L)
    {
        var options = LfgRules.DurationChoicesMinutes.Select(m => (Value: LfgForm.FormatDuration(TimeSpan.FromMinutes(m)), Minutes: m, Suffix: (string?)null)).ToList();
        // Offered for as long as the form lives (also after another choice): an edited listing's own duration when it is not
        // a choice, and for a new listing a configured default that is not a choice.
        if (draft.Opened?.Duration is { } current && options.All(o => o.Value != current) && LfgFormText.DurationMinutes(current) is { } currentMinutes and > 0)
            options.Add((current, currentMinutes, L("lfg.form.duration_current")));
        if (draft.Kind == LfgFormKind.Create && !LfgRules.DurationChoicesMinutes.Contains(defaultMinutes))
            options.Add((DefaultDuration, defaultMinutes, L("lfg.form.duration_default")));
        var selected = draft.Values.Duration
                       ?? (LfgRules.DurationChoicesMinutes.Contains(defaultMinutes) ? LfgForm.FormatDuration(TimeSpan.FromMinutes(defaultMinutes)) : DefaultDuration);

        var select = new SelectMenuBuilder()
            .WithCustomId(DurationPrefix + draft.Id)
            .WithPlaceholder(L("lfg.form.duration_placeholder"))
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var (value, minutes, suffix) in options)
        {
            var label = DurationText(TimeSpan.FromMinutes(minutes), L) + (suffix is null ? "" : " " + suffix);
            select.AddOption(label, value, isDefault: value == selected);
        }

        return select;
    }

    private static TextInputBuilder Input(string id, TextInputStyle style, string placeholder, int? minLength, int maxLength, bool required, string? value)
    {
        var input = new TextInputBuilder()
            .WithCustomId(id)
            .WithStyle(style)
            .WithPlaceholder(placeholder)
            .WithMaxLength(maxLength)
            .WithRequired(required);
        if (minLength is { } min)
            input.WithMinLength(min);
        if (!string.IsNullOrEmpty(value) && value.Length <= maxLength)
            input.WithValue(value); // a longer value is not prefilled (never cut mid-character); the owner retypes it
        return input;
    }
}

/// <summary>
/// The main modal's text fields as the Interaction Framework binds them (by field custom id). The modal itself is built by
/// <see cref="LfgFormUi.Modal"/>; the selects (team size, voice channel, notices) are read with the <c>LfgFormUi.Read*</c>
/// helpers. Everything is re-validated server-side.
/// </summary>
public sealed class LfgFormModal : IModal
{
    public string Title => "TSQ LFG";

    [ModalTextInput(LfgForm.GameField)]
    public string? Game { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(LfgForm.StartField)]
    public string? Start { get; set; }
}

/// <summary>The details modal's one field (opened from the settings message).</summary>
public sealed class LfgDetailsModal : IModal
{
    public string Title => "TSQ LFG";

    [RequiredInput(false)]
    [ModalTextInput(LfgForm.DetailsField, TextInputStyle.Paragraph)]
    public string? Details { get; set; }
}
