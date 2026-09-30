namespace ToroSquad.Modules.News.Domain;

/// <summary>Why an item is (not) about the target team. Diagnostics and tests only — never shown on the card.</summary>
public enum NewsMatchReason
{
    /// <summary>Reserved: the feed item carries a verified team id/tag. The HLTV RSS feed has none (observed 2026-09-30).</summary>
    TeamMetadata = 1,
    TeamInTitle = 2,
    TeamInDescription = 3,
    CurrentPlayerInTitle = 4,

    /// <summary>Evidence exists but is not enough on its own (a player name that is also an ordinary word, a player only in the description).</summary>
    Ambiguous = 5,
    Unrelated = 6,
}

public sealed record NewsMatch(NewsMatchReason Reason, string Evidence)
{
    public bool Relevant => Reason is NewsMatchReason.TeamMetadata or NewsMatchReason.TeamInTitle or NewsMatchReason.TeamInDescription or NewsMatchReason.CurrentPlayerInTitle;
}

/// <summary>The current players and when that list was last confirmed (never older than the source says).</summary>
public sealed record RosterSnapshot(IReadOnlyList<string> Players, DateTimeOffset VerifiedAt, string Source)
{
    public bool IsFresh(DateTimeOffset now, TimeSpan maxAge) => Players.Count > 0 && now - VerifiedAt <= maxAge;
}

/// <summary>
/// Evidence-based, explainable matching of one feed item against the target team. It only sees what the feed provides
/// (headline and short description): a relation that exists only in the full article is invisible here by design.
/// <list type="bullet">
/// <item>Team: an alias as a whole word in the headline or the description — unless that occurrence is part of an
/// excluded look-alike name (CRUISER AURORA, Aurora Young Blood …). A look-alike does not hide a separate mention of the
/// team in the same text.</item>
/// <item>Player: a current player's name as a whole word in the headline, only while the roster is fresh; names that are
/// short or ordinary words count only next to the team name.</item>
/// </list>
/// </summary>
public sealed class AuroraNewsMatcher
{
    /// <summary>Short/ordinary words that are also player names somewhere: never enough on their own.</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "ace", "ash", "bot", "cat", "dev", "ez", "fire", "gg", "hero", "ice", "king", "lion", "magic", "nova", "one", "pro", "rush",
        "snow", "star", "zero",
    };

    private readonly IReadOnlyList<string> _aliases;
    private readonly IReadOnlyList<string> _excluded;
    private readonly HashSet<string> _ambiguous;

    public AuroraNewsMatcher(IEnumerable<string> aliases, IEnumerable<string> excludedNames, IEnumerable<string> ambiguousPlayerNames)
    {
        _aliases = aliases.Select(NewsText.ForMatching).Where(a => a.Length >= 3).Distinct(StringComparer.Ordinal).OrderByDescending(a => a.Length).ToList();
        _excluded = excludedNames.Select(NewsText.ForMatching).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        _ambiguous = ambiguousPlayerNames.Select(NewsText.ForMatching).ToHashSet(StringComparer.Ordinal);
    }

    public NewsMatch Evaluate(NewsArticle article, RosterSnapshot? roster, DateTimeOffset now, TimeSpan rosterMaxAge)
    {
        var title = NewsText.ForMatching(article.Title);
        var description = NewsText.ForMatching(article.MatchText);

        if (TeamMention(title, out var titleEvidence))
            return new(NewsMatchReason.TeamInTitle, titleEvidence);

        var rosterFresh = roster?.IsFresh(now, rosterMaxAge) == true;
        string? ambiguous = null;
        string? stale = null;
        foreach (var player in roster?.Players ?? [])
        {
            var name = NewsText.ForMatching(player);
            if (name.Length == 0 || Occurrences(title, name).Count == 0)
                continue;
            if (IsAmbiguous(name))
            {
                ambiguous ??= "player (needs team context): " + player;
                continue;
            }

            if (!rosterFresh)
            {
                stale ??= "player in headline but roster is stale: " + player;
                continue;
            }

            return new(NewsMatchReason.CurrentPlayerInTitle, "current player in headline: " + player);
        }

        if (TeamMention(description, out var descriptionEvidence))
            return new(NewsMatchReason.TeamInDescription, descriptionEvidence);

        if (ambiguous is not null)
            return new(NewsMatchReason.Ambiguous, ambiguous);
        if (rosterFresh)
        {
            foreach (var player in roster!.Players)
            {
                var name = NewsText.ForMatching(player);
                if (name.Length > 0 && Occurrences(description, name).Count > 0)
                    return new(NewsMatchReason.Ambiguous, "player only in the description: " + player);
            }
        }

        if (stale is not null)
            return new(NewsMatchReason.Unrelated, stale);
        return new(NewsMatchReason.Unrelated, ExcludedOnly(title + " " + description) is { } excluded ? "only a look-alike team: " + excluded : "no team or player evidence");
    }

    private bool IsAmbiguous(string name) => name.Length < 4 || CommonWords.Contains(name) || _ambiguous.Contains(name);

    /// <summary>True when an alias occurs as a whole word outside every excluded look-alike name.</summary>
    private bool TeamMention(string text, out string evidence)
    {
        evidence = "";
        if (text.Length == 0)
            return false;
        var excludedSpans = _excluded.SelectMany(e => Occurrences(text, e).Select(i => (Start: i, End: i + e.Length))).ToList();
        foreach (var alias in _aliases)
        {
            foreach (var start in Occurrences(text, alias))
            {
                var end = start + alias.Length;
                if (excludedSpans.Any(s => start >= s.Start && end <= s.End))
                    continue;
                evidence = "team name: " + alias;
                return true;
            }
        }

        return false;
    }

    private string? ExcludedOnly(string text) => _excluded.FirstOrDefault(e => Occurrences(text, e).Count > 0);

    /// <summary>Start indexes of <paramref name="phrase"/> in <paramref name="text"/> with no letter or digit on either side.</summary>
    public static IReadOnlyList<int> Occurrences(string text, string phrase)
    {
        var result = new List<int>();
        if (phrase.Length == 0 || text.Length < phrase.Length)
            return result;
        var from = 0;
        while (from <= text.Length - phrase.Length)
        {
            var index = text.IndexOf(phrase, from, StringComparison.Ordinal);
            if (index < 0)
                break;
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + phrase.Length;
            var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
                result.Add(index);
            from = index + 1;
        }

        return result;
    }
}
