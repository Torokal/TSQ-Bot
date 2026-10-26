using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Sor.Persistence;

namespace ToroSquad.Modules.Sor.Application;

/// <summary>A guild's effective /sor settings: who may ask where, and the limits.</summary>
public sealed record SorGuildSettings(IReadOnlyList<ulong> AllowedChannels, IReadOnlyList<ulong> AllowedRoles, SorLimits Limits)
{
    /// <summary>
    /// Access in this channel (a thread follows its parent channel's listing) for a member with these roles. Empty lists mean
    /// no restriction (normal members may use /sor once the module is enabled).
    /// </summary>
    public bool Allows(ulong channel, ulong? parentChannel, IEnumerable<ulong> roles, out string? refusalKey)
    {
        refusalKey = null;
        if (AllowedChannels.Count > 0 && !AllowedChannels.Contains(channel) && !(parentChannel is { } parent && AllowedChannels.Contains(parent)))
            refusalKey = "sor.channel_not_allowed";
        else if (AllowedRoles.Count > 0 && !roles.Any(AllowedRoles.Contains))
            refusalKey = "sor.role_not_allowed";
        return refusalKey is null;
    }

    public static IReadOnlyList<ulong> ParseIds(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0UL)
            .Where(id => id != 0).Distinct().ToList();

    public static string FormatIds(IEnumerable<ulong> ids) => string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Reads a guild's /sor settings (the configured defaults where the guild set nothing).</summary>
public sealed class SorSettingsReader(ToroDbContext db, IOptions<SorOptions> options)
{
    public const int MaxListedIds = 25;

    public async Task<SorGuildSettings> GetAsync(GuildId guild, CancellationToken ct)
    {
        var row = await db.Set<SorGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, ct);
        return From(row, options.Value);
    }

    public static SorGuildSettings From(SorGuildConfigEntity? row, SorOptions options)
    {
        var d = options.DefaultLimits;
        if (row is null)
            return new SorGuildSettings([], [], d);
        var limits = new SorLimits(
            row.UserCooldownSeconds ?? d.UserCooldownSeconds,
            row.UserDailyLimit ?? d.UserDailyLimit,
            row.GuildDailyLimit ?? d.GuildDailyLimit,
            row.MaxContextTurns ?? d.MaxContextTurns,
            row.IdleMinutes ?? d.IdleMinutes,
            Math.Min(row.HistoryHours ?? d.HistoryHours, options.HistoryRetentionHours));
        // A stored combination that is no longer valid (e.g. the defaults changed) falls back to the defaults, never to "unlimited".
        return new SorGuildSettings(SorGuildSettings.ParseIds(row.AllowedChannelIds), SorGuildSettings.ParseIds(row.AllowedRoleIds),
            limits.Problems().Count == 0 ? limits : d);
    }
}
