using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;

namespace ToroSquad.Modules.Live.Application;

/// <summary>Reconciliation health of one platform (cached for doctor and /bot status; persisted in live_provider_state).</summary>
public sealed record LiveFeed(
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    LiveProviderOutcome? LastOutcome,
    string? LastDetail,
    DateTimeOffset? LastErrorAt,
    int ConsecutiveFailures,
    IReadOnlyList<string> Warnings)
{
    public static LiveFeed Empty { get; } = new(null, null, null, null, null, 0, []);
}

/// <summary>Process-wide, thread-safe view of provider health and the Discord channel state (no secrets).</summary>
public sealed class LiveHealth
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LivePlatform, LiveFeed> _feeds = [];

    public string? ChannelProblem { get; private set; }

    public DateTimeOffset? ChannelProblemAt { get; private set; }

    public LiveFeed Feed(LivePlatform platform)
    {
        lock (_gate)
            return _feeds.GetValueOrDefault(platform) ?? LiveFeed.Empty;
    }

    public void Restore(LivePlatform platform, LiveFeed feed)
    {
        lock (_gate)
            _feeds[platform] = feed;
    }

    public LiveFeed Record(LivePlatform platform, LiveProviderResult result, DateTimeOffset attemptAt)
    {
        lock (_gate)
        {
            var previous = _feeds.GetValueOrDefault(platform) ?? LiveFeed.Empty;
            var next = result.Succeeded
                ? previous with { LastAttemptAt = attemptAt, LastSuccessAt = result.At, LastOutcome = result.Outcome, ConsecutiveFailures = 0, Warnings = result.Warnings ?? [] }
                : previous with
                {
                    LastAttemptAt = attemptAt,
                    LastOutcome = result.Outcome,
                    LastDetail = result.Detail,
                    LastErrorAt = attemptAt,
                    ConsecutiveFailures = previous.ConsecutiveFailures + 1,
                };
            _feeds[platform] = next;
            return next;
        }
    }

    public void ReportChannelProblem(string problem, DateTimeOffset at)
    {
        ChannelProblem = problem;
        ChannelProblemAt = at;
    }
}
