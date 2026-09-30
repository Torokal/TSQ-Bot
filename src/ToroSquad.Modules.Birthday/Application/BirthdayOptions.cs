using ToroSquad.Core;
using ToroSquad.Core.Guilds;

namespace ToroSquad.Modules.Birthday.Application;

/// <summary>
/// Section "Birthday". The module itself is switched on per guild (/modules enable birthday, off by default) and the
/// announcement channel is guild data (/tsq-admin modul:birthday islem:configure), not configuration.
/// </summary>
public sealed class BirthdayOptions
{
    public const string Section = "Birthday";

    /// <summary>TSQ's "Doğum Günü Bireyi" role.</summary>
    public const ulong DefaultRoleId = 1553890408348520468;

    /// <summary>The temporary role for the day; 0 = no role (announcements only).</summary>
    public ulong RoleId { get; set; } = DefaultRoleId;

    /// <summary>IANA id of the zone whose midnight starts and ends a birthday (never the UTC date).</summary>
    public string TimeZone { get; set; } = GuildSettings.DefaultTimeZoneId;

    /// <summary>How often the reconciliation runs; it also always wakes up just after local midnight.</summary>
    public int ReconciliationIntervalSeconds { get; set; } = 60;

    public RoleId? Role => RoleId == 0 ? null : new RoleId(RoleId);

    public TimeSpan ReconciliationInterval => TimeSpan.FromSeconds(ReconciliationIntervalSeconds);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!GuildTime.TryResolve(TimeZone, out _))
            errors.Add($"Birthday:TimeZone '{TimeZone}' is not a known time zone id");
        if (ReconciliationIntervalSeconds is < 30 or > 600)
            errors.Add("Birthday:ReconciliationIntervalSeconds must be between 30 and 600");
        return errors;
    }
}
