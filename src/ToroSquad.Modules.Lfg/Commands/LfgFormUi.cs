using Discord;
using Discord.Interactions;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Commands;

/// <summary>
/// The listing form's Discord surface, shared by create and edit. Discord allows at most five top-level components in a
/// modal (Label + text input each), so the modal holds the five texts — game, players, details, start, duration — and the
/// structured settings (the two notice opt-ins and the voice channel) follow in a private settings message with native
/// selects. Every custom id carries only the random draft id; what a click may do is decided server-side.
/// </summary>
public static class LfgFormUi
{
    public const string NotifyPrefix = "tsq:lfg:draft:notify:";
    public const string VoicePrefix = "tsq:lfg:draft:voice:";
    public const string SavePrefix = "tsq:lfg:draft:save:";
    public const string BackPrefix = "tsq:lfg:draft:back:";
    public const string CancelPrefix = "tsq:lfg:draft:cancel:";

    public const string NotifyBefore = "before";
    public const string NotifyStart = "start";

    /// <summary>Discord: at most 5 top-level components in a modal; titles and labels at most 45 characters.</summary>
    public const int MaxModalComponents = 5;
    public const int MaxTitleLength = 45;
    public const int MaxLabelLength = 45;
    public const int MaxDescriptionLength = 100;

    public delegate string Text(string key, params object?[] args);

    public static Modal Modal(LfgFormDraft draft, int maxPlayers, Text L)
    {
        var values = draft.Values;
        return new ModalBuilder()
            .WithTitle(L(draft.Kind == LfgFormKind.Create ? "lfg.form.title_create" : "lfg.form.title_edit"))
            .WithCustomId(LfgForm.ModalPrefix + draft.Id)
            .AddLabel(L("lfg.form.game"), Input(LfgForm.GameField, TextInputStyle.Short, L("lfg.form.game_placeholder"), LfgRules.GameNameMinLength,
                LfgRules.GameNameMaxLength, required: true, values.Game), L("lfg.form.game_hint"))
            .AddLabel(L("lfg.form.players"), Input(LfgForm.PlayersField, TextInputStyle.Short, "6", 1, LfgForm.PlayersMaxLength, required: true,
                values.Players), L("lfg.form.players_hint", LfgRules.MinPlayers, maxPlayers))
            .AddLabel(L("lfg.form.details"), Input(LfgForm.DetailsField, TextInputStyle.Paragraph, L("lfg.form.details_placeholder"), null,
                LfgRules.DetailsMaxLength, required: false, values.Details), L("lfg.form.details_hint"))
            .AddLabel(L("lfg.form.start"), Input(LfgForm.StartField, TextInputStyle.Short, L("lfg.form.start_placeholder"), null, LfgForm.StartMaxLength,
                required: false, values.Start), L("lfg.form.start_hint"))
            .AddLabel(L("lfg.form.duration"), Input(LfgForm.DurationField, TextInputStyle.Short, "2", null, LfgForm.DurationMaxLength, required: false,
                values.Duration), L("lfg.form.duration_hint"))
            .Build();
    }

    /// <summary>The second step: what the form will open (or change to) and the native settings controls.</summary>
    public static (string Content, MessageComponent Components) Settings(LfgFormDraft draft, DateTimeOffset now, Text L)
    {
        var preview = draft.Preview ?? throw new ArgumentException("A checked form is required.", nameof(draft));
        var lines = new List<string>
        {
            L(draft.Kind == LfgFormKind.Create ? "lfg.form.settings_title_create" : "lfg.form.settings_title_edit"),
            L("lfg.form.summary_game", DiscordText.Untrusted(preview.GameName, 120), preview.MaxPlayers),
            L("lfg.form.summary_start", StartText(preview, L)),
            L("lfg.form.summary_duration", DurationText(preview.Duration, L)),
        };
        if (!string.IsNullOrEmpty(preview.Details))
            lines.Add("📝 " + DiscordText.Untrusted(preview.Details, 300));
        lines.Add("");

        var notices = preview.EventAt > now || draft.NotifyBeforeStart || draft.NotifyAtStart;
        lines.Add(L(draft.Kind == LfgFormKind.Create ? "lfg.form.settings_hint_create" : "lfg.form.settings_hint_edit"));
        if (!notices)
            lines.Add(L("lfg.form.notices_need_start"));

        var builder = new ComponentBuilder();
        var row = 0;
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

        var voice = new SelectMenuBuilder()
            .WithType(ComponentType.ChannelSelect)
            .WithCustomId(VoicePrefix + draft.Id)
            .WithChannelTypes(ChannelType.Voice)
            .WithPlaceholder(L("lfg.form.voice_placeholder"))
            .WithMinValues(0)
            .WithMaxValues(1);
        if (draft.VoiceChannel is { } channel)
            voice.WithDefaultValues(new SelectMenuDefaultValue(channel.Value, SelectDefaultValueType.Channel));
        builder.WithSelectMenu(voice, row++);

        builder.WithButton(L(draft.Kind == LfgFormKind.Create ? "lfg.form.create" : "lfg.form.save"), SavePrefix + draft.Id, ButtonStyle.Success, row: row);
        builder.WithButton(L("lfg.form.back"), BackPrefix + draft.Id, ButtonStyle.Secondary, row: row);
        builder.WithButton(L("lfg.form.cancel"), CancelPrefix + draft.Id, ButtonStyle.Secondary, row: row);
        return (string.Join("\n", lines), builder.Build());
    }

    /// <summary>A refused form: the reason and the way back into the same form (with what was typed) or out.</summary>
    public static MessageComponent Retry(LfgFormDraft draft, Text L) => new ComponentBuilder()
        .WithButton(L("lfg.form.back"), BackPrefix + draft.Id, ButtonStyle.Primary)
        .WithButton(L("lfg.form.cancel"), CancelPrefix + draft.Id, ButtonStyle.Secondary)
        .Build();

    public static string StartText(LfgFormPreview preview, Text L)
    {
        if (preview.Start.Delay is { } delay)
            return L("lfg.form.start_in", DurationText(delay, L));
        if (preview.EventAt is { } at && !preview.Start.IsNow)
            return DiscordText.Timestamp(at, 'F') + " • " + DiscordText.Timestamp(at, 'R');
        return L("lfg.form.start_now");
    }

    public static string DurationText(TimeSpan span, Text L)
    {
        var total = (int)Math.Round(span.TotalMinutes);
        int hours = total / 60, minutes = total % 60;
        if (hours == 0)
            return L("lfg.form.minutes", minutes);
        return minutes == 0 ? L("lfg.form.hours", hours) : L("lfg.form.hours", hours) + " " + L("lfg.form.minutes", minutes);
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
        if (!string.IsNullOrEmpty(value))
            input.WithValue(value.Length <= maxLength ? value : value[..maxLength]);
        return input;
    }
}

/// <summary>
/// The submitted form as the Interaction Framework binds it (by field custom id). The modal itself is built by
/// <see cref="LfgFormUi.Modal"/>; this type only reads it back. All fields are plain text and re-validated server-side.
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

    [RequiredInput(false)]
    [ModalTextInput(LfgForm.DurationField)]
    public string? Duration { get; set; }

    public LfgFormValues ToValues() => new(Game, Players, Details, Start, Duration);
}
