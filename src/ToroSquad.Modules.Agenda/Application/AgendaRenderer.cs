using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Modules.Agenda.Application;

/// <summary>One page of the card: the embed and its buttons.</summary>
public sealed record AgendaCard(MessageEmbed Embed, IReadOnlyList<MessageButton> Buttons, int Page, int Pages);

/// <summary>
/// The card of /ajanda and /gundem: ONE embed, days as headings in the guild's time zone, times as Discord timestamps (each
/// reader sees their own local time), a "data status" field, and page buttons when the list is long. Every item stays
/// reachable through the pages (never a silent cut); a day continued on the next page repeats its heading; /gundem's first
/// page shows both days whenever at least one item of each fits. Programme only:
/// no score, winner, classification or race-control text exists in the items, and nothing here invents one. All member- and
/// provider-supplied text is defused (no mentions, markdown or links); links are built only from verified ids or URLs.
/// </summary>
public sealed class AgendaRenderer(ILocalizer localizer)
{
    public const uint Color = 0x1C7ED6;
    public const int MaxItemsPerPage = 10;

    /// <summary>Description budget per page (Discord allows 4096; the rest stays free for the heading of a continued day).</summary>
    public const int DescriptionBudget = 3600;

    public const int TitleMax = 90;
    public const int ContextMax = 80;

    public AgendaCard Render(AgendaSnapshot snapshot, int requestedPage)
    {
        var lang = snapshot.Language;
        var pages = Paginate(snapshot);
        var page = Math.Clamp(requestedPage, 0, pages.Count - 1);
        var fields = new List<EmbedField> { new(L(lang, "agenda.status.title"), Clip(StatusLines(snapshot), DiscordLimits.EmbedFieldValueMax), false) };
        var footer = L(lang, "agenda.footer", page + 1, pages.Count, snapshot.TimeZoneId);
        var lines = new List<string>(pages[page].Lines);
        if (Remaining(snapshot, pages, page) is { } more)
            lines.AddRange(["", more]);
        var embed = new MessageEmbed(Clip(Title(snapshot), DiscordLimits.EmbedTitleMax), string.Join("\n", lines), null, fields, footer, null, Color);
        return new AgendaCard(embed, Buttons(snapshot, page, pages.Count), page, pages.Count);
    }

    // ---- pages ----

    /// <summary>One page: its lines and how many items of each day it shows.</summary>
    private sealed record Page(List<string> Lines, Dictionary<DateOnly, int> Counts);

    /// <summary>"➕ N more on the next pages: Tomorrow 4 · 30 October 2" — so page 1 always says what is still to come.</summary>
    private string? Remaining(AgendaSnapshot snapshot, List<Page> pages, int page)
    {
        var later = new Dictionary<DateOnly, int>();
        foreach (var p in pages.Skip(page + 1))
        {
            foreach (var (date, count) in p.Counts)
                later[date] = later.GetValueOrDefault(date) + count;
        }

        var total = later.Values.Sum();
        if (total == 0)
            return null;
        var lang = snapshot.Language;
        var days = snapshot.Days.Where(d => later.GetValueOrDefault(d.Day.Date) > 0).Take(3)
            .Select(d => ShortDay(d.Day, lang) + " " + later[d.Day.Date].ToString(CultureInfo.InvariantCulture)).ToList();
        if (later.Count(kv => kv.Value > 0) > 3)
            days.Add("…");
        return L(lang, "agenda.more", total, string.Join(" · ", days));
    }

    private string ShortDay(AgendaDay day, string lang) => day.OffsetFromToday switch
    {
        0 => L(lang, "agenda.period.today"),
        1 => L(lang, "agenda.period.tomorrow"),
        _ => day.Date.ToString("d MMMM", Culture(lang)),
    };

    /// <summary>On /gundem's first page each day keeps at least this many of its items when both days do not fit together.</summary>
    public const int FirstPageShare = 3;

    /// <summary>A run of one day's items [From, To); <see cref="BreakAfter"/> ends the page after it.</summary>
    private sealed record Chunk(AgendaDayGroup Group, string Heading, List<(string? Sub, string Text)> Blocks, int From, int To, bool BreakAfter = false);

    private List<Page> Paginate(AgendaSnapshot snapshot)
    {
        var lang = snapshot.Language;
        var shown = DaysToShow(snapshot);
        if (shown.Count == 0)
            return [new Page([L(lang, Incomplete(snapshot) ? "agenda.empty_period_incomplete" : "agenda.empty_period")], [])];

        var chunks = shown.Select(g =>
        {
            var blocks = g.Now.Select(i => ((string?)L(lang, "agenda.lfg.now_heading"), Item(i, snapshot)))
                .Concat(g.Items.Select(i => ((string?)null, Item(i, snapshot))))
                .ToList();
            return new Chunk(g, DayHeading(g.Day, lang), blocks, 0, blocks.Count);
        }).ToList();
        var pages = Layout(snapshot, chunks);
        if (pages.Count > 1 && snapshot.Request.View == AgendaView.Digest && chunks.Count == 2 && BothDaysFirst(snapshot, chunks[0], chunks[1]) is { } split)
            return Layout(snapshot, split);
        return pages;
    }

    /// <summary>
    /// /gundem when today and tomorrow do not fit on one page: page 1 still shows both days — today's first items, then
    /// tomorrow's first items (or its "nothing planned" line) — and each day's remaining items follow in order on the next
    /// pages under "(devam)". Each day keeps up to <see cref="FirstPageShare"/> items on page 1 (more when the other day has
    /// fewer), fewer only when the text would not fit. Null when today has nothing (it then already starts page 1 with its
    /// own line) or when even one item of each day does not fit.
    /// </summary>
    private List<Chunk>? BothDaysFirst(AgendaSnapshot snapshot, Chunk today, Chunk tomorrow)
    {
        int t = today.Blocks.Count, m = tomorrow.Blocks.Count;
        if (t == 0)
            return null;
        var b = Math.Min(m, Math.Max(MaxItemsPerPage - t, FirstPageShare));
        var a = Math.Min(t, MaxItemsPerPage - b);
        while (true)
        {
            var chunks = new List<Chunk> { today with { To = a }, tomorrow with { To = b, BreakAfter = true } };
            if (Layout(snapshot, chunks).Count == 1)
            {
                if (a < t)
                    chunks.Add(today with { From = a });
                if (b < m)
                    chunks.Add(tomorrow with { From = b });
                return chunks;
            }

            if (a > 1 && (a >= b || b <= 1))
                a--;
            else if (b > 1)
                b--;
            else
                return null;
        }
    }

    /// <summary>
    /// Lays the chunks out on pages: at most <see cref="MaxItemsPerPage"/> items and <see cref="DescriptionBudget"/> characters
    /// a page, items never cut; a day's heading opens each page part of that day and says "(devam)" once the day was shown.
    /// </summary>
    private List<Page> Layout(AgendaSnapshot snapshot, IReadOnlyList<Chunk> chunks)
    {
        var lang = snapshot.Language;
        var continued = L(lang, "agenda.continued");
        var pages = new List<Page>();
        var current = new List<string>();
        var counts = new Dictionary<DateOnly, int>();
        var length = 0;
        var itemsOnPage = 0;
        DateOnly? dayOnPage = null;
        string? subShown = null;
        var started = new HashSet<DateOnly>();

        void Add(string line)
        {
            current.Add(line);
            length += line.Length + 1;
        }

        void Break()
        {
            pages.Add(new Page(current, counts));
            current = [];
            counts = [];
            length = 0;
            itemsOnPage = 0;
            dayOnPage = null;
            subShown = null;
        }

        void Heading(Chunk chunk)
        {
            if (current.Count > 0)
                Add("");
            var date = chunk.Group.Day.Date;
            Add(started.Contains(date) ? chunk.Heading + " " + continued : chunk.Heading);
            started.Add(date);
            dayOnPage = date;
            subShown = null;
        }

        foreach (var chunk in chunks)
        {
            var date = chunk.Group.Day.Date;
            if (chunk.Blocks.Count == 0)
            {
                var empty = L(lang, Incomplete(snapshot) ? "agenda.empty_day_incomplete" : "agenda.empty_day");
                if (current.Count > 0 && length + chunk.Heading.Length + empty.Length + 4 > DescriptionBudget)
                    Break();
                Heading(chunk);
                Add(empty);
            }

            for (var i = chunk.From; i < chunk.To; i++)
            {
                var (sub, text) = chunk.Blocks[i];
                var need = text.Length + 1 + (dayOnPage != date ? chunk.Heading.Length + continued.Length + 3 : 0) +
                           (sub is not null && sub != subShown ? sub.Length + 1 : 0);
                if (current.Count > 0 && (itemsOnPage >= MaxItemsPerPage || length + need > DescriptionBudget))
                    Break();
                if (dayOnPage != date)
                    Heading(chunk);
                if (sub is not null && sub != subShown)
                {
                    Add(sub);
                    subShown = sub;
                }

                Add(text);
                itemsOnPage++;
                counts[date] = counts.GetValueOrDefault(date) + 1;
            }

            if (chunk.BreakAfter && current.Count > 0)
                Break();
        }

        if (current.Count > 0)
            pages.Add(new Page(current, counts));
        return pages;
    }

    /// <summary>/gundem: always today and tomorrow; /ajanda: a single day always, several days only those with items.</summary>
    private static List<AgendaDayGroup> DaysToShow(AgendaSnapshot snapshot) =>
        snapshot.Request.View == AgendaView.Digest || snapshot.Days.Count == 1
            ? snapshot.Days.ToList()
            : snapshot.Days.Where(d => d.Count > 0).ToList();

    /// <summary>Whether "nothing here" could be a gap in the data rather than a free day.</summary>
    private static bool Incomplete(AgendaSnapshot snapshot) =>
        snapshot.Sources.Any(s => s.Partial || s.Unchecked || s.Result.State is AgendaSourceState.Failed or AgendaSourceState.NotConfigured or AgendaSourceState.Stale);

    // ---- pieces ----

    private string Title(AgendaSnapshot snapshot)
    {
        var lang = snapshot.Language;
        return snapshot.Request.View == AgendaView.Digest
            ? L(lang, "agenda.title.digest")
            : L(lang, "agenda.title.agenda", L(lang, "agenda.sport." + snapshot.Request.Sport.ToString().ToLowerInvariant()),
                L(lang, "agenda.period." + snapshot.Request.Period.ToString().ToLowerInvariant()));
    }

    private string DayHeading(AgendaDay day, string lang)
    {
        var culture = Culture(lang);
        var date = day.Date.ToString(lang == "tr" ? "d MMMM dddd" : "dddd, d MMMM", culture);
        return day.OffsetFromToday switch
        {
            0 => L(lang, "agenda.day.today", date),
            1 => L(lang, "agenda.day.tomorrow", date),
            _ => L(lang, "agenda.day.other", date),
        };
    }

    private string Item(AgendaItem item, AgendaSnapshot snapshot)
    {
        var lang = snapshot.Language;
        var head = Emoji(item.Kind) + " **" + L(lang, "agenda.kind." + KindKey(item.Kind)) + " · " + DiscordText.Untrusted(item.Title, TitleMax) + "**";
        var parts = new List<string>();
        if (item.StartUtc is { } start)
            parts.Add(DiscordText.Timestamp(start, 'f') + " · " + DiscordText.Timestamp(start, 'R'));
        else if (item.Date is { } date)
            parts.Add(L(lang, "agenda.time_unknown", date.ToString(lang == "tr" ? "d MMMM" : "d MMMM", Culture(lang))));
        else if (item.Status == AgendaStatus.LookingNow)
            parts.Add(L(lang, "agenda.lfg.now"));
        if (!string.IsNullOrWhiteSpace(item.Context))
            parts.Add(DiscordText.Untrusted(item.Context, ContextMax));
        if (item.Slots is { } slots)
            parts.Add(slots.Full ? L(lang, "agenda.lfg.full", slots.Joined, slots.Capacity) : L(lang, "agenda.lfg.slots", slots.Joined, slots.Capacity));
        if (StatusText(item.Status, lang) is { } status)
            parts.Add(status);
        parts.Add(LinkText(item, snapshot));
        return head + "\n" + string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    private string? StatusText(AgendaStatus status, string lang) => status switch
    {
        AgendaStatus.InProgress => L(lang, "agenda.state.in_progress"),
        AgendaStatus.StartPassed => L(lang, "agenda.state.start_passed"),
        AgendaStatus.Postponed => L(lang, "agenda.state.postponed"),
        AgendaStatus.Cancelled => L(lang, "agenda.state.cancelled"),
        AgendaStatus.ListingStartPassed => L(lang, "agenda.state.listing_start_passed"),
        _ => null,
    };

    /// <summary>A verified link, else the related command, else nothing (never a guessed link).</summary>
    private string LinkText(AgendaItem item, AgendaSnapshot snapshot)
    {
        var lang = snapshot.Language;
        if (item.Link is { } link)
        {
            if (link.Kind is AgendaLinkKind.Card or AgendaLinkKind.Listing && link.Channel is { } channel && link.Message is { } message)
                return "[" + L(lang, link.Kind == AgendaLinkKind.Listing ? "agenda.link.listing" : "agenda.link.card") + "](" + JumpUrl(snapshot.Request.Guild, channel, message) + ")";
            if (link.Kind == AgendaLinkKind.OfficialPage && SafeUrl(link.Url) is { } url)
                return "[" + L(lang, "agenda.link.page") + "](" + url + ")";
        }

        return item.Kind switch
        {
            AgendaKind.Formula1 => "`/f1 next`",
            AgendaKind.Esports => "`/esports matches`",
            AgendaKind.Volleyball => "`/volleyball schedule`",
            _ => "",
        };
    }

    /// <summary>Discord's message link, from ids only (the guild is always the card's own guild).</summary>
    public static string JumpUrl(GuildId guild, ChannelId channel, MessageId message) =>
        string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{guild.Value}/{channel.Value}/{message.Value}");

    /// <summary>Only an absolute https URL that cannot break out of the markdown link.</summary>
    public static string? SafeUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && url.Length <= 300 &&
        !url.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '<' or '>' or '[' or ']' or '|' or '`')
            ? url
            : null;

    private string StatusLines(AgendaSnapshot snapshot)
    {
        var lang = snapshot.Language;
        var lines = new List<string>();
        foreach (var source in snapshot.Sources)
        {
            var r = source.Result;
            var label = Emoji(source.Kind) + " " + L(lang, "agenda.kind." + KindKey(source.Kind)) + ": ";
            var text = r.State switch
            {
                AgendaSourceState.Disabled => L(lang, "agenda.status.disabled"),
                AgendaSourceState.NotConfigured => L(lang, "agenda.status.not_configured"),
                AgendaSourceState.Failed => L(lang, "agenda.status.failed"),
                AgendaSourceState.Stale when r.FreshAt is { } at => L(lang, "agenda.status.stale", DiscordText.Timestamp(at, 'R')),
                AgendaSourceState.Stale => L(lang, "agenda.status.stale_unknown"),
                _ when source.Kind == AgendaKind.Lfg => L(lang, "agenda.status.lfg"),
                _ when r.FreshAt is { } at => L(lang, "agenda.status.ready", DiscordText.Timestamp(at, 'R')),
                _ => L(lang, "agenda.status.ready_unknown"),
            };
            if (source.Partial && r.CoversUntil is { } until && r.State is AgendaSourceState.Ready or AgendaSourceState.Stale)
                text += " · " + L(lang, "agenda.status.partial", DiscordText.Timestamp(until, 'f'));
            if (r.Demo)
                text += " · " + L(lang, "agenda.status.demo");
            if (r.Undated > 0)
                text += " · " + L(lang, "agenda.status.undated", r.Undated);
            if (r.Unverifiable > 0)
                text += " · " + L(lang, "agenda.status.unverifiable", r.Unverifiable);
            if (source.Unchecked)
                text += " · " + L(lang, "agenda.status.unchecked"); // never a count: some of those may be private
            lines.Add(label + text);
        }

        return string.Join("\n", lines);
    }

    private List<MessageButton> Buttons(AgendaSnapshot snapshot, int page, int pages)
    {
        var lang = snapshot.Language;
        var r = snapshot.Request;
        var buttons = new List<MessageButton>();
        if (pages > 1)
        {
            buttons.Add(new MessageButton(L(lang, "agenda.button.previous"), AgendaCustomId.Format(AgendaCustomId.Navigate, r, Math.Max(0, page - 1)), null, page == 0));
            buttons.Add(new MessageButton(L(lang, "agenda.button.next"), AgendaCustomId.Format(AgendaCustomId.Navigate, r, Math.Min(pages - 1, page + 1)), null, page == pages - 1));
        }

        buttons.Add(new MessageButton(L(lang, "agenda.button.refresh"), AgendaCustomId.Format(AgendaCustomId.Refresh, r, page), null));
        return buttons;
    }

    public static string KindKey(AgendaKind kind) => kind switch
    {
        AgendaKind.Formula1 => "f1",
        AgendaKind.Esports => "cs2",
        AgendaKind.Volleyball => "volleyball",
        _ => "lfg",
    };

    private static string Emoji(AgendaKind kind) => kind switch
    {
        AgendaKind.Formula1 => "🏎️",
        AgendaKind.Esports => "🎮",
        AgendaKind.Volleyball => "🏐",
        _ => "👥",
    };

    private static CultureInfo Culture(string lang) => CultureInfo.GetCultureInfo(lang == "tr" ? "tr-TR" : "en-US");

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private string L(string lang, string key, params object?[] args) => localizer.Get(lang, key, args);
}
