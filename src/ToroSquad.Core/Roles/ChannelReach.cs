using ToroSquad.Core.Security;

namespace ToroSquad.Core.Roles;

/// <summary>Whether everyone who can read one channel can also read another.</summary>
public enum ChannelReach
{
    /// <summary>A channel is not in the gateway cache (deleted, or not loaded): nothing is proven.</summary>
    Unknown = 0,

    /// <summary>Every member who can read the reader channel can also read the source channel.</summary>
    Contained = 1,

    /// <summary>Some member could read the reader channel but not the source channel (or it cannot be ruled out).</summary>
    NotContained = 2,
}

/// <summary>
/// The View Channel overwrites of one channel: the @everyone overwrite, role overwrites that allow or deny View Channel,
/// member overwrites that allow or deny it, and whether the channel is age-restricted.
/// </summary>
public sealed record ChannelViewRules(
    bool EveryoneAllows,
    bool EveryoneDenies,
    IReadOnlySet<ulong> RolesAllowed,
    IReadOnlySet<ulong> RolesDenied,
    IReadOnlySet<ulong> MembersAllowed,
    IReadOnlySet<ulong> MembersDenied,
    bool AgeRestricted = false)
{
    public static ChannelViewRules None { get; } = new(false, false, new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>(), new HashSet<ulong>());
}

/// <summary>
/// Decides <see cref="ChannelReach"/> from the guild's role permissions and two channels' View Channel overwrites — no member
/// list needed. Discord's rule for one member: Administrator sees everything; otherwise a member overwrite decides; otherwise
/// any of the member's role overwrites that allows wins, else any that denies; otherwise the @everyone overwrite applied to
/// the member's guild permissions (@everyone plus their roles). The check looks for any role combination (and any member
/// with an overwrite) that could read the reader channel but not the source; it answers Contained only when none exists.
/// Conservative: a member overwrite on either side that could make a difference counts as not contained.
/// </summary>
public static class ChannelReachRules
{
    private const ulong View = (ulong)GuildPermission.ViewChannel;
    private const ulong Admin = (ulong)GuildPermission.Administrator;

    public static ChannelReach Reach(ulong everyonePermissions, IReadOnlyDictionary<ulong, ulong> rolePermissions, ChannelViewRules reader, ChannelViewRules source)
    {
        if ((everyonePermissions & Admin) != 0)
            return ChannelReach.Contained;
        if (source.AgeRestricted && !reader.AgeRestricted)
            return ChannelReach.NotContained;

        // Members whose own overwrite decides: one shut out of the source must be shut out of the reader too, and one let
        // into the reader must be let into the source.
        if (source.MembersDenied.Any(m => !reader.MembersDenied.Contains(m)) || reader.MembersAllowed.Any(m => !source.MembersAllowed.Contains(m)))
            return ChannelReach.NotContained;

        // Role combinations: a role that allows the source makes any combination with it read the source, so a counterexample
        // only uses the other (non-administrator) roles.
        var usable = rolePermissions.Where(r => (r.Value & Admin) == 0 && !source.RolesAllowed.Contains(r.Key)).Select(r => r.Key).ToHashSet();
        var notDeniedByReader = usable.Where(r => !reader.RolesDenied.Contains(r)).ToList();

        bool BaseView(IEnumerable<ulong> roles) => ((everyonePermissions | roles.Aggregate(0UL, (p, r) => p | rolePermissions[r])) & View) != 0;
        bool Everyone(ChannelViewRules rules, IEnumerable<ulong> roles) => !rules.EveryoneDenies && (rules.EveryoneAllows || BaseView(roles));

        // A role denied in the source, together with a role allowed in the reader (or with the reader's @everyone rule).
        if (usable.Any(source.RolesDenied.Contains) && usable.Any(reader.RolesAllowed.Contains))
            return ChannelReach.NotContained;
        if (notDeniedByReader.Any(source.RolesDenied.Contains) && Everyone(reader, notDeniedByReader))
            return ChannelReach.NotContained;

        // The source's @everyone rule shuts a member out while the reader lets them in.
        if (usable.Where(reader.RolesAllowed.Contains).Any(q => !source.RolesDenied.Contains(q) && !Everyone(source, [q])))
            return ChannelReach.NotContained;
        if (reader.EveryoneDenies || source.EveryoneAllows)
            return ChannelReach.Contained;
        if (source.EveryoneDenies)
            return reader.EveryoneAllows || BaseView(notDeniedByReader) ? ChannelReach.NotContained : ChannelReach.Contained;
        // Both follow the guild permissions; only a reader that lets everyone in can differ (a member without View).
        return reader.EveryoneAllows && !BaseView([]) ? ChannelReach.NotContained : ChannelReach.Contained;
    }
}
