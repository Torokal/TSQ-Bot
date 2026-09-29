using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions;

/// <summary>
/// Section "Predictions" — the ONE place the module's channels, role and economy constants come from. Defaults are the
/// production values of the main guild; everything is validated at startup (a typo stops the bot with a clear CONFIG line
/// instead of failing every command later). Nothing here is a secret.
/// </summary>
public sealed class PredictionsOptions
{
    public const string Section = "Predictions";

    public const ulong DefaultChannelId = 1048525775919390840;
    public const ulong DefaultCommandsChannelId = 689814679056547857;
    public const ulong DefaultCreatorRoleId = 1233057768408350741;

    /// <summary>Smallest id Discord can issue (timestamp bits above the 22 worker/process/increment bits).</summary>
    public const ulong MinSnowflake = 1UL << 22;

    /// <summary>The only channel where predictions are created and managed and where their public cards live.</summary>
    public ulong ChannelId { get; set; } = DefaultChannelId;

    /// <summary>The only channel for the member commands (wallet, daily, entries, leaderboard, tournament).</summary>
    public ulong CommandsChannelId { get; set; } = DefaultCommandsChannelId;

    /// <summary>Members with this role may create predictions (and manage their own). Administrator alone does not grant creating.</summary>
    public ulong CreatorRoleId { get; set; } = DefaultCreatorRoleId;

    /// <summary>Every member's balance at the start of each tournament (whole TSQ Coin).</summary>
    public int InitialBalanceCoins { get; set; } = 1000;

    public int DailyMinCoins { get; set; } = 10;
    public int DailyMaxCoins { get; set; } = 100;

    /// <summary>The odds of an outcome written without one ("Beraberlik" instead of "Beraberlik | 2.30").</summary>
    public decimal DefaultOdds { get; set; } = 2.00m;

    public int MaxOutcomes { get; set; } = PredictionRules.HardMaxOutcomes;

    public long InitialBalanceMinor => InitialBalanceCoins * Coins.MinorPerCoin;

    /// <summary>The default odds as the ×100 integer every calculation uses (validated: exact, in range).</summary>
    public int DefaultOddsX100 => (int)(DefaultOdds * 100m);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (name, id) in new[] { (nameof(ChannelId), ChannelId), (nameof(CommandsChannelId), CommandsChannelId), (nameof(CreatorRoleId), CreatorRoleId) })
        {
            if (id is < MinSnowflake or > long.MaxValue)
                errors.Add($"{Section}:{name} must be a Discord id (got {id})");
        }

        if (InitialBalanceCoins is < 1 or > 1_000_000)
            errors.Add($"{Section}:InitialBalanceCoins must be 1-1000000 (got {InitialBalanceCoins})");
        if (DailyMinCoins < 1 || DailyMaxCoins < DailyMinCoins || DailyMaxCoins > 1_000_000)
            errors.Add($"{Section}:DailyMinCoins/DailyMaxCoins must satisfy 1 <= min <= max <= 1000000 (got {DailyMinCoins}-{DailyMaxCoins})");
        if (DefaultOdds * 100m != decimal.Truncate(DefaultOdds * 100m) || DefaultOdds * 100m is < Odds.MinX100 or > Odds.MaxX100)
            errors.Add($"{Section}:DefaultOdds must be {Odds.Format(Odds.MinX100)}-{Odds.Format(Odds.MaxX100)} with at most two decimals (got {DefaultOdds})");
        if (MaxOutcomes is < PredictionRules.MinOutcomes or > PredictionRules.HardMaxOutcomes)
            errors.Add($"{Section}:MaxOutcomes must be {PredictionRules.MinOutcomes}-{PredictionRules.HardMaxOutcomes} (got {MaxOutcomes})");
        return errors;
    }
}
