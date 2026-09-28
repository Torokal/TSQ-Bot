namespace ToroSquad.Modules.Randomizer.Application;

/// <summary>The two faces of the coin. The mapping from the random draw is fixed: 0 → Yazı, 1 → Tura.</summary>
public enum CoinFace
{
    Yazi = 0,
    Tura = 1,
}

/// <summary>A dice roll: every die in [1, sides], in the order rolled, and their sum.</summary>
public sealed record DiceRoll(DiceSpec Spec, IReadOnlyList<int> Dice)
{
    public int Total => Dice.Sum(); // at most 20 × 10,000
}

/// <summary>
/// The four draws of TSQ Randomizer on top of the single <see cref="IRandomSource"/>. Plain uniform draws: no weighting, no
/// re-rolls, no memory of earlier results.
/// </summary>
public sealed class RandomizerService(IRandomSource random)
{
    /// <summary>Each die independently in [1, sides]; <c>sides + 1</c> is safe because sides ≤ 10,000.</summary>
    public DiceRoll Roll(DiceSpec spec)
    {
        var dice = new int[spec.Count];
        for (var i = 0; i < dice.Length; i++)
            dice[i] = random.NextInt32(1, spec.Sides + 1);
        return new DiceRoll(spec, dice);
    }

    /// <summary>Inclusive on both ends; <c>Maximum + 1</c> is safe because |Maximum| ≤ 1,000,000,000.</summary>
    public int Between(NumberRange range) => random.NextInt32(range.Minimum, range.Maximum + 1);

    /// <summary>Index in [0, count) — each option exactly the same weight.</summary>
    public string Choose(IReadOnlyList<string> options)
    {
        ArgumentOutOfRangeException.ThrowIfZero(options.Count);
        return options[random.NextInt32(0, options.Count)];
    }

    public CoinFace Flip() => random.NextInt32(0, 2) == 0 ? CoinFace.Yazi : CoinFace.Tura;
}
