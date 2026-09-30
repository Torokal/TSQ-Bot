using Discord;
using ToroSquad.Core;

namespace ToroSquad.Discord.Admin;

/// <summary>
/// The small private forms admin operations open when a required input is missing or a setting has several fields. Every
/// component's custom id is <c>tsq:adm:&lt;draft&gt;:&lt;action&gt;</c> (the draft id is random; the router checks owner, guild,
/// expiry and permission on every click). Native channel/user/role selects are used; no ids to copy, no JSON.
/// </summary>
public static class AdminForms
{
    public const string ChannelAction = "channel";
    public const string UserAction = "user";
    public const string RoleAction = "role";
    public const string ClearAction = "clear";

    /// <summary>A private message with a text/announcement channel select (and optionally an explicit "remove" button).</summary>
    public static Task PickChannelAsync(AdminCall call, string promptKey, string? clearLabelKey = null, object? state = null)
    {
        var draft = call.OpenDraft(state ?? new object());
        var select = new SelectMenuBuilder()
            .WithType(ComponentType.ChannelSelect)
            .WithCustomId(AdminCall.CustomId(draft, ChannelAction))
            .WithChannelTypes(ChannelType.Text, ChannelType.News)
            .WithPlaceholder(call.T("admin.form.channel_placeholder"))
            .WithMinValues(1)
            .WithMaxValues(1);
        var builder = new ComponentBuilder().WithSelectMenu(select);
        if (clearLabelKey is not null)
            builder.WithButton(call.T(clearLabelKey), AdminCall.CustomId(draft, ClearAction), ButtonStyle.Danger, row: 1);
        builder.WithButton(call.T("admin.form.cancel"), AdminCall.CustomId(draft, AdminRouter.CancelAction), ButtonStyle.Secondary, row: 1);
        return call.Respond.SendAsync(call.T(promptKey), null, builder.Build());
    }

    /// <summary>A private message with a member select.</summary>
    public static Task PickUserAsync(AdminCall call, string promptKey, object? state = null)
    {
        var draft = call.OpenDraft(state ?? new object());
        var select = new SelectMenuBuilder()
            .WithType(ComponentType.UserSelect)
            .WithCustomId(AdminCall.CustomId(draft, UserAction))
            .WithPlaceholder(call.T("admin.form.user_placeholder"))
            .WithMinValues(1)
            .WithMaxValues(1);
        return call.Respond.SendAsync(call.T(promptKey), null, new ComponentBuilder().WithSelectMenu(select)
            .WithButton(call.T("admin.form.cancel"), AdminCall.CustomId(draft, AdminRouter.CancelAction), ButtonStyle.Secondary, row: 1).Build());
    }

    /// <summary>The channel picked in this interaction, only if it is a text/announcement channel of this guild.</summary>
    public static ulong? ChosenChannel(AdminCall call)
    {
        var id = call.Input.FirstValueId;
        var channel = call.Input.Channels.FirstOrDefault(c => c.Id == id);
        return id is not null && AdminRouter.IsUsableChannel(channel, call.Actor.GuildId) ? id : null;
    }

    /// <summary>The member picked in this interaction, with Discord's resolved guild membership.</summary>
    public static AdminMember? ChosenMember(AdminCall call)
    {
        if (call.Input.FirstValueId is not { } id)
            return null;
        var member = call.Input.Members.FirstOrDefault(m => m.Id == id);
        return member is null ? new AdminMember(id, false) : AdminMember.From(member, call.Actor.GuildId);
    }

    /// <summary>A private message with one string select (at most 25 options; callers page longer lists).</summary>
    public static Task ChooseAsync(AdminCall call, string text, string action, IEnumerable<(string Label, string Value, bool Selected)> options,
        object? state = null, IEnumerable<(string Label, string Action)>? buttons = null, AdminDraft? reuse = null)
    {
        var draft = reuse ?? call.OpenDraft(state ?? new object());
        var list = options.Take(25).ToList();
        var builder = new ComponentBuilder();
        if (list.Count > 0)
        {
            var select = new SelectMenuBuilder().WithCustomId(AdminCall.CustomId(draft, action)).WithMinValues(1).WithMaxValues(1)
                .WithPlaceholder(call.T("admin.form.choose_placeholder"));
            foreach (var (label, value, selected) in list)
                select.AddOption(Cut(label, 100), value, isDefault: selected);
            builder.WithSelectMenu(select);
        }

        foreach (var (label, buttonAction) in buttons ?? [])
            builder.WithButton(Cut(label, 80), AdminCall.CustomId(draft, buttonAction), ButtonStyle.Secondary, row: 1);
        builder.WithButton(call.T("admin.form.cancel"), AdminCall.CustomId(draft, AdminRouter.CancelAction), ButtonStyle.Secondary, row: 1);
        return reuse is null ? call.Respond.SendAsync(text, null, builder.Build()) : call.Respond.UpdateAsync(text, builder.Build());
    }

    /// <summary>Buttons only (e.g. on/off); the first ones primary, cancel last.</summary>
    public static Task ButtonsAsync(AdminCall call, string text, IEnumerable<(string Label, string Action)> buttons, object? state = null)
    {
        var draft = call.OpenDraft(state ?? new object());
        var builder = new ComponentBuilder();
        foreach (var (label, action) in buttons)
            builder.WithButton(Cut(label, 80), AdminCall.CustomId(draft, action), ButtonStyle.Primary);
        builder.WithButton(call.T("admin.form.cancel"), AdminCall.CustomId(draft, AdminRouter.CancelAction), ButtonStyle.Secondary);
        return call.Respond.SendAsync(text, null, builder.Build());
    }

    /// <summary>A one-field text modal (as the first answer to a slash command or a click).</summary>
    public static Task TextModalAsync(AdminCall call, AdminDraft draft, string action, string title, string label, string field,
        string? value, int maxLength, bool required = true, string? placeholder = null)
    {
        var input = new TextInputBuilder().WithCustomId(field).WithStyle(TextInputStyle.Short).WithMaxLength(maxLength).WithRequired(required);
        if (placeholder is not null)
            input.WithPlaceholder(Cut(placeholder, 100));
        if (!string.IsNullOrEmpty(value) && value.Length <= maxLength)
            input.WithValue(value);
        return call.Respond.ModalAsync(new ModalBuilder().WithTitle(Cut(title, 45)).WithCustomId(AdminCall.CustomId(draft, action))
            .AddLabel(Cut(label, 45), input).Build());
    }

    /// <summary>
    /// A modal listing on/off switches (a multi select, every current value pre-selected). <see cref="ChangedSwitches"/> later
    /// compares the submitted selection with the snapshot taken here, so only switches the admin actually flipped are written:
    /// untouched ones keep whatever is stored then (also a change another admin made meanwhile).
    /// </summary>
    public static Task SwitchesModalAsync(AdminCall call, AdminDraft draft, string action, string title, string label,
        IReadOnlyList<(string Name, string Label, bool On)> switches)
    {
        var select = new SelectMenuBuilder().WithCustomId(SwitchesField).WithMinValues(0).WithMaxValues(switches.Count).WithRequired(false)
            .WithPlaceholder(call.T("admin.form.switches_placeholder"));
        foreach (var (name, text, on) in switches)
            select.AddOption(Cut(text, 100), name, isDefault: on);
        return call.Respond.ModalAsync(new ModalBuilder().WithTitle(Cut(title, 45)).WithCustomId(AdminCall.CustomId(draft, action))
            .AddLabel(Cut(label, 45), select, Cut(call.T("admin.form.switches_hint"), 100)).Build());
    }

    public const string SwitchesField = "on";

    /// <summary>Switch name → new value, only for switches whose submitted state differs from <paramref name="snapshot"/>.</summary>
    public static IReadOnlyDictionary<string, bool> ChangedSwitches(AdminCall call, IReadOnlyDictionary<string, bool> snapshot)
    {
        var on = call.Input.Selected(SwitchesField).ToHashSet(StringComparer.Ordinal);
        return snapshot.Where(s => on.Contains(s.Key) != s.Value).ToDictionary(s => s.Key, s => !s.Value);
    }

    /// <summary>
    /// A modal for an optional notification role: a native role select (pre-filled), ping switches (pre-ticked) and an explicit
    /// "remove the role" box — an empty role select alone keeps the current role.
    /// </summary>
    public static Task RoleModalAsync(AdminCall call, AdminDraft draft, string action, string title, ulong? role,
        IReadOnlyList<(string Name, string Label, bool On)> pings)
    {
        var select = new SelectMenuBuilder().WithType(ComponentType.RoleSelect).WithCustomId(RoleField).WithMinValues(0).WithMaxValues(1).WithRequired(false)
            .WithPlaceholder(call.T("admin.form.role_placeholder"));
        if (role is { } id)
            select.WithDefaultValues(new SelectMenuDefaultValue(id, SelectDefaultValueType.Role));
        var boxes = new CheckboxGroupBuilder().WithCustomId(SwitchesField).WithMinValues(0).WithMaxValues(pings.Count).WithRequired(false);
        foreach (var (name, text, on) in pings)
            boxes.AddOption(Cut(text, 100), name, null, on);
        var clear = new CheckboxGroupBuilder().WithCustomId(ClearField).WithMinValues(0).WithMaxValues(1).WithRequired(false)
            .AddOption(Cut(call.T("admin.form.role_clear"), 100), ClearField, null, false);
        return call.Respond.ModalAsync(new ModalBuilder().WithTitle(Cut(title, 45)).WithCustomId(AdminCall.CustomId(draft, action))
            .AddLabel(Cut(call.T("admin.form.role_label"), 45), select, Cut(call.T("admin.form.role_hint"), 100))
            .AddLabel(Cut(call.T("admin.form.pings_label"), 45), boxes)
            .AddLabel(Cut(call.T("admin.form.role_clear_label"), 45), clear)
            .Build());
    }

    public const string RoleField = "role";
    public const string ClearField = "clear";

    /// <summary>The role submitted in <see cref="RoleModalAsync"/>: null keeps the current one unless "remove" was ticked.</summary>
    public static (bool Clear, ulong? Role) SubmittedRole(AdminCall call) =>
        (call.Input.Selected(ClearField).Contains(ClearField), call.Input.Id(RoleField));

    /// <summary>Claims the form for its final step; a second (double) click is told the form is already done.</summary>
    public static async Task<bool> ClaimAsync(AdminCall call)
    {
        if (call.TakeDraft() is not null)
            return true;
        await call.Respond.SendAsync(call.T("admin.form.done"));
        return false;
    }

    public static string YesNo(AdminCall call, bool value) => call.T(value ? "common.yes" : "common.no");

    public static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    public static string Mention(ulong channel) => "<#" + channel.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">";

    public static RoleId Role(ulong id) => new(id);
}
