using System.Globalization;
using System.Text.Json;
using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Modules.Esports.Providers.PandaScore;

/// <summary>
/// Maps PandaScore match/tournament JSON (fields and lifecycle per developers.pandascore.co "Matches lifecycle",
/// verified 2026-09-25) to the provider-independent model. Rules:
/// <list type="bullet">
/// <item><c>not_started</c> → Scheduled (with <c>rescheduled</c>/<c>original_scheduled_at</c>), <c>running</c> → Live,
/// <c>finished</c> → Finished, <c>postponed</c> → Postponed, <c>canceled</c> → Cancelled, or a forfeit result when
/// <c>forfeit</c> is true. Any other value → Unknown (never guessed).</item>
/// <item><c>begin_at</c> equals <c>scheduled_at</c> for not-started matches, so it is only used as the actual start once
/// the match is running/finished.</item>
/// <item>Scores are taken from <c>results</c> only when the match is running, finished or forfeited — a scheduled
/// match's 0–0 is not a result. Winners come only from <c>winner_id</c>.</item>
/// <item>PandaScore exposes no public match page, so no match link is produced here.</item>
/// </list>
/// </summary>
public static class PandaScoreParser
{
    public const string Source = "pandascore";

    public static string TeamKey(long id) => string.Create(CultureInfo.InvariantCulture, $"ps-team:{id}");

    public static EsportsMatch? ParseMatch(JsonElement e, List<string> warnings)
    {
        if (e.ValueKind != JsonValueKind.Object || Long(e, "id") is not { } id)
        {
            warnings.Add("match without id skipped");
            return null;
        }

        var matchId = id.ToString(CultureInfo.InvariantCulture);
        var statusText = Str(e, "status");
        var forfeit = Bool(e, "forfeit") == true;
        var status = statusText switch
        {
            "not_started" => MatchStatus.Scheduled,
            "running" => MatchStatus.Live,
            "finished" => MatchStatus.Finished,
            "postponed" => MatchStatus.Postponed,
            "canceled" when forfeit => MatchStatus.Finished,
            "canceled" => MatchStatus.Cancelled,
            _ => MatchStatus.Unknown,
        };
        if (status == MatchStatus.Unknown)
            warnings.Add($"match {matchId}: unknown status '{Trim(statusText)}' kept as Unknown");

        var scheduled = Date(e, "scheduled_at", matchId, warnings);
        var begin = status is MatchStatus.Live or MatchStatus.Finished && !forfeit ? Date(e, "begin_at", matchId, warnings) : null;
        var end = status == MatchStatus.Finished && !forfeit ? Date(e, "end_at", matchId, warnings) : null;
        var original = Date(e, "original_scheduled_at", matchId, warnings);
        var rescheduled = Bool(e, "rescheduled") == true;

        var opponents = Opponents(e);
        var teamA = opponents.Count > 0 ? opponents[0] : null;
        var teamB = opponents.Count > 1 ? opponents[1] : null;
        var winnerId = Long(e, "winner_id");
        var scoresKnown = status is MatchStatus.Live or MatchStatus.Finished && !forfeit;
        var scores = scoresKnown ? Scores(e) : new Dictionary<long, int>();

        int? winnerIndex = winnerId is { } w
            ? (teamA?.Id == w ? 0 : teamB?.Id == w ? 1 : null)
            : null;
        if (winnerId is not null && winnerIndex is null)
            warnings.Add($"match {matchId}: winner_id is not one of the opponents");
        var isDraw = status == MatchStatus.Finished && Bool(e, "draw") == true;

        MatchOpponent Opponent(Team? team, int index)
        {
            if (team is null)
                return MatchOpponent.Tbd;
            if (!team.IsTeam)
                return MatchOpponent.UnknownOpponent;
            var result = status != MatchStatus.Finished ? OpponentResult.Scored
                : isDraw ? OpponentResult.Draw
                : winnerIndex is null ? OpponentResult.Scored
                : winnerIndex == index ? OpponentResult.Win
                : forfeit ? OpponentResult.Forfeit : OpponentResult.Loss;
            int? score = scores.TryGetValue(team.Id, out var s) ? s : null;
            return new MatchOpponent(OpponentKind.Team, new TeamRef(Source, TeamKey(team.Id), team.Name, team.Acronym), score, result);
        }

        var a = Opponent(teamA, 0);
        var b = Opponent(teamB, 1);
        if (!scoresKnown || a.Score is null || b.Score is null)
        {
            a = a with { Score = null };
            b = b with { Score = null };
        }

        var bestOf = Str(e, "match_type") == "best_of" ? (int?)Long(e, "number_of_games") : null;

        return new EsportsMatch(
            new MatchKey(Source, matchId),
            Tournament(e),
            scheduled,
            StartTimeExact: scheduled is not null,
            bestOf,
            status,
            $"pandascore status={Trim(statusText)}{(forfeit ? " forfeit" : "")}",
            a,
            b,
            winnerIndex,
            isDraw,
            IsForfeit: forfeit,
            Games(e, teamA?.Id, teamB?.Id),
            Stage: NonEmpty(Str(Obj(e, "tournament"), "name")),
            SourceUrl: null,
            Streams(e),
            begin,
            end,
            original,
            rescheduled,
            MatchLinks.None);
    }

    /// <summary>One event per PandaScore serie (a serie groups its stage tournaments).</summary>
    public static List<EsportsEvent> ParseEvents(IEnumerable<JsonElement> tournaments, List<string> warnings)
    {
        var events = new List<EsportsEvent>();
        foreach (var group in tournaments.Where(t => t.ValueKind == JsonValueKind.Object).GroupBy(t => Long(t, "serie_id") ?? -Long(t, "id") ?? 0))
        {
            var items = group.ToList();
            var first = items[0];
            var name = EventName(Obj(first, "league"), Obj(first, "serie"), Str(first, "name"));
            if (name is null)
            {
                warnings.Add("tournament without name skipped");
                continue;
            }

            var starts = items.Select(t => Date(t, "begin_at", "event", warnings)).OfType<DateTimeOffset>().ToList();
            var ends = items.Select(t => Date(t, "end_at", "event", warnings)).OfType<DateTimeOffset>().ToList();
            var teams = items.SelectMany(t => Arr(t, "teams")).Select(t => Long(t, "id")).OfType<long>().Distinct().Count();
            events.Add(new EsportsEvent(
                new TournamentRef(Source, string.Create(CultureInfo.InvariantCulture, $"ps-serie:{group.Key}"), name, TierScale(Str(first, "tier")), null, null, null),
                starts.Count > 0 ? DateOnly.FromDateTime(starts.Min().UtcDateTime) : null,
                ends.Count > 0 ? DateOnly.FromDateTime(ends.Max().UtcDateTime) : null,
                NonEmpty(Str(first, "country")) ?? NonEmpty(Str(first, "region")),
                NonEmpty(Str(first, "type")),
                PrizePool(items.Select(t => Str(t, "prizepool")).FirstOrDefault(p => p is not null)),
                teams > 0 ? teams : null,
                SourceUrl: null));
        }

        return events;
    }

    /// <summary>PandaScore tiers s/a/b/c/d on the existing 1..5 scale used by tier filters; "unranked" → none.</summary>
    public static string? TierScale(string? tier) => tier switch
    {
        "s" => "1",
        "a" => "2",
        "b" => "3",
        "c" => "4",
        "d" => "5",
        _ => null,
    };

    private sealed record Team(long Id, string Name, string? Acronym, bool IsTeam);

    private static List<Team?> Opponents(JsonElement e)
    {
        var list = new List<Team?>();
        foreach (var o in Arr(e, "opponents"))
        {
            var opponent = Obj(o, "opponent");
            if (Long(opponent, "id") is not { } oid || NonEmpty(Str(opponent, "name")) is not { } name)
            {
                list.Add(null);
                continue;
            }

            list.Add(new Team(oid, name, NonEmpty(Str(opponent, "acronym")), Str(o, "type") == "Team"));
        }

        return list;
    }

    private static Dictionary<long, int> Scores(JsonElement e)
    {
        var scores = new Dictionary<long, int>();
        foreach (var r in Arr(e, "results"))
        {
            if (Long(r, "team_id") is { } team && Long(r, "score") is { } score && score is >= 0 and <= 99)
                scores[team] = (int)score;
        }

        return scores;
    }

    private static List<MapGame> Games(JsonElement e, long? teamA, long? teamB) =>
        Arr(e, "games")
            .Select(g =>
            {
                var status = Str(g, "status") switch
                {
                    "finished" => GameStatus.Played,
                    "not_played" => GameStatus.NotPlayed,
                    _ => GameStatus.Unknown,
                };
                var winner = Long(Obj(g, "winner"), "id");
                int? winnerIndex = winner is null ? null : winner == teamA ? 0 : winner == teamB ? 1 : null;
                return new MapGame((int)(Long(g, "position") ?? 0), null, status, null, null, winnerIndex);
            })
            .Where(m => m.Index > 0)
            .OrderBy(m => m.Index)
            .ToList();

    private static TournamentRef Tournament(JsonElement e)
    {
        var tournament = Obj(e, "tournament");
        var name = EventName(Obj(e, "league"), Obj(e, "serie"), Str(tournament, "name")) ?? "?";
        var key = Long(e, "tournament_id") ?? Long(tournament, "id");
        return new TournamentRef(
            Source,
            key is null ? "ps-tournament:?" : string.Create(CultureInfo.InvariantCulture, $"ps-tournament:{key}"),
            name,
            TierScale(Str(tournament, "tier")),
            null,
            null,
            Long(e, "serie_id") is { } serie ? string.Create(CultureInfo.InvariantCulture, $"ps-serie:{serie}") : null);
    }

    /// <summary>"League + serie" (e.g. "StarLadder StarSeries" + "Fall 2026"), falling back to what exists.</summary>
    private static string? EventName(JsonElement league, JsonElement serie, string? fallback)
    {
        var l = NonEmpty(Str(league, "name"));
        var s = NonEmpty(Str(serie, "full_name")) ?? NonEmpty(Str(serie, "name"));
        if (l is not null && s is not null)
            return s.StartsWith(l, StringComparison.OrdinalIgnoreCase) ? s : $"{l} {s}";
        return l ?? s ?? NonEmpty(fallback);
    }

    private static List<StreamLink> Streams(JsonElement e) =>
        Arr(e, "streams_list")
            .OrderByDescending(s => Bool(s, "main") == true)
            .ThenByDescending(s => Bool(s, "official") == true)
            .Select(s => Str(s, "raw_url"))
            .OfType<string>()
            .Select(url => new StreamLink(Platform(url), url))
            .Take(5)
            .ToList();

    private static string Platform(string url) =>
        url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase) ? "Twitch"
        : url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ? "YouTube"
        : url.Contains("kick.com", StringComparison.OrdinalIgnoreCase) ? "Kick"
        : "Stream";

    private static double? PrizePool(string? text)
    {
        // e.g. "250000 United States Dollar"; only USD amounts are shown as a number.
        if (text is null || !text.EndsWith("United States Dollar", StringComparison.Ordinal))
            return null;
        var number = text.Split(' ')[0];
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static DateTimeOffset? Date(JsonElement e, string name, string context, List<string> warnings)
    {
        var text = Str(e, name);
        if (text is null)
            return null;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            return value;
        warnings.Add($"{context}: invalid {name}");
        return null;
    }

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Trim(string? s) => s is null ? "null" : s.Length <= 30 ? s : s[..30];
}
