using System.Collections.Concurrent;
using System.Security.Cryptography;
using ToroSquad.Core;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>What a pending step remembers between two interactions (never stored in the database).</summary>
public abstract record PendingStep;

/// <summary>The creation form between modal, preview and publish; bound to the channel and the tournament it was opened in.</summary>
public sealed record FormDraftStep(ChannelId Channel, long TournamentId, PredictionFormValues Values) : PendingStep;

public sealed record CancelStep(long PredictionId, string Reason) : PendingStep;

/// <summary>Ending exactly this tournament (never whichever one is active when the button is clicked).</summary>
public sealed record TournamentEndStep(long TournamentId) : PendingStep;

public sealed record PendingToken(string Id, GuildId Guild, UserId User, PendingStep Step, DateTimeOffset TouchedAt, TimeSpan Lifetime);

/// <summary>
/// Short-lived, in-memory state between the steps of a flow (form → preview → publish, cancel reason/end → confirm; entries
/// need none: their form submit is the decision). A token id is 128 random bits and is honoured only for the member and guild that created it — a guessed or
/// copied id is simply unknown. Tokens expire (<see cref="DraftLifetime"/> after the last use for forms,
/// <see cref="ConfirmLifetime"/> for confirmations), are capped per member and in total, and are lost on restart (the
/// member just starts the step again; nothing economic depends on them). <see cref="Take"/> removes a token atomically, so
/// a double click acts once; the database transaction behind every confirmation is the real guard.
/// </summary>
public sealed class PredictionTokens(TimeProvider clock)
{
    public static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ConfirmLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan TournamentEndLifetime = TimeSpan.FromMinutes(5);

    public const int MaxPerUser = 8;
    public const int MaxTotal = 4000;

    private readonly ConcurrentDictionary<string, PendingToken> _tokens = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int Count => _tokens.Count;

    public string Create(ActorContext actor, PendingStep step, TimeSpan lifetime)
    {
        var token = new PendingToken(NewId(), actor.GuildId, actor.UserId, step, clock.GetUtcNow(), lifetime);
        lock (_gate)
        {
            Prune();
            var mine = _tokens.Values.Where(t => t.User == actor.UserId && t.Guild == actor.GuildId).OrderBy(t => t.TouchedAt).ToList();
            foreach (var old in mine.Take(Math.Max(0, mine.Count - MaxPerUser + 1)))
                _tokens.TryRemove(old.Id, out _);
            foreach (var old in _tokens.Values.OrderBy(t => t.TouchedAt).Take(Math.Max(0, _tokens.Count - MaxTotal + 1)).ToList())
                _tokens.TryRemove(old.Id, out _);
            _tokens[token.Id] = token;
        }

        return token.Id;
    }

    /// <summary>The caller's own, unexpired token of this kind; null for anything else.</summary>
    public T? Get<T>(string? id, ActorContext actor) where T : PendingStep
    {
        if (id is null || !_tokens.TryGetValue(id, out var token))
            return null;
        if (clock.GetUtcNow() - token.TouchedAt > token.Lifetime)
        {
            _tokens.TryRemove(new KeyValuePair<string, PendingToken>(id, token));
            return null;
        }

        return token.User == actor.UserId && token.Guild == actor.GuildId ? token.Step as T : null;
    }

    /// <summary>Replaces the step of the caller's token and keeps it alive; false when it is not the caller's valid token.</summary>
    public bool Update<T>(string id, ActorContext actor, Func<T, T> change) where T : PendingStep
    {
        lock (_gate)
        {
            if (Get<T>(id, actor) is not { } step)
                return false;
            _tokens[id] = _tokens[id] with { Step = change(step), TouchedAt = clock.GetUtcNow() };
            return true;
        }
    }

    /// <summary>Removes and returns the caller's token (single use).</summary>
    public T? Take<T>(string? id, ActorContext actor) where T : PendingStep
    {
        lock (_gate)
        {
            if (Get<T>(id, actor) is not { } step)
                return null;
            _tokens.TryRemove(id!, out _);
            return step;
        }
    }

    /// <summary>Puts a taken form draft back (publishing was refused before anything was stored).</summary>
    public void Return(string id, ActorContext actor, PendingStep step, TimeSpan lifetime)
    {
        lock (_gate)
            _tokens[id] = new PendingToken(id, actor.GuildId, actor.UserId, step, clock.GetUtcNow(), lifetime);
    }

    public void Remove(string? id, ActorContext actor)
    {
        lock (_gate)
        {
            if (id is not null && _tokens.TryGetValue(id, out var token) && token.User == actor.UserId && token.Guild == actor.GuildId)
                _tokens.TryRemove(id, out _);
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (id, token) in _tokens)
        {
            if (now - token.TouchedAt > token.Lifetime)
                _tokens.TryRemove(id, out _);
        }
    }

    private static string NewId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
