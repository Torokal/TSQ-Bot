namespace ToroSquad.Modules.Timezone.Application;

/// <summary>
/// A time zone the entered time can be read in: the value autocomplete sends (<see cref="Key"/>), the IANA id, the
/// localization key of its label on the card and the names people type for it.
/// </summary>
public sealed record SourceZone(string Key, string ZoneId, string NameKey, IReadOnlyList<string> Aliases);

/// <summary>
/// The /saat <c>timezone</c> option. Every alias names a <b>zone</b>, never an offset: <c>pdt</c>, <c>pst</c> and <c>pt</c> all
/// mean America/Los_Angeles, and <see cref="TimeZoneInfo"/> decides whether that date is on PST or PDT (same for EST/EDT,
/// CST/CDT, GMT/BST). That is also why the card labels a zone by place and region ("Los Angeles (Pacific Time)"), not by an
/// abbreviation that could be wrong for the date. Matching ignores case and surrounding whitespace; the IANA id itself is
/// accepted too. No option (or an empty one) = <see cref="Default"/>, the behaviour /saat had before the option existed.
/// </summary>
public static class SourceTimeZones
{
    public const string InvalidKey = "timezone.invalid_zone";
    public const int MaxInputLength = 40;

    public static IReadOnlyList<SourceZone> All { get; } =
    [
        new("tr", "Europe/Istanbul", "timezone.zone.turkey", ["tr", "turkey", "turkiye", "türkiye", "istanbul"]),
        new("uk", "Europe/London", "timezone.zone.uk", ["uk", "gb", "london", "gmt", "bst"]),
        new("ny", "America/New_York", "timezone.source.eastern", ["ny", "et", "est", "edt", "eastern", "newyork", "new_york"]),
        new("chicago", "America/Chicago", "timezone.source.central", ["chicago", "ct", "cst", "cdt", "central"]),
        new("la", "America/Los_Angeles", "timezone.source.pacific", ["la", "pt", "pst", "pdt", "pacific", "losangeles", "los_angeles"]),
        new("utc", "UTC", "timezone.source.utc", ["utc"]),
    ];

    public static SourceZone Default => All[0];

    private static readonly Dictionary<string, SourceZone> ByName = All
        .SelectMany(z => Names(z).Select(a => (Name: a, Zone: z)))
        .ToDictionary(x => x.Name, x => x.Zone, StringComparer.Ordinal);

    /// <summary>The aliases plus the lower-cased IANA id (for UTC the two coincide).</summary>
    private static IEnumerable<string> Names(SourceZone zone) => zone.Aliases.Append(zone.ZoneId.ToLowerInvariant()).Distinct(StringComparer.Ordinal);

    /// <summary>The zone for the typed text, <see cref="Default"/> when nothing was typed, <c>null</c> when it is unknown.</summary>
    public static SourceZone? Resolve(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Default;
        if (input.Length > MaxInputLength)
            return null;
        return ByName.GetValueOrDefault(Normalize(input));
    }

    /// <summary>
    /// Autocomplete: every zone when nothing is typed; otherwise the zones whose label (as shown to this guild) or one of whose
    /// names starts with the typed text — so <c>pdt</c> offers Los Angeles and <c>new</c> offers New York.
    /// </summary>
    public static IEnumerable<SourceZone> Suggest(string? typed, Func<SourceZone, string> label)
    {
        var text = Normalize(typed ?? "");
        if (text.Length == 0)
            return All;
        return All.Where(z => Names(z).Any(a => a.StartsWith(text, StringComparison.Ordinal))
            || Normalize(label(z)).Contains(text, StringComparison.Ordinal)
            || Normalize(label(z)).Replace(" ", "", StringComparison.Ordinal).Contains(text, StringComparison.Ordinal));
    }

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();
}
