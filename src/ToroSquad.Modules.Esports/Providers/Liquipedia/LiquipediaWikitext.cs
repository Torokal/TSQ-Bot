using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Esports.Domain;

namespace ToroSquad.Modules.Esports.Providers.Liquipedia;

/// <summary>
/// One match as written by Liquipedia editors in wikitext (<c>{{Match|opponent1=…|opponent2=…|date=…|hltv=…}}</c> in
/// tournament brackets, or the <c>TemplateMatchPage</c> invocation on <c>Match:</c> pages). <see cref="StartUtc"/> is null
/// when the date or its time zone cannot be read unambiguously; <see cref="HltvId"/> is null unless the editor-entered
/// value is a plain numeric HLTV match id.
/// </summary>
public sealed record WikiMatch(string? Opponent1, string? Opponent2, DateTimeOffset? StartUtc, long? HltvId);

/// <summary>
/// Minimal, defensive reader for the Liquipedia Counter-Strike match wikitext (read through the MediaWiki API — never
/// HTML). Templates are matched by balanced braces; only top-level parameters of a match template are read, so map
/// sub-templates cannot leak values. Anything unexpected yields "no value", never a guess.
/// </summary>
public static partial class LiquipediaWikitext
{
    /// <summary>Hard cap: larger pages are ignored rather than parsed (they are not match pages we can trust cheaply).</summary>
    public const int MaxWikitextLength = 2_000_000;

    public static IReadOnlyList<WikiMatch> ParseMatches(string? wikitext)
    {
        var result = new List<WikiMatch>();
        if (string.IsNullOrEmpty(wikitext) || wikitext.Length > MaxWikitextLength)
            return result;

        for (var i = wikitext.IndexOf("{{", StringComparison.Ordinal); i >= 0; i = wikitext.IndexOf("{{", i + 2, StringComparison.Ordinal))
        {
            var end = BalancedEnd(wikitext, i);
            if (end < 0)
                break; // unbalanced from here on: stop, never guess
            var inner = wikitext.AsSpan(i + 2, end - i - 2);
            var parts = SplitTopLevel(inner.ToString());
            if (parts.Count == 0 || !IsMatchTemplate(parts))
                continue;

            var args = NamedArgs(parts);
            result.Add(new WikiMatch(
                TeamTemplate(args.GetValueOrDefault("opponent1")),
                TeamTemplate(args.GetValueOrDefault("opponent2")),
                ParseDate(args.GetValueOrDefault("date")),
                ParseHltvId(args.GetValueOrDefault("hltv"))));
        }

        return result;
    }

    /// <summary>
    /// "September 25, 2026 - 11:00 {{Abbr/CEST}}" (also "Sep 25, 2026" and "2026-09-25") → UTC. The time zone must be one
    /// of the unambiguous abbreviations below; a missing, unknown or ambiguous zone (IST, CST, AST, GST…) → null.
    /// </summary>
    public static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var m = DatePattern().Match(raw.Trim());
        if (!m.Success || !ZoneOffsets.TryGetValue(m.Groups["tz"].Value.ToUpperInvariant(), out var offset))
            return null;

        DateOnly date;
        if (m.Groups["iso"].Success)
        {
            if (!DateOnly.TryParseExact(m.Groups["iso"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return null;
        }
        else
        {
            var text = $"{m.Groups["month"].Value} {m.Groups["day"].Value} {m.Groups["year"].Value}";
            if (!DateOnly.TryParseExact(text, ["MMMM d yyyy", "MMM d yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return null;
        }

        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59)
            return null;
        return new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, minute)), offset).ToUniversalTime();
    }

    /// <summary>Only a plain positive numeric id is accepted (the HLTV URL is then built deterministically).</summary>
    public static long? ParseHltvId(string? raw)
    {
        var value = raw?.Trim();
        return value is { Length: > 0 and <= 10 } && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;
    }

    /// <summary>The HLTV match page for an id, in the same form Liquipedia itself builds: https://www.hltv.org/matches/&lt;id&gt;/match.</summary>
    public static string HltvUrl(long id) =>
        MatchLinkPolicy.ValidHltvMatchUrl(string.Create(CultureInfo.InvariantCulture, $"https://{MatchLinkPolicy.HltvHost}/matches/{id}/match"))!;

    /// <summary>Fixed offsets of unambiguous zone abbreviations used on Liquipedia (abbreviations with several meanings are left out on purpose).</summary>
    private static readonly Dictionary<string, TimeSpan> ZoneOffsets = new(StringComparer.Ordinal)
    {
        ["UTC"] = TimeSpan.Zero,
        ["GMT"] = TimeSpan.Zero,
        ["WET"] = TimeSpan.Zero,
        ["WEST"] = TimeSpan.FromHours(1),
        ["BST"] = TimeSpan.FromHours(1),
        ["CET"] = TimeSpan.FromHours(1),
        ["CEST"] = TimeSpan.FromHours(2),
        ["EET"] = TimeSpan.FromHours(2),
        ["EEST"] = TimeSpan.FromHours(3),
        ["MSK"] = TimeSpan.FromHours(3),
        ["TRT"] = TimeSpan.FromHours(3),
        ["SGT"] = TimeSpan.FromHours(8),
        ["PHT"] = TimeSpan.FromHours(8),
        ["AWST"] = TimeSpan.FromHours(8),
        ["KST"] = TimeSpan.FromHours(9),
        ["JST"] = TimeSpan.FromHours(9),
        ["AEST"] = TimeSpan.FromHours(10),
        ["AEDT"] = TimeSpan.FromHours(11),
        ["BRT"] = TimeSpan.FromHours(-3),
        ["ART"] = TimeSpan.FromHours(-3),
        ["EDT"] = TimeSpan.FromHours(-4),
        ["EST"] = TimeSpan.FromHours(-5),
        ["CDT"] = TimeSpan.FromHours(-5),
        ["MDT"] = TimeSpan.FromHours(-6),
        ["MST"] = TimeSpan.FromHours(-7),
        ["PDT"] = TimeSpan.FromHours(-7),
        ["PST"] = TimeSpan.FromHours(-8),
    };

    private static bool IsMatchTemplate(List<string> parts)
    {
        var name = parts[0].Trim();
        if (string.Equals(name, "Match", StringComparison.OrdinalIgnoreCase))
            return true;
        // Match: namespace pages: {{#invoke:Lua|invoke|module=MatchGroup|fn=TemplateMatchPage|…}}
        if (!name.StartsWith("#invoke:", StringComparison.OrdinalIgnoreCase))
            return false;
        var args = NamedArgs(parts);
        return string.Equals(args.GetValueOrDefault("module")?.Trim(), "MatchGroup", StringComparison.Ordinal) &&
               string.Equals(args.GetValueOrDefault("fn")?.Trim(), "TemplateMatchPage", StringComparison.Ordinal);
    }

    /// <summary>{{TeamOpponent|eternal fire|score=2}} → "eternal fire". Anything else (literal, solo player, TBD) → null.</summary>
    private static string? TeamTemplate(string? value)
    {
        var v = value?.Trim();
        if (v is null || !v.StartsWith("{{", StringComparison.Ordinal))
            return null;
        var end = BalancedEnd(v, 0);
        if (end < 0)
            return null;
        var parts = SplitTopLevel(v[2..end]);
        if (parts.Count < 2 || !string.Equals(parts[0].Trim(), "TeamOpponent", StringComparison.OrdinalIgnoreCase))
            return null;
        var first = parts.Skip(1).FirstOrDefault(p => !p.Contains('=', StringComparison.Ordinal))?.Trim();
        return string.IsNullOrWhiteSpace(first) || string.Equals(first, "tbd", StringComparison.OrdinalIgnoreCase) ? null : first;
    }

    private static Dictionary<string, string> NamedArgs(List<string> parts)
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim();
            if (key.Length > 0 && !args.ContainsKey(key))
                args[key] = part[(eq + 1)..].Trim();
        }

        return args;
    }

    /// <summary>Splits template content on '|' that are not inside nested {{…}} or [[…]].</summary>
    private static List<string> SplitTopLevel(string content)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int braces = 0, links = 0;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '{' && i + 1 < content.Length && content[i + 1] == '{') { braces++; sb.Append("{{"); i++; continue; }
            if (c == '}' && i + 1 < content.Length && content[i + 1] == '}' && braces > 0) { braces--; sb.Append("}}"); i++; continue; }
            if (c == '[' && i + 1 < content.Length && content[i + 1] == '[') { links++; sb.Append("[["); i++; continue; }
            if (c == ']' && i + 1 < content.Length && content[i + 1] == ']' && links > 0) { links--; sb.Append("]]"); i++; continue; }
            if (c == '|' && braces == 0 && links == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Index of the "}}" closing the template that starts at <paramref name="start"/> ("{{"), or -1.</summary>
    private static int BalancedEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length - 1; i++)
        {
            if (text[i] == '{' && text[i + 1] == '{')
            {
                depth++;
                i++;
            }
            else if (text[i] == '}' && text[i + 1] == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
                i++;
            }
        }

        return -1;
    }

    [GeneratedRegex(@"^(?:(?<month>[A-Za-z]{3,9})\s+(?<day>\d{1,2}),\s*(?<year>\d{4})|(?<iso>\d{4}-\d{2}-\d{2}))\s*-\s*(?<h>\d{1,2}):(?<m>\d{2})\s*\{\{\s*Abbr/(?<tz>[A-Za-z]{2,5})\s*\}\}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}

/// <summary>
/// PandaScore match ↔ Liquipedia wikitext match. A link is attached only when EXACTLY ONE distinct valid HLTV id belongs to
/// a wikitext match whose two team templates match the two teams (order-insensitive; name or acronym, normalized) and
/// whose start is within the tolerance. Zero or several distinct ids → no link. Nothing is fetched from HLTV.
/// </summary>
public static class WikiLinkMatcher
{
    public static string? Find(EsportsMatch target, IEnumerable<WikiMatch> candidates, TimeSpan tolerance)
    {
        if (!target.A.IsTeam || !target.B.IsTeam || target.ScheduledStartUtc is not { } start)
            return null;
        var a = Keys(target.A.Team!);
        var b = Keys(target.B.Team!);
        if (a.Count == 0 || b.Count == 0 || a.Overlaps(b))
            return null; // the two teams must be distinguishable

        var ids = candidates
            .Where(c => c.HltvId is not null && c.StartUtc is { } s && (s - start).Duration() <= tolerance)
            .Where(c => c.Opponent1 is not null && c.Opponent2 is not null)
            .Where(c =>
            {
                var o1 = Keys(c.Opponent1!);
                var o2 = Keys(c.Opponent2!);
                return (a.Overlaps(o1) && b.Overlaps(o2)) || (a.Overlaps(o2) && b.Overlaps(o1));
            })
            .Select(c => c.HltvId!.Value)
            .Distinct()
            .ToList();
        return ids.Count == 1 ? LiquipediaWikitext.HltvUrl(ids[0]) : null;
    }

    /// <summary>Comparison keys: normalized name and acronym, each also without spaces ("eternal fire" = "eternalfire").</summary>
    private static HashSet<string> Keys(TeamRef team)
    {
        var keys = Keys(team.Name);
        if (!string.IsNullOrWhiteSpace(team.ShortName))
            keys.UnionWith(Keys(team.ShortName));
        return keys;
    }

    private static HashSet<string> Keys(string value)
    {
        var normalized = TeamRankingResolver.Normalize(value);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (normalized.Length >= 2)
        {
            keys.Add(normalized);
            keys.Add(normalized.Replace(" ", "", StringComparison.Ordinal));
        }

        return keys;
    }
}
