using System.Globalization;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Providers.PandaScore;

/// <summary>
/// Default match provider. One <c>/csgo/matches?range[scheduled_at]=…</c> query per poll returns every status inside the
/// window (not started, running, finished, postponed, canceled), so lifecycle changes are seen without extra endpoints.
/// PandaScore states <c>running</c> explicitly, hence <see cref="ProviderCapability.VerifiedLiveStatus"/>.
/// </summary>
public sealed class PandaScoreProvider(PandaScoreClient client, EsportsDataMode mode) : IEsportsDataProvider
{
    public string Id => PandaScoreParser.Source;

    public ProviderCapability Capabilities =>
        ProviderCapability.Fixtures | ProviderCapability.Results | ProviderCapability.Tournaments | ProviderCapability.Teams | ProviderCapability.VerifiedLiveStatus;

    public bool IsConfigured => Problem() is null;

    /// <summary>Last X-Rate-Limit-Remaining seen by this provider's client (diagnostics only).</summary>
    public int? RateLimitRemaining => client.LastRateLimitRemaining;

    public async Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken)
    {
        if (Problem() is { } problem)
            return ProviderResult<IReadOnlyList<EsportsMatch>>.Fail(ProviderOutcome.NotConfigured, problem, window.FromUtc);

        var raw = await client.ListAsync($"{client.Options.Game}/matches",
        [
            new("range[scheduled_at]", $"{Iso(window.FromUtc)},{Iso(window.ToUtc)}"),
            new("sort", "scheduled_at"),
        ], cancellationToken);
        if (!raw.HasData)
            return raw.WithoutValue<IReadOnlyList<EsportsMatch>>();

        var warnings = new List<string>(raw.Warnings ?? []);
        var matches = raw.Value!.Select(e => PandaScoreParser.ParseMatch(e, warnings)).OfType<EsportsMatch>().ToList();
        return raw.Outcome == ProviderOutcome.Partial
            ? ProviderResult<IReadOnlyList<EsportsMatch>>.PartialData(matches, raw.Detail!, raw.At, warnings)
            : ProviderResult<IReadOnlyList<EsportsMatch>>.Ok(matches, raw.At, warnings);
    }

    public async Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (Problem() is { } problem)
            return ProviderResult<IReadOnlyList<EsportsEvent>>.Fail(ProviderOutcome.NotConfigured, problem, DateTimeOffset.MinValue);

        // Stage tournaments that began up to 60 days before the window can still be running inside it.
        var begin = from.AddDays(-60).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = to.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
        var raw = await client.ListAsync($"{client.Options.Game}/tournaments",
        [
            new("range[begin_at]", $"{Iso(begin)},{Iso(end)}"),
            new("sort", "begin_at"),
        ], cancellationToken);
        if (!raw.HasData)
            return raw.WithoutValue<IReadOnlyList<EsportsEvent>>();

        var warnings = new List<string>(raw.Warnings ?? []);
        var events = PandaScoreParser.ParseEvents(raw.Value!, warnings)
            .Where(e => e.EndDate is null || e.EndDate >= from)
            .OrderBy(e => e.StartDate)
            .ToList();
        return raw.Outcome == ProviderOutcome.Partial
            ? ProviderResult<IReadOnlyList<EsportsEvent>>.PartialData(events, raw.Detail!, raw.At, warnings)
            : ProviderResult<IReadOnlyList<EsportsEvent>>.Ok(events, raw.At, warnings);
    }

    private string? Problem() => PandaScoreOptions.ConfigurationProblem(client.Options, requireToken: mode.Mode == ProviderMode.Live);

    private static string Iso(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
