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

    /// <summary>
    /// How long a settled or cancelled prediction's public card stays in the channel after the settlement/cancellation was
    /// committed; then only the Discord message is removed (the prediction, its entries, coins and statistics stay).
    /// </summary>
    public int TerminalCardRetentionHours { get; set; } = 12;

    public WeeklyLeaderboardOptions WeeklyLeaderboard { get; set; } = new();

    public TimeSpan TerminalCardRetention => TimeSpan.FromHours(TerminalCardRetentionHours);

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
        if (TerminalCardRetentionHours is < 1 or > 168)
            errors.Add($"{Section}:TerminalCardRetentionHours must be 1-168 (got {TerminalCardRetentionHours})");
        errors.AddRange(WeeklyLeaderboard.Validate());
        return errors;
    }
}

/// <summary>
/// Section "Predictions:WeeklyLeaderboard" — the automatic weekly post of the active tournament's leaderboard in the
/// commands channel: one day and local time per week in one time zone (default Sunday 20:00 Europe/Istanbul), delivered
/// at most once per guild and week, late by at most <see cref="CatchUpHours"/> (a bot that was down longer skips that week).
/// Strings (not enums/TimeOnly) so a typo is a clear CONFIG line instead of a binder exception.
/// </summary>
public sealed class WeeklyLeaderboardOptions
{
    public const string Section = PredictionsOptions.Section + ":WeeklyLeaderboard";

    public bool Enabled { get; set; } = true;
    public string DayOfWeek { get; set; } = nameof(System.DayOfWeek.Sunday);
    public string LocalTime { get; set; } = "20:00";
    public string TimeZone { get; set; } = TurkeyCalendar.TimeZoneId;
    public int CatchUpHours { get; set; } = 12;

    /// <summary>The schedule, or null when a setting is invalid (<see cref="Validate"/> names it).</summary>
    public WeeklySchedule? Schedule()
    {
        if (!Enum.TryParse<DayOfWeek>(DayOfWeek, ignoreCase: true, out var day) || !Enum.IsDefined(day) || int.TryParse(DayOfWeek, out _))
            return null;
        if (!TimeOnly.TryParseExact(LocalTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time))
            return null;
        if (!ToroSquad.Core.Guilds.GuildTime.TryResolve(TimeZone, out var zone))
            return null;
        return CatchUpHours is < 1 or > 72 ? null : new WeeklySchedule(day, time, zone, TimeSpan.FromHours(CatchUpHours));
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.TryParse<DayOfWeek>(DayOfWeek, ignoreCase: true, out var day) || !Enum.IsDefined(day) || int.TryParse(DayOfWeek, out _))
            errors.Add($"{Section}:DayOfWeek must be Monday-Sunday (got {DayOfWeek})");
        if (!TimeOnly.TryParseExact(LocalTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            errors.Add($"{Section}:LocalTime must be HH:mm (got {LocalTime})");
        if (!ToroSquad.Core.Guilds.GuildTime.TryResolve(TimeZone, out _))
            errors.Add($"{Section}:TimeZone must be an IANA time zone (got {TimeZone})");
        if (CatchUpHours is < 1 or > 72)
            errors.Add($"{Section}:CatchUpHours must be 1-72 (got {CatchUpHours})");
        return errors;
    }
}
