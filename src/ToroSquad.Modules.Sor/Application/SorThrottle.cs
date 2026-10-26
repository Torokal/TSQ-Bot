using Microsoft.Extensions.Options;
using ToroSquad.Ai;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>The conversation a generation belongs to: one owner in one guild, channel or thread, and visibility.</summary>
public readonly record struct SorConversationKey(ulong Guild, ulong Owner, ulong Channel, bool IsPrivate);

public enum SorAdmission
{
    Admitted = 0,

    /// <summary>A generation for this same conversation is running.</summary>
    ConversationBusy = 1,

    /// <summary><see cref="SorOptions.MaxConcurrentRequests"/> Sor generations are running (all Sor calls together; TSQ Özet is not counted; no queue).</summary>
    AtCapacity = 2,

    /// <summary>The provider answered 429 a moment ago: no new request until its Retry-After passed.</summary>
    ProviderBackoff = 3,
}

/// <summary>
/// In-memory admission for running generations only: one per conversation, at most <see cref="SorOptions.MaxConcurrentRequests"/>
/// across all Sor calls (Sor's own share of the shared OpenCode Go key — TSQ Özet keeps its own SummaryThrottle), refused at once when full. After a 429
/// no request is sent until the provider's Retry-After (or <see cref="DefaultBackoff"/>) has passed. Cooldowns and daily
/// limits are NOT here: they are persistent and checked in the database. Thread-safe.
/// </summary>
public sealed class SorThrottle(IOptions<SorOptions> options, TimeProvider clock)
{
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly HashSet<SorConversationKey> _running = [];
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;

    public int Running
    {
        get
        {
            lock (_gate)
                return _running.Count;
        }
    }

    public (SorAdmission Admission, Ticket? Ticket, DateTimeOffset? RetryAt) TryEnter(SorConversationKey key)
    {
        lock (_gate)
        {
            if (_backoffUntil > clock.GetUtcNow())
                return (SorAdmission.ProviderBackoff, null, _backoffUntil);
            if (_running.Contains(key))
                return (SorAdmission.ConversationBusy, null, null);
            if (_running.Count >= options.Value.MaxConcurrentRequests)
                return (SorAdmission.AtCapacity, null, null);
            _running.Add(key);
            return (SorAdmission.Admitted, new Ticket(this, key), null);
        }
    }

    /// <summary>The provider asked to slow down: no request before the wait it gave (bounded) or the default.</summary>
    public void BackOff(TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } w && w > TimeSpan.Zero ? (w > OpenCodeGo.MaxRetryAfter ? OpenCodeGo.MaxRetryAfter : w) : DefaultBackoff;
        lock (_gate)
        {
            var until = clock.GetUtcNow() + wait;
            if (until > _backoffUntil)
                _backoffUntil = until;
        }
    }

    private void Leave(SorConversationKey key)
    {
        lock (_gate)
            _running.Remove(key);
    }

    public sealed class Ticket(SorThrottle owner, SorConversationKey key) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Leave(key);
        }
    }
}

/// <summary>How the last /sor ended, for /bot status (categories only — never text).</summary>
public sealed record SorLastRun(DateTimeOffset At, string Outcome, string? Failure, int? HttpStatus, string? Trace);

/// <summary>Remembered outcome of the last run (in memory; a restart forgets it, which is harmless).</summary>
public sealed class SorStatus
{
    private SorLastRun? _last;

    public SorLastRun? Last => Volatile.Read(ref _last);

    public void Record(SorLastRun run) => Volatile.Write(ref _last, run);
}
