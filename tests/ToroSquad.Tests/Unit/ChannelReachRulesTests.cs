using Discord;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Guilds;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// "Can everyone who reads the card's channel also read the source channel?" — the rule behind showing LFG listings and
/// predictions of another channel on a public agenda card. Real server layouts, member overwrites, age restriction, and an
/// exhaustive cross-check of every role combination against Discord's View Channel rule.
/// </summary>
public sealed class ChannelReachRulesTests
{
    private const ulong View = 1024;
    private const ulong Admin = 8;
    private const ulong Member = 1, Muted = 2, Guest = 3, Staff = 4, Boss = 5;

    private static HashSet<ulong> Set(params ulong[] ids) => [.. ids];

    private static ChannelViewRules Rules(bool everyoneAllows = false, bool everyoneDenies = false, ulong[]? allow = null, ulong[]? deny = null,
        ulong[]? membersAllowed = null, ulong[]? membersDenied = null, bool nsfw = false) =>
        new(everyoneAllows, everyoneDenies, Set(allow ?? []), Set(deny ?? []), Set(membersAllowed ?? []), Set(membersDenied ?? []), nsfw);

    /// <summary>A typical guild: @everyone may view by default; Member, Muted, Guest, Staff without extra View; Boss is an administrator.</summary>
    private static readonly Dictionary<ulong, ulong> Roles = new() { [Member] = 0, [Muted] = 0, [Guest] = 0, [Staff] = 0, [Boss] = Admin };

    private static ChannelReach Reach(ChannelViewRules reader, ChannelViewRules source, ulong everyone = View, Dictionary<ulong, ulong>? roles = null) =>
        ChannelReachRules.Reach(everyone, roles ?? Roles, reader, source);

    [Fact]
    public void A_channel_with_the_same_rules_as_the_card_channel_is_reachable()
    {
        Reach(Rules(), Rules()).Should().Be(ChannelReach.Contained, "no overwrites anywhere");
        Reach(Rules(deny: [Muted]), Rules(deny: [Muted])).Should().Be(ChannelReach.Contained, "the muted role is shut out of both");
        Reach(Rules(everyoneDenies: true, allow: [Member]), Rules(everyoneDenies: true, allow: [Member])).Should().Be(ChannelReach.Contained, "member-role layout");
        Reach(Rules(), Rules(), everyone: 0, roles: new() { [Member] = View, [Guest] = 0 }).Should().Be(ChannelReach.Contained, "View comes from the member role");
    }

    [Fact]
    public void A_source_shut_to_someone_who_reads_the_card_channel_is_not_reachable()
    {
        Reach(Rules(), Rules(deny: [Muted])).Should().Be(ChannelReach.NotContained, "muted members read #bot but not the source");
        Reach(Rules(), Rules(everyoneDenies: true, allow: [Staff])).Should().Be(ChannelReach.NotContained, "a staff-only source");
        Reach(Rules(everyoneDenies: true, allow: [Member, Guest]), Rules(everyoneDenies: true, allow: [Member])).Should().Be(ChannelReach.NotContained, "guests read #bot only");
        Reach(Rules(allow: [Muted]), Rules(deny: [Muted])).Should().Be(ChannelReach.NotContained);
        Reach(Rules(everyoneAllows: true), Rules(), everyone: 0, roles: new() { [Member] = View }).Should().Be(ChannelReach.NotContained,
            "#bot lets in members without View, the source follows the guild permission");
    }

    [Fact]
    public void A_narrower_card_channel_makes_a_wider_source_reachable()
    {
        Reach(Rules(everyoneDenies: true, allow: [Staff]), Rules()).Should().Be(ChannelReach.Contained, "a staff channel reading a public one");
        Reach(Rules(deny: [Muted, Guest]), Rules(deny: [Muted])).Should().Be(ChannelReach.Contained);
        Reach(Rules(everyoneDenies: true, allow: [Member], deny: [Muted]), Rules(everyoneDenies: true, allow: [Member], deny: [Muted])).Should().Be(ChannelReach.Contained);
    }

    [Fact]
    public void Member_overwrites_administrators_and_age_restriction_are_respected()
    {
        Reach(Rules(), Rules(membersDenied: [77])).Should().Be(ChannelReach.NotContained, "one member is shut out of the source");
        Reach(Rules(membersDenied: [77]), Rules(membersDenied: [77])).Should().Be(ChannelReach.Contained, "and out of #bot too");
        Reach(Rules(everyoneDenies: true, membersAllowed: [78]), Rules(everyoneDenies: true)).Should().Be(ChannelReach.NotContained, "a member let into #bot only");
        Reach(Rules(), Rules(nsfw: true)).Should().Be(ChannelReach.NotContained, "an age-restricted source from a normal channel");
        Reach(Rules(nsfw: true), Rules(nsfw: true)).Should().Be(ChannelReach.Contained);
        Reach(Rules(), Rules(everyoneDenies: true, allow: [Boss])).Should().Be(ChannelReach.NotContained, "administrators see everything, others don't");
        Reach(Rules(), Rules(everyoneDenies: true), everyone: View | Admin).Should().Be(ChannelReach.Contained, "@everyone is administrator");
    }

    [Fact]
    public void Discord_overwrites_are_read_into_view_rules()
    {
        const ulong everyoneId = 100;
        var rules = DiscordGuildGateway.ViewRules(everyoneId,
        [
            new Overwrite(everyoneId, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
            new Overwrite(Member, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Allow)),
            new Overwrite(Muted, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
            new Overwrite(Guest, PermissionTarget.Role, new OverwritePermissions(sendMessages: PermValue.Deny)),
            new Overwrite(77, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Deny)),
            new Overwrite(78, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Allow)),
        ], ageRestricted: true);

        (rules.EveryoneAllows, rules.EveryoneDenies, rules.AgeRestricted).Should().Be((false, true, true));
        rules.RolesAllowed.Should().BeEquivalentTo([Member]);
        rules.RolesDenied.Should().BeEquivalentTo([Muted], "a role overwrite that does not touch View Channel does not count");
        rules.MembersDenied.Should().BeEquivalentTo([77UL]);
        rules.MembersAllowed.Should().BeEquivalentTo([78UL]);
    }

    // ---------------------------------------------------------------- exhaustive cross-check

    /// <summary>Discord's View Channel rule for a member with these roles and no member overwrite.</summary>
    private static bool CanView(ulong everyone, Dictionary<ulong, ulong> roles, ChannelViewRules channel, IReadOnlyCollection<ulong> memberRoles)
    {
        var guild = memberRoles.Aggregate(everyone, (p, r) => p | roles[r]);
        if ((guild & Admin) != 0)
            return true;
        if (memberRoles.Any(channel.RolesAllowed.Contains))
            return true;
        if (memberRoles.Any(channel.RolesDenied.Contains))
            return false;
        return !channel.EveryoneDenies && (channel.EveryoneAllows || (guild & View) != 0);
    }

    [Fact]
    public void The_rule_matches_every_role_combination_exactly()
    {
        var random = new Random(20261030);
        var checkedCases = 0;
        for (var round = 0; round < 3000; round++)
        {
            var roleCount = random.Next(1, 6);
            var ids = Enumerable.Range(1, roleCount).Select(i => (ulong)i).ToArray();
            var roles = ids.ToDictionary(i => i, _ => random.Next(10) switch { 0 => Admin, < 4 => View, _ => 0UL });
            var everyone = random.Next(3) == 0 ? 0UL : View;

            ChannelViewRules Random()
            {
                var everyoneRule = random.Next(4);
                var allow = new HashSet<ulong>();
                var deny = new HashSet<ulong>();
                foreach (var id in ids)
                {
                    switch (random.Next(4))
                    {
                        case 0: allow.Add(id); break;
                        case 1: deny.Add(id); break;
                    }
                }

                return new ChannelViewRules(everyoneRule == 0, everyoneRule == 1, allow, deny, new HashSet<ulong>(), new HashSet<ulong>());
            }

            var reader = Random();
            var source = Random();
            var leak = Enumerable.Range(0, 1 << roleCount)
                .Select(mask => ids.Where((_, i) => (mask & (1 << i)) != 0).ToArray())
                .Any(member => CanView(everyone, roles, reader, member) && !CanView(everyone, roles, source, member));

            ChannelReachRules.Reach(everyone, roles, reader, source).Should().Be(leak ? ChannelReach.NotContained : ChannelReach.Contained,
                $"round {round}: everyone={everyone} roles=[{string.Join(",", roles.Select(r => r.Key + ":" + r.Value))}] " +
                $"reader=({reader.EveryoneAllows},{reader.EveryoneDenies},+{string.Join(",", reader.RolesAllowed)},-{string.Join(",", reader.RolesDenied)}) " +
                $"source=({source.EveryoneAllows},{source.EveryoneDenies},+{string.Join(",", source.RolesAllowed)},-{string.Join(",", source.RolesDenied)})");
            checkedCases++;
        }

        checkedCases.Should().Be(3000);
    }
}
