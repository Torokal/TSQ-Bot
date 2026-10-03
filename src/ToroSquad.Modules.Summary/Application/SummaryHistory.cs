using ToroSquad.Core;

namespace ToroSquad.Modules.Summary.Application;

/// <summary>One page of a channel's history, newest first. <paramref name="ReachedStart"/>: nothing older exists.</summary>
public sealed record SummaryHistoryPage(SummaryFetchStatus Status, IReadOnlyList<SummarySourceMessage> Messages, bool ReachedStart)
{
    public static SummaryHistoryPage NoAccess { get; } = new(SummaryFetchStatus.NoAccess, [], false);
    public static SummaryHistoryPage Failed { get; } = new(SummaryFetchStatus.Failed, [], false);
}

public enum SummaryHistoryOutcome
{
    /// <summary>The required number of member messages was found before any earlier summary: summarize them.</summary>
    Enough = 0,

    /// <summary>An earlier TSQ summary came first: only the member messages after it were counted (fewer than required).</summary>
    AfterEarlierSummary = 1,

    /// <summary>The channel's real start was reached without an earlier summary: a first summary (MinMessages applies).</summary>
    WholeHistory = 2,

    /// <summary>The page limit was hit with neither enough messages, nor a marker, nor the start: unknown — fail closed.</summary>
    LimitHit = 3,

    NoAccess = 4,
    Failed = 5,
}

/// <summary>
/// The result of one scan. <paramref name="MemberMessages"/> are the member messages newer than any earlier summary, newest
/// first (at most the required number). <see cref="Read"/> is every message of the pages this scan read (any kind) — what
/// a reply's older target is looked up in, without another Discord read.
/// </summary>
public sealed record SummaryHistoryScan(SummaryHistoryOutcome Outcome, IReadOnlyList<SummarySourceMessage> MemberMessages, int PageCount)
{
    public IReadOnlyDictionary<ulong, SummarySourceMessage> Read { get; init; } = new Dictionary<ulong, SummarySourceMessage>();

    public int EligibleCount => MemberMessages.Count;

    public bool MarkerFound => Outcome == SummaryHistoryOutcome.AfterEarlierSummary;

    public bool Exhausted => Outcome == SummaryHistoryOutcome.WholeHistory;

    public bool LimitHit => Outcome == SummaryHistoryOutcome.LimitHit;
}

/// <summary>
/// Reads one channel's (or thread's — never its parent's) history newest → oldest, page by page, until one of: the
/// required number of member messages (<see cref="SummaryHistoryOutcome.Enough"/> — older markers no longer matter), an
/// earlier TSQ summary (<see cref="SummaryHistoryOutcome.AfterEarlierSummary"/>), the channel's start
/// (<see cref="SummaryHistoryOutcome.WholeHistory"/>), or <see cref="MaxPages"/> pages
/// (<see cref="SummaryHistoryOutcome.LimitHit"/>: nothing is guessed about an earlier summary). Member messages are the same
/// ones the transcript uses (<see cref="SummaryAuthorKind.Member"/>): bots, webhooks and system events never count, so bot
/// traffic cannot fill the threshold.
/// </summary>
public static class SummaryHistory
{
    /// <summary>At most this many history pages (100 messages each) per /ozetle.</summary>
    public const int MaxPages = 10;

    /// <summary>An earlier summary: TSQ Bot's own message starting with the exact title (the first part of a split summary).</summary>
    public static bool IsSummaryMarker(SummarySourceMessage message) =>
        message.FromThisBot && message.Content.StartsWith(SummaryPrompt.Title, StringComparison.Ordinal);

    public static async Task<SummaryHistoryScan> ScanAsync(
        ISummaryDiscord discord, GuildId guild, ChannelId channel, int required, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(required, 1);
        var members = new List<SummarySourceMessage>(required);
        var read = new Dictionary<ulong, SummarySourceMessage>();
        SummaryHistoryScan Done(SummaryHistoryOutcome outcome, int pageCount) => new(outcome, members, pageCount) { Read = read };
        ulong? before = null;
        for (var pages = 1; pages <= MaxPages; pages++)
        {
            var page = await discord.ReadHistoryPageAsync(guild, channel, before, cancellationToken);
            if (page.Status != SummaryFetchStatus.Ok)
                return Done(page.Status == SummaryFetchStatus.NoAccess ? SummaryHistoryOutcome.NoAccess : SummaryHistoryOutcome.Failed, pages);

            foreach (var message in page.Messages)
                read[message.Id] = message;

            foreach (var message in page.Messages.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id))
            {
                if (IsSummaryMarker(message))
                    return Done(SummaryHistoryOutcome.AfterEarlierSummary, pages);
                if (message.Kind != SummaryAuthorKind.Member)
                    continue;
                members.Add(message);
                if (members.Count >= required)
                    return Done(SummaryHistoryOutcome.Enough, pages);
            }

            if (page.ReachedStart || page.Messages.Count == 0)
                return Done(SummaryHistoryOutcome.WholeHistory, pages);
            before = page.Messages.Min(m => m.Id);
        }

        return Done(SummaryHistoryOutcome.LimitHit, MaxPages);
    }
}
