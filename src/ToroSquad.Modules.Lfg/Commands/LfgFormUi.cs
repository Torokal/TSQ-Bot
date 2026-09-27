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
/// modal, so the modal holds game, players, details, the start date (empty = now) and the voice channel (a native channel
/// select, voice channels only); the listing duration and the two notice opt-ins follow in a private settings message with
/// native selects. Every custom id carries only the random draft id; what a click may do is decided server-side.
/// </summary>
public static class LfgFormUi
{
    public const string NotifyPrefix = "tsq:lfg:draft:notify:";
    public const string DurationPrefix = "tsq:lfg:draft:duration:";
    public const string SavePrefix = "tsq:lfg:draft:save:";
    public const string BackPrefix = "tsq:lfg:draft:back:";
    public const string CancelPrefix = "tsq:lfg:draft:cancel:";

    public const string NotifyBefore = "before";
    public const string NotifyStart = "start";

    /// <summary>Duration option for a configured default that is not one of the choices (create only).</summary>
    public const string DefaultDuration = "default";

    /// <summary>Discord: at most 5 top-level components in a modal; titles and labels at most 45 characters.</summary>
    public const int MaxModalComponents = 5;
    public const int MaxTitleLength = 45;
    public const int MaxLabelLength = 45;
    public const int MaxDescriptionLength = 100;

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

        return new ModalBuilder()
            .WithTitle(L(draft.Kind == LfgFormKind.Create ? "lfg.form.title_create" : "lfg.form.title_edit"))
            .WithCustomId(LfgForm.ModalPrefix + draft.Id)
            .AddLabel(L("lfg.form.game"), Input(LfgForm.GameField, TextInputStyle.Short, L("lfg.form.game_placeholder"), LfgRules.GameNameMinLength,
                LfgRules.GameNameMaxLength * factor, required: true, values.Game), L("lfg.form.game_hint"))
            .AddLabel(L("lfg.form.players"), Input(LfgForm.PlayersField, TextInputStyle.Short, "6", 1, LfgForm.PlayersMaxLength, required: true,
                values.Players), L("lfg.form.players_hint", LfgRules.MinPlayers, maxPlayers))
            .AddLabel(L("lfg.form.details"), Input(LfgForm.DetailsField, TextInputStyle.Paragraph, L("lfg.form.details_placeholder"), null,
                LfgRules.DetailsMaxLength * factor, required: false, values.Details), L("lfg.form.details_hint"))
            .AddLabel(L("lfg.form.start"), Input(LfgForm.StartField, TextInputStyle.Short, L("lfg.form.start_placeholder"), null, LfgForm.StartMaxLength,
                required: false, values.Start), L("lfg.form.start_hint"))
            .AddLabel(L("lfg.form.voice"), voice, L("lfg.form.voice_hint"))
            .Build();
    }

    /// <summary>The voice channel chosen in the modal's channel select (none when nothing is selected).</summary>
    public static ChannelId? ReadVoice(IEnumerable<IComponentInteractionData> components)
    {
        var values = components.FirstOrDefault(c => c.CustomId == LfgForm.VoiceField)?.Values;
        return values?.FirstOrDefault() is { } id && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var channel)
            ? new ChannelId(channel)
            : null;
    }

    /// <summary>
    /// The second step: what the form will open (or change to), the listing duration (1/2/3 h; the configured default, or an
    /// edited listing's current duration, is also offered when it is not one of them) and, for a later start, the notices.
    /// </summary>
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
        if (!string.IsNullOrEmpty(preview.Details))
            lines.Add("📝 " + DiscordText.Untrusted(preview.Details, 300));
        lines.Add("");

        var notices = preview.EventAt > now || draft.NotifyBeforeStart || draft.NotifyAtStart;
        lines.Add(L(draft.Kind == LfgFormKind.Create ? "lfg.form.settings_hint_create" : "lfg.form.settings_hint_edit"));
        if (!notices)
            lines.Add(L("lfg.form.notices_need_start"));

        var builder = new ComponentBuilder();
        var row = 0;
        builder.WithSelectMenu(DurationSelect(draft, defaultMinutes, L), row++);
        if (notices)
        {
            builder.WithSelectMenu(new SelectMenuBuilder()
                .WithCustomId(NotifyPrefix + draft.Id)
                .WithPlaceholder(L("lfg.form.notices_placeholder"))
                .WithMinValues(0)
                .WithMaxValues(2)
                .AddOption(L("lfg.form.notice_before"), NotifyBefore, L("lfg.form.notice_before_hint"), isDefault: draft.NotifyBeforeStart)
                .AddOption(L("lfg.form.notice_start"), NotifyStart, L("lfg.form.notice_start_hint"), isDefault: draft.NotifyAtStart), row++);
        }

        builder.WithButton(L(draft.Kind == LfgFormKind.Create ? "lfg.form.create" : "lfg.form.save"), SavePrefix + draft.Id, ButtonStyle.Success, row: row);
        builder.WithButton(L("lfg.form.back"), BackPrefix + draft.Id, ButtonStyle.Secondary, row: row);
        builder.WithButton(L("lfg.form.cancel"), CancelPrefix + draft.Id, ButtonStyle.Secondary, row: row);
        return (string.Join("\n", lines), builder.Build());
    }

    /// <summary>The duration value stored in the draft for a chosen option (the default option stores none = default).</summary>
    public static string? DurationValue(string option) => option == DefaultDuration ? null : option;

    /// <summary>A refused form: the reason and the way back into the same form (with what was typed) or out.</summary>
    public static MessageComponent Retry(LfgFormDraft draft, Text L) => new ComponentBuilder()
        .WithButton(L("lfg.form.back"), BackPrefix + draft.Id, ButtonStyle.Primary)
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
/// The submitted form's text fields as the Interaction Framework binds them (by field custom id). The modal itself is built
/// by <see cref="LfgFormUi.Modal"/>; the voice channel select is read with <see cref="LfgFormUi.ReadVoice"/>. Everything is
/// re-validated server-side.
/// </summary>
public sealed class LfgFormModal : IModal
{
    public string Title => "TSQ LFG";

    [ModalTextInput(LfgForm.GameField)]
    public string? Game { get; set; }

    [ModalTextInput(LfgForm.PlayersField)]
    public string? Players { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(LfgForm.DetailsField, TextInputStyle.Paragraph)]
    public string? Details { get; set; }

    [RequiredInput(false)]
    [ModalTextInput(LfgForm.StartField)]
    public string? Start { get; set; }

    /// <summary>The typed texts; the duration is not in the modal (settings step) and is carried over from the draft.</summary>
    public LfgFormValues ToValues(string? duration) => new(Game, Players, Details, Start, duration);
}
