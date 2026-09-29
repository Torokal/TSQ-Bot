using System.Net;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Domain;

namespace ToroSquad.Modules.Giveaway.Commands;

/// <summary>
/// The 🎉 reactions over Discord REST with the bot's own credentials (Guilds intent only: no reaction events are received or
/// needed — the reactions are read once, when the giveaway is drawn). Reading lists EVERY user: Discord returns at most 100
/// per request, and Discord.Net's paged enumerable asks again with <c>after</c> = the highest user id so far until a short
/// page (DiscordReactionContractTests pins the requests). Any failure on any page makes the whole read
/// <see cref="EntrantRead.Unavailable"/> — a partial list would silently exclude entrants.
/// </summary>
public sealed class DiscordGiveawayReactions(DiscordSocketClient client) : IGiveawayReactions
{
    /// <summary>Safety bound for one read (500 requests); far above any single-server giveaway.</summary>
    public const int MaxEntrants = 50_000;

    private static readonly Emoji Entry = new(GiveawayRules.EntryEmoji);

    public async Task<ReactionAddOutcome> AddEntryReactionAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken)
    {
        if (client.LoginState != LoginState.LoggedIn)
            return ReactionAddOutcome.Failed;
        try
        {
            var options = new RequestOptions { CancelToken = cancellationToken };
            if (await ResolveAsync(channel) is not { } target || await target.GetMessageAsync(message.Value, CacheMode.AllowDownload, options) is not { } card)
                return ReactionAddOutcome.Failed;
            await card.AddReactionAsync(Entry, options);
            return ReactionAddOutcome.Added;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException or RateLimitedException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            return ReactionAddOutcome.Failed; // e.g. no Add Reactions: members can still add 🎉 themselves
        }
    }

    public async Task<EntrantRead> ReadEntrantsAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken)
    {
        if (client.LoginState != LoginState.LoggedIn)
            return new EntrantRead.Unavailable("discord client not logged in yet");
        try
        {
            var options = new RequestOptions { CancelToken = cancellationToken };
            if (await ResolveAsync(channel) is not { } target)
                return new EntrantRead.Missing("channel not found");
            // GET /channels/{channel}/messages/{message}: Discord.Net answers null for 404 Unknown Message.
            if (await target.GetMessageAsync(message.Value, CacheMode.AllowDownload, options) is not { } card)
                return new EntrantRead.Missing("message not found");

            var users = new List<ReactionUser>();
            await foreach (var page in card.GetReactionUsersAsync(Entry, MaxEntrants, options).WithCancellation(cancellationToken))
                users.AddRange(page.Select(u => new ReactionUser(new UserId(u.Id), u.IsBot)));
            return new EntrantRead.Read(users);
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound)
        {
            return new EntrantRead.Missing(ex.DiscordCode?.ToString() ?? "404");
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException or RateLimitedException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw;
            // 403 (no Read Message History / access), 5xx, timeouts, rate limits: cannot tell now — never "no entrants".
            return new EntrantRead.Unavailable(ex is HttpException http ? ((int)http.HttpCode).ToString(System.Globalization.CultureInfo.InvariantCulture) : ex.GetType().Name);
        }
    }

    private async Task<IMessageChannel?> ResolveAsync(ChannelId channel)
    {
        if (client.GetChannel(channel.Value) is IMessageChannel cached)
            return cached;
        return await client.Rest.GetChannelAsync(channel.Value) as IMessageChannel;
    }
}
