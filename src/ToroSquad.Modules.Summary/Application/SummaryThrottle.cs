using Microsoft.Extensions.Options;

namespace ToroSquad.Modules.Summary.Application;

public enum SummaryAdmission
{
    Admitted = 0,

    /// <summary>A summary of this channel is being prepared right now.</summary>
    ChannelBusy = 1,

    /// <summary>The member produced a summary less than <see cref="SummaryOptions.UserCooldownSeconds"/> ago.</summary>
    UserCooldown = 2,

    /// <summary>The channel was summarized less than <see cref="SummaryOptions.ChannelCooldownSeconds"/> ago.</summary>
    ChannelCooldown = 3,

    /// <summary><see cref="SummaryOptions.MaxConcurrentRequests"/> summaries are already running (no queue).</summary>
    AtCapacity = 4,
}

/// <summary>How a summary run ended, which decides the cooldown it leaves behind.</summary>
public enum SummaryRunEnd
{
    /// <summary>Refused before any AI request (permissions, too few messages, Discord read failed): no cooldown.</summary>
    NoInference = 0,

    /// <summary>The AI request failed: a short cooldown only, so a provider outage is not hammered but nobody waits long.</summary>
    InferenceFailed = 1,

    /// <summary>A summary was produced: the full member and channel cooldowns.</summary>
    Completed = 2,
}

/// <summary>
/// In-memory admission for /ozetle: one run per channel at a time, a member and a channel cooldown after a produced summary,
/// and at most <see cref="SummaryOptions.MaxConcurrentRequests"/> runs bot-wide — refused at once when full (no queue, no
/// waiting). Cooldowns start when a run ends and depend on how it ended (<see cref="SummaryRunEnd"/>). Nothing is persisted:
/// a restart forgets cooldowns, which is harmless. Thread-safe.
/// </summary>
public sealed class SummaryThrottle(IOptions<SummaryOptions> options, TimeProvider clock)
{
    /// <summary>After a failed AI request: member and channel wait this long (instead of the full cooldowns).</summary>
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly HashSet<ulong> _running = [];
    private readonly Dictionary<ulong, DateTimeOffset> _userUntil = [];
    private readonly Dictionary<ulong, DateTimeOffset> _channelUntil = [];

    public int Running
    {
        get
        {
            lock (_gate)
                return _running.Count;
        }
    }

    /// <summary>Admits a run (the caller must <see cref="Ticket.Dispose"/> it) or says why not and until when.</summary>
    public (SummaryAdmission Admission, Ticket? Ticket, DateTimeOffset? RetryAt) TryEnter(ulong user, ulong channel)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            if (_running.Contains(channel))
                return (SummaryAdmission.ChannelBusy, null, null);
            if (_userUntil.TryGetValue(user, out var userUntil))
                return (SummaryAdmission.UserCooldown, null, userUntil);
            if (_channelUntil.TryGetValue(channel, out var channelUntil))
                return (SummaryAdmission.ChannelCooldown, null, channelUntil);
            if (_running.Count >= options.Value.MaxConcurrentRequests)
                return (SummaryAdmission.AtCapacity, null, null);
            _running.Add(channel);
            return (SummaryAdmission.Admitted, new Ticket(this, user, channel), null);
        }
    }

    private void Leave(ulong user, ulong channel, SummaryRunEnd end)
    {
        var now = clock.GetUtcNow();
        var settings = options.Value;
        lock (_gate)
        {
            _running.Remove(channel);
            var (userWait, channelWait) = end switch
            {
                SummaryRunEnd.Completed => (settings.UserCooldown, settings.ChannelCooldown),
                SummaryRunEnd.InferenceFailed => (Min(FailureCooldown, settings.UserCooldown), Min(FailureCooldown, settings.ChannelCooldown)),
                _ => (TimeSpan.Zero, TimeSpan.Zero),
            };
            if (userWait > TimeSpan.Zero)
                _userUntil[user] = now + userWait;
            if (channelWait > TimeSpan.Zero)
                _channelUntil[channel] = now + channelWait;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _userUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            _userUntil.Remove(key);
        foreach (var key in _channelUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            _channelUntil.Remove(key);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>An admitted run. <see cref="End"/> records how it ended; disposing without it counts as <see cref="SummaryRunEnd.NoInference"/>.</summary>
    public sealed class Ticket(SummaryThrottle owner, ulong user, ulong channel) : IDisposable
    {
        private SummaryRunEnd _end = SummaryRunEnd.NoInference;
        private int _disposed;

        public void End(SummaryRunEnd end) => _end = end;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Leave(user, channel, _end);
        }
    }
}
