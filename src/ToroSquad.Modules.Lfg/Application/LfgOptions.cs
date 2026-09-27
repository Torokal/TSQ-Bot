using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Application;

/// <summary>
/// Section "Lfg" — server-side safety limits only. The module itself is switched on per guild (/modules enable lfg, off by
/// default) and the optional listing channel is guild data (/lfg-admin channel), not configuration.
/// </summary>
public sealed class LfgOptions
{
    public const string Section = "Lfg";

    /// <summary>Lifetime of a listing when the creator picks no duration.</summary>
    public int DefaultExpirationMinutes { get; set; } = 120;

    /// <summary>Active (open or full) listings one user may have in a guild at the same time.</summary>
    public int MaxActiveListingsPerUser { get; set; } = 2;

    /// <summary>Largest team size, owner included (2..<see cref="LfgRules.HardMaxPlayers"/>).</summary>
    public int MaxPlayersPerListing { get; set; } = 20;

    public TimeSpan DefaultDuration => TimeSpan.FromMinutes(DefaultExpirationMinutes);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DefaultExpirationMinutes is < 15 or > 720)
            errors.Add("Lfg:DefaultExpirationMinutes must be between 15 and 720");
        if (MaxActiveListingsPerUser is < 1 or > 10)
            errors.Add("Lfg:MaxActiveListingsPerUser must be between 1 and 10");
        if (MaxPlayersPerListing < LfgRules.MinPlayers || MaxPlayersPerListing > LfgRules.HardMaxPlayers)
            errors.Add($"Lfg:MaxPlayersPerListing must be between {LfgRules.MinPlayers} and {LfgRules.HardMaxPlayers}");
        return errors;
    }
}
