using System.Text;
using ToroSquad.Core;

namespace ToroSquad.Modules.Lfg.Domain;

/// <summary>
/// Lifecycle of a listing. <see cref="Open"/> and <see cref="Full"/> are active (Full reopens when someone leaves before
/// expiry); every other state is terminal. <see cref="Orphaned"/>: the listing message was deleted in Discord (or its
/// channel is gone), so nobody can interact with it any more — kept apart from <see cref="Closed"/> (an explicit decision
/// of the owner or a moderator) so history and diagnostics stay truthful.
/// </summary>
public enum LfgStatus
{
    Open = 0,
    Full = 1,
    Closed = 2,
    Expired = 3,
    Orphaned = 4,
}

/// <summary>
/// One group-finder listing as the rest of the module sees it. Game-agnostic by design: the bot only knows that
/// <see cref="Owner"/> looks for <see cref="MaxPlayers"/> players for <see cref="GameName"/> and wrote <see cref="Details"/>
/// — it never interprets either text. <see cref="Players"/> are Discord user ids (owner first, then join order); display
/// names are never stored. <see cref="Version"/> changes with every stored state change (used to detect a card drawn from an older state).
/// </summary>
public sealed record LfgListingView(
    long Id,
    GuildId Guild,
    ChannelId Channel,
    MessageId? Message,
    UserId Owner,
    string GameName,
    string? Details,
    int MaxPlayers,
    LfgStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<UserId> Players,
    long Version = 0)
{
    public bool IsActive => Status is LfgStatus.Open or LfgStatus.Full;
}

public enum LfgDraftError
{
    None = 0,
    GameMissing = 1,
    GameTooShort = 2,
    GameTooLong = 3,
    DetailsTooLong = 4,
    PlayersOutOfRange = 5,
    DurationInvalid = 6,
}

/// <summary>A validated request to open a listing (normalized texts, bounded size, one of the offered durations).</summary>
public sealed record LfgDraft(string GameName, string? Details, int MaxPlayers, TimeSpan Duration);

/// <summary>
/// Input rules shared by the slash command (Discord's own option bounds) and the server-side check (never trust the client).
/// Lengths count Unicode characters (runes), like Discord's option limits, not UTF-16 units.
/// </summary>
public static class LfgRules
{
    public const int GameNameMinLength = 2;
    public const int GameNameMaxLength = 50;

    /// <summary>One short line for the card: mode, rank, roles, plan… — enough for any game, never a wall of text.</summary>
    public const int DetailsMaxLength = 200;

    public const int MinPlayers = 2;

    /// <summary>Upper bound of the slash option. Lfg:MaxPlayersPerListing can only lower it.</summary>
    public const int HardMaxPlayers = 50;

    /// <summary>The optional durations offered by /ekip (no free minute input); no choice = the configured default.</summary>
    public static IReadOnlyList<int> DurationChoicesMinutes { get; } = [60, 120, 180];

    private const int ZeroWidthJoiner = 0x200D;

    public static (LfgDraft? Draft, LfgDraftError Error) Validate(string? game, string? details, int players, int? durationMinutes, int maxPlayers, int defaultMinutes)
    {
        var name = Normalize(game);
        if (name is null)
            return (null, LfgDraftError.GameMissing);
        var nameLength = Length(name);
        if (nameLength < GameNameMinLength)
            return (null, LfgDraftError.GameTooShort);
        if (nameLength > GameNameMaxLength)
            return (null, LfgDraftError.GameTooLong);

        var text = Normalize(details);
        if (text is not null && Length(text) > DetailsMaxLength)
            return (null, LfgDraftError.DetailsTooLong);

        if (players < MinPlayers || players > Math.Min(maxPlayers, HardMaxPlayers))
            return (null, LfgDraftError.PlayersOutOfRange);

        if (durationMinutes is { } minutes && !DurationChoicesMinutes.Contains(minutes))
            return (null, LfgDraftError.DurationInvalid);

        return (new LfgDraft(name, text, players, TimeSpan.FromMinutes(durationMinutes ?? defaultMinutes)), LfgDraftError.None);
    }

    /// <summary>Drops control/format characters, collapses whitespace runs to one space and trims; null when nothing is left.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = sb.Length > 0;
                continue;
            }

            // Format characters (bidi overrides, zero-width spaces) can disguise text; the zero-width joiner stays for emoji.
            if (Rune.IsControl(rune) || (Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format && rune.Value != ZeroWidthJoiner))
                continue;
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(rune.ToString());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public static int Length(string value) => value.EnumerateRunes().Count();
}
