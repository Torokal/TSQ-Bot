using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>What one finalization did to the asker's notes (counted for the footer the application writes).</summary>
public sealed record SorMemoryOutcome(int Added, int Updated, int Forgotten, bool ForgetAmbiguous, bool Full, int Refused)
{
    public static SorMemoryOutcome None { get; } = new(0, 0, 0, false, false, 0);

    public bool Changed => Added + Updated + Forgotten > 0;
}

/// <summary>
/// The note and conversation primitives every path shares (/sor finalization, /hafizam, /privacy). Every query is filtered by
/// guild AND owner; scopes are applied in the queries (a note that may not be used is never loaded). The callers run the
/// writing methods inside <see cref="SorWrites.RunAsync{T}"/>.
/// </summary>
public sealed class SorMemoryStore(ToroDbContext db, IOptions<SorOptions> options, TimeProvider clock)
{
    private IQueryable<SorMemoryEntity> Notes(ulong guild, ulong user) =>
        db.Set<SorMemoryEntity>().Where(m => m.GuildId == guild && m.UserId == user);

    /// <summary>
    /// The asker's own notes allowed in this answer. Private answer: every own note (private, channel and shared — the member
    /// is the only reader). Channel answer: the notes learned in THIS channel or thread, and the shared ones; never a private
    /// note or a note from another channel.
    /// </summary>
    public async Task<IReadOnlyList<SorNote>> OwnNotesForAsync(ulong guild, ulong user, bool privateAnswer, ulong channel, CancellationToken ct)
    {
        var query = Notes(guild, user).AsNoTracking();
        if (!privateAnswer)
            query = query.Where(m => m.Scope == SorMemoryScope.Shared || (m.Scope == SorMemoryScope.Channel && m.SourceChannelId == channel));
        return (await query.OrderBy(m => m.Id).ToListAsync(ct)).Select(ToNote).ToList();
    }

    /// <summary>Another member's notes they explicitly shared (only Shared scope; the caller checks their memory is on).</summary>
    public async Task<IReadOnlyList<SorNote>> SharedNotesOfAsync(ulong guild, ulong user, CancellationToken ct) =>
        (await Notes(guild, user).AsNoTracking().Where(m => m.Scope == SorMemoryScope.Shared).OrderBy(m => m.Id).ToListAsync(ct))
        .Select(ToNote).ToList();

    public static SorNote ToNote(SorMemoryEntity m) => new(m.Id, m.UserId, m.Kind, m.Text, m.Scope, m.SourceChannelId, m.Stamp, m.UpdatedAt);

    /// <summary>The member's settings row, created on first use (a new row always has a new random stamp).</summary>
    public async Task<SorUserEntity> UserAsync(ulong guild, ulong user, CancellationToken ct)
    {
        var row = await db.Set<SorUserEntity>().FirstOrDefaultAsync(u => u.GuildId == guild && u.UserId == user, ct);
        if (row is not null)
            return row;
        var now = clock.GetUtcNow();
        row = new SorUserEntity { GuildId = guild, UserId = user, MemoryEnabled = true, Stamp = SorStamps.New(), CreatedAt = now, UpdatedAt = now };
        db.Add(row);
        return row;
    }

    /// <summary>
    /// Deletes conversations (turns and use records with them) so no copy of a changed or removed note can come back as
    /// context: the conversations that used one of <paramref name="memoryIds"/> (optionally not those of
    /// <paramref name="keepOwner"/>) and, when <paramref name="includeSources"/>, the conversations the notes were learned in.
    /// Returns the deleted conversation ids.
    /// </summary>
    public async Task<IReadOnlyList<long>> InvalidateAsync(IReadOnlyCollection<long> memoryIds, bool includeSources, ulong? keepOwner, CancellationToken ct)
    {
        if (memoryIds.Count == 0)
            return [];
        var ids = await db.Set<SorMemoryUseEntity>().Where(u => memoryIds.Contains(u.MemoryId)).Select(u => u.ConversationId).Distinct().ToListAsync(ct);
        if (includeSources)
        {
            ids.AddRange(await db.Set<SorMemoryEntity>().Where(m => memoryIds.Contains(m.Id) && m.SourceConversationId != null)
                .Select(m => m.SourceConversationId!.Value).ToListAsync(ct));
        }

        var targets = db.Set<SorConversationEntity>().Where(c => ids.Contains(c.Id));
        if (keepOwner is { } owner)
            targets = targets.Where(c => c.OwnerUserId != owner);
        var doomed = await targets.Select(c => c.Id).Distinct().ToListAsync(ct);
        await DeleteConversationsAsync(doomed, ct);
        return doomed;
    }

    /// <summary>Deletes conversations with their turns and use records (explicitly, not relying on the database cascade).</summary>
    public async Task<int> DeleteConversationsAsync(IReadOnlyCollection<long> conversationIds, CancellationToken ct)
    {
        if (conversationIds.Count == 0)
            return 0;
        var n = await db.Set<SorTurnEntity>().Where(t => conversationIds.Contains(t.ConversationId)).ExecuteDeleteAsync(ct);
        await db.Set<SorMemoryUseEntity>().Where(u => conversationIds.Contains(u.ConversationId)).ExecuteDeleteAsync(ct);
        n += await db.Set<SorConversationEntity>().Where(c => conversationIds.Contains(c.Id)).ExecuteDeleteAsync(ct);
        return n;
    }

    /// <summary>
    /// Applies the model's proposals to the asker's notes after <see cref="SorMemoryRules.CheckCandidate"/>. The owner is the
    /// asker, the scope comes from where it was said (private → private, channel → that channel), never shared. Single-valued
    /// kinds are replaced by a clear new statement; a full memory refuses new notes (nothing old is dropped at random); a
    /// "forget" that matches more than one note deletes nothing. A changed shared note invalidates other members'
    /// conversations that used it; a forgotten note invalidates every conversation that used or taught it.
    /// </summary>
    public async Task<(SorMemoryOutcome Outcome, IReadOnlyList<long> Invalidated)> ApplyAsync(
        ulong guild, ulong user, long conversationId, ulong interactionId, ulong channel, bool privateAnswer,
        string question, IReadOnlyCollection<string> otherNames, IReadOnlyList<SorMemoryCandidate> candidates, CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var notes = await Notes(guild, user).OrderBy(m => m.Id).ToListAsync(ct);
        int added = 0, updated = 0, forgotten = 0, refused = 0;
        bool ambiguous = false, full = false;
        var invalidated = new List<long>();
        var defaultScope = privateAnswer ? SorMemoryScope.Private : SorMemoryScope.Channel;
        ulong? source = privateAnswer ? null : channel;

        foreach (var candidate in candidates)
        {
            if (SorMemoryRules.CheckCandidate(candidate, question, otherNames, settings.MaxMemoryItemLength) != SorMemoryRefusal.None)
            {
                refused++;
                continue;
            }

            var text = SorMemoryRules.CleanNote(candidate.Text);
            if (candidate.Op == SorMemoryOp.Forget)
            {
                var matches = Match(notes, candidate.Target ?? text, null);
                if (matches.Count == 1)
                {
                    invalidated.AddRange(await InvalidateAsync([matches[0].Id], includeSources: true, keepOwner: null, ct));
                    db.Remove(matches[0]);
                    notes.Remove(matches[0]);
                    forgotten++;
                }
                else if (matches.Count > 1)
                {
                    ambiguous = true;
                }

                continue;
            }

            if (notes.Any(n => SorMemoryRules.Normalize(n.Text) == SorMemoryRules.Normalize(text)))
                continue; // already known: nothing to change

            SorMemoryEntity? target = null;
            if (SorMemoryKinds.IsSingleValued(candidate.Kind))
            {
                target = notes.FirstOrDefault(n => n.Kind == candidate.Kind);
            }
            else if (candidate.Op == SorMemoryOp.Update && candidate.Target is { } wanted)
            {
                var matches = Match(notes, wanted, candidate.Kind);
                if (matches.Count > 1)
                {
                    refused++;
                    continue;
                }

                target = matches.Count == 1 ? matches[0] : null;
            }

            if (target is not null)
            {
                if (target.Scope == SorMemoryScope.Shared)
                    invalidated.AddRange(await InvalidateAsync([target.Id], includeSources: false, keepOwner: user, ct));
                target.Text = text;
                target.Kind = candidate.Kind;
                target.Scope = defaultScope; // a changed note is not shared until the member shares it again
                target.SourceChannelId = source;
                target.SourceConversationId = conversationId;
                target.SourceInteractionId = interactionId;
                target.Origin = SorMemoryOrigin.Auto;
                target.Version++;
                target.Stamp = SorStamps.New();
                target.UpdatedAt = now;
                updated++;
                continue;
            }

            if (notes.Count >= settings.MaxMemoryItems)
            {
                full = true;
                continue;
            }

            var created = new SorMemoryEntity
            {
                GuildId = guild,
                UserId = user,
                Kind = candidate.Kind,
                Text = text,
                Scope = defaultScope,
                SourceChannelId = source,
                SourceConversationId = conversationId,
                SourceInteractionId = interactionId,
                Origin = IsExplicit(question) ? SorMemoryOrigin.Explicit : SorMemoryOrigin.Auto,
                Stamp = SorStamps.New(),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Add(created);
            notes.Add(created);
            added++;
        }

        return (new SorMemoryOutcome(added, updated, forgotten, ambiguous, full, refused), invalidated);
    }

    /// <summary>Notes whose text contains the target (or the reverse), optionally of one kind first.</summary>
    private static List<SorMemoryEntity> Match(IReadOnlyList<SorMemoryEntity> notes, string target, SorMemoryKind? kind)
    {
        var wanted = SorMemoryRules.Normalize(target);
        if (wanted.Length < 3)
            return [];
        var pool = kind is { } k && notes.Any(n => n.Kind == k) ? notes.Where(n => n.Kind == k) : notes;
        var exact = pool.Where(n => SorMemoryRules.Normalize(n.Text) == wanted).ToList();
        if (exact.Count > 0)
            return exact;
        return pool.Where(n =>
        {
            var text = SorMemoryRules.Normalize(n.Text);
            return text.Contains(wanted, StringComparison.Ordinal) || (text.Length >= 4 && wanted.Contains(text, StringComparison.Ordinal));
        }).ToList();
    }

    private static bool IsExplicit(string question)
    {
        var q = SorMemoryRules.Normalize(question);
        return q.Contains("hatırla", StringComparison.Ordinal) || q.Contains("aklında tut", StringComparison.Ordinal) ||
               q.Contains("remember", StringComparison.Ordinal);
    }
}
