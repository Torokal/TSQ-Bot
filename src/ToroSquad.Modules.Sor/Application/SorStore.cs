using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Ai;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Domain;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

public enum SorRefusal
{
    None = 0,
    Cooldown = 1,
    UserQuota = 2,
    GuildQuota = 3,

    /// <summary>This interaction was already admitted (a repeated event): no second generation.</summary>
    Duplicate = 4,
}

/// <summary>The result of an admission check or reservation.</summary>
public sealed record SorReservation(SorRefusal Refusal, long RequestId = 0, bool ShowNotice = false, DateTimeOffset? RetryAt = null, int Limit = 0)
{
    public bool Admitted => Refusal == SorRefusal.None;
}

/// <summary>How a request ended without a delivered answer (stage and class: what an operator needs, never text).</summary>
public enum SorFailureStage
{
    Provider = 0,
    Timeout = 1,
    Parse = 2,
    Delivery = 3,
    Stale = 4,
    Database = 5,
    Unknown = 6,
}

/// <summary>
/// The request lifecycle in the database: admission (quota and cooldown, race-free), context selection, the pending answer,
/// the version check, and the idempotent finalization. Short transactions only — the AI request and Discord happen between
/// them, never inside.
/// </summary>
public sealed class SorStore(ToroDbContext db, SorMemoryStore memory, IOptions<SorOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The day a request counts for: the guild's time zone (the project standard: /setup timezone, default Europe/Istanbul).</summary>
    public static string DayKey(DateTimeOffset now, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Read-only admission check, answered privately BEFORE the acknowledgement (no AI request, nothing written).</summary>
    public async Task<SorReservation> CheckAsync(SorAsk ask, SorLimits limits, CancellationToken ct) =>
        await RefusalAsync(ask, limits, DayKey(clock.GetUtcNow(), ask.Zone), ct) ?? new SorReservation(SorRefusal.None);

    /// <summary>
    /// Admits and counts the request in ONE write transaction: the interaction is unique, then cooldown, the member's and the
    /// guild's daily limits are checked against what is committed — two requests for the last slot cannot both pass.
    /// </summary>
    public Task<SorReservation> ReserveAsync(SorAsk ask, SorLimits limits, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var now = clock.GetUtcNow();
        var day = DayKey(now, ask.Zone);
        if (await db.Set<SorRequestEntity>().AnyAsync(r => r.InteractionId == ask.InteractionId, ct))
            return new SorReservation(SorRefusal.Duplicate);
        if (await RefusalAsync(ask, limits, day, ct) is { } refusal)
            return refusal;

        var guild = ask.Actor.GuildId.Value;
        var request = new SorRequestEntity
        {
            InteractionId = ask.InteractionId,
            GuildId = guild,
            UserId = ask.Actor.UserId.Value,
            ChannelId = ask.ChannelId,
            IsPrivate = ask.IsPrivate,
            DayKey = day,
            State = SorRequestState.Generating,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Add(request);
        var usage = await UsageAsync(guild, day, ct);
        usage.Requests++;
        usage.UpdatedAt = now;
        var user = await memory.UserAsync(guild, ask.Actor.UserId.Value, ct);
        await db.SaveChangesAsync(ct);
        return new SorReservation(SorRefusal.None, request.Id, ShowNotice: user.NoticeShownAt is null);
    }, ct);

    private async Task<SorReservation?> RefusalAsync(SorAsk ask, SorLimits limits, string day, CancellationToken ct)
    {
        var guild = ask.Actor.GuildId.Value;
        var user = ask.Actor.UserId.Value;
        var counted = db.Set<SorRequestEntity>().AsNoTracking().Where(r => r.GuildId == guild && r.UserId == user && r.State != SorRequestState.Released);
        if (limits.UserCooldownSeconds > 0)
        {
            var since = clock.GetUtcNow() - limits.Cooldown;
            var last = await counted.Where(r => r.CreatedAt > since).OrderByDescending(r => r.CreatedAt).Select(r => (DateTimeOffset?)r.CreatedAt).FirstOrDefaultAsync(ct);
            if (last is { } at)
                return new SorReservation(SorRefusal.Cooldown, RetryAt: at + limits.Cooldown);
        }

        if (await counted.CountAsync(r => r.DayKey == day, ct) >= limits.UserDailyLimit)
            return new SorReservation(SorRefusal.UserQuota, Limit: limits.UserDailyLimit);
        var used = await db.Set<SorGuildUsageEntity>().AsNoTracking().Where(u => u.GuildId == guild && u.DayKey == day).Select(u => u.Requests).FirstOrDefaultAsync(ct);
        return used >= limits.GuildDailyLimit ? new SorReservation(SorRefusal.GuildQuota, Limit: limits.GuildDailyLimit) : null;
    }

    private async Task<SorGuildUsageEntity> UsageAsync(ulong guild, string day, CancellationToken ct)
    {
        var usage = await db.Set<SorGuildUsageEntity>().FirstOrDefaultAsync(u => u.GuildId == guild && u.DayKey == day, ct);
        if (usage is null)
        {
            usage = new SorGuildUsageEntity { GuildId = guild, DayKey = day };
            db.Add(usage);
        }

        return usage;
    }

    /// <summary>
    /// Selects the context: the conversation (continued only for the same owner, guild, channel/thread and visibility within
    /// the idle window; "new" closes the open one now that a valid question arrived), its last turns, the asker's allowed
    /// notes (when memory is on) and the mentioned member's shared notes (when their memory is on). Nothing else is loaded.
    /// </summary>
    public Task<SorContext> LoadContextAsync(SorAsk ask, SorLimits limits, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var now = clock.GetUtcNow();
        var guild = ask.Actor.GuildId.Value;
        var owner = ask.Actor.UserId.Value;
        var open = db.Set<SorConversationEntity>().Where(c => c.GuildId == guild && c.OwnerUserId == owner && c.ChannelId == ask.ChannelId &&
                                                               c.IsPrivate == ask.IsPrivate && c.State == SorConversationState.Open);
        var fresh = now - limits.Idle;
        SorConversationEntity? conversation = null;
        var previousClosed = false;
        if (ask.NewConversation)
        {
            await open.ExecuteUpdateAsync(s => s.SetProperty(c => c.State, SorConversationState.Closed), ct);
        }
        else if (ask.ConversationId is { } wanted)
        {
            conversation = await open.FirstOrDefaultAsync(c => c.Id == wanted && c.LastActivityAt >= fresh, ct);
            previousClosed = conversation is null;
            if (conversation is null)
                await open.ExecuteUpdateAsync(s => s.SetProperty(c => c.State, SorConversationState.Closed), ct);
        }
        else
        {
            conversation = await open.Where(c => c.LastActivityAt >= fresh).OrderByDescending(c => c.LastActivityAt).ThenByDescending(c => c.Id).FirstOrDefaultAsync(ct);
            var keep = conversation?.Id ?? -1;
            await open.Where(c => c.Id != keep).ExecuteUpdateAsync(s => s.SetProperty(c => c.State, SorConversationState.Closed), ct);
        }

        if (conversation is null)
        {
            conversation = new SorConversationEntity
            {
                GuildId = guild,
                OwnerUserId = owner,
                ChannelId = ask.ChannelId,
                IsPrivate = ask.IsPrivate,
                State = SorConversationState.Open,
                Stamp = SorStamps.New(),
                CreatedAt = now,
                LastActivityAt = now,
            };
            db.Add(conversation);
        }

        var asker = await memory.UserAsync(guild, owner, ct);
        await db.SaveChangesAsync(ct);

        var turns = limits.MaxContextTurns == 0
            ? []
            : (await db.Set<SorTurnEntity>().AsNoTracking().Where(t => t.ConversationId == conversation.Id)
                .OrderByDescending(t => t.Seq).Take(limits.MaxContextTurns).ToListAsync(ct))
            .OrderBy(t => t.Seq).Select(t => new SorHistoryTurn(t.Seq, t.Question, t.Answer)).ToList();

        var own = asker.MemoryEnabled ? await memory.OwnNotesForAsync(guild, owner, ask.IsPrivate, ask.ChannelId, ct) : [];

        SorPerson? other = ask.About is { IsBot: false } about && about.UserId != owner ? about : null;
        string? otherStamp = null;
        var otherShares = false;
        IReadOnlyList<SorNote> otherNotes = [];
        if (other is not null)
        {
            var row = await db.Set<SorUserEntity>().AsNoTracking().FirstOrDefaultAsync(u => u.GuildId == guild && u.UserId == other.UserId, ct);
            if (row is { MemoryEnabled: true })
            {
                otherStamp = row.Stamp;
                otherNotes = await memory.SharedNotesOfAsync(guild, other.UserId, ct);
                otherShares = otherNotes.Count > 0;
            }
        }

        return new SorContext(conversation.Id, conversation.Stamp, previousClosed, turns, asker.MemoryEnabled, asker.Stamp, own, other, otherStamp, otherShares, otherNotes);
    }, ct);

    /// <summary>The answer is here: kept pending (with its proposals and the context versions) until it is delivered.</summary>
    public async Task SaveGeneratedAsync(long requestId, long conversationId, string answer, IReadOnlyList<SorMemoryCandidate> candidates, SorSnapshot snapshot,
        AiUsage usage, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.Set<SorRequestEntity>().Where(r => r.Id == requestId && r.State == SorRequestState.Generating).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.State, SorRequestState.Generated)
            .SetProperty(r => r.ConversationId, conversationId)
            .SetProperty(r => r.PendingAnswer, answer)
            .SetProperty(r => r.PendingMemory, JsonSerializer.Serialize(candidates, Json))
            .SetProperty(r => r.PendingContext, JsonSerializer.Serialize(snapshot, Json))
            .SetProperty(r => r.InputTokens, usage.InputTokens)
            .SetProperty(r => r.OutputTokens, usage.OutputTokens)
            .SetProperty(r => r.ReasoningTokens, usage.ReasoningTokens)
            .SetProperty(r => r.CachedInputTokens, usage.CachedInputTokens)
            .SetProperty(r => r.UpdatedAt, now), ct);
    }

    /// <summary>
    /// True when nothing the answer depended on changed: the asker's memory stamp and switch, the mentioned member's stamp
    /// (their memory still on), the conversation (still open, same stamp) and every note used (same version). Read-only.
    /// </summary>
    public async Task<bool> IsCurrentAsync(ulong guild, ulong asker, SorSnapshot snapshot, CancellationToken ct)
    {
        var user = await db.Set<SorUserEntity>().AsNoTracking().FirstOrDefaultAsync(u => u.GuildId == guild && u.UserId == asker, ct);
        if (user is null || user.Stamp != snapshot.AskerStamp || user.MemoryEnabled != snapshot.AskerMemory)
            return false;
        if (snapshot is { OtherUser: { } other, OtherStamp: { } otherStamp })
        {
            var row = await db.Set<SorUserEntity>().AsNoTracking().FirstOrDefaultAsync(u => u.GuildId == guild && u.UserId == other, ct);
            if (row is null || row.Stamp != otherStamp || !row.MemoryEnabled)
                return false;
        }

        var conversation = await db.Set<SorConversationEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == snapshot.ConversationId, ct);
        if (conversation is null || conversation.Stamp != snapshot.ConversationStamp || conversation.State != SorConversationState.Open ||
            conversation.GuildId != guild || conversation.OwnerUserId != asker)
            return false;
        if (snapshot.Notes.Count == 0)
            return true;
        var ids = snapshot.Notes.Select(n => n.Id).ToList();
        var current = await db.Set<SorMemoryEntity>().AsNoTracking().Where(m => m.GuildId == guild && ids.Contains(m.Id)).Select(m => new { m.Id, m.Stamp }).ToListAsync(ct);
        return snapshot.Notes.All(n => current.Any(c => c.Id == n.Id && c.Stamp == n.Stamp));
    }

    /// <summary>
    /// After a confirmed delivery, in ONE transaction and only once per request (state Generated → Completed): the version
    /// check again, then the asker's note changes, the turn (only if its conversation still exists), the trimmed history,
    /// the use records and the counters. A stale context saves neither the turn nor any note.
    /// </summary>
    public Task<(bool Saved, SorMemoryOutcome Memory)> FinalizeAsync(long requestId, string question, IReadOnlyCollection<string> otherNames,
        SorLimits limits, bool noticeShown, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var now = clock.GetUtcNow();
        var request = await db.Set<SorRequestEntity>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is not { State: SorRequestState.Generated, PendingAnswer: { } answer, PendingContext: { } context })
            return (false, SorMemoryOutcome.None);

        var snapshot = JsonSerializer.Deserialize<SorSnapshot>(context, Json)!;
        var candidates = JsonSerializer.Deserialize<List<SorMemoryCandidate>>(request.PendingMemory ?? "[]", Json) ?? [];
        var usage = await UsageAsync(request.GuildId, request.DayKey, ct);
        Complete(request, usage, now);
        if (noticeShown)
            (await memory.UserAsync(request.GuildId, request.UserId, ct)).NoticeShownAt ??= now;

        if (!await IsCurrentAsync(request.GuildId, request.UserId, snapshot, ct))
        {
            request.Failure = "stale-after-delivery";
            await db.SaveChangesAsync(ct);
            return (false, SorMemoryOutcome.None);
        }

        var outcome = SorMemoryOutcome.None;
        IReadOnlyList<long> invalidated = [];
        if (snapshot.AskerMemory && candidates.Count > 0)
        {
            (outcome, invalidated) = await memory.ApplyAsync(request.GuildId, request.UserId, snapshot.ConversationId, request.InteractionId,
                request.ChannelId, request.IsPrivate, question, otherNames, candidates, ct);
            if (outcome.Changed)
                (await memory.UserAsync(request.GuildId, request.UserId, ct)).UpdatedAt = now;
        }

        if (!invalidated.Contains(snapshot.ConversationId) &&
            await db.Set<SorConversationEntity>().FirstOrDefaultAsync(c => c.Id == snapshot.ConversationId, ct) is { } conversation)
        {
            var seq = (await db.Set<SorTurnEntity>().Where(t => t.ConversationId == conversation.Id).MaxAsync(t => (int?)t.Seq, ct) ?? 0) + 1;
            db.Add(new SorTurnEntity
            {
                ConversationId = conversation.Id,
                GuildId = request.GuildId,
                UserId = request.UserId,
                Seq = seq,
                Question = Cap(question, SorModelContributor.MaxStoredQuestion),
                Answer = Cap(answer, SorModelContributor.MaxStoredAnswer),
                CreatedAt = now,
            });
            // Old turns go when a new one is written (the history never grows beyond what can be sent).
            var keep = Math.Max(1, limits.MaxContextTurns);
            await db.Set<SorTurnEntity>().Where(t => t.ConversationId == conversation.Id && t.Seq <= seq - keep).ExecuteDeleteAsync(ct);
            conversation.LastActivityAt = now;
            var used = snapshot.Notes.Select(n => n.Id).ToList();
            var known = await db.Set<SorMemoryUseEntity>().Where(u => u.ConversationId == conversation.Id && used.Contains(u.MemoryId)).Select(u => u.MemoryId).ToListAsync(ct);
            foreach (var id in used.Except(known))
                db.Add(new SorMemoryUseEntity { ConversationId = conversation.Id, MemoryId = id });
        }

        await db.SaveChangesAsync(ct);
        return (true, outcome);
    }, ct);

    private static void Complete(SorRequestEntity request, SorGuildUsageEntity usage, DateTimeOffset now)
    {
        request.State = SorRequestState.Completed;
        request.PendingAnswer = null;
        request.PendingMemory = null;
        request.PendingContext = null;
        request.UpdatedAt = now;
        usage.Completed++;
        AddTokens(usage, request.InputTokens, request.OutputTokens, request.ReasoningTokens, request.CachedInputTokens);
        usage.UpdatedAt = now;
    }

    private static void AddTokens(SorGuildUsageEntity usage, int? input, int? output, int? reasoning, int? cached)
    {
        usage.InputTokens += input ?? 0;
        usage.OutputTokens += output ?? 0;
        usage.ReasoningTokens += reasoning ?? 0;
        usage.CachedInputTokens += cached ?? 0;
    }

    /// <summary>
    /// A request that produced no delivered answer: its state, failure class and whatever usage the provider reported (a
    /// timeout's usage is UNKNOWN, not zero, and counted as such). The pending answer, if any, is cleared.
    /// </summary>
    public Task MarkFailedAsync(long requestId, SorFailureStage stage, string? detail, AiUsage? usage, string? trace, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var request = await db.Set<SorRequestEntity>().FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null || request.State is SorRequestState.Completed or SorRequestState.Failed or SorRequestState.Discarded or SorRequestState.Unknown or SorRequestState.Released)
            return 0;
        var now = clock.GetUtcNow();
        var g = await UsageAsync(request.GuildId, request.DayKey, ct);
        request.State = stage switch
        {
            SorFailureStage.Stale => SorRequestState.Discarded,
            SorFailureStage.Timeout or SorFailureStage.Unknown => SorRequestState.Unknown,
            _ => SorRequestState.Failed,
        };
        request.Failure = Cap((stage.ToString().ToLowerInvariant() + (detail is null ? "" : ":" + detail)), 40);
        request.TraceCode = trace;
        if (usage is not null && (usage.InputTokens is not null || usage.OutputTokens is not null))
        {
            request.InputTokens = usage.InputTokens;
            request.OutputTokens = usage.OutputTokens;
            request.ReasoningTokens = usage.ReasoningTokens;
            request.CachedInputTokens = usage.CachedInputTokens;
        }

        AddTokens(g, request.InputTokens, request.OutputTokens, request.ReasoningTokens, request.CachedInputTokens);
        switch (stage)
        {
            case SorFailureStage.Provider:
                g.ProviderFailures++;
                break;
            case SorFailureStage.Timeout:
                g.Timeouts++;
                g.UnknownUsage++;
                break;
            case SorFailureStage.Parse:
                g.ParseFailures++;
                break;
            case SorFailureStage.Delivery:
                g.DeliveryFailures++;
                break;
            case SorFailureStage.Stale:
                g.Discarded++;
                break;
            default:
                g.UnknownUsage++;
                break;
        }

        request.PendingAnswer = null;
        request.PendingMemory = null;
        request.PendingContext = null;
        request.UpdatedAt = now;
        g.UpdatedAt = now;
        return await db.SaveChangesAsync(ct);
    }, ct);

    /// <summary>Refused after admission but before any AI request (e.g. the module was disabled meanwhile): not counted.</summary>
    public Task ReleaseAsync(long requestId, CancellationToken ct) => SorWrites.RunAsync(db, async () =>
    {
        var request = await db.Set<SorRequestEntity>().FirstOrDefaultAsync(r => r.Id == requestId && r.State == SorRequestState.Generating, ct);
        if (request is null)
            return 0;
        request.State = SorRequestState.Released;
        request.UpdatedAt = clock.GetUtcNow();
        var g = await UsageAsync(request.GuildId, request.DayKey, ct);
        g.Requests = Math.Max(0, g.Requests - 1);
        return await db.SaveChangesAsync(ct);
    }, ct);

    /// <summary>The owner of a conversation in this guild (null when it does not exist any more).</summary>
    public async Task<ulong?> OwnerOfAsync(ulong guild, long conversationId, CancellationToken ct) =>
        await db.Set<SorConversationEntity>().AsNoTracking().Where(c => c.Id == conversationId && c.GuildId == guild)
            .Select(c => (ulong?)c.OwnerUserId).FirstOrDefaultAsync(ct);

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max];

    public static int EstimateTokens(int characters) => (characters + SorOptions.CharsPerToken - 1) / SorOptions.CharsPerToken;

    public SorOptions Options => options.Value;
}
