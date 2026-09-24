using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Discord.Transport;

/// <summary>
/// Real delivery via Discord REST. The client is configured with RetryMode.RetryRatelimit only: Discord.Net honours
/// Retry-After for 429s, but timeouts/502s on message *creation* are NOT retried by the SDK (that could duplicate a
/// message) — they surface here as <see cref="SendOutcome.Ambiguous"/> and go through reconciliation.
/// </summary>
public sealed class DiscordMessageTransport(DiscordSocketClient client, ILogger<DiscordMessageTransport> logger) : IMessageTransport
{
    public string Name => "discord";

    public async Task<SendOutcome> SendAsync(ChannelId channel, OutgoingMessage message, CancellationToken cancellationToken)
    {
        if (client.LoginState != LoginState.LoggedIn)
            return new SendOutcome.Transient("discord client not logged in yet");

        IMessageChannel? target;
        try
        {
            target = await ResolveAsync(channel);
        }
        catch (HttpException ex)
        {
            return Classify(ex, isCreate: false);
        }

        if (target is null)
            return new SendOutcome.Permanent(PermanentFailureKind.UnknownChannel, "channel not found or not a text channel");

        try
        {
            var sent = await target.SendMessageAsync(
                text: message.Content,
                embed: DiscordConversions.ToEmbed(message.Embed),
                allowedMentions: DiscordConversions.ToAllowedMentions(message.Mentions),
                components: DiscordConversions.ToComponents(message.Buttons),
                options: new RequestOptions { CancelToken = cancellationToken });
            return new SendOutcome.Sent(new MessageId(sent.Id));
        }
        catch (HttpException ex)
        {
            return Classify(ex, isCreate: true);
        }
        catch (RateLimitedException ex)
        {
            logger.LogWarning("Discord rate limit on send: {Message}", ex.Message);
            return new SendOutcome.RateLimited(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or HttpRequestException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            // The request may have reached Discord. Never assume it did not.
            return new SendOutcome.Ambiguous(ex.GetType().Name);
        }
    }

    public async Task<SendOutcome> EditAsync(ChannelId channel, MessageId message, OutgoingMessage content, CancellationToken cancellationToken)
    {
        if (client.LoginState != LoginState.LoggedIn)
            return new SendOutcome.Transient("discord client not logged in yet");

        try
        {
            var target = await ResolveAsync(channel);
            if (target is null)
                return new SendOutcome.Permanent(PermanentFailureKind.UnknownChannel, "channel not found");
            await target.ModifyMessageAsync(message.Value, p =>
            {
                p.Content = content.Content ?? string.Empty;
                p.Embed = DiscordConversions.ToEmbed(content.Embed);
                p.AllowedMentions = DiscordConversions.ToAllowedMentions(MentionPolicy.None); // edits never ping
                p.Components = DiscordConversions.ToComponents(content.Buttons);
            }, new RequestOptions { CancelToken = cancellationToken });
            return new SendOutcome.Sent(message);
        }
        catch (HttpException ex)
        {
            return Classify(ex, isCreate: false);
        }
        catch (RateLimitedException)
        {
            return new SendOutcome.RateLimited(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException or HttpRequestException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new SendOutcome.Transient(ex.GetType().Name); // edits are idempotent
        }
    }

    public async Task<ReconcileOutcome> FindRecentByMarkerAsync(ChannelId channel, string marker, int scanLimit, CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveAsync(channel);
            if (target is null)
                return new ReconcileOutcome.NotPossible("channel not found");
            var selfId = client.CurrentUser?.Id ?? 0;
            var needle = "ref " + marker;
            var messages = await target.GetMessagesAsync(Math.Clamp(scanLimit, 1, 100), CacheMode.AllowDownload,
                new RequestOptions { CancelToken = cancellationToken }).FlattenAsync();
            var match = messages.FirstOrDefault(m =>
                m.Author.Id == selfId &&
                m.Embeds.Any(e => e.Footer?.Text?.Contains(needle, StringComparison.Ordinal) == true));
            return match is null ? new ReconcileOutcome.NotFound() : new ReconcileOutcome.Found(new MessageId(match.Id));
        }
        catch (HttpException ex) when (ex.HttpCode is HttpStatusCode.Forbidden)
        {
            return new ReconcileOutcome.NotPossible("missing Read Message History / View Channel");
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return new ReconcileOutcome.NotPossible(ex.GetType().Name);
        }
    }

    private async Task<IMessageChannel?> ResolveAsync(ChannelId channel)
    {
        if (client.GetChannel(channel.Value) is IMessageChannel cached)
            return cached;
        return await client.Rest.GetChannelAsync(channel.Value) as IMessageChannel;
    }

    /// <summary>Maps Discord HTTP errors to delivery outcomes. Public for unit tests.</summary>
    public static SendOutcome Classify(HttpStatusCode status, DiscordErrorCode? code, string reason, bool isCreate)
    {
        switch (status)
        {
            case HttpStatusCode.Forbidden:
                return new SendOutcome.Permanent(code == DiscordErrorCode.MissingPermissions ? PermanentFailureKind.MissingPermissions : PermanentFailureKind.MissingAccess, reason);
            case HttpStatusCode.NotFound:
                return new SendOutcome.Permanent(code == DiscordErrorCode.UnknownMessage ? PermanentFailureKind.UnknownMessage : PermanentFailureKind.UnknownChannel, reason);
            case HttpStatusCode.BadRequest:
                return new SendOutcome.Permanent(PermanentFailureKind.InvalidPayload, reason);
            case HttpStatusCode.TooManyRequests:
                return new SendOutcome.RateLimited(TimeSpan.FromSeconds(5));
            case HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout when isCreate:
                // Discord may have created the message before failing the response.
                return new SendOutcome.Ambiguous($"{(int)status} on create");
            case >= HttpStatusCode.InternalServerError:
                return new SendOutcome.Transient($"{(int)status}");
            default:
                return new SendOutcome.Permanent(PermanentFailureKind.Other, $"{(int)status} {reason}");
        }
    }

    private static SendOutcome Classify(HttpException ex, bool isCreate) =>
        Classify(ex.HttpCode, ex.DiscordCode, ex.Reason ?? ex.Message, isCreate);
}
