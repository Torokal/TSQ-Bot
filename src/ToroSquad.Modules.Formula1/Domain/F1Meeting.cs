using System.Globalization;

namespace ToroSquad.Modules.Formula1.Domain;

/// <summary>
/// Provider-independent identities. A real-world session keeps the same key across restarts, provider refreshes and
/// provider replacement: it is derived only from season, round and normalized session type (a weekend never has two
/// sessions of the same type). Provider ids (OpenF1 session_key, Jolpica circuitId, ...) are never used as keys.
/// </summary>
public static class F1Keys
{
    public static string Meeting(int season, int round) =>
        string.Create(CultureInfo.InvariantCulture, $"{season:0000}-{round:00}");

    public static string Session(int season, int round, F1SessionType type) =>
        Meeting(season, round) + "-" + F1SessionTypes.Slug(type);

    public static bool TryParseSession(string key, out int season, out int round, out F1SessionType type)
    {
        season = 0;
        round = 0;
        type = F1SessionType.Unknown;
        var parts = key.Split('-');
        return parts.Length == 3 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out season) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out round) &&
               F1SessionTypes.TryParseSlug(parts[2], out type);
    }
}

/// <summary>One scheduled session of a Grand Prix weekend. Scheduled times are schedule information only.</summary>
public sealed record F1Session(int Season, int Round, F1SessionType Type, DateTimeOffset ScheduledStartUtc, DateTimeOffset? ScheduledEndUtc = null)
{
    public string Key => F1Keys.Session(Season, Round, Type);

    public string MeetingKey => F1Keys.Meeting(Season, Round);

    /// <summary>Provider end time when known, otherwise a planning estimate (never a claim that the session ended).</summary>
    public DateTimeOffset PlannedEndUtc => ScheduledEndUtc ?? ScheduledStartUtc + F1SessionTypes.TypicalDuration(Type);
}

/// <summary>One Grand Prix weekend (normalized from the schedule provider).</summary>
public sealed record F1Meeting(
    int Season,
    int Round,
    string MeetingName,
    string? OfficialName,
    string CircuitName,
    string? Country,
    string? Location,
    string ProviderKey,
    IReadOnlyList<F1Session> Sessions)
{
    public string Key => F1Keys.Meeting(Season, Round);

    public DateTimeOffset? StartUtc => Sessions.Count == 0 ? null : Sessions.Min(s => s.ScheduledStartUtc);

    public DateTimeOffset? EndUtc => Sessions.Count == 0 ? null : Sessions.Max(s => s.PlannedEndUtc);

    public bool IsSprintWeekend => Sessions.Any(s => s.Type == F1SessionType.Sprint);

    public F1Session? Find(F1SessionType type) => Sessions.FirstOrDefault(s => s.Type == type);
}

/// <summary>A season's calendar as published by the schedule provider.</summary>
public sealed record F1SeasonSchedule(int Season, string Source, IReadOnlyList<F1Meeting> Meetings)
{
    public IEnumerable<F1Session> Sessions => Meetings.SelectMany(m => m.Sessions);

    public F1Meeting? Meeting(int round) => Meetings.FirstOrDefault(m => m.Round == round);
}

/// <summary>
/// Maps a provider's session onto the normalized schedule. Conservative by design: season and type must match and the
/// provider's start must be within the tolerance of exactly ONE scheduled session. No candidate, or more than one, means
/// no mapping (fail closed) — a result is never attached to another race because it was "probably the same".
/// </summary>
public static class F1SessionMatcher
{
    public static F1Session? Match(IEnumerable<F1Session> schedule, int season, F1SessionType type, DateTimeOffset providerStartUtc, TimeSpan tolerance)
    {
        if (type == F1SessionType.Unknown)
            return null;
        var candidates = schedule
            .Where(s => s.Season == season && s.Type == type && (s.ScheduledStartUtc - providerStartUtc).Duration() <= tolerance)
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
