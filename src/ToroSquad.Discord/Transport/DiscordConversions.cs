using Discord;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Discord.Transport;

/// <summary>Maps SDK-agnostic message models to Discord.Net types. The only place allowed_mentions is built.</summary>
public static class DiscordConversions
{
    /// <summary>
    /// allowed_mentions = { parse: [], roles: [explicitly permitted role ids] }. Users, @everyone and @here can never
    /// ping from automated/bot messages.
    /// </summary>
    public static AllowedMentions ToAllowedMentions(MentionPolicy policy)
    {
        var allowed = new AllowedMentions(AllowedMentionTypes.None)
        {
            MentionRepliedUser = false,
        };
        if (policy.Roles.Count > 0)
            allowed.RoleIds = policy.Roles.Select(r => r.Value).Distinct().ToList();
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
        return builder.Build();
    }

    public static MessageComponent? ToComponents(IReadOnlyList<MessageButton>? buttons)
    {
        if (buttons is null || buttons.Count == 0)
            return null;
        var builder = new ComponentBuilder();
        var row = 0;
        for (var i = 0; i < buttons.Count && i < 25; i++)
        {
            var b = buttons[i];
            if (i > 0 && i % 5 == 0)
                row++;
            if (b.Url is not null)
                builder.WithButton(b.Label, url: b.Url, style: ButtonStyle.Link, disabled: b.Disabled, row: row);
            else
                builder.WithButton(b.Label, b.CustomId, ButtonStyle.Secondary, disabled: b.Disabled, row: row);
        }

        return builder.Build();
    }
}
