// Portions of this file are derived from BOT-Greg-v2_API by Julius Gmeinder
// (https://github.com/julius-gmeinder/BOT-Greg-v2_API, commit 3898b4ebfd4ed26ec077f4167a780f1684d91331,
// Services/LiquipediaService.cs), licensed under the GNU AGPL v3. Modified for TSQ Bot (formerly ToroSquad Bot): rewritten as a
// defensive, provider-independent parser (no fixed opponent indexes, null/[]/{} tolerant, explicit UTC dates,
// no invented scores, forfeit/draw/not-played handling). See docs/PROVENANCE.md.
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Modules.Esports.Providers.Liquipedia;

/// <summary>Tolerant accessors for LiquipediaDB JSON (PHP serialisation: empty objects may arrive as []).</summary>
internal static class J
{
    public static string? Str(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    public static int? Int(JsonElement el, string name)
    {
        var s = Str(el, name);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static double? Dbl(JsonElement el, string name)
    {
        var s = Str(el, name);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static bool Flag(JsonElement el, string name) => Str(el, name) is "1" or "true";

    /// <summary>Returns the object, or null for missing/null/[] (PHP's empty-object serialisation).</summary>
    public static JsonElement? Obj(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static IEnumerable<JsonElement> Arr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    public static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// Converts LiquipediaDB v3 match2 records into <see cref="EsportsMatch"/>. Returns null (with a warning) for records
/// that cannot be identified; never throws on missing fields.
/// Field semantics per Help:LiquipediaDB/Match (rev 2026-09-03): `date` is UTC ("YYYY-MM-DD HH:MM:SS", no offset);
/// `finished` 0/1; `winner` "1"/"2" or "0" for draw; opponent `status` S/W/L/D/FF/DQ; `status=notplayed`.
/// resulttype/walkover are deprecated (still read as a fallback).
/// </summary>
public static partial class LiquipediaParser
{
    public const string Source = "liquipedia";
    public const string SiteBase = "https://liquipedia.net/";

    public static EsportsMatch? ParseMatch(JsonElement el, string wiki, ICollection<string> warnings)
    {
        var id = J.NonEmpty(J.Str(el, "match2id"));
        if (id is null)
        {
            warnings.Add("match without match2id skipped");
            return null;
        }

        var opponentsRaw = J.Arr(el, "match2opponents").ToList();
        if (opponentsRaw.Count > 2)
        {
            warnings.Add($"{id}: {opponentsRaw.Count} opponents (not a 1v1 series) skipped");
            return null;
        }

        // Never index blindly: missing opponents become explicit Unknown/TBD instead of crashing.
        var a = opponentsRaw.Count > 0 ? ParseOpponent(opponentsRaw[0], wiki) : MatchOpponent.UnknownOpponent;
        var b = opponentsRaw.Count > 1 ? ParseOpponent(opponentsRaw[1], wiki) : MatchOpponent.UnknownOpponent;

        var date = ParseUtc(J.Str(el, "date"));
        if (date is null)
            warnings.Add($"{id}: missing/invalid date");

        var finished = J.Flag(el, "finished");
        var matchStatus = J.Str(el, "status")?.Trim().ToLowerInvariant();
        var resultType = J.Str(el, "resulttype")?.Trim().ToLowerInvariant();
        var walkover = J.NonEmpty(J.Str(el, "walkover"));

        var winnerRaw = J.NonEmpty(J.Str(el, "winner"));
        int? winnerIndex = winnerRaw switch { "1" => 0, "2" => 1, _ => null };
        var isDraw = winnerRaw == "0" || resultType == "draw" || (a.Result == OpponentResult.Draw && b.Result == OpponentResult.Draw);
        winnerIndex ??= a.Result == OpponentResult.Win ? 0 : b.Result == OpponentResult.Win ? 1 : null;

        var isForfeit = a.Result is OpponentResult.Forfeit or OpponentResult.Disqualified ||
                        b.Result is OpponentResult.Forfeit or OpponentResult.Disqualified ||
                        walkover is not null || resultType is "ff" or "walkover" or "forfeit";

        MatchStatus status;
        string evidence;
        if (matchStatus == "notplayed" || resultType == "np")
        {
            status = MatchStatus.Cancelled;
            evidence = "provider: match not played";
        }
        else if (finished)
        {
            status = MatchStatus.Finished;
            evidence = "provider: finished=1";
        }
        else if (date is null)
        {
            status = MatchStatus.Unknown;
            evidence = "provider: no date";
        }
        else
        {
            // Liquipedia has no live flag. A passed start time only means "should have started".
            status = MatchStatus.Scheduled;
            evidence = "provider: finished=0";
        }

        var pagename = J.NonEmpty(J.Str(el, "pagename"));
        var parent = J.NonEmpty(J.Str(el, "parent"));
        var tournament = new TournamentRef(
            Source,
            pagename ?? parent ?? "unknown",
            J.NonEmpty(J.Str(el, "tournament")) ?? J.NonEmpty(J.Str(el, "tickername")) ?? pagename ?? "?",
            J.NonEmpty(J.Str(el, "liquipediatier")),
            J.NonEmpty(J.Str(el, "liquipediatiertype")),
            J.NonEmpty(J.Str(el, "publishertier")),
            parent);

        var maps = J.Arr(el, "match2games").Select((g, i) => ParseGame(g, i)).ToList();

        return new EsportsMatch(
            new MatchKey($"{Source}:{wiki}", id),
            tournament,
            date,
            J.Flag(el, "dateexact"),
            J.Int(el, "bestof") is > 0 and var bo ? bo : null,
            status,
            evidence,
            a,
            b,
            status == MatchStatus.Finished && !isDraw ? winnerIndex : null,
            status == MatchStatus.Finished && isDraw,
            status == MatchStatus.Finished && isForfeit,
            maps,
            J.NonEmpty(J.Str(el, "section")),
            pagename is null ? null : PageUrl(wiki, pagename),
            ParseStreams(el, wiki));
    }

    public static EsportsEvent? ParseEvent(JsonElement el, string wiki, ICollection<string> warnings)
    {
        var pagename = J.NonEmpty(J.Str(el, "pagename"));
        var name = J.NonEmpty(J.Str(el, "name"));
        if (pagename is null || name is null)
        {
            warnings.Add("tournament without pagename/name skipped");
            return null;
        }

        string? location = null;
        if (J.Obj(el, "locations") is { } loc)
        {
            var parts = new[] { J.NonEmpty(J.Str(loc, "city1")), J.NonEmpty(J.Str(loc, "country1")), J.NonEmpty(J.Str(loc, "region1")) }
                .Where(p => p is not null).Take(2);
            location = string.Join(", ", parts);
        }

        return new EsportsEvent(
            new TournamentRef(Source, pagename, name, J.NonEmpty(J.Str(el, "liquipediatier")), J.NonEmpty(J.Str(el, "liquipediatiertype")),
                J.NonEmpty(J.Str(el, "publishertier")), J.NonEmpty(J.Str(el, "parent"))),
            ParseDate(J.Str(el, "startdate")),
            ParseDate(J.Str(el, "enddate")),
            string.IsNullOrEmpty(location) ? null : location,
            J.NonEmpty(J.Str(el, "type")),
            J.Dbl(el, "prizepool") is > 0 and var p ? p : null,
            J.Int(el, "participantsnumber") is > 0 and var n ? n : null,
            PageUrl(wiki, pagename));
    }

    private static MatchOpponent ParseOpponent(JsonElement op, string wiki)
    {
        var type = J.Str(op, "type");
        var status = J.Str(op, "status")?.Trim().ToUpperInvariant();
        var result = status switch
        {
            "S" => OpponentResult.Scored,
            "W" => OpponentResult.Win,
            "L" => OpponentResult.Loss,
            "D" => OpponentResult.Draw,
            "FF" => OpponentResult.Forfeit,
            "DQ" => OpponentResult.Disqualified,
            _ => OpponentResult.None,
        };
        // Score is only meaningful with status S; -1 means "no score". Never turn a default 0 into "0".
        var rawScore = J.Int(op, "score");
        int? score = result == OpponentResult.Scored && rawScore is >= 0 ? rawScore : null;

        if (type is not null && type != "team")
            return new MatchOpponent(OpponentKind.Unknown, null, score, result);

        var template = J.Obj(op, "teamtemplate");
        var name = J.NonEmpty(template is { } t1 ? J.Str(t1, "name") : null) ?? J.NonEmpty(J.Str(op, "name"));
        var page = J.NonEmpty(template is { } t2 ? J.Str(t2, "page") : null) ?? J.NonEmpty(J.Str(op, "name"));
        var shortName = J.NonEmpty(template is { } t3 ? J.Str(t3, "shortname") : null);
        if (name is null || page is null || IsPlaceholder(name) || IsPlaceholder(page))
            return new MatchOpponent(OpponentKind.Tbd, null, score, result);

        return new MatchOpponent(OpponentKind.Team, new TeamRef(Source, TeamKey(wiki, page), name, shortName), score, result);
    }

    private static MapGame ParseGame(JsonElement g, int index)
    {
        var status = J.Str(g, "status")?.Trim().ToLowerInvariant();
        var winner = J.NonEmpty(J.Str(g, "winner"));
        int? winnerIndex = winner switch { "1" => 0, "2" => 1, _ => null };
        var scores = J.Arr(g, "scores")
            .Select(s => s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n) ? n
                : s.ValueKind == JsonValueKind.String && int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m
                : (int?)null)
            .ToList();

        GameStatus gameStatus;
        if (status == "notplayed")
            gameStatus = GameStatus.NotPlayed;
        else if (winner is "1" or "2" or "0")
            gameStatus = GameStatus.Played;
        else
            gameStatus = GameStatus.Unknown;

        int? scoreA = null, scoreB = null;
        if (gameStatus == GameStatus.Played && scores.Count >= 2 && scores[0] is >= 0 && scores[1] is >= 0)
        {
            scoreA = scores[0];
            scoreB = scores[1];
        }

        var mapName = J.NonEmpty(J.Str(g, "map"));
        return new MapGame(index + 1, mapName is "TBD" or "TBA" ? null : mapName, gameStatus, scoreA, scoreB, winnerIndex);
    }

    private static List<StreamLink> ParseStreams(JsonElement el, string wiki)
    {
        var result = new List<StreamLink>();
        if (J.Obj(el, "stream") is not { } streams)
            return result;
        foreach (var property in streams.EnumerateObject())
        {
            var platform = property.Name.Split('_')[0].ToLowerInvariant();
            if (platform is not ("twitch" or "youtube" or "kick"))
                continue;
            var channel = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (channel is null || !ChannelPattern().IsMatch(channel))
                continue;
            var url = $"{SiteBase}{wiki}/Special:Stream/{platform}/{channel}";
            if (result.All(r => r.Url != url))
                result.Add(new StreamLink(platform, url));
        }

        return result;
    }

    /// <summary>Liquipedia `date` is UTC without an offset. Parsed exactly; anything else is rejected, not guessed.</summary>
    public static DateTimeOffset? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return DateTimeOffset.TryParseExact(value.Trim(), ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:sszzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            && dt.Year > 1970
            ? dt
            : null;
    }

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1970 ? d : null;

    public static string TeamKey(string wiki, string page) => $"{wiki}/{page.Replace(' ', '_')}";

    public static string PageUrl(string wiki, string page) => $"{SiteBase}{wiki}/{Uri.EscapeDataString(page.Replace(' ', '_')).Replace("%2F", "/", StringComparison.Ordinal)}";

    private static bool IsPlaceholder(string name) =>
        name.Equals("TBD", StringComparison.OrdinalIgnoreCase) || name.Equals("TBA", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("definitions", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();
}
