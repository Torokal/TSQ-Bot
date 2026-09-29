using System.Collections.Concurrent;
using ToroSquad.Core;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>One user behind a 🎉 reaction, as Discord listed them.</summary>
public sealed record ReactionUser(UserId User, bool IsBot);

/// <summary>
/// The 🎉 reactions of a card. <see cref="Read"/> is the COMPLETE list (every page); <see cref="Missing"/>: the message or
/// its channel is gone; <see cref="Unavailable"/>: could not be read now (no access, rate limit, 5xx, not connected) —
/// never "no entrants", and never a partial list.
/// </summary>
public abstract record EntrantRead
{
    public sealed record Read(IReadOnlyList<ReactionUser> Users) : EntrantRead;
    public sealed record Missing(string Reason) : EntrantRead;
    public sealed record Unavailable(string Reason) : EntrantRead;
}

public enum ReactionAddOutcome
{
    Added = 0,
    Failed = 1,
}

/// <summary>
/// The reaction side of a giveaway, without the Discord SDK: the bot's own 🎉 on a new card, and reading who reacted with
/// 🎉 when the giveaway is drawn. Implementations: <c>DiscordGiveawayReactions</c> (REST, all pages) and
/// <see cref="OfflineGiveawayReactions"/> (Fake transport: development and tests).
/// </summary>
public interface IGiveawayReactions
{
    Task<ReactionAddOutcome> AddEntryReactionAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken);

    Task<EntrantRead> ReadEntrantsAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken);
}

/// <summary>Who may win: every user who reacted with 🎉, once, bots excluded (the bot's own 🎉 included).</summary>
public static class GiveawayEntrants
{
    public static IReadOnlyList<UserId> Valid(IEnumerable<ReactionUser> users) =>
        users.Where(u => !u.IsBot).Select(u => u.User).Distinct().ToList();
}

/// <summary>In-memory reactions for the Fake transport (local development and tests). Nothing leaves the machine.</summary>
public sealed class OfflineGiveawayReactions : IGiveawayReactions
{
    private readonly ConcurrentDictionary<ulong, List<ReactionUser>> _reactions = new();
    private readonly ConcurrentDictionary<ulong, bool> _missing = new();

    /// <summary>Outcomes answered before the stored reactions (e.g. <see cref="EntrantRead.Unavailable"/>).</summary>
    public ConcurrentQueue<EntrantRead> ScriptedReads { get; } = new();

    public ConcurrentQueue<ReactionAddOutcome> ScriptedAdds { get; } = new();

    public int Reads { get; private set; }

    public ConcurrentBag<MessageId> BotReactions { get; } = [];

    public void React(MessageId message, UserId user, bool isBot = false)
    {
        var list = _reactions.GetOrAdd(message.Value, _ => []);
        lock (list)
        {
            if (!list.Any(u => u.User == user))
                list.Add(new ReactionUser(user, isBot));
        }
    }

    public void Unreact(MessageId message, UserId user)
    {
        if (_reactions.TryGetValue(message.Value, out var list))
        {
            lock (list)
                list.RemoveAll(u => u.User == user);
        }
    }

    /// <summary>The card (or its channel) was deleted in Discord.</summary>
    public void Delete(MessageId message) => _missing[message.Value] = true;

    public Task<ReactionAddOutcome> AddEntryReactionAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken)
    {
        if (ScriptedAdds.TryDequeue(out var scripted) && scripted != ReactionAddOutcome.Added)
            return Task.FromResult(scripted);
        BotReactions.Add(message);
        React(message, new UserId(0), isBot: true); // the bot's own 🎉, as Discord lists it
        return Task.FromResult(ReactionAddOutcome.Added);
    }

    public Task<EntrantRead> ReadEntrantsAsync(ChannelId channel, MessageId message, CancellationToken cancellationToken)
    {
        Reads++;
        if (ScriptedReads.TryDequeue(out var scripted))
            return Task.FromResult(scripted);
        if (_missing.ContainsKey(message.Value))
            return Task.FromResult<EntrantRead>(new EntrantRead.Missing("Unknown Message"));
        if (!_reactions.TryGetValue(message.Value, out var list))
            return Task.FromResult<EntrantRead>(new EntrantRead.Read([]));
        lock (list)
            return Task.FromResult<EntrantRead>(new EntrantRead.Read(list.ToList()));
    }
}
