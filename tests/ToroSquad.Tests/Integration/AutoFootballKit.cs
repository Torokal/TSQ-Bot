using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Predictions;
using ToroSquad.Modules.Predictions.Application.Automation;
using ToroSquad.Modules.Predictions.Domain;
using ToroSquad.Modules.Predictions.Persistence;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// A scripted odds provider for the automation tests. SYNTHETIC: every team, id and price here is made up; nothing proves
/// the real provider's coverage. It counts every call by endpoint, charges its own "credits" like the documentation says
/// (odds calls cost 1 when they return data) and can fail, rate-limit, omit usage headers or hold an odds call at a barrier.
/// </summary>
public sealed class FakeFootballOdds : IFootballOddsProvider
{
    public const string SuperLig = "soccer_turkey_super_league";
    public const string Europa = "soccer_uefa_europa_league";
    public const string NationsLeague = "soccer_uefa_nations_league";
    public const string WorldCupQualifiers = "soccer_fifa_world_cup_qualifiers_europe";
    public const string Euro = "soccer_uefa_european_championship";

    private readonly Lock _lock = new();

    public string Name => AutoFootballOptions.ProviderName;
    public bool IsConfigured { get; set; } = true;
    public TimeProvider? Clock { get; set; }
    public HashSet<string> Active { get; } = [.. AutoFootballOptions.KnownCompetitions];

    /// <summary>Catalog entries that are outright ("who wins") markets, listed with has_outrights.</summary>
    public HashSet<string> Outrights { get; } = ["soccer_fifa_world_cup_winner"];

    /// <summary>Malformed items the next events answers report as dropped (a partial list).</summary>
    public int DroppedEvents { get; set; }
    public Dictionary<string, List<ProviderEvent>> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, (decimal Home, decimal Draw, decimal Away, string Bookmaker, TimeSpan Age)> Prices { get; } = new(StringComparer.Ordinal);
    public int Remaining { get; set; } = 400;
    public bool OmitUsageHeaders { get; set; }
    public ProviderCallOutcome? FailEverything { get; set; }
    public ProviderCallOutcome? FailOdds { get; set; }
    public TimeSpan? RetryAfter { get; set; }
    public Func<Task>? BeforeOdds { get; set; }
    public List<string> Calls { get; } = [];

    public int OddsCalls => Count("odds");
    public int FreeCalls => Count("sports") + Count("events");

    public int Count(string endpoint)
    {
        lock (_lock)
            return Calls.Count(c => c.StartsWith(endpoint, StringComparison.Ordinal));
    }

    public static string Id(int n) => n.ToString("x32", CultureInfo.InvariantCulture);

    public ProviderEvent Add(int n, string home, string away, DateTimeOffset kickoff, string sport = SuperLig)
    {
        var e = new ProviderEvent(Id(n), sport, kickoff, home, away);
        if (!Events.TryGetValue(sport, out var list))
            Events[sport] = list = [];
        list.RemoveAll(x => x.Id == e.Id);
        list.Add(e);
        return e;
    }

    public void Price(ProviderEvent e, decimal home = 1.85m, decimal draw = 3.40m, decimal away = 4.20m, string bookmaker = "pinnacle", TimeSpan? age = null) =>
        Prices[e.Id] = (home, draw, away, bookmaker, age ?? TimeSpan.FromMinutes(5));

    private ProviderQuota Quota(int last) => OmitUsageHeaders ? ProviderQuota.None : new ProviderQuota(Remaining, 500 - Remaining, last);

    public Task<ProviderCall<IReadOnlyList<ProviderSport>>> GetSportsAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        Record("sports" + (includeInactive ? ":all" : ""));
        if (FailEverything is { } fail)
            return Task.FromResult(new ProviderCall<IReadOnlyList<ProviderSport>>(fail, null, Quota(0), RetryAfter: RetryAfter));
        IReadOnlyList<ProviderSport> sports = AutoFootballOptions.KnownCompetitions.Select(k => new ProviderSport(k, k, Active.Contains(k), Outrights.Contains(k)))
            .Concat(Outrights.Select(k => new ProviderSport(k, k, true, true))).Where(s => includeInactive || s.Active).ToList();
        return Task.FromResult(new ProviderCall<IReadOnlyList<ProviderSport>>(ProviderCallOutcome.Ok, sports, Quota(0)));
    }

    public Task<ProviderCall<IReadOnlyList<ProviderEvent>>> GetEventsAsync(string sportKey, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        Record("events:" + sportKey);
        if (FailEverything is { } fail)
            return Task.FromResult(new ProviderCall<IReadOnlyList<ProviderEvent>>(fail, null, Quota(0), RetryAfter: RetryAfter));
        IReadOnlyList<ProviderEvent> list = (Events.GetValueOrDefault(sportKey) ?? []).Where(e => e.CommenceTime >= from && e.CommenceTime <= to).ToList();
        return Task.FromResult(new ProviderCall<IReadOnlyList<ProviderEvent>>(ProviderCallOutcome.Ok, list, Quota(0), Dropped: DroppedEvents));
    }

    public async Task<ProviderCall<IReadOnlyList<ProviderOddsEvent>>> GetOddsAsync(string sportKey, IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        Record("odds:" + sportKey + ":" + string.Join(',', eventIds));
        if (BeforeOdds is { } gate)
            await gate();
        if ((FailEverything ?? FailOdds) is { } fail)
            return new ProviderCall<IReadOnlyList<ProviderOddsEvent>>(fail, null, Quota(0), RetryAfter: RetryAfter, MayHaveCost: fail is ProviderCallOutcome.Timeout);
        var now = (Clock ?? TimeProvider.System).GetUtcNow();
        var events = (Events.GetValueOrDefault(sportKey) ?? []).Where(e => eventIds.Contains(e.Id) && Prices.ContainsKey(e.Id)).Select(e =>
        {
            var p = Prices[e.Id];
            return new ProviderOddsEvent(e.Id, e.SportKey, e.CommenceTime, e.HomeTeam, e.AwayTeam,
            [
                new ProviderBookmaker(p.Bookmaker, p.Bookmaker.ToUpperInvariant(),
                    [new ProviderMarket("h2h", now - p.Age, [new ProviderPrice(e.AwayTeam, p.Away), new ProviderPrice("Draw", p.Draw), new ProviderPrice(e.HomeTeam, p.Home)])]),
            ]);
        }).ToList();
        var cost = events.Count > 0 ? 1 : 0; // documented: an empty answer costs nothing
        lock (_lock)
            Remaining -= cost;
        return new ProviderCall<IReadOnlyList<ProviderOddsEvent>>(ProviderCallOutcome.Ok, events, Quota(cost));
    }

    private void Record(string call)
    {
        lock (_lock)
            Calls.Add(call);
    }
}

/// <summary>Automation helpers on top of <see cref="PredictionTestKit"/> (guild 777 is the single allowed guild).</summary>
public static class AutoFootballKit
{
    public static Dictionary<string, string?> Settings(string mode, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Discord:AllowedGuildIds:0"] = PredictionTestKit.Guild.Value.ToString(CultureInfo.InvariantCulture),
            ["Predictions:Automation:Mode"] = mode,
        };
        foreach (var (k, v) in extra ?? [])
            settings[k] = v;
        return settings;
    }

    /// <summary>SYNTHETIC approval for the tests (production approves only bookmakers whose rule is verified — pinnacle).</summary>
    public static readonly FootballMarketRules TestRules = new(["pinnacle", "onexbet"]);

    public static async Task<PredictionTestKit> CreateAsync(FakeFootballOdds odds, string mode, DateTimeOffset start, Dictionary<string, string?>? extra = null,
        string? directory = null, FakeMessageTransport? transport = null, FootballMarketRules? rules = null)
    {
        var kit = await PredictionTestKit.CreateAsync(start, Settings(mode, extra), directory, transport, s =>
        {
            s.AddSingleton<IFootballOddsProvider>(odds);
            s.AddSingleton(rules ?? TestRules);
        });
        odds.Clock = kit.Host.Clock;
        return kit;
    }

    public static Task<AutoRunSummary> PassAsync(this PredictionTestKit kit) =>
        kit.Host.InScopeAsync(sp => sp.GetRequiredService<AutoFootballService>().RunOnceAsync(TestContext.Current.CancellationToken));

    public static Task<List<PredictionAutoEventEntity>> AutoRowsAsync(this PredictionTestKit kit) =>
        kit.Db(db => db.Set<PredictionAutoEventEntity>().AsNoTracking().OrderBy(a => a.Id).ToListAsync());

    public static Task<PredictionAutoProviderEntity?> ProviderRowAsync(this PredictionTestKit kit) =>
        kit.Db(db => db.Set<PredictionAutoProviderEntity>().AsNoTracking().FirstOrDefaultAsync());

    public static Task<List<PredictionEntity>> AutoPredictionsAsync(this PredictionTestKit kit) =>
        kit.Db(db => db.Set<PredictionEntity>().AsNoTracking().Where(p => p.Origin == PredictionOrigin.AutoFootball).OrderBy(p => p.Id).ToListAsync());
}
