namespace ToroSquad.Modules.Formula1.Domain;

/// <summary>
/// Normalized Formula 1 session types. Provider names ("Practice 1", "Sprint Shootout", "SprintQualifying", ...) are
/// mapped onto these in the provider parsers; nothing downstream sees provider strings. The numeric values are persisted.
/// </summary>
public enum F1SessionType
{
    Unknown = 0,
    Practice1 = 1,
    Practice2 = 2,
    Practice3 = 3,
    SprintQualifying = 4,
    Sprint = 5,
    Qualifying = 6,
    Race = 7,
}

/// <summary>Notification switch groups (what an admin turns on/off). Qualifying formats exist but default off.</summary>
public enum F1SessionCategory
{
    Practice = 0,
    SprintQualifying = 1,
    Sprint = 2,
    Qualifying = 3,
    Race = 4,
}

public static class F1SessionTypes
{
    /// <summary>Stable short ids used in keys, command choices and outbox kinds. Never change them (persisted).</summary>
    public static string Slug(F1SessionType type) => type switch
    {
        F1SessionType.Practice1 => "fp1",
        F1SessionType.Practice2 => "fp2",
        F1SessionType.Practice3 => "fp3",
        F1SessionType.SprintQualifying => "sq",
        F1SessionType.Sprint => "sprint",
        F1SessionType.Qualifying => "quali",
        F1SessionType.Race => "race",
        _ => "unknown",
    };

    public static bool TryParseSlug(string? slug, out F1SessionType type)
    {
        type = slug switch
        {
            "fp1" => F1SessionType.Practice1,
            "fp2" => F1SessionType.Practice2,
            "fp3" => F1SessionType.Practice3,
            "sq" => F1SessionType.SprintQualifying,
            "sprint" => F1SessionType.Sprint,
            "quali" => F1SessionType.Qualifying,
            "race" => F1SessionType.Race,
            _ => F1SessionType.Unknown,
        };
        return type != F1SessionType.Unknown;
    }

    public static F1SessionCategory Category(F1SessionType type) => type switch
    {
        F1SessionType.SprintQualifying => F1SessionCategory.SprintQualifying,
        F1SessionType.Sprint => F1SessionCategory.Sprint,
        F1SessionType.Qualifying => F1SessionCategory.Qualifying,
        F1SessionType.Race => F1SessionCategory.Race,
        _ => F1SessionCategory.Practice,
    };

    public static bool IsPractice(F1SessionType type) => type is F1SessionType.Practice1 or F1SessionType.Practice2 or F1SessionType.Practice3;

    /// <summary>Qualifying formats run in segments (Q1/Q2/Q3, SQ1/SQ2/SQ3); a segment end is not the session end.</summary>
    public static bool IsSegmented(F1SessionType type) => type is F1SessionType.Qualifying or F1SessionType.SprintQualifying;

    /// <summary>Only sessions that award championship points trigger the standings workflow.</summary>
    public static bool AwardsChampionshipPoints(F1SessionType type) => type is F1SessionType.Sprint or F1SessionType.Race;

    /// <summary>Order within a weekend when two sessions share a start time (never happens in practice; keeps sorting stable).</summary>
    public static int Order(F1SessionType type) => (int)type;

    /// <summary>
    /// Planning-only estimate of a session's length (used to decide when to START polling for results when no provider
    /// end time is known). Never used to claim that a session started or finished.
    /// </summary>
    public static TimeSpan TypicalDuration(F1SessionType type) => type switch
    {
        F1SessionType.SprintQualifying => TimeSpan.FromMinutes(45),
        F1SessionType.Race => TimeSpan.FromHours(2),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>
    /// Normalizes provider session names. Provider parsers call this; anything unrecognized is <see cref="F1SessionType.Unknown"/>
    /// (and is then ignored rather than guessed).
    /// </summary>
    public static F1SessionType FromProviderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return F1SessionType.Unknown;
        var n = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return n switch
        {
            "PRACTICE1" or "FP1" or "FIRSTPRACTICE" or "FREEPRACTICE1" => F1SessionType.Practice1,
            "PRACTICE2" or "FP2" or "SECONDPRACTICE" or "FREEPRACTICE2" => F1SessionType.Practice2,
            "PRACTICE3" or "FP3" or "THIRDPRACTICE" or "FREEPRACTICE3" => F1SessionType.Practice3,
            "SPRINTQUALIFYING" or "SPRINTSHOOTOUT" or "SQ" => F1SessionType.SprintQualifying,
            "SPRINT" or "SPRINTRACE" => F1SessionType.Sprint,
            "QUALIFYING" => F1SessionType.Qualifying,
            "RACE" or "GRANDPRIX" => F1SessionType.Race,
            _ => F1SessionType.Unknown,
        };
    }
}
