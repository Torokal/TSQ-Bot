using System.Security.Cryptography;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>The daily reward's only random primitive: a uniform integer in [fromInclusive, toInclusive] (tests script it).</summary>
public interface IPredictionRandom
{
    int NextInclusive(int fromInclusive, int toInclusive);
}

/// <summary>
/// Production source, as TSQ Randomizer: <see cref="RandomNumberGenerator.GetInt32(int, int)"/> — cryptographically secure
/// and unbiased (rejection sampling), thread-safe, no seed. Never System.Random (architecture tests).
/// </summary>
public sealed class SecurePredictionRandom : IPredictionRandom
{
    public int NextInclusive(int fromInclusive, int toInclusive) => RandomNumberGenerator.GetInt32(fromInclusive, checked(toInclusive + 1));
}
