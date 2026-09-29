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
                components: DiscordConversions.ToComponents(message),
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
                p.Components = DiscordConversions.ToComponents(content);
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

    public async Task<ReconcileOutcome> FindRecentAsync(ChannelId channel, DeliveryProbe probe, int scanLimit, CancellationToken cancellationToken)
    {
        try
        {
            var target = await ResolveAsync(channel);
            if (target is null)
                return new ReconcileOutcome.NotPossible("channel not found");
            var selfId = client.CurrentUser?.Id ?? 0;
            var messages = await target.GetMessagesAsync(Math.Clamp(scanLimit, 1, 100), CacheMode.AllowDownload,
                new RequestOptions { CancelToken = cancellationToken }).FlattenAsync();
            // Newest first: the most recent own message created after the attempt with exactly the sent content.
            var match = messages
                .Where(m => m.Author.Id == selfId && m.CreatedAt >= probe.NotBefore && !probe.Exclude.Contains(new MessageId(m.Id)))
                .FirstOrDefault(m => Fingerprint(m) == probe.Fingerprint || m.Embeds.Any(e => probe.MatchesLegacyFooter(e.Footer?.Text)));
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

    public async Task<MessagePresence> GetPresenceAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken)
    {
        if (client.LoginState != LoginState.LoggedIn)
            return MessagePresence.Unknown;
        try
        {
            var target = await ResolveAsync(channel);
            if (target is null)
                return MessagePresence.Missing; // channel deleted (or no longer a text channel)
            // GET /channels/{channel}/messages/{message}; Discord.Net answers null for 404 Unknown Message.
            var found = await target.GetMessageAsync(message.Value, CacheMode.AllowDownload, new RequestOptions { CancelToken = cancellationToken });
            if (found is null)
                return MessagePresence.Missing;
            // An id that is not a message of this bot is never ours to edit. The bot's own id is only known after the gateway
            // READY; before that the answer is "cannot tell", never "missing" (found by the offline contract test).
            if (client.CurrentUser?.Id is not { } self)
                return MessagePresence.Unknown;
            return found.Author.Id == self ? MessagePresence.Present : MessagePresence.Missing;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
        {
            return MessagePresence.Missing;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException or RateLimitedException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return MessagePresence.Unknown; // 403 (no Read Message History / access), 5xx, timeouts: never "missing"
        }
    }

    /// <summary>The <see cref="MessageFingerprint"/> of a message as Discord returned it (first embed only; we send one).</summary>
    public static string Fingerprint(IMessage message) => Fingerprint(message.Content, message.Embeds.FirstOrDefault());

    public static string Fingerprint(string? content, IEmbed? embed) =>
        MessageFingerprint.Compute(content, embed?.Title, embed?.Description, embed?.Footer?.Text, embed?.Timestamp,
            embed?.Color?.RawValue, embed?.Fields.Select(f => (f.Name, f.Value)) ?? []);

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
                // Discord 50013 "Missing Permissions" is DiscordErrorCode.InsufficientPermissions in Discord.Net; its "MissingPermissions"
                // is Discord 50001 "Missing Access" (the names were swapped here before; found by DiscordEditRouteContractTests).
                return new SendOutcome.Permanent(code == DiscordErrorCode.InsufficientPermissions ? PermanentFailureKind.MissingPermissions : PermanentFailureKind.MissingAccess, reason);
            case HttpStatusCode.NotFound:
                return new SendOutcome.Permanent(code == DiscordErrorCode.UnknownMessage ? PermanentFailureKind.UnknownMessage : PermanentFailureKind.UnknownChannel, reason);
            case HttpStatusCode.BadRequest:
                return new SendOutcome.Permanent(PermanentFailureKind.InvalidPayload, reason);
            case HttpStatusCode.TooManyRequests:
                return new SendOutcome.RateLimited(TimeSpan.FromSeconds(5));
            case >= HttpStatusCode.InternalServerError when isCreate:
                // Discord may have created the message before failing the response (any 5xx: none proves it did not).
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
