using System.Security.Cryptography;
using ToroSquad.Core;
using ToroSquad.Core.Roles;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>A uniformly distributed integer in [fromInclusive, toExclusive): the one random primitive of the draw.</summary>
public interface IGiveawayRandom
{
    int NextInt32(int fromInclusive, int toExclusive);
}

/// <summary>
/// Production source: <see cref="RandomNumberGenerator.GetInt32(int, int)"/> — unbiased (rejection sampling), thread-safe,
/// no seed. Never System.Random.
/// </summary>
public sealed class SecureGiveawayRandom : IGiveawayRandom
{
    public int NextInt32(int fromInclusive, int toExclusive) => RandomNumberGenerator.GetInt32(fromInclusive, toExclusive);
}

/// <summary>Winners in draw order (place 1 first); <see cref="Unavailable"/>: a membership check could not be answered.</summary>
public sealed record DrawResult(IReadOnlyList<UserId> Winners, bool Unavailable);

/// <summary>
/// The draw: a Fisher–Yates shuffle run only as far as needed. Each step picks uniformly among the candidates not drawn yet
/// and keeps the pick if the user is still a member of the server (checked over REST, only for picks); a user who left is
/// skipped and the next pick is again uniform among the rest — so every current member among the candidates has the same
/// chance and nobody can win twice. Fewer valid candidates than winners: all of them win. None: no winner.
/// A lookup Discord could not answer stops the draw (<see cref="DrawResult.Unavailable"/>; tried again later), so a
/// transient error never silently changes who can win.
/// </summary>
public static class WinnerDraw
{
    public static async Task<DrawResult> DrawAsync(IReadOnlyList<UserId> candidates, int count, IGiveawayRandom random,
        Func<UserId, Task<MemberLookupOutcome>> membership)
    {
        var pool = candidates.Distinct().ToArray();
        var winners = new List<UserId>(Math.Min(count, pool.Length));
        for (var i = 0; i < pool.Length && winners.Count < count; i++)
        {
            var j = random.NextInt32(i, pool.Length);
            (pool[i], pool[j]) = (pool[j], pool[i]);
            switch (await membership(pool[i]))
            {
                case MemberLookupOutcome.Found:
                    winners.Add(pool[i]);
                    break;
                case MemberLookupOutcome.NotMember:
                    break;
                default:
                    return new DrawResult([], Unavailable: true);
            }
        }

        return new DrawResult(winners, Unavailable: false);
    }
}
