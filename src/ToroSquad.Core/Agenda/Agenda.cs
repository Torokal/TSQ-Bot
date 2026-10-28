using ToroSquad.Core.Modules;

namespace ToroSquad.Core.Agenda;

/// <summary>Which kind of programme an agenda item belongs to (one per source module).</summary>
public enum AgendaKind
{
    Formula1 = 1,
    /// <summary>CS2 matches of the esports module.</summary>
    Esports = 2,
    Volleyball = 3,
    Lfg = 4,
}

/// <summary>
/// The programme state of one item — never a result. <see cref="InProgress"/> only when the source's CURRENT data says the
/// event has started and not ended (never because the planned time passed); <see cref="StartPassed"/> when the planned start
/// is behind us and nothing confirms what happened since.
/// </summary>
public enum AgendaStatus
{
    Scheduled = 0,
    InProgress = 1,
    StartPassed = 2,
    Postponed = 3,
    Cancelled = 4,

    /// <summary>An LFG listing without a planned time: looking for players now.</summary>
    LookingNow = 5,

    /// <summary>An LFG listing whose planned time has passed but which is still open (nobody claims the game started).</summary>
    ListingStartPassed = 6,
}

/// <summary>What a link points at; the agenda chooses the wording (a result-bearing target says so).</summary>
public enum AgendaLinkKind
{
    /// <summary>A TSQ programme card in Discord (weekend schedule, reminder, start card): no results.</summary>
    Card = 1,

    /// <summary>The LFG listing's own card.</summary>
    Listing = 2,

    /// <summary>An official event page outside Discord, already validated by the source module: it may show results.</summary>
    OfficialPage = 3,
}

/// <summary>
/// A link the source has verified. Discord cards carry only channel + message ids (the agenda builds the jump URL with the
/// guild of the query — never a guild from elsewhere); an official page carries its validated URL.
/// </summary>
public sealed record AgendaLink(AgendaLinkKind Kind, ChannelId? Channel = null, MessageId? Message = null, string? Url = null)
{
    public static AgendaLink ToCard(ChannelId channel, MessageId message, AgendaLinkKind kind = AgendaLinkKind.Card) => new(kind, channel, message);

    public static AgendaLink ToPage(string url) => new(AgendaLinkKind.OfficialPage, Url: url);
}

/// <summary>LFG capacity: joined players (maybe/waitlist never counted) of the listing's capacity.</summary>
public sealed record AgendaSlots(int Joined, int Capacity, bool Full);

/// <summary>
/// One programme entry, normalized and result-free. Time: <see cref="StartUtc"/> for a known instant; <see cref="Date"/> alone
/// for a known day without a time (never an invented 00:00); neither only for <see cref="AgendaStatus.LookingNow"/>.
/// <see cref="AudienceChannel"/>: the channel whose readers may see this item (LFG); the agenda shows it in another channel
/// only when that channel is provably open to everyone.
/// </summary>
public sealed record AgendaItem(
    AgendaKind Kind,
    string SourceId,
    string Title,
    string? Context,
    DateTimeOffset? StartUtc,
    DateOnly? Date,
    AgendaStatus Status,
    AgendaLink? Link = null,
    AgendaSlots? Slots = null,
    ChannelId? AudienceChannel = null);

/// <summary>
/// One read: the guild, its language, ONE "now" for the whole read, and the window [FromUtc, ToUtc) — local calendar days
/// [FromDate, ToDate) of the guild's time zone converted day by day. Read-only: a source never calls a provider, writes,
/// stages a notification or touches delivery state.
/// </summary>
public sealed record AgendaQuery(GuildId Guild, string Language, DateTimeOffset Now, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateOnly FromDate, DateOnly ToDate);

public enum AgendaSourceState
{
    /// <summary>Data is current (an empty item list then really means "nothing in this window").</summary>
    Ready = 0,

    /// <summary>No data yet, provider not configured, or demo data this guild may not see.</summary>
    NotConfigured = 1,

    /// <summary>The last good data is older than the source's freshness limit: items are shown, live states are not.</summary>
    Stale = 2,

    /// <summary>The source could not be read.</summary>
    Failed = 3,

    /// <summary>The source module is off in this guild (set by the agenda; a source is not even asked).</summary>
    Disabled = 4,
}

/// <summary>
/// A source's answer. <paramref name="FreshAt"/>: the source's real last successful fetch (never "now");
/// <paramref name="CoversUntil"/>: how far its data reaches (null = the whole window); <paramref name="Undated"/>: items it
/// knows but cannot place on a day; <paramref name="Unverifiable"/>: items a guild filter could not judge (e.g. missing VRS
/// data) and that were therefore left out, never let through.
/// </summary>
public sealed record AgendaSourceResult(
    AgendaSourceState State,
    IReadOnlyList<AgendaItem> Items,
    DateTimeOffset? FreshAt = null,
    DateTimeOffset? CoversUntil = null,
    bool Demo = false,
    int Undated = 0,
    int Unverifiable = 0)
{
    public static AgendaSourceResult NotConfigured(DateTimeOffset? freshAt = null) => new(AgendaSourceState.NotConfigured, [], freshAt);

    public static AgendaSourceResult Failed() => new(AgendaSourceState.Failed, []);

    public static AgendaSourceResult Disabled() => new(AgendaSourceState.Disabled, []);
}

/// <summary>
/// A module's read-only programme for TSQ Ajanda (registered by the module itself: <c>AddScoped&lt;IAgendaSource, …&gt;</c> —
/// no central switch). Implementations read only what the module already caches or stores, apply the module's own guild
/// interest rules, and return programme data only (no scores, winners, classifications or live race control).
/// </summary>
public interface IAgendaSource
{
    AgendaKind Kind { get; }

    /// <summary>The module whose gate decides whether this source is used at all in a guild.</summary>
    ModuleId Module { get; }

    Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken);
}
