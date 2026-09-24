using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Transport;

namespace ToroSquad.Discord.Interactions;

/// <summary>Services every interaction module needs (constructor-injected as one bundle).</summary>
public sealed record InteractionServices(ILocalizer Localizer, IGuildSettingsStore GuildSettings, TimeProvider Clock);

/// <summary>
/// Base class for all ToroSquad slash/component handlers: localized, ephemeral-by-default replies that never ping,
/// and a server-side <see cref="ActorContext"/>.
/// </summary>
public abstract class ToroInteractionModule(InteractionServices services) : InteractionModuleBase<SocketInteractionContext>
{
    public const uint BrandColor = 0xE8590C;
    public const uint NeutralColor = 0x5865F2;
    public const uint WarningColor = 0xF59F00;

    private GuildSettings? _settings;

    protected InteractionServices Services => services;
    protected ILocalizer Localizer => services.Localizer;

    /// <summary>The caller, verified from the interaction payload. Guild context is enforced by <see cref="ToroModuleAttribute"/>.</summary>
    protected ActorContext Actor => ActorFactory.From(Context) ?? throw new InvalidOperationException("Guild context required.");

    protected async Task<GuildSettings> SettingsAsync()
    {
        if (_settings is null)
        {
            _settings = Context.Guild is null
                ? GuildSettings.Default(new GuildId(0))
                : await services.GuildSettings.GetAsync(new GuildId(Context.Guild.Id), CancellationToken.None);
        }

        return _settings;
    }

    protected async Task<string> LangAsync() => (await SettingsAsync()).Language;

    protected async Task<string> T(string key, params object?[] args) => Localizer.Get(await LangAsync(), key, args);

    /// <summary>Acknowledge within Discord's 3-second window; follow-ups are then sent via the interaction token.</summary>
    protected Task DeferEphemeralAsync() => Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync(ephemeral: true);

    protected async Task ReplyResultAsync(OperationResult result)
    {
        var text = await T(result.MessageKey, result.Args.ToArray());
        if (!result.Succeeded && result.TraceCode is not null)
            text += "\n" + await T("error.trace_code", result.TraceCode);
        await SendEphemeralAsync(text, null, null);
    }

    protected async Task ReplyTextAsync(string key, params object?[] args) =>
        await SendEphemeralAsync(await T(key, args), null, null);

    protected Task ReplyEmbedAsync(MessageEmbed embed, MessageComponent? components = null, bool ephemeral = true) =>
        SendAsync(null, DiscordConversions.ToEmbed(embed), components, ephemeral);

    protected Task SendEphemeralAsync(string? text, Embed? embed, MessageComponent? components) =>
        SendAsync(text, embed, components, ephemeral: true);

    protected async Task SendAsync(string? text, Embed? embed, MessageComponent? components, bool ephemeral)
    {
        var none = DiscordConversions.ToAllowedMentions(MentionPolicy.None);
        if (Context.Interaction.HasResponded)
            await FollowupAsync(text, embed: embed, components: components, ephemeral: ephemeral, allowedMentions: none);
        else
            await RespondAsync(text, embed: embed, components: components, ephemeral: ephemeral, allowedMentions: none);
    }

    protected static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
