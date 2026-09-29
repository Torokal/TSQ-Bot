using System.Globalization;
using System.Text.RegularExpressions;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Giveaway.Domain;

/// <summary>The fixed limits of a giveaway (no configuration: the same safe bounds everywhere).</summary>
public static class GiveawayRules
{
    /// <summary>The entry: Discord's own reaction with this emoji on the giveaway card. Nothing else counts.</summary>
    public const string EntryEmoji = "🎉";

    public const int PrizeMaxLength = 100;
    public const int DescriptionMaxLength = 500;
    public const int DurationInputMaxLength = 20;
    public const int WinnersInputMaxLength = 2;

    public const int MinWinners = 1;
    public const int MaxWinners = 10;
    public const int DefaultWinners = 1;

    /// <summary>The worker checks about twice a minute, so a minute is the shortest honest duration.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(30);

    /// <summary>Active giveaways per guild (also keeps the /giveaway autocomplete within Discord's 25 choices).</summary>
    public const int MaxActivePerGuild = 20;

    /// <summary>
    /// Post the card, add 🎉, read the reactions (Read Message History) and edit the card later. Checked before the form opens,
    /// so an admin never fills in a giveaway the bot cannot run.
    /// </summary>
    public const GuildPermission RequiredChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks |
                                                              GuildPermission.AddReactions | GuildPermission.ReadMessageHistory;
}

/// <summary>What the form asked for, validated.</summary>
public sealed record GiveawayInput(string Prize, TimeSpan Duration, int Winners, string? Description);

/// <summary>A validated form, or the localization key of the first problem (field order).</summary>
public sealed record GiveawayFormCheck(GiveawayInput? Input, string? ErrorKey, IReadOnlyList<object> Args)
{
    public static GiveawayFormCheck Error(string key, params object[] args) => new(null, key, args);
}

/// <summary>The giveaway form's raw text fields → <see cref="GiveawayInput"/>. Every check the modal cannot enforce happens here.</summary>
public static class GiveawayForm
{
    public static GiveawayFormCheck Parse(string? prize, string? duration, string? winners, string? description)
    {
        var p = (prize ?? "").Trim();
        if (p.Length == 0)
            return GiveawayFormCheck.Error("giveaway.form.prize_required");
        if (p.Length > GiveawayRules.PrizeMaxLength)
            return GiveawayFormCheck.Error("giveaway.form.prize_too_long", GiveawayRules.PrizeMaxLength);

        if (GiveawayDuration.Parse(duration) is not { } length)
            return GiveawayFormCheck.Error("giveaway.form.duration_invalid");
        if (length < GiveawayRules.MinDuration)
            return GiveawayFormCheck.Error("giveaway.form.duration_too_short");
        if (length > GiveawayRules.MaxDuration)
            return GiveawayFormCheck.Error("giveaway.form.duration_too_long", (int)GiveawayRules.MaxDuration.TotalDays);

        var count = GiveawayRules.DefaultWinners;
        var w = (winners ?? "").Trim();
        if (w.Length > 0 && (!int.TryParse(w, NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
                             count is < GiveawayRules.MinWinners or > GiveawayRules.MaxWinners))
            return GiveawayFormCheck.Error("giveaway.form.winners_invalid", GiveawayRules.MinWinners, GiveawayRules.MaxWinners);

        var d = description?.Trim();
        if (d is { Length: > GiveawayRules.DescriptionMaxLength })
            return GiveawayFormCheck.Error("giveaway.form.description_too_long", GiveawayRules.DescriptionMaxLength);

        return new GiveawayFormCheck(new GiveawayInput(p, length, count, string.IsNullOrEmpty(d) ? null : d), null, []);
    }
}

/// <summary>
/// Durations as people type them: <c>30m</c>, <c>2h</c>, <c>1d</c>, <c>1d 12h</c>, <c>1d12h</c>, and the Turkish
/// <c>30dk</c>, <c>2s</c> / <c>2sa</c> / <c>2 saat</c>, <c>1g</c> / <c>1 gün</c>. Minutes, hours, days and weeks only — there
/// are no seconds, so <c>s</c> is the Turkish "saat". A bare number is refused (its unit would be a guess).
/// </summary>
public static partial class GiveawayDuration
{
    public static TimeSpan? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > GiveawayRules.DurationInputMaxLength)
            return null;

        var text = input.Replace('İ', 'i').ToLowerInvariant();
        long minutes = 0;
        var position = 0;
        foreach (Match m in Token().Matches(text))
        {
            if (!string.IsNullOrWhiteSpace(text[position..m.Index]) || UnitMinutes(m.Groups[2].Value) is not { } unit)
                return null;
            minutes += long.Parse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture) * unit;
            position = m.Index + m.Length;
        }

        if (position == 0 || !string.IsNullOrWhiteSpace(text[position..]))
            return null;
        return TimeSpan.FromMinutes(minutes);
    }

    private static long? UnitMinutes(string unit) => unit switch
    {
        "m" or "min" or "dk" or "dak" or "dakika" => 1,
        "h" or "hr" or "hour" or "hours" or "s" or "sa" or "saat" => 60,
        "d" or "day" or "days" or "g" or "gün" or "gun" => 60 * 24,
        "w" or "week" or "weeks" or "hf" or "hafta" => 60 * 24 * 7,
        _ => null,
    };

    [GeneratedRegex(@"(\d{1,6})\s*([a-zçğıöşü]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Token();
}
