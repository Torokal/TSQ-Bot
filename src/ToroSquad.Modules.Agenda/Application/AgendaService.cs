using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;

namespace ToroSquad.Modules.Agenda.Application;

/// <summary>
/// The one pipeline behind /ajanda and /gundem (and every page or refresh of their cards): one "now", the guild's local days,
/// each requested source read in turn (never in parallel on the scoped database), and the same rules for both commands:
/// <list type="bullet">
/// <item>a source whose module is off in this guild is not read at all (its cache may still hold data);</item>
/// <item>a source that throws only marks itself failed — the others are still shown;</item>
/// <item>"in progress" survives only with current data; a planned start in the past without confirmation becomes
/// "start passed, state not verified" — never a guessed live or finished state;</item>
/// <item>items are placed on local days by their exact start, or by their known date when the time is unknown; an item
/// without a date is never placed;</item>
/// <item>an item tied to a channel's readers (LFG) is shown in another channel only when that channel is provably open to
/// everyone — otherwise it is dropped without a trace (no title, link or count);</item>
/// <item>identity is source + event id (two sessions or two matches with similar titles stay two items).</item>
/// </list>
/// Read-only: no provider call, no write, no notification.
/// </summary>
public sealed class AgendaService(
    IEnumerable<IAgendaSource> sources,
    IModuleGate gate,
    IGuildSettingsStore settings,
    IGuildGateway guilds,
    TimeProvider clock,
    ILogger<AgendaService> logger)
{
    /// <summary>At most this many other channels are checked per card for LFG listings; listings beyond that are left out.</summary>
    public const int MaxAudienceLookups = 10;

    public async Task<AgendaSnapshot> BuildAsync(AgendaRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var guildSettings = await settings.GetAsync(request.Guild, ct);
        var zoneId = GuildTime.TryResolve(guildSettings.TimeZoneId, out var zone) ? guildSettings.TimeZoneId
            : GuildTime.TryResolve(GuildSettings.DefaultTimeZoneId, out zone) ? GuildSettings.DefaultTimeZoneId : "UTC";
        var (offset, count) = AgendaOptions.Days(request.View, request.Period);
        var days = AgendaCalendar.Days(now, zone, offset, count);
        var query = new AgendaQuery(request.Guild, guildSettings.Language, now, days[0].StartUtc, days[^1].EndUtc, days[0].Date, days[^1].Date.AddDays(1));

        var views = new List<AgendaSourceView>();
        var items = new List<AgendaItem>();
        foreach (var kind in AgendaOptions.Kinds(request.View, request.Sport))
        {
            var result = await ReadAsync(kind, query, ct);
            views.Add(new AgendaSourceView(kind, result, result.CoversUntil is { } until && until < query.ToUtc));
            items.AddRange(result.Items.Where(i => i.Kind == kind).Select(i => Normalize(i, result.State, now)));
        }

        var visible = await VisibleAsync(items, request, ct);
        return new AgendaSnapshot(request, guildSettings.Language, zoneId, now, Group(visible, days, now), views);
    }

    private async Task<AgendaSourceResult> ReadAsync(AgendaKind kind, AgendaQuery query, CancellationToken ct)
    {
        var source = sources.FirstOrDefault(s => s.Kind == kind);
        if (source is null || !await gate.IsEnabledAsync(query.Guild, source.Module, ct))
            return AgendaSourceResult.Disabled();
        try
        {
            return await source.ReadAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The kind only: never a title, a member or a listing.
            logger.LogWarning(ex, "agenda_source_failed kind={Kind} guild={Guild}", kind, query.Guild);
            return AgendaSourceResult.Failed();
        }
    }

    /// <summary>The shared status rules (both commands, every source).</summary>
    public static AgendaItem Normalize(AgendaItem item, AgendaSourceState state, DateTimeOffset now)
    {
        var status = item.Status;
        if (status == AgendaStatus.InProgress && state != AgendaSourceState.Ready)
            status = AgendaStatus.StartPassed; // old data cannot say "running"
        if (status == AgendaStatus.Scheduled && item.StartUtc is { } start && start <= now)
            status = AgendaStatus.StartPassed; // the planned time passed — nothing more is known
        return status == item.Status ? item : item with { Status = status };
    }

    /// <summary>LFG privacy: same channel, or a channel provably open to everyone; anything unproven is dropped silently.</summary>
    private async Task<List<AgendaItem>> VisibleAsync(List<AgendaItem> items, AgendaRequest request, CancellationToken ct)
    {
        var verdicts = new Dictionary<ulong, bool>();
        var visible = new List<AgendaItem>(items.Count);
        foreach (var item in items.DistinctBy(i => (i.Kind, i.SourceId)))
        {
            if (item.AudienceChannel is not { } channel || channel == request.Channel)
            {
                visible.Add(item);
                continue;
            }

            if (!verdicts.TryGetValue(channel.Value, out var open))
            {
                open = false;
                if (verdicts.Count < MaxAudienceLookups)
                {
                    try
                    {
                        open = (await guilds.GetChannelAudienceAsync(request.Guild, channel, ct)).IsOpenToEveryone;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "agenda_audience_failed guild={Guild}", request.Guild); // closed on error
                    }
                }

                verdicts[channel.Value] = open;
            }

            if (open)
                visible.Add(item);
        }

        return visible;
    }

    /// <summary>
    /// Places items on the local days. Today also takes the LFG listings looking for players now, open listings whose start is
    /// already behind us, and events the current data shows running although they began before midnight.
    /// </summary>
    public static IReadOnlyList<AgendaDayGroup> Group(IReadOnlyList<AgendaItem> items, IReadOnlyList<AgendaDay> days, DateTimeOffset now)
    {
        var today = days.FirstOrDefault(d => d.OffsetFromToday == 0);
        var nowGroup = new List<AgendaItem>();
        var perDay = days.ToDictionary(d => d.Date, _ => new List<AgendaItem>());
        foreach (var item in items)
        {
            if (item.Status == AgendaStatus.LookingNow || (item.Status == AgendaStatus.ListingStartPassed && today is not null && item.StartUtc < today.StartUtc))
            {
                if (today is not null)
                    nowGroup.Add(item);
                continue;
            }

            if (item.StartUtc is { } start)
            {
                var day = days.FirstOrDefault(d => start >= d.StartUtc && start < d.EndUtc);
                if (day is null && today is not null && start < today.StartUtc && item.Status == AgendaStatus.InProgress)
                    day = today;
                if (day is not null)
                    perDay[day.Date].Add(item);
            }
            else if (item.Date is { } date && perDay.TryGetValue(date, out var list))
            {
                list.Add(item);
            }
        }

        return days.Select(d => new AgendaDayGroup(
                d,
                d == today ? Sort(nowGroup) : [],
                Sort(perDay[d.Date])))
            .ToList();
    }

    /// <summary>By start; a day's undated-time items after the timed ones; then by kind and id (stable).</summary>
    private static List<AgendaItem> Sort(List<AgendaItem> items) =>
        items.OrderBy(i => i.StartUtc is null ? 1 : 0)
            .ThenBy(i => i.StartUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.SourceId, StringComparer.Ordinal)
            .ToList();
}
