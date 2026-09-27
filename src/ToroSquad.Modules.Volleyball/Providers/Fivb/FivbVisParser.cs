using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Volleyball.Domain;

namespace ToroSquad.Modules.Volleyball.Providers.Fivb;

/// <summary>Parsed VIS match list: normalized matches, the list's global version and per-item problems.</summary>
public sealed record FivbMatchList(IReadOnlyList<VolleyballMatch> Matches, long? Version, int ItemsRejected, int ItemsTotal)
{
    /// <summary>Per-match VIS version (changes whenever that match changes; used to detect a frozen live feed).</summary>
    public IReadOnlyDictionary<string, long> ItemVersions { get; init; } = new Dictionary<string, long>(StringComparer.Ordinal);
}

/// <summary>
/// Normalizes VIS <c>GetVolleyMatchList</c> JSON (<c>{"data":[…],"nbItems":N,"version":V}</c>, camelCase fields) into
/// <see cref="VolleyballMatch"/>. Contract (verified against real responses on 2026-09-26, see
/// docs/volleyball/PROVIDER_RESEARCH.md):
/// <list type="bullet">
/// <item><c>dateTimeUtc</c> is UTC ("…Z"); <c>dateTimeLocal</c> has NO offset and is never used for scheduling. The start is
/// only taken when <c>scheduleInfo</c> says date AND time are confirmed (4) — a "TBC" time never drives a reminder.</item>
/// <item><c>status</c>: 1–3 not started; 4–23 in play (Set N ready / in set N / set N finished); 24 finished, 25 official,
/// 26 corrected, 27 closed. VIS has no postponed/cancelled status. A forfeit (<c>resultType</c> ≠ 0) is not a played
/// result and is left Unknown (no card).</item>
/// <item><c>matchPointsA/B</c> = sets won; <c>pointsTeamASetN/BSetN</c> = set points; <c>nbSets</c> = current set while live.</item>
/// <item>Team identity: country code = <c>teamACode</c> (only meaningful for national-team tournaments), gender from the
/// tournament relation (<c>gender</c> 0 men, 1 women), senior level only for allow-listed tournament types and never when
/// the tournament or team name/code carries an age marker (U17…U23, girls, junior, youth).</item>
/// </list>
/// Missing required properties (renamed/removed fields, inaccessible data) are a schema change — never "no data".
/// </summary>
public static partial class FivbVisParser
{
    public const string ProviderId = "fivb";

    /// <summary>Provider id of the synthetic fixture (TEST/DEMO) data: its rows can never be mistaken for real FIVB data.</summary>
    public const string DemoProviderId = "fivb-demo";

    /// <summary>VIS VolleyTournamentType values (docs: VolleyTournamentType.html).</summary>
    public static readonly IReadOnlyDictionary<string, int> KnownTournamentTypes = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Unknown"] = 1,
        ["OlympicGames"] = 2,
        ["WorldChampionship"] = 3,
        ["WorldGrandPrix"] = 4,
        ["WorldLeague"] = 5,
        ["Test"] = 6,
        ["CevChallengeCup"] = 7,
        ["CevChampionsLeague"] = 8,
        ["CevCup"] = 9,
        ["ContinentalChampionship"] = 10,
        ["OlympicGamesQualification"] = 11,
        ["NationsLeague"] = 12,
        ["ChallengerCup"] = 13,
        ["ClubWorldChampionship"] = 14,
        ["WorldCup"] = 15,
        ["AgeGroupWorldChampionship"] = 16,
        ["Other"] = 17,
        ["NationalLeague"] = 18,
        ["ContinentalLeague"] = 19,
        ["ZonalChampionship"] = 20,
        ["WorldChampionshipQualification"] = 21,
    };

    /// <summary>
    /// Senior national-team competitions. A code constant on purpose (safety-critical, not configurable): Test (6),
    /// age-group (16), club/league types and "Other" (17 — seen with U23 events) are excluded, because the "TUR" code alone
    /// is shared by youth teams and a VIS test tournament ("VNL 2026 - WOMEN (TEST ONLY)").
    /// </summary>
    public static readonly IReadOnlyList<string> SeniorTournamentTypes =
    [
        "OlympicGames", "WorldChampionship", "ContinentalChampionship", "OlympicGamesQualification", "NationsLeague", "WorldCup",
        "WorldChampionshipQualification",
    ];

    public static IReadOnlyCollection<int> SeniorTypeValues { get; } = SeniorTournamentTypes.Select(t => KnownTournamentTypes[t]).ToHashSet();

    private static readonly int[] ClubTypes = [7, 8, 9, 14, 18];

    private static readonly string[] RequiredMatchProperties =
        ["no", "status", "teamACode", "teamBCode", "teamAName", "teamBName", "matchPointsA", "matchPointsB", "dateTimeUtc", "scheduleInfo", "tournament"];

    /// <summary>
    /// The request fields TSQ needs (VIS requires an explicit field list; unknown fields are silently ignored by VIS, which
    /// is why <see cref="RequiredMatchProperties"/> is checked on every item).
    /// </summary>
    public const string MatchFields =
        "No NoTournament DateTimeUtc ScheduleInfo City Hall NoTeamA NoTeamB TeamACode TeamBCode TeamAName TeamBName MatchPointsA MatchPointsB " +
        "PointsTeamASet1 PointsTeamBSet1 PointsTeamASet2 PointsTeamBSet2 PointsTeamASet3 PointsTeamBSet3 PointsTeamASet4 PointsTeamBSet4 " +
        "PointsTeamASet5 PointsTeamBSet5 Status ResultType NbSets Version";

    public const string TournamentFields = "No Code Name Season Gender Type";

    public static FivbMatchList Parse(JsonDocument document, IReadOnlyCollection<int> seniorTypes, out string? schemaProblem, string providerId = ProviderId)
    {
        schemaProblem = null;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            schemaProblem = "response has no data array";
            return new FivbMatchList([], null, 0, 0);
        }

        long? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var vv) ? vv : null;
        var matches = new List<VolleyballMatch>();
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        var rejected = 0;
        var total = 0;
        foreach (var item in data.EnumerateArray())
        {
            total++;
            var missing = RequiredMatchProperties.FirstOrDefault(p => !item.TryGetProperty(p, out _));
            if (missing is not null)
            {
                // A field we asked for is not there at all: the contract changed (or became non-public).
                schemaProblem = "match item without '" + missing + "'";
                rejected++;
                continue;
            }

            if (ParseMatch(item, seniorTypes, providerId) is { } match)
            {
                matches.Add(match);
                if (item.TryGetProperty("version", out var iv) && iv.ValueKind == JsonValueKind.Number && iv.TryGetInt64(out var itemVersion))
                    versions[match.ProviderMatchId] = itemVersion;
            }
            else
                rejected++;
        }

        if (total > 0 && matches.Count == 0 && schemaProblem is null && rejected == total)
            schemaProblem = "no match item could be parsed";
        return new FivbMatchList(matches, version, rejected, total) { ItemVersions = versions };
    }

    public static VolleyballMatch? ParseMatch(JsonElement m, IReadOnlyCollection<int> seniorTypes, string providerId = ProviderId)
    {
        if (Int(m, "no") is not { } no || no <= 0)
            return null;
        if (!m.TryGetProperty("tournament", out var t) || t.ValueKind != JsonValueKind.Object)
            return null;

        var tournamentType = Int(t, "type");
        var tournamentName = Str(t, "name");
        var gender = Int(t, "gender") switch
        {
            0 => TeamGender.Men,
            1 => TeamGender.Women,
            _ => TeamGender.Unknown,
        };
        // Tournament CODES are not used for age detection ("…EU2026…" would look like "U20"); names carry the age marker.
        // A tournament without a name, or named as a test / club event, is never a senior national-team competition even if its
        // type says so (VIS runs "VNL … (TEST ONLY)" tournaments; a mistyped club event must not become Türkiye's match).
        var youthEvent = tournamentName is not null && YouthMarker().IsMatch(tournamentName);
        var excluded = tournamentName is null || ExcludedEvent().IsMatch(tournamentName);
        var national = !excluded && tournamentType is { } tt && seniorTypes.Contains(tt);
        var kind = national ? TeamKind.NationalTeam : tournamentType is { } ct && ClubTypes.Contains(ct) ? TeamKind.Club : TeamKind.Unknown;

        VolleyballTeam Team(string side)
        {
            var code = Str(m, "team" + side + "Code");
            var name = Str(m, "team" + side + "Name") ?? code ?? "?";
            var youth = youthEvent || YouthMarker().IsMatch(name);
            var level = youth ? TeamLevel.AgeGroup : national ? TeamLevel.Senior : TeamLevel.Unknown;
            var providerTeam = Int(m, "noTeam" + side);
            return new VolleyballTeam(
                providerTeam is { } id ? providerId + ":team:" + id.ToString(CultureInfo.InvariantCulture) : providerId + ":team:?",
                providerTeam?.ToString(CultureInfo.InvariantCulture),
                name,
                code is { Length: 3 } && code.All(char.IsAsciiLetterUpper) ? code : null,
                gender,
                level,
                kind,
                youth ? AgeLimit(tournamentName + " " + name) : null);
        }

        var homeSets = Int(m, "matchPointsA");
        var awaySets = Int(m, "matchPointsB");
        var status = MapStatus(Int(m, "status"), Int(m, "resultType"));
        var completed = (homeSets ?? 0) + (awaySets ?? 0);
        var sets = new List<VolleyballSet>();
        for (var n = 1; n <= MatchProgress.MaxSets; n++)
        {
            if (Int(m, "pointsTeamASet" + n.ToString(CultureInfo.InvariantCulture)) is { } a && Int(m, "pointsTeamBSet" + n.ToString(CultureInfo.InvariantCulture)) is { } b)
                sets.Add(new VolleyballSet(n, a, b, n <= completed));
        }

        var live = status is VolleyballMatchStatus.Live or VolleyballMatchStatus.Suspended;
        var current = live ? Int(m, "nbSets") : null;
        var currentPoints = current is { } c ? sets.FirstOrDefault(s => s.Number == c && !s.Completed) : null;
        DateTimeOffset? start = Int(m, "scheduleInfo") == 4 && Date(m, "dateTimeUtc") is { } utc ? utc : null;
        var season = Str(t, "season") is { } s && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year
            : Int(t, "season");
        var id = no.ToString(CultureInfo.InvariantCulture);
        return new VolleyballMatch(
            VolleyballMatch.Key(providerId, id), providerId, id,
            Int(t, "no")?.ToString(CultureInfo.InvariantCulture) ?? Int(m, "noTournament")?.ToString(CultureInfo.InvariantCulture),
            tournamentName ?? Str(t, "code") ?? "?", season, null, null, start,
            Team("A"), Team("B"), status, homeSets, awaySets, sets,
            current, currentPoints?.HomePoints, currentPoints?.AwayPoints,
            Str(m, "hall"), Str(m, "city"), [], null);
    }

    /// <summary>VIS VolleyMatchStatus → normalized status (the values are ordered through a match).</summary>
    public static VolleyballMatchStatus MapStatus(int? status, int? resultType) => status switch
    {
        >= 1 and <= 3 => VolleyballMatchStatus.Scheduled,
        >= 4 and <= 23 => VolleyballMatchStatus.Live,
        >= 24 and <= 27 when resultType is null or 0 => VolleyballMatchStatus.Finished,
        _ => VolleyballMatchStatus.Unknown,
    };

    private static int? AgeLimit(string text) =>
        AgeNumber().Match(text) is { Success: true } m &&
        int.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var age) ? age : 0;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString()!.Trim() : null;

    private static int? Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(p.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static DateTimeOffset? Date(JsonElement e, string name)
    {
        // Only explicit UTC ("Z" or an offset) is accepted: an offset-less value would be silently interpreted in some zone.
        if (Str(e, name) is not { } text || !(text.EndsWith('Z') || OffsetSuffix().IsMatch(text)))
            return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value.ToUniversalTime() : null;
    }

    [GeneratedRegex(@"\bU[\s\-]?\d{2}\b|\bunder[\s\-]?\d{2}\b|girls|boys|junior|youth|\bjr\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YouthMarker();

    [GeneratedRegex(@"U[\s\-]?(\d{2})|under[\s\-]?(\d{2})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgeNumber();

    [GeneratedRegex(@"\btest\b|\bclub\b|\bclubs\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedEvent();

    [GeneratedRegex(@"[+\-]\d{2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetSuffix();
}
