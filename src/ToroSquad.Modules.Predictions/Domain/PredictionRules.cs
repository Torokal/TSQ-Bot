using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Predictions.Domain;

/// <summary>
/// Draft (form, never stored) → Publishing (row stored, card being posted) → Open → Locked → Settled; Open/Locked →
/// Cancelled. A card that could not be confirmed ends as <see cref="Abandoned"/> (nobody could enter it). Terminal states
/// are one-way: the write transaction re-checks the stored status before every change, so a prediction is settled or
/// cancelled at most once and never reopened.
/// </summary>
public enum PredictionStatus
{
    Publishing = 0,
    Open = 1,
    Locked = 2,
    Settled = 3,
    Cancelled = 4,
    Abandoned = 5,
}

/// <summary>Why a prediction stopped taking entries.</summary>
public enum PredictionLockReason
{
    Manual = 0,
    Deadline = 1,

    /// <summary>Its public card is gone (deleted message or channel); stakes are kept, it can still be settled or cancelled by id.</summary>
    CardMissing = 2,
}

/// <summary>
/// An entry's life: <see cref="Pending"/> is the one ACTIVE bet (its stake is in the wallet's pending coins); while the
/// prediction is open the member may change it or withdraw it (<see cref="Withdrawn"/>: the stake is back, the bet takes no
/// part in anything any more) and enter again (Withdrawn → Pending, the same row). Won / Lost / Refunded are final.
/// </summary>
public enum PredictionEntryStatus
{
    Pending = 0,
    Won = 1,
    Lost = 2,
    Refunded = 3,
    Withdrawn = 4,
}

/// <summary>Every coin movement: a signed amount and the balance after it.</summary>
public enum PredictionLedgerKind
{
    Initial = 0,
    Daily = 1,

    /// <summary>A new entry (or a new entry after a withdrawal): the stake is debited.</summary>
    Stake = 2,

    Payout = 3,

    /// <summary>The prediction was cancelled: the stake comes back.</summary>
    Refund = 4,

    /// <summary>The member raised the stake of their entry: only the difference is debited.</summary>
    StakeIncrease = 5,

    /// <summary>The member lowered the stake of their entry: only the difference comes back.</summary>
    StakeDecrease = 6,

    /// <summary>The member withdrew their entry: the stake (never a possible payout) comes back.</summary>
    Withdrawal = 7,
}

public enum PredictionTournamentStatus
{
    Active = 0,
    Closed = 1,
}

/// <summary>The fixed limits of TSQ Öngörü (bounds that are not configuration).</summary>
public static class PredictionRules
{
    public const int TitleMinLength = 5;
    public const int TitleMaxLength = 200;
    public const int RulesMaxLength = 1000;
    public const int OutcomeLabelMaxLength = 80;
    public const int MinOutcomes = 2;

    /// <summary>Discord: a string select holds at most 25 options — one per outcome.</summary>
    public const int HardMaxOutcomes = 25;

    /// <summary>Separates an outcome from its odds on one line of the form.</summary>
    public const char Separator = '|';

    /// <summary>The outcomes field: 25 lines of 80 characters plus odds fit comfortably; Discord's text input maximum is 4000.</summary>
    public const int OutcomesInputMaxLength = 4000;

    public const int LockDateInputMaxLength = 12;
    public const int LockTimeInputMaxLength = 8;
    public const int AmountInputMaxLength = 20;
    public const int CancelReasonMinLength = 3;
    public const int CancelReasonMaxLength = 300;

    /// <summary>Entries shown per page of /ongoru tahminlerim.</summary>
    public const int EntriesPerPage = 10;

    public const int LeaderboardSize = 10;
    public const int PodiumSize = 3;

    /// <summary>
    /// Post the card, embed it, find it again after an uncertain send (Read Message History) and edit it later. No Add
    /// Reactions, no Administrator: entries are a select menu on the bot's own message.
    /// </summary>
    public const GuildPermission RequiredChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks |
                                                              GuildPermission.ReadMessageHistory;

    /// <summary>The tournament announcement: a plain embed message in the commands channel.</summary>
    public const GuildPermission RequiredCommandsChannelPermissions = GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks;
}
