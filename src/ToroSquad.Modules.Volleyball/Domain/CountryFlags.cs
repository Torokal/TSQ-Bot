namespace ToroSquad.Modules.Volleyball.Domain;

/// <summary>
/// Unicode flag emoji for the three-letter country codes volleyball providers use (FIVB/IOC federation codes). A flag is
/// plain text (no image hosting, no licence question) and the safe default "logo". Unknown code = no flag.
/// </summary>
public static class CountryFlags
{
    // Federation (IOC-style) code -> ISO 3166-1 alpha-2, for national teams that play in FIVB/CEV women's competitions.
    // Where the IOC code equals ISO alpha-3 the mapping is still explicit: no guessing from string shapes.
    private static readonly Dictionary<string, string> Iso2 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TUR"] = "TR",
        ["ITA"] = "IT",
        ["BRA"] = "BR",
        ["USA"] = "US",
        ["CHN"] = "CN",
        ["JPN"] = "JP",
        ["POL"] = "PL",
        ["SRB"] = "RS",
        ["NED"] = "NL",
        ["GER"] = "DE",
        ["FRA"] = "FR",
        ["BEL"] = "BE",
        ["CAN"] = "CA",
        ["DOM"] = "DO",
        ["THA"] = "TH",
        ["KOR"] = "KR",
        ["BUL"] = "BG",
        ["CZE"] = "CZ",
        ["SLO"] = "SI",
        ["UKR"] = "UA",
        ["CRO"] = "HR",
        ["ESP"] = "ES",
        ["SWE"] = "SE",
        ["AZE"] = "AZ",
        ["ROU"] = "RO",
        ["GRE"] = "GR",
        ["HUN"] = "HU",
        ["SVK"] = "SK",
        ["FIN"] = "FI",
        ["POR"] = "PT",
        ["SUI"] = "CH",
        ["AUT"] = "AT",
        ["DEN"] = "DK",
        ["NOR"] = "NO",
        ["BIH"] = "BA",
        ["MNE"] = "ME",
        ["MKD"] = "MK",
        ["ALB"] = "AL",
        ["LAT"] = "LV",
        ["LTU"] = "LT",
        ["EST"] = "EE",
        ["ISR"] = "IL",
        ["GEO"] = "GE",
        ["ARM"] = "AM",
        ["BLR"] = "BY",
        ["RUS"] = "RU",
        ["ISL"] = "IS",
        ["IRL"] = "IE",
        ["LUX"] = "LU",
        ["CYP"] = "CY",
        ["MLT"] = "MT",
        ["MDA"] = "MD",
        ["KAZ"] = "KZ",
        ["ARG"] = "AR",
        ["COL"] = "CO",
        ["PER"] = "PE",
        ["MEX"] = "MX",
        ["CUB"] = "CU",
        ["PUR"] = "PR",
        ["CHI"] = "CL",
        ["VEN"] = "VE",
        ["KEN"] = "KE",
        ["EGY"] = "EG",
        ["CMR"] = "CM",
        ["NGR"] = "NG",
        ["TUN"] = "TN",
        ["ALG"] = "DZ",
        ["MAR"] = "MA",
        ["RSA"] = "ZA",
        ["AUS"] = "AU",
        ["NZL"] = "NZ",
        ["VIE"] = "VN",
        ["PHI"] = "PH",
        ["INA"] = "ID",
        ["TPE"] = "TW",
        ["HKG"] = "HK",
        ["IRI"] = "IR",
        ["IND"] = "IN",
        ["UZB"] = "UZ",
        ["KGZ"] = "KG",
    };

    /// <summary>The flag emoji, or null when the code is unknown.</summary>
    public static string? For(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || !Iso2.TryGetValue(countryCode.Trim(), out var iso))
            return null;
        return string.Concat(iso.Select(c => char.ConvertFromUtf32(0x1F1E6 + (char.ToUpperInvariant(c) - 'A'))));
    }
}
