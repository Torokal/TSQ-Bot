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
/// /quote message:&lt;message id&gt; [channel] (a message link also works) — posts the message as a black-and-white quote
/// card (quote.png) in this channel. A bare id is looked up in this channel, or in the channel option; never searched for.
/// Acknowledged privately at once (the avatar download and drawing can take longer than Discord's 3 seconds); every refusal
/// stays private; only the finished card is public, as an attachment without text. Nothing here ever pings, and nothing
/// of the message (text, names, avatar) is stored or logged.
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
        var zone = GuildTime.TryResolve(settings.TimeZoneId, out var resolved) ? resolved : TimeZoneInfo.Utc;
        var request = new QuoteRequest(Actor.GuildId, Actor.UserId, new ChannelId(Context.Interaction.ChannelId ?? Context.Channel.Id), message,
            channel is null ? null : new ChannelId(channel.Id));

        var resolution = await resolver.ResolveAsync(new DiscordQuoteSource(Context.Client, (IGuildUser)Context.User), request,
            Languages.Culture(settings.Language), zone, CancellationToken.None);
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
                Actor.GuildId, request.InvokedIn, ex.HttpCode);
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
