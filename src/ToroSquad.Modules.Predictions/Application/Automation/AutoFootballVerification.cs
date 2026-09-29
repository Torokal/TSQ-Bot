using System.Globalization;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application.Automation;

/// <summary>One tracked match the check found, with what the selection made of its odds (null choice: odds not read).</summary>
public sealed record VerifiedMatch(string Competition, TeamScope Scope, ProviderEvent Event, OddsChoice? Choice);

/// <summary>
/// The READ-ONLY provider check behind <c>predictions football-check</c>: what the provider really returns for the followed
/// teams — the whole catalog (each allow-listed competition: in season / known but not in season / not in the catalog;
/// outrights never read), the free events lists (tracked matches with home/away as sent, full event id, UTC and Türkiye
/// time; similar names that did NOT match an alias), and, only when asked, odds within a small TOTAL credit budget — the
/// followed team given by <c>focus</c> first — with the bookmakers returned, the three outcomes, the set our rules would
/// take (approved bookmakers only) or the reason (valid data rejected by our rule is said as such). Nothing is written to
/// the database, nothing is sent to Discord, the key is never printed. The caller prints <see cref="Lines"/>.
/// </summary>
public sealed class AutoFootballVerification(IFootballOddsProvider provider, AutoFootballOptions options, TimeProvider clock, FootballMarketRules? rules = null)
{
    public const int MaxBudget = 25;
    public const int MaxDays = 30;

    private readonly FootballMarketRules _rules = rules ?? FootballMarketRules.Production;

    public List<string> Lines { get; } = [];

    public List<VerifiedMatch> Matches { get; } = [];

    /// <summary>Credits reported spent by this check (x-requests-last sums; a call without the header counts its documented cost 1).</summary>
    public int CreditsSpent { get; private set; }

    public int? RemainingCredits { get; private set; }

    public int TrackedMatches => Matches.Count;

    public async Task<bool> RunAsync(bool withOdds, int budget, CancellationToken ct, int days = 7, string? focus = null)
    {
        budget = Math.Clamp(budget, 0, MaxBudget);
        days = Math.Clamp(days, 1, MaxDays);
        if (!provider.IsConfigured)
        {
            Lines.Add("BLOCKED: no API key (" + AutoFootballOptions.ApiKeySetting + "); nothing was requested");
            return false;
        }

        var now = clock.GetUtcNow();
        var zone = options.Timing()?.Zone;
        Lines.Add($"source: live HTTP ({provider.GetType().Name}) to {new Uri(options.BaseUrl).Host}; checked at {now:yyyy-MM-dd HH:mm:ss}Z; no fixture, no cache");
        Lines.Add("followed teams: " + string.Join(", ", TrackedTeams.All.Select(t => $"{t.DisplayName} ({t.Code}, {t.Scope})")));
        var usable = _rules.Usable(options.Bookmakers);
        Lines.Add("bookmakers Live may use (full-time rule verified): " + (usable.Count == 0 ? "NONE — MARKET_RULE_UNVERIFIED" : string.Join(", ", usable)));

        var sports = await provider.GetSportsAsync(includeInactive: true, ct);
        Note("sports (all=true)", sports.Outcome, sports.Quota, sports.HttpStatus);
        if (!sports.Ok)
            return false;
        var catalog = sports.Value!.GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var inSeason = new List<string>();
        foreach (var key in options.Competitions)
        {
            var scope = AutoFootballOptions.ScopeOf(key);
            if (!catalog.TryGetValue(key, out var sport))
            {
                Lines.Add($"competition {key} [{scope}]: NOT IN THE PROVIDER'S CATALOG (unsupported)");
            }
            else if (sport.HasOutrights)
            {
                Lines.Add($"competition {key} [{scope}]: outright market (not matches) — never read");
            }
            else if (!sport.Active)
            {
                Lines.Add($"competition {key} [{scope}]: supported, not in season now (joins when the catalog lists it active)");
            }
            else
            {
                Lines.Add($"competition {key} [{scope}]: IN SEASON");
                inSeason.Add(key);
            }
        }

        foreach (var outright in catalog.Values.Where(s => s.HasOutrights && s.Key.StartsWith("soccer_", StringComparison.Ordinal) &&
                                                          (s.Key.Contains("world_cup", StringComparison.Ordinal) || s.Key.Contains("euro", StringComparison.Ordinal))))
            Lines.Add($"excluded outright (tournament winner, not a match): {outright.Key}");
        Lines.Add("not covered: friendlies (no provider key), Türkiye Kupası, Türkiye Süper Kupası, any other competition");

        foreach (var sport in inSeason)
        {
            var scope = AutoFootballOptions.ScopeOf(sport)!.Value;
            var events = await provider.GetEventsAsync(sport, now - TimeSpan.FromHours(3), now + TimeSpan.FromDays(days), ct);
            Note("events " + sport, events.Outcome, events.Quota, events.HttpStatus);
            if (!events.Ok)
                continue;
            var list = events.Value!;
            var mine = list.Where(e => TrackedTeams.Match(e.HomeTeam, scope) is not null || TrackedTeams.Match(e.AwayTeam, scope) is not null).ToList();
            Lines.Add($"  {list.Count} event(s) in the next {days} days, {mine.Count} with a followed team{(events.Dropped > 0 ? $"; {events.Dropped} malformed item(s) dropped" : "")}");
            foreach (var e in mine.OrderBy(e => e.CommenceTime))
            {
                var local = zone is null ? "" : " / " + TimeZoneInfo.ConvertTime(e.CommenceTime, zone).ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " TR";
                var codes = string.Join(',', new[] { TrackedTeams.Match(e.HomeTeam, scope)?.Code, TrackedTeams.Match(e.AwayTeam, scope)?.Code }.OfType<string>());
                Lines.Add($"  match {e.Id}  {e.CommenceTime:yyyy-MM-dd HH:mm}Z{local}  home: {e.HomeTeam}  away: {e.AwayTeam}  [{codes}]");
                Matches.Add(new VerifiedMatch(sport, scope, e, null));
            }

            // A spelling the aliases do not know yet would silently miss a match: list lookalikes for review (never auto-matched).
            foreach (var name in list.SelectMany(e => new[] { e.HomeTeam, e.AwayTeam }).Distinct(StringComparer.Ordinal)
                         .Where(n => TrackedTeams.Match(n, scope) is null && Lookalike(n)))
                Lines.Add($"  REVIEW: similar name not matched (not followed): '{name}'");
        }

        if (!withOdds || budget == 0)
        {
            Lines.Add("odds: not requested (use --odds [--budget N]; each call costs 1 credit when it returns data)");
            return true;
        }

        // The focus team's competitions first; one call per competition for all its tracked (focus) matches.
        var groups = Matches.GroupBy(m => m.Competition, StringComparer.Ordinal)
            .OrderByDescending(g => focus is not null && g.Any(m => Codes(m).Contains(focus, StringComparer.OrdinalIgnoreCase)))
            .ThenBy(g => g.Min(m => m.Event.CommenceTime)).ToList();
        foreach (var group in groups)
        {
            var wanted = group.Where(m => focus is null || Codes(m).Contains(focus, StringComparer.OrdinalIgnoreCase)).ToList();
            if (wanted.Count == 0)
                continue;
            if (CreditsSpent + 1 > budget)
            {
                Lines.Add($"odds {group.Key}: skipped (total budget {budget} reached)");
                continue;
            }

            if (RemainingCredits is { } left && left - 1 < options.CreditReserve)
            {
                Lines.Add($"odds {group.Key}: skipped (provider reports {left} credits left; reserve {options.CreditReserve})");
                continue;
            }

            var odds = await provider.GetOddsAsync(group.Key, wanted.Select(m => m.Event.Id).ToList(), ct);
            CreditsSpent += odds.Quota.LastCost ?? (odds.Ok || odds.MayHaveCost ? 1 : 0);
            Note("odds " + group.Key, odds.Outcome, odds.Quota, odds.HttpStatus);
            if (!odds.Ok)
            {
                if (odds.Outcome == ProviderCallOutcome.AuthFailed)
                    Lines.Add("  AUTH_ERROR: the key was refused");
                continue;
            }

            foreach (var m in wanted)
                Report(m, odds.Value!.FirstOrDefault(o => o.Id == m.Event.Id), now);
        }

        Lines.Add($"credits spent by this check: {CreditsSpent}; remaining reported: {(RemainingCredits is { } r ? r.ToString(CultureInfo.InvariantCulture) : "unknown")}");
        return true;
    }

    private void Report(VerifiedMatch m, ProviderOddsEvent? match, DateTimeOffset now)
    {
        if (match is null)
        {
            Lines.Add($"  match {m.Event.Id}: NO_ODDS (not in the odds answer)");
            return;
        }

        var h2h = match.Bookmakers.Where(b => b.Markets.Any(x => x.Key == OddsSelector.Market)).Select(b => b.Key).ToList();
        Lines.Add($"  match {m.Event.Id}: {match.Bookmakers.Count} bookmaker(s); h2h from: {(h2h.Count == 0 ? "none" : string.Join(", ", h2h))}");
        var choice = OddsSelector.Choose(match, options.Bookmakers, _rules.ApprovedBookmakers, now, options.MaxOddsAge);
        Matches[Matches.IndexOf(m)] = m with { Choice = choice };
        var shown = choice.Odds ?? choice.Unapproved;
        if (shown is not null)
        {
            Lines.Add($"    outcomes: {match.HomeTeam} / Draw / {match.AwayTeam} (by name; response order ignored)");
            Lines.Add($"    {(choice.Odds is null ? "candidate" : "selected")} {shown.BookmakerKey}: raw {OddsSelector.Raw(shown.HomeRaw)} / {OddsSelector.Raw(shown.DrawRaw)} / " +
                      $"{OddsSelector.Raw(shown.AwayRaw)} → fixed {Odds.Format(shown.HomeX100)} / {Odds.Format(shown.DrawX100)} / {Odds.Format(shown.AwayX100)}; " +
                      $"market updated {shown.LastUpdate:yyyy-MM-dd HH:mm:ss}Z (age {(now - shown.LastUpdate).Ticks / TimeSpan.TicksPerSecond} s)");
        }

        Lines.Add(choice.Odds is not null ? "    ACCEPTED" : $"    REJECTED: {AutoBlockCodes.Code(choice.Reason)}");
    }

    private static IEnumerable<string> Codes(VerifiedMatch m) =>
        new[] { TrackedTeams.Match(m.Event.HomeTeam, m.Scope)?.Code, TrackedTeams.Match(m.Event.AwayTeam, m.Scope)?.Code }.OfType<string>();

    private void Note(string what, ProviderCallOutcome outcome, ProviderQuota quota, int? status)
    {
        if (quota.Remaining is { } remaining)
            RemainingCredits = remaining;
        Lines.Add($"{what}: {outcome}{(status is { } s ? " (HTTP " + s.ToString(CultureInfo.InvariantCulture) + ")" : "")}; " +
                  $"x-requests-remaining={Show(quota.Remaining)} used={Show(quota.Used)} last={Show(quota.LastCost)}");
    }

    private static string Show(int? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "?";

    private static bool Lookalike(string name)
    {
        var folded = TrackedTeams.Fold(name);
        return folded.Contains("galatasaray", StringComparison.Ordinal) || folded.Contains("fenerbahce", StringComparison.Ordinal) ||
               folded.Contains("besiktas", StringComparison.Ordinal) || folded.Contains("turkey", StringComparison.Ordinal) ||
               folded.Contains("turkiye", StringComparison.Ordinal);
    }
}
