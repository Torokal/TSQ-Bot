using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Privacy;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>What /hafizam shows: the caller's own notes in this guild and the memory switch.</summary>
public sealed record SorMemoryPanel(bool Enabled, IReadOnlyList<SorNote> Notes, int Max);

/// <summary>What "delete my conversations and memory" will remove (shown before the confirmation).</summary>
public sealed record SorWipePreview(int Notes, int Conversations, int Turns, string ConfirmationId);

/// <summary>
/// /hafizam: the CALLER's own notes in the CURRENT guild — user and guild always come from the interaction; a note id or
/// version from a button is only a pointer that is checked again (owner, guild, current version) on every action. Nothing
/// here can open or change another member's memory, and there is no admin view of anyone's notes.
/// </summary>
public sealed class SorMemoryService(ToroDbContext db, SorMemoryStore memory, IConfirmationStore confirmations, IOptions<SorOptions> options, TimeProvider clock)
{
    public const string WipeAction = "sor-memory-wipe";
    public static readonly TimeSpan WipeConfirmationLifetime = TimeSpan.FromMinutes(5);

    private IQueryable<SorMemoryEntity> Own(ActorContext actor) =>
        db.Set<SorMemoryEntity>().Where(m => m.GuildId == actor.GuildId.Value && m.UserId == actor.UserId.Value);

    public async Task<SorMemoryPanel> PanelAsync(ActorContext actor, CancellationToken ct)
    {
        var user = await db.Set<SorUserEntity>().AsNoTracking().FirstOrDefaultAsync(u => u.GuildId == actor.GuildId.Value && u.UserId == actor.UserId.Value, ct);
        var notes = await Own(actor).AsNoTracking().OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.Id).ToListAsync(ct);
        return new SorMemoryPanel(user?.MemoryEnabled ?? true, notes.Select(SorMemoryStore.ToNote).ToList(), options.Value.MaxMemoryItems);
    }

    /// <summary>The caller's own note at exactly this version (null when it is gone, someone else's, or changed since).</summary>
    public async Task<SorNote?> GetAsync(ActorContext actor, long id, string stamp, CancellationToken ct) =>
        await Own(actor).AsNoTracking().Where(m => m.Id == id && m.Stamp == stamp).Select(m => SorMemoryStore.ToNote(m)).FirstOrDefaultAsync(ct);

    /// <summary>A note written by hand: private, the member's own words (secrets are still refused).</summary>
    public Task<OperationResult> AddAsync(ActorContext actor, string? text, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var clean = SorMemoryRules.CleanNote(text);
        if (Refused(clean) is { } refusal)
            return refusal;
        var user = await memory.UserAsync(actor.GuildId.Value, actor.UserId.Value, ct);
        if (!user.MemoryEnabled)
            return OperationResult.Fail(OperationError.Conflict, "sor.mem.disabled_cannot_add");
        var notes = await Own(actor).Select(m => m.Text).ToListAsync(ct);
        if (notes.Any(n => SorMemoryRules.Normalize(n) == SorMemoryRules.Normalize(clean)))
            return OperationResult.Ok("sor.mem.already");
        if (notes.Count >= options.Value.MaxMemoryItems)
            return OperationResult.Fail(OperationError.Conflict, "sor.memory_full", options.Value.MaxMemoryItems);
        var now = clock.GetUtcNow();
        db.Add(new SorMemoryEntity
        {
            GuildId = actor.GuildId.Value,
            UserId = actor.UserId.Value,
            Kind = SorMemoryKind.Preference,
            Text = clean,
            Scope = SorMemoryScope.Private,
            Origin = SorMemoryOrigin.Manual,
            Stamp = SorStamps.New(),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("sor.mem.added");
    }, ct);

    /// <summary>
    /// A correction: new text, new version. A shared note stops being shared (the new text was not previewed), and every
    /// other member's conversation that used it is invalidated.
    /// </summary>
    public Task<OperationResult> EditAsync(ActorContext actor, long id, string stamp, string? text, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var clean = SorMemoryRules.CleanNote(text);
        if (Refused(clean) is { } refusal)
            return refusal;
        var note = await Own(actor).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (note is null || note.Stamp != stamp)
            return OperationResult.Fail(OperationError.Conflict, "sor.mem.changed");
        var wasShared = note.Scope == SorMemoryScope.Shared;
        if (wasShared)
            await memory.InvalidateAsync([note.Id], includeSources: false, keepOwner: actor.UserId.Value, ct);
        note.Text = clean;
        note.Scope = DefaultScope(note);
        note.Origin = SorMemoryOrigin.Manual;
        note.Version++;
        note.Stamp = SorStamps.New();
        note.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(wasShared ? "sor.mem.edited_unshared" : "sor.mem.edited");
    }, ct);

    /// <summary>
    /// Deletes one note. Every conversation that used it or taught it is deleted too (the caller's own included), so the
    /// note cannot be learned again from an old turn and no derived copy comes back as context.
    /// </summary>
    public Task<OperationResult> DeleteAsync(ActorContext actor, long id, string stamp, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var note = await Own(actor).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (note is null || note.Stamp != stamp)
            return OperationResult.Fail(OperationError.Conflict, "sor.mem.changed");
        await memory.InvalidateAsync([note.Id], includeSources: true, keepOwner: null, ct);
        db.Remove(note);
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("sor.mem.deleted");
    }, ct);

    /// <summary>Shares one note (after the member saw the preview): other members' questions about them may use it.</summary>
    public Task<OperationResult> ShareAsync(ActorContext actor, long id, string stamp, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var note = await Own(actor).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (note is null || note.Stamp != stamp)
            return OperationResult.Fail(OperationError.Conflict, "sor.mem.changed");
        if (note.Scope == SorMemoryScope.Shared)
            return OperationResult.Ok("sor.mem.shared");
        note.Scope = SorMemoryScope.Shared;
        note.Stamp = SorStamps.New();
        note.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("sor.mem.shared");
    }, ct);

    /// <summary>Takes a share back: the note returns to where it was learned; other members' conversations that used it are invalidated.</summary>
    public Task<OperationResult> UnshareAsync(ActorContext actor, long id, string stamp, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var note = await Own(actor).FirstOrDefaultAsync(m => m.Id == id, ct);
        if (note is null || note.Stamp != stamp)
            return OperationResult.Fail(OperationError.Conflict, "sor.mem.changed");
        if (note.Scope != SorMemoryScope.Shared)
            return OperationResult.Ok("sor.mem.unshared");
        await memory.InvalidateAsync([note.Id], includeSources: false, keepOwner: actor.UserId.Value, ct);
        note.Scope = DefaultScope(note);
        note.Stamp = SorStamps.New();
        note.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok("sor.mem.unshared");
    }, ct);

    /// <summary>
    /// Lasting memory on or off. Off: no note is read (shared ones included) or written; notes are kept, and sharing comes back
    /// as it was when memory is switched on again. The member's own conversations are reset (notes already in earlier turns
    /// must not come back indirectly) and other members' conversations that used a shared note are invalidated.
    /// </summary>
    public Task<OperationResult> SetEnabledAsync(ActorContext actor, bool enabled, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var user = await memory.UserAsync(actor.GuildId.Value, actor.UserId.Value, ct);
        if (user.MemoryEnabled == enabled)
            return OperationResult.Ok(enabled ? "sor.mem.enabled" : "sor.mem.disabled");
        if (!enabled)
        {
            var ids = await Own(actor).Select(m => m.Id).ToListAsync(ct);
            await memory.InvalidateAsync(ids, includeSources: true, keepOwner: null, ct);
            await memory.DeleteConversationsAsync(await OwnConversations(actor).ToListAsync(ct), ct);
        }

        user.MemoryEnabled = enabled;
        user.Stamp = SorStamps.New();
        user.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return OperationResult.Ok(enabled ? "sor.mem.enabled" : "sor.mem.disabled");
    }, ct);

    public async Task<SorWipePreview> PreviewWipeAsync(ActorContext actor, CancellationToken ct)
    {
        var conversations = await OwnConversations(actor).ToListAsync(ct);
        var notes = await Own(actor).CountAsync(ct);
        var turns = await db.Set<SorTurnEntity>().CountAsync(t => conversations.Contains(t.ConversationId), ct);
        var id = await confirmations.CreateAsync(actor.GuildId, actor.UserId, WipeAction, WipeConfirmationLifetime, ct);
        return new SorWipePreview(notes, conversations.Count, turns, id);
    }

    /// <summary>
    /// "Delete my conversations and memory": every conversation, turn and note of the caller in this guild, the content of
    /// pending requests, and other members' conversations that used a shared note. Usage records (counts only) stay, so the
    /// daily allowance is NOT renewed; the memory switch is kept. Requires the single-use confirmation (5 minutes, bound to
    /// guild + user + action).
    /// </summary>
    public async Task<OperationResult> WipeAsync(ActorContext actor, string confirmationId, CancellationToken ct)
    {
        if (!await confirmations.TryConsumeAsync(confirmationId, actor.GuildId, actor.UserId, WipeAction, ct))
            return OperationResult.Fail(OperationError.Expired, "sor.mem.confirmation_invalid");
        var deleted = await SorWrites.RunAsync(db, async () =>
        {
            var ids = await Own(actor).Select(m => m.Id).ToListAsync(ct);
            var n = (await memory.InvalidateAsync(ids, includeSources: true, keepOwner: null, ct)).Count;
            n += await memory.DeleteConversationsAsync(await OwnConversations(actor).ToListAsync(ct), ct);
            n += await Own(actor).ExecuteDeleteAsync(ct);
            await db.Set<SorRequestEntity>().Where(r => r.GuildId == actor.GuildId.Value && r.UserId == actor.UserId.Value && r.PendingAnswer != null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PendingAnswer, (string?)null).SetProperty(r => r.PendingMemory, (string?)null)
                    .SetProperty(r => r.PendingContext, (string?)null).SetProperty(r => r.State, SorRequestState.Discarded), ct);
            var user = await memory.UserAsync(actor.GuildId.Value, actor.UserId.Value, ct);
            user.Stamp = SorStamps.New();
            user.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return n;
        }, ct);
        return OperationResult.Ok("sor.mem.wiped", deleted);
    }

    private IQueryable<long> OwnConversations(ActorContext actor) =>
        db.Set<SorConversationEntity>().Where(c => c.GuildId == actor.GuildId.Value && c.OwnerUserId == actor.UserId.Value).Select(c => c.Id);

    private static SorMemoryScope DefaultScope(SorMemoryEntity note) => note.SourceChannelId is null ? SorMemoryScope.Private : SorMemoryScope.Channel;

    private OperationResult? Refused(string text) => SorMemoryRules.CheckText(text, options.Value.MaxMemoryItemLength) switch
    {
        SorMemoryRefusal.None => null,
        SorMemoryRefusal.TooLong => OperationResult.Fail(OperationError.InvalidInput, "sor.mem.too_long", options.Value.MaxMemoryItemLength),
        SorMemoryRefusal.Secret => OperationResult.Fail(OperationError.InvalidInput, "sor.mem.secret"),
        _ => OperationResult.Fail(OperationError.InvalidInput, "sor.mem.empty"),
    };
}
