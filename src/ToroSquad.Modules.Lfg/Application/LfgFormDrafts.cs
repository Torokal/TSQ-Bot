using System.Collections.Concurrent;
using System.Security.Cryptography;
using ToroSquad.Core;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Lfg.Application;

public enum LfgFormKind
{
    Create = 0,
    Edit = 1,
}

/// <summary>
/// A form between its steps (modal → settings → save): who opened it, where, for which listing (edit), what was typed and
/// the chosen settings. Never stored in the database: a half-filled form is not a listing.
/// </summary>
public sealed record LfgFormDraft(
    string Id,
    GuildId Guild,
    ChannelId Channel,
    UserId User,
    LfgFormKind Kind,
    long? ListingId,
    LfgFormValues Values,
    bool NotifyBeforeStart,
    bool NotifyAtStart,
    ChannelId? VoiceChannel,
    DateTimeOffset TouchedAt,
    LfgFormPreview? Preview = null,
    LfgFormValues? Opened = null,
    LfgFormSettings? OpenedSettings = null)
{
    public LfgCreateInput ToCreateInput() => LfgForm.ToCreateInput(Values, NotifyBeforeStart, NotifyAtStart, VoiceChannel);

    public LfgEditInput ToEditInput() => new(Values, NotifyBeforeStart, NotifyAtStart, VoiceChannel, Opened, OpenedSettings);
}

/// <summary>
/// Short-lived, in-memory form drafts (single bot instance). A draft id is 128 random bits and is only ever honoured for the
/// user and guild that opened it — a guessed or copied id from someone else is simply unknown. Drafts expire
/// <see cref="Lifetime"/> after their last use and are lost on restart (the user just opens the form again); per user and in
/// total they are capped so nobody can grow the memory. Taking a draft to save it removes it, so a double click saves once.
/// </summary>
public sealed class LfgFormDrafts(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    public const int MaxPerUser = 3;
    public const int MaxTotal = 2000;

    private readonly ConcurrentDictionary<string, LfgFormDraft> _drafts = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public int Count => _drafts.Count;

    /// <summary>
    /// A new draft; for an edit, <paramref name="values"/> and <paramref name="stored"/> are also kept as the listing the
    /// form found (to tell untouched fields from edited ones on save).
    /// </summary>
    public LfgFormDraft Open(ActorContext actor, ChannelId channel, LfgFormKind kind, long? listingId, LfgFormValues values,
        bool notifyBeforeStart = false, bool notifyAtStart = false, ChannelId? voice = null, LfgFormSettings? stored = null)
    {
        var draft = new LfgFormDraft(NewId(), actor.GuildId, channel, actor.UserId, kind, listingId, values, notifyBeforeStart, notifyAtStart, voice,
            clock.GetUtcNow(), Opened: kind == LfgFormKind.Edit ? values : null, OpenedSettings: kind == LfgFormKind.Edit ? stored : null);
        lock (_gate)
        {
            Prune();
            var mine = _drafts.Values.Where(d => d.User == actor.UserId).OrderBy(d => d.TouchedAt).ToList();
            foreach (var old in mine.Take(Math.Max(0, mine.Count - MaxPerUser + 1)))
                _drafts.TryRemove(old.Id, out _);
            foreach (var old in _drafts.Values.OrderBy(d => d.TouchedAt).Take(Math.Max(0, _drafts.Count - MaxTotal + 1)).ToList())
                _drafts.TryRemove(old.Id, out _);
            _drafts[draft.Id] = draft;
        }

        return draft;
    }

    /// <summary>The caller's own, still valid draft; null for an unknown, expired or someone else's id.</summary>
    public LfgFormDraft? Get(string id, ActorContext actor)
    {
        if (!_drafts.TryGetValue(id, out var draft))
            return null;
        if (clock.GetUtcNow() - draft.TouchedAt > Lifetime)
        {
            _drafts.TryRemove(new KeyValuePair<string, LfgFormDraft>(id, draft));
            return null;
        }

        return draft.User == actor.UserId && draft.Guild == actor.GuildId ? draft : null;
    }

    /// <summary>Changes the caller's draft (and keeps it alive); null when it is not the caller's valid draft.</summary>
    public LfgFormDraft? Update(string id, ActorContext actor, Func<LfgFormDraft, LfgFormDraft> change)
    {
        lock (_gate)
        {
            if (Get(id, actor) is not { } draft)
                return null;
            var updated = change(draft) with
            {
                Id = draft.Id,
                Guild = draft.Guild,
                Channel = draft.Channel,
                User = draft.User,
                Kind = draft.Kind,
                ListingId = draft.ListingId,
                Opened = draft.Opened,
                OpenedSettings = draft.OpenedSettings,
                TouchedAt = clock.GetUtcNow(),
            };
            _drafts[id] = updated;
            return updated;
        }
    }

    /// <summary>Removes and returns the caller's draft (to save it once). <see cref="Return"/> puts it back when saving is refused.</summary>
    public LfgFormDraft? Take(string id, ActorContext actor)
    {
        lock (_gate)
        {
            if (Get(id, actor) is not { } draft)
                return null;
            _drafts.TryRemove(id, out _);
            return draft;
        }
    }

    public void Return(LfgFormDraft draft)
    {
        lock (_gate)
            _drafts[draft.Id] = draft with { TouchedAt = clock.GetUtcNow() };
    }

    public void Remove(string id, ActorContext actor)
    {
        lock (_gate)
        {
            if (Get(id, actor) is not null)
                _drafts.TryRemove(id, out _);
        }
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (id, draft) in _drafts)
        {
            if (now - draft.TouchedAt > Lifetime)
                _drafts.TryRemove(id, out _);
        }
    }

    private static string NewId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
