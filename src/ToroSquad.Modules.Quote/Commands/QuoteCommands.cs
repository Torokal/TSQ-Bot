using Discord;
using Discord.Interactions;
using Discord.Net;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Quote.Application;

namespace ToroSquad.Modules.Quote.Commands;

/// <summary>
/// Two ways to one quote card (quote.png) posted in this channel:
/// <list type="bullet">
/// <item>Apps → Quote (MESSAGE command, message → right click / long press): the message comes in the interaction payload;
/// nothing is read from Discord.</item>
/// <item>/quote message:&lt;message id&gt; [channel] (a message link also works): a bare id is looked up in this channel, or in
/// the channel option; never searched for.</item>
/// </list>
/// Both are acknowledged privately at once (the avatar download and drawing can take longer than Discord's 3 seconds), share
/// the same checks' tail, card builder and posting; every refusal stays private; only the finished card is public, as an
/// attachment without text. Nothing here ever pings, and nothing of the message (text, names, avatar) is stored or logged.
/// </summary>
[ToroModule(QuoteModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class QuoteCommands(
    InteractionServices services,
    QuoteMessageResolver resolver,
    QuoteCardBuilder cards,
    ILogger<QuoteCommands> logger) : ToroInteractionModule(services)
{
    public const string FileName = "quote.png";

    /// <summary>The MESSAGE command's name as shown under Apps (message commands may be mixed case).</summary>
    public const string MessageCommandName = "Quote";

    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [SlashCommand("quote", "Turn a message into a black-and-white quote card")]
    public async Task QuoteAsync(
        [Summary("message", "Message ID (Copy Message ID) or message link")] string message,
        [Summary("channel", "Channel of the message when you give a bare ID (default: this channel)")]
        [ChannelTypes(ChannelType.Text, ChannelType.News, ChannelType.Voice, ChannelType.Stage, ChannelType.PublicThread, ChannelType.NewsThread)]
        IChannel? channel = null)
    {
        await DeferEphemeralAsync();
        var settings = await SettingsAsync();
        var request = new QuoteRequest(Actor.GuildId, Actor.UserId, Here, message, channel is null ? null : new ChannelId(channel.Id));
        var resolution = await resolver.ResolveAsync(new DiscordQuoteSource(Context.Client, (IGuildUser)Context.User), request,
            Languages.Culture(settings.Language), Zone(settings), CancellationToken.None);
        await PostAsync(resolution, settings);
    }

    /// <summary>
    /// Apps → Quote. Discord.Net hands over the message from the interaction's resolved data; it is mapped as delivered — no
    /// REST read of the message (its content is always included for the message a context-menu command is used on).
    /// </summary>
    [MessageCommand(MessageCommandName)]
    public async Task QuoteMessageAsync(IMessage message)
    {
        await DeferEphemeralAsync();
        var settings = await SettingsAsync();
        var source = new DiscordQuoteSource(Context.Client, (IGuildUser)Context.User);
        var selected = await source.DescribeSelectedAsync(Actor.GuildId, message, CancellationToken.None);
        var resolution = await resolver.ResolveSelectedAsync(source, new QuoteSelection(Actor.GuildId, Actor.UserId, Here, selected),
            Languages.Culture(settings.Language), Zone(settings), CancellationToken.None);
        await PostAsync(resolution, settings);
    }

    private ChannelId Here => new(Context.Interaction.ChannelId ?? Context.Channel.Id);

    private static TimeZoneInfo Zone(GuildSettings settings) => GuildTime.TryResolve(settings.TimeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>The one way out for both commands: a private refusal, or the public card.</summary>
    private async Task PostAsync(QuoteResolution resolution, GuildSettings settings)
    {
        if (resolution is not { Succeeded: true, Message: { } source })
        {
            await ReplyTextAsync(resolution.MessageKey);
            return;
        }

        var built = await cards.BuildAsync(source, resolution.Text, CancellationToken.None);
        if (built is not { Card: { } card })
        {
            await SendEphemeralAsync(await T("error.internal") + "\n" + await T("error.trace_code", built.TraceCode), null, null);
            return;
        }

        // The deferred reply is private. Settle it first so the card below is a separate, public message (the first follow-up
        // of a deferred reply would otherwise take the deferred reply's place — and its privacy).
        await Context.Interaction.ModifyOriginalResponseAsync(m =>
        {
            m.Content = Localizer.Get(settings.Language, "quote.posting");
            m.AllowedMentions = NoPings;
        });
        try
        {
            using var png = new MemoryStream(card.Png, writable: false);
            await FollowupWithFileAsync(png, FileName, ephemeral: false, allowedMentions: NoPings);
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Quote card not posted in guild {Guild} channel {Channel}: Discord answered {Status}",
                Actor.GuildId, Here, ex.HttpCode);
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = Localizer.Get(settings.Language, "quote.post_failed");
                m.AllowedMentions = NoPings;
            });
            return;
        }

        try
        {
            await Context.Interaction.DeleteOriginalResponseAsync(); // the card is the answer; the private note is no longer needed
        }
        catch (HttpException ex)
        {
            logger.LogDebug(ex, "Quote: the private acknowledgement could not be removed");
        }
    }
}
