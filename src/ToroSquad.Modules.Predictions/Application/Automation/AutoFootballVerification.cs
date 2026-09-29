using System.Globalization;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application.Automation;

/// <summary>
/// The READ-ONLY provider check behind <c>predictions football-check</c>: what the provider really returns for the three
/// clubs — catalog (which allow-listed competitions are in season), the free events lists (tracked matches, home/away as
/// sent, similar names that did NOT match an alias), and, only when asked, odds within a small credit budget (which
/// bookmakers, whether a complete fresh 1-X-2 set exists and which one the priority would pick). Nothing is written to the
/// database, nothing is sent to Discord, the key is never printed. The caller prints <see cref="Lines"/>.
/// </summary>
public sealed class AutoFootballVerification(IFootballOddsProvider provider, AutoFootballOptions options, TimeProvider clock)
{
    public const int MaxBudget = 25;
    public const int MaxDays = 30;

    public List<string> Lines { get; } = [];

    /// <summary>Credits reported spent by this check (x-requests-last sums; a call without the header counts its documented cost 1).</summary>
    public int CreditsSpent { get; private set; }

    public int? RemainingCredits { get; private set; }

    public int TrackedMatches { get; private set; }

    public async Task<bool> RunAsync(bool withOdds, int budget, CancellationToken ct, int days = 7)
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
        var sports = await provider.GetSportsAsync(ct);
        Note("sports", sports.Outcome, sports.Quota, sports.HttpStatus);
        if (!sports.Ok)
            return false;
        var active = sports.Value!.Where(s => s.Active).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in options.Competitions)
            Lines.Add($"competition {key}: {(active.Contains(key) ? "IN SEASON (listed active)" : "not listed as active now")}");
        Lines.Add("not in the provider's allow-list here (never read): Türkiye Kupası, Türkiye Süper Kupası and any other competition");

        var tracked = new List<(string Sport, ProviderEvent Event)>();
        foreach (var sport in options.Competitions.Where(active.Contains))
        {
            var events = await provider.GetEventsAsync(sport, now - TimeSpan.FromHours(3), now + TimeSpan.FromDays(days), ct);
            Note("events " + sport, events.Outcome, events.Quota, events.HttpStatus);
            if (!events.Ok)
                continue;
            var list = events.Value!;
            var mine = list.Where(e => TrackedTeams.Match(e.HomeTeam) is not null || TrackedTeams.Match(e.AwayTeam) is not null).ToList();
            Lines.Add($"  {list.Count} event(s) in the next {days} days, {mine.Count} with a followed club");
            foreach (var e in mine.OrderBy(e => e.CommenceTime))
            {
                var local = zone is null ? "" : " (" + TimeZoneInfo.ConvertTime(e.CommenceTime, zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) + " TR)";
                Lines.Add($"  match {e.Id[..Math.Min(8, e.Id.Length)]}… {e.CommenceTime:yyyy-MM-dd HH:mm}Z{local}  home: {e.HomeTeam}  away: {e.AwayTeam}  " +
                          $"[{string.Join(',', new[] { TrackedTeams.Match(e.HomeTeam)?.Code, TrackedTeams.Match(e.AwayTeam)?.Code }.OfType<string>())}]");
                tracked.Add((sport, e));
            }

            // A spelling the aliases do not know yet would silently miss a match: list lookalikes for review (never auto-matched).
            foreach (var name in list.SelectMany(e => new[] { e.HomeTeam, e.AwayTeam }).Distinct(StringComparer.Ordinal)
                         .Where(n => TrackedTeams.Match(n) is null && Lookalike(n)))
                Lines.Add($"  REVIEW: similar name not matched: '{name}'");
        }

        TrackedMatches = tracked.Count;
        if (!withOdds || budget == 0)
        {
            Lines.Add("odds: not requested (use --odds [--budget N]; each call costs 1 credit when it returns data)");
            return true;
        }

        foreach (var group in tracked.GroupBy(t => t.Sport, StringComparer.Ordinal))
        {
            if (CreditsSpent + 1 > budget)
            {
                Lines.Add($"odds {group.Key}: skipped (budget {budget} reached)");
                continue;
            }

            if (RemainingCredits is { } left && left - 1 < options.CreditReserve)
            {
                Lines.Add($"odds {group.Key}: skipped (provider reports {left} credits left; reserve {options.CreditReserve})");
                continue;
            }

            var odds = await provider.GetOddsAsync(group.Key, group.Select(t => t.Event.Id).ToList(), ct);
            CreditsSpent += odds.Quota.LastCost ?? (odds.Ok || odds.MayHaveCost ? 1 : 0);
            Note("odds " + group.Key, odds.Outcome, odds.Quota, odds.HttpStatus);
            if (!odds.Ok)
                continue;
            foreach (var (_, e) in group)
            {
                var match = odds.Value!.FirstOrDefault(o => o.Id == e.Id);
                if (match is null)
                {
                    Lines.Add($"  match {e.Id[..Math.Min(8, e.Id.Length)]}…: no odds returned (NO_ODDS)");
                    continue;
                }

                var h2h = match.Bookmakers.Where(b => b.Markets.Any(m => m.Key == OddsSelector.Market)).Select(b => b.Key).ToList();
                Lines.Add($"  match {e.Id[..Math.Min(8, e.Id.Length)]}…: {match.Bookmakers.Count} bookmaker(s), h2h from: {(h2h.Count == 0 ? "none" : string.Join(", ", h2h))}");
                var (selected, reason) = OddsSelector.Select(match, options.Bookmakers, now, options.MaxOddsAge);
                Lines.Add(selected is null
                    ? $"    would NOT open: {AutoBlockCodes.Code(reason)}"
                    : $"    would open with {selected.BookmakerKey}: {Odds.Format(selected.HomeX100)} / {Odds.Format(selected.DrawX100)} / {Odds.Format(selected.AwayX100)} " +
                      $"(raw {OddsSelector.Raw(selected.HomeRaw)} / {OddsSelector.Raw(selected.DrawRaw)} / {OddsSelector.Raw(selected.AwayRaw)}; market updated {selected.LastUpdate:yyyy-MM-dd HH:mm:ss}Z)");
            }
        }

        Lines.Add($"credits spent by this check: {CreditsSpent}; remaining reported: {(RemainingCredits is { } r ? r.ToString(CultureInfo.InvariantCulture) : "unknown")}");
        return true;
    }

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
               folded.Contains("besiktas", StringComparison.Ordinal);
    }
}
