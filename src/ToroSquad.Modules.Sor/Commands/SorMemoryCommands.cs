using System.Globalization;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Domain;

namespace ToroSquad.Modules.Sor.Commands;

/// <summary>
/// /hafizam and the "Hafızam" button: ALWAYS the clicker's own memory in this guild, in a private message — the button on
/// someone else's answer opens the clicker's memory, never the answer owner's. A note id and version in a component is only
/// a pointer: <see cref="SorMemoryService"/> checks owner, guild and current version on every action, and a screen older than
/// <see cref="PanelLifetime"/> must be opened again. Deleting one clearly chosen note needs no second confirmation; deleting
/// everything does (single use, 5 minutes). Nothing sent here pings.
/// </summary>
[ToroModule(SorModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class SorMemoryCommands(InteractionServices services, SorMemoryService memory, IOptions<SorOptions> options) : ToroInteractionModule(services)
{
    public static readonly TimeSpan PanelLifetime = TimeSpan.FromMinutes(15);

    private const string Prefix = "tsq:sor:mem:";
    public const string SelectId = Prefix + "sel";
    public const string SelectMoreId = Prefix + "sel2";
    public const string AddId = Prefix + "add";
    public const string BackId = Prefix + "back";
    public const string WipeId = Prefix + "wipe";
    public const string TogglePrefix = Prefix + "toggle:";
    public const string EditPrefix = Prefix + "edit:";
    public const string DeletePrefix = Prefix + "del:";
    public const string SharePrefix = Prefix + "share:";
    public const string ShareConfirmPrefix = Prefix + "shareok:";
    public const string UnsharePrefix = Prefix + "unshare:";
    public const string WipeConfirmPrefix = Prefix + "wipeok:";
    public const string AddModalId = "tsq:sor:memm:add";
    public const string EditModalPrefix = "tsq:sor:memm:edit:";
    public const string NoteField = "not";

    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [SlashCommand("hafizam", "See and manage what TSQ Sor remembers about you (only you see it)")]
    public async Task OpenCommandAsync()
    {
        await DeferEphemeralAsync();
        var (embed, components) = await PanelAsync(null);
        await FollowupAsync(embed: embed, components: components, ephemeral: true, allowedMentions: NoPings);
    }

    /// <summary>The button under a /sor answer: a NEW private panel for the clicker.</summary>
    [ComponentInteraction(SorUi.MemoryId, ignoreGroupNames: true)]
    public async Task OpenButtonAsync()
    {
        await DeferEphemeralAsync();
        var (embed, components) = await PanelAsync(null);
        await FollowupAsync(embed: embed, components: components, ephemeral: true, allowedMentions: NoPings);
    }

    [ComponentInteraction(SelectMoreId, ignoreGroupNames: true)]
    public Task SelectMoreAsync(string[] values) => SelectAsync(values);

    [ComponentInteraction(SelectId, ignoreGroupNames: true)]
    public async Task SelectAsync(string[] values)
    {
        if (await StaleAsync())
            return;
        var note = values.Length > 0 && Pointer(values[0], out var id, out var stamp) ? await memory.GetAsync(Actor, id, stamp, CancellationToken.None) : null;
        if (note is null)
        {
            await ShowPanelAsync("sor.mem.changed");
            return;
        }

        await UpdateAsync(await NoteEmbedAsync(note, null), await NoteButtonsAsync(note));
    }

    [ComponentInteraction(BackId, ignoreGroupNames: true)]
    public Task BackAsync() => ShowPanelAsync(null);

    [ComponentInteraction(EditPrefix + "*:*", ignoreGroupNames: true)]
    public async Task EditAsync(string id, string stamp)
    {
        if (await StaleAsync() || !Pointer(id + ":" + stamp, out var noteId, out var noteStamp))
            return;
        var note = await memory.GetAsync(Actor, noteId, noteStamp, CancellationToken.None);
        if (note is null)
        {
            await ShowPanelAsync("sor.mem.changed");
            return;
        }

        await RespondWithModalAsync(NoteModal(EditModalPrefix + id + ":" + stamp, await T("sor.mem.form.edit_title"), note.Text));
    }

    [ComponentInteraction(AddId, ignoreGroupNames: true)]
    public async Task AddAsync()
    {
        if (await StaleAsync())
            return;
        await RespondWithModalAsync(NoteModal(AddModalId, await T("sor.mem.form.add_title"), null));
    }

    [ModalInteraction(AddModalId, ignoreGroupNames: true)]
    public async Task AddSubmitAsync(SorNoteModal modal) =>
        await ShowResultAsync(await memory.AddAsync(Actor, modal.Note, CancellationToken.None));

    [ModalInteraction(EditModalPrefix + "*:*", ignoreGroupNames: true)]
    public async Task EditSubmitAsync(string id, string stamp, SorNoteModal modal)
    {
        if (!Pointer(id + ":" + stamp, out var noteId, out var noteStamp))
            return;
        await ShowResultAsync(await memory.EditAsync(Actor, noteId, noteStamp, modal.Note, CancellationToken.None));
    }

    [ComponentInteraction(DeletePrefix + "*:*", ignoreGroupNames: true)]
    public async Task DeleteAsync(string id, string stamp)
    {
        if (await StaleAsync() || !Pointer(id + ":" + stamp, out var noteId, out var noteStamp))
            return;
        await ShowResultAsync(await memory.DeleteAsync(Actor, noteId, noteStamp, CancellationToken.None));
    }

    /// <summary>Sharing starts with a preview of exactly what others' questions could use.</summary>
    [ComponentInteraction(SharePrefix + "*:*", ignoreGroupNames: true)]
    public async Task SharePreviewAsync(string id, string stamp)
    {
        if (await StaleAsync() || !Pointer(id + ":" + stamp, out var noteId, out var noteStamp))
            return;
        var note = await memory.GetAsync(Actor, noteId, noteStamp, CancellationToken.None);
        if (note is null)
        {
            await ShowPanelAsync("sor.mem.changed");
            return;
        }

        var embed = new EmbedBuilder().WithTitle(await T("sor.mem.share.title")).WithColor(WarningColor)
            .WithDescription(await T("sor.mem.share.preview", Quote(note.Text))).Build();
        var buttons = new ComponentBuilder()
            .WithButton(await T("sor.mem.share.confirm"), ShareConfirmPrefix + id + ":" + stamp, ButtonStyle.Danger)
            .WithButton(await T("sor.mem.back"), BackId, ButtonStyle.Secondary).Build();
        await UpdateAsync(embed, buttons);
    }

    [ComponentInteraction(ShareConfirmPrefix + "*:*", ignoreGroupNames: true)]
    public async Task ShareAsync(string id, string stamp)
    {
        if (await StaleAsync() || !Pointer(id + ":" + stamp, out var noteId, out var noteStamp))
            return;
        await ShowResultAsync(await memory.ShareAsync(Actor, noteId, noteStamp, CancellationToken.None));
    }

    [ComponentInteraction(UnsharePrefix + "*:*", ignoreGroupNames: true)]
    public async Task UnshareAsync(string id, string stamp)
    {
        if (await StaleAsync() || !Pointer(id + ":" + stamp, out var noteId, out var noteStamp))
            return;
        await ShowResultAsync(await memory.UnshareAsync(Actor, noteId, noteStamp, CancellationToken.None));
    }

    [ComponentInteraction(TogglePrefix + "*", ignoreGroupNames: true)]
    public async Task ToggleAsync(string state)
    {
        if (await StaleAsync() || state is not ("on" or "off"))
            return;
        await ShowResultAsync(await memory.SetEnabledAsync(Actor, state == "on", CancellationToken.None));
    }

    [ComponentInteraction(WipeId, ignoreGroupNames: true)]
    public async Task WipePreviewAsync()
    {
        if (await StaleAsync())
            return;
        var preview = await memory.PreviewWipeAsync(Actor, CancellationToken.None);
        var embed = new EmbedBuilder().WithTitle(await T("sor.mem.wipe.title")).WithColor(WarningColor)
            .WithDescription(await T("sor.mem.wipe.preview", preview.Notes, preview.Conversations, preview.Turns)).Build();
        var buttons = new ComponentBuilder()
            .WithButton(await T("sor.mem.wipe.confirm"), WipeConfirmPrefix + preview.ConfirmationId, ButtonStyle.Danger)
            .WithButton(await T("sor.mem.back"), BackId, ButtonStyle.Secondary).Build();
        await UpdateAsync(embed, buttons);
    }

    [ComponentInteraction(WipeConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task WipeAsync(string confirmationId) =>
        // The confirmation is bound to guild + user + action in the store; another member's click cannot consume it.
        await ShowResultAsync(await memory.WipeAsync(Actor, confirmationId, CancellationToken.None));

    private async Task ShowResultAsync(OperationResult result)
    {
        var line = await T(result.MessageKey, result.Args.ToArray()) + await TraceLineAsync(result);
        var (embed, components) = await PanelAsync(line);
        await UpdateAsync(embed, components);
    }

    private async Task ShowPanelAsync(string? noteKey)
    {
        var (embed, components) = await PanelAsync(noteKey is null ? null : await T(noteKey));
        await UpdateAsync(embed, components);
    }

    private async Task<(Embed Embed, MessageComponent Components)> PanelAsync(string? line)
    {
        var panel = await memory.PanelAsync(Actor, CancellationToken.None);
        var language = await LangAsync();
        string L(string key, params object?[] args) => Localizer.Get(language, key, args);

        var text = new StringBuilder();
        if (line is not null)
            text.Append("-# ").Append(line).Append("\n\n");
        text.Append(L(panel.Enabled ? "sor.mem.on" : "sor.mem.off")).Append('\n');
        text.Append("-# ").Append(L("sor.mem.explain")).Append("\n\n");
        if (panel.Notes.Count == 0)
            text.Append(L("sor.mem.empty_list"));
        for (var i = 0; i < panel.Notes.Count; i++)
        {
            var n = panel.Notes[i];
            text.Append(CultureInfo.InvariantCulture, $"`{i + 1}.` {ScopeIcon(n.Scope)} *{L("sor.kind." + SorMemoryKinds.Wire(n.Kind))}* — {Escape(n.Text)}\n");
        }

        var embed = new EmbedBuilder().WithTitle(L("sor.mem.title")).WithColor(BrandColor).WithDescription(SorUi.Cut(text.ToString(), 4000))
            .WithFooter(L("sor.mem.footer", panel.Notes.Count, panel.Max)).Build();

        var builder = new ComponentBuilder();
        var row = 0;
        foreach (var chunk in panel.Notes.Select((n, i) => (n, i)).Chunk(25).Take(2))
        {
            var select = new SelectMenuBuilder().WithCustomId(row == 0 ? SelectId : SelectMoreId).WithPlaceholder(L("sor.mem.select")).WithMinValues(1).WithMaxValues(1);
            foreach (var (n, i) in chunk)
                select.AddOption(SorUi.Cut((i + 1).ToString(CultureInfo.InvariantCulture) + ". " + n.Text, 100), Pointer(n), SorUi.Cut(L(ScopeKey(n.Scope)), 100));
            builder.WithSelectMenu(select, row++);
        }

        builder.WithButton(L("sor.mem.add"), AddId, ButtonStyle.Primary, new Emoji("➕"), row: row);
        builder.WithButton(L(panel.Enabled ? "sor.mem.turn_off" : "sor.mem.turn_on"), TogglePrefix + (panel.Enabled ? "off" : "on"), ButtonStyle.Secondary, row: row);
        builder.WithButton(L("sor.mem.wipe"), WipeId, ButtonStyle.Danger, new Emoji("🗑️"), row: row);
        return (embed, builder.Build());
    }

    private async Task<Embed> NoteEmbedAsync(SorNote note, string? line)
    {
        var language = await LangAsync();
        string L(string key, params object?[] args) => Localizer.Get(language, key, args);
        var scope = note.Scope == SorMemoryScope.Channel && note.SourceChannel is { } channel
            ? L("sor.scope.channel_in", "<#" + channel.ToString(CultureInfo.InvariantCulture) + ">")
            : L(ScopeKey(note.Scope));
        var text = (line is null ? "" : "-# " + line + "\n\n") + Quote(note.Text) + "\n\n" +
                   L("sor.mem.note_meta", L("sor.kind." + SorMemoryKinds.Wire(note.Kind)), ScopeIcon(note.Scope) + " " + scope, DiscordText.Timestamp(note.UpdatedAt, 'R'));
        return new EmbedBuilder().WithTitle(L("sor.mem.note_title")).WithColor(BrandColor).WithDescription(text).Build();
    }

    private async Task<MessageComponent> NoteButtonsAsync(SorNote note)
    {
        var pointer = note.Id.ToString(CultureInfo.InvariantCulture) + ":" + note.Stamp;
        var builder = new ComponentBuilder()
            .WithButton(await T("sor.mem.edit"), EditPrefix + pointer, ButtonStyle.Primary, new Emoji("✏️"))
            .WithButton(await T("sor.mem.delete"), DeletePrefix + pointer, ButtonStyle.Danger, new Emoji("🗑️"));
        builder = note.Scope == SorMemoryScope.Shared
            ? builder.WithButton(await T("sor.mem.unshare"), UnsharePrefix + pointer, ButtonStyle.Secondary, new Emoji("🔒"))
            : builder.WithButton(await T("sor.mem.share"), SharePrefix + pointer, ButtonStyle.Secondary, new Emoji("📢"));
        return builder.WithButton(await T("sor.mem.back"), BackId, ButtonStyle.Secondary).Build();
    }

    private Modal NoteModal(string customId, string title, string? value)
    {
        var input = new TextInputBuilder().WithCustomId(NoteField).WithStyle(TextInputStyle.Paragraph).WithMinLength(1).WithMaxLength(options.Value.MaxMemoryItemLength).WithRequired(true);
        if (value is not null)
            input.WithValue(value);
        return new ModalBuilder().WithTitle(SorUi.Cut(title, SorUi.MaxTitleLength)).WithCustomId(customId).AddLabel(SorUi.Cut(title, SorUi.MaxTitleLength), input).Build();
    }

    /// <summary>A panel older than <see cref="PanelLifetime"/> is not acted on (versions are checked anyway).</summary>
    private async Task<bool> StaleAsync()
    {
        if (Context.Interaction is IComponentInteraction { Message: { } message } && Services.Clock.GetUtcNow() - message.Timestamp > PanelLifetime)
        {
            await SendEphemeralAsync(await T("sor.mem.expired"), null, null);
            return true;
        }

        return false;
    }

    /// <summary>Replaces the panel message this click came from (a modal submitted from it updates it too).</summary>
    private async Task UpdateAsync(Embed embed, MessageComponent components)
    {
        void Edit(MessageProperties m)
        {
            m.Content = "";
            m.Embeds = new[] { embed };
            m.Components = components;
            m.AllowedMentions = NoPings;
        }

        switch (Context.Interaction)
        {
            case IComponentInteraction component when !Context.Interaction.HasResponded:
                await component.UpdateAsync(Edit);
                break;
            case SocketModal { Message: not null } modal when !Context.Interaction.HasResponded:
                await modal.UpdateAsync(Edit);
                break;
            default:
                await SendAsync(null, embed, components, ephemeral: true);
                break;
        }
    }

    private static string Pointer(SorNote note) => note.Id.ToString(CultureInfo.InvariantCulture) + ":" + note.Stamp;

    private static bool Pointer(string value, out long id, out string stamp)
    {
        var parts = value.Split(':');
        stamp = parts.Length == 2 ? parts[1] : "";
        id = 0;
        return parts.Length == 2 && stamp.Length is > 0 and <= 16 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static string ScopeKey(SorMemoryScope scope) => scope switch
    {
        SorMemoryScope.Shared => "sor.scope.shared",
        SorMemoryScope.Channel => "sor.scope.channel",
        _ => "sor.scope.private",
    };

    private static string ScopeIcon(SorMemoryScope scope) => scope switch
    {
        SorMemoryScope.Shared => "🌐",
        SorMemoryScope.Channel => "#️⃣",
        _ => "🔒",
    };

    private static string Quote(string text) => "> " + Escape(text);

    private static string Escape(string text) => text.Replace("\n", " ", StringComparison.Ordinal).Replace("@", "@\u200b", StringComparison.Ordinal);
}

/// <summary>The note form (add or correct), bound by field custom id.</summary>
public sealed class SorNoteModal : IModal
{
    public string Title => SorUi.Title;

    [ModalTextInput(SorMemoryCommands.NoteField, TextInputStyle.Paragraph)]
    public string? Note { get; set; }
}
