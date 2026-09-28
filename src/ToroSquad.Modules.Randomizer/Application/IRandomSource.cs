using System.Security.Cryptography;

namespace ToroSquad.Modules.Randomizer.Application;

/// <summary>
/// The one random primitive of TSQ Randomizer: a uniformly distributed integer in [fromInclusive, toExclusive). Every
/// command (dice, number, pick, coin) is built on it, so the randomness lives in exactly one place and tests can script it.
/// </summary>
public interface IRandomSource
{
    int NextInt32(int fromInclusive, int toExclusive);
}

/// <summary>
/// Production source: <see cref="RandomNumberGenerator.GetInt32(int, int)"/> — cryptographically secure and unbiased
/// (rejection sampling), thread-safe, no seed and no state. Never System.Random (architecture tests). Nothing is adjusted:
/// no streak prevention, no memory of earlier results; every call is independent.
/// </summary>
public sealed class SecureRandomSource : IRandomSource
{
    public int NextInt32(int fromInclusive, int toExclusive) => RandomNumberGenerator.GetInt32(fromInclusive, toExclusive);
}
