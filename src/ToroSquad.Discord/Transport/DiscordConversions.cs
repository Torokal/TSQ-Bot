using Discord;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Discord.Transport;

/// <summary>Maps SDK-agnostic message models to Discord.Net types. The only place allowed_mentions is built.</summary>
public static class DiscordConversions
{
    /// <summary>
    /// allowed_mentions = { parse: [], roles: [explicitly permitted role ids], users: [explicitly listed user ids] }. @here
    /// can never ping from automated/bot messages; @everyone only when the policy explicitly opts in (TSQ Live's first
    /// announcement); users only when listed (TSQ LFG event notices, TSQ Birthday announcement). Edits always pass <see cref="MentionPolicy.None"/>.
    /// </summary>
    public static AllowedMentions ToAllowedMentions(MentionPolicy policy)
    {
        var allowed = new AllowedMentions(policy.Everyone ? AllowedMentionTypes.Everyone : AllowedMentionTypes.None)
        {
            MentionRepliedUser = false,
        };
        if (policy.Roles.Count > 0)
            allowed.RoleIds = policy.Roles.Select(r => r.Value).Distinct().ToList();
        // Users are never parsed from the text: only the explicitly listed ids (TSQ LFG notices, TSQ Birthday announcement) can ping.
        if (policy.Users is { Count: > 0 } users)
            allowed.UserIds = users.Select(u => u.Value).Distinct().ToList();
        return allowed;
    }

    public static Embed? ToEmbed(MessageEmbed? model)
    {
        if (model is null)
            return null;
        var builder = new EmbedBuilder();
        if (!string.IsNullOrEmpty(model.Title))
            builder.WithTitle(model.Title);
        if (!string.IsNullOrEmpty(model.Description))
            builder.WithDescription(model.Description);
        if (!string.IsNullOrEmpty(model.Url))
            builder.WithUrl(model.Url);
        foreach (var field in model.Fields)
            builder.AddField(field.Name, field.Value, field.Inline);
        if (!string.IsNullOrEmpty(model.Footer))
            builder.WithFooter(model.Footer);
        if (model.Timestamp is { } ts)
            builder.WithTimestamp(ts);
        if (model.Color is { } color)
            builder.WithColor(new Color(color));
        if (!string.IsNullOrEmpty(model.ThumbnailUrl))
            builder.WithThumbnailUrl(model.ThumbnailUrl);
        return builder.Build();
    }

    /// <summary>Discord allows five rows of five buttons.</summary>
    public const int ButtonsPerRow = 5;

    public const int MaxRows = 5;

    /// <summary>The message's select menu (its own first row, when present) and its buttons below it.</summary>
    public static MessageComponent? ToComponents(OutgoingMessage message)
    {
        if (message.Select is not { } select)
            return ToComponents(message.Buttons);
        var builder = new ComponentBuilder();
        var menu = new SelectMenuBuilder()
            .WithCustomId(select.CustomId)
            .WithMinValues(1)
            .WithMaxValues(1)
            .WithDisabled(select.Disabled);
        if (!string.IsNullOrEmpty(select.Placeholder))
            menu.WithPlaceholder(select.Placeholder);
        foreach (var option in select.Options.Take(MessageSelectMenu.MaxOptions))
            menu.AddOption(option.Label, option.Value, option.Description);
        builder.WithSelectMenu(menu, 0);
        AddButtons(builder, message.Buttons, firstRow: 1);
        return builder.Build();
    }

    public static MessageComponent? ToComponents(IReadOnlyList<MessageButton>? buttons)
    {
        if (buttons is null || buttons.Count == 0)
            return null;
        var builder = new ComponentBuilder();
        AddButtons(builder, buttons, firstRow: 0);
        return builder.Build();
    }

    private static void AddButtons(ComponentBuilder builder, IReadOnlyList<MessageButton>? buttons, int firstRow)
    {
        if (buttons is null)
            return;
        var row = firstRow;
        var inRow = 0;
        foreach (var b in buttons)
        {
            if (inRow > 0 && (inRow == ButtonsPerRow || b.NewRow))
            {
                row++;
                inRow = 0;
            }

            if (row == MaxRows)
                break;
            inRow++;
            if (b.Url is not null)
                builder.WithButton(b.Label, url: b.Url, style: ButtonStyle.Link, disabled: b.Disabled, row: row);
            else
                builder.WithButton(b.Label, b.CustomId, ToStyle(b.Style), disabled: b.Disabled, row: row);
        }
    }

    private static ButtonStyle ToStyle(MessageButtonStyle style) => style switch
    {
        MessageButtonStyle.Primary => ButtonStyle.Primary,
        MessageButtonStyle.Success => ButtonStyle.Success,
        MessageButtonStyle.Danger => ButtonStyle.Danger,
        _ => ButtonStyle.Secondary,
    };
}
