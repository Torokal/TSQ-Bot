using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Core;
using ToroSquad.Core.Agenda;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Agenda;
using ToroSquad.Modules.Agenda.Application;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// TSQ Ajanda without Discord or a database: local days (Europe/Istanbul, DST zones, month/year ends), the button ids, the
/// shared status rules, the day grouping, LFG audience checks, source failures and module gates, and the card's Discord
/// limits, paging and defusing.
/// </summary>
public sealed class AgendaUnitTests
{
    private static readonly GuildId Guild = new(900);
    private static readonly ChannelId Here = new(901);
    private static readonly UserId Owner = new(902);

    private static TimeZoneInfo Zone(string id)
    {
        GuildTime.TryResolve(id, out var zone).Should().BeTrue(id);
        return zone;
    }

    // ---------------------------------------------------------------- days

    [Fact]
    public void Today_and_tomorrow_follow_the_guild_zone_not_utc()
    {
        var istanbul = Zone("Europe/Istanbul");
        var beforeMidnight = new DateTimeOffset(2026, 10, 28, 20, 59, 59, TimeSpan.Zero); // 23:59:59 in Istanbul
        var days = AgendaCalendar.Days(beforeMidnight, istanbul, 0, 2);
        days.Select(d => d.Date).Should().Equal(new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 29));
        days[1].StartUtc.Should().Be(new DateTimeOffset(2026, 10, 28, 21, 0, 0, TimeSpan.Zero), "tomorrow starts at local 00:00, included");
        days[1].EndUtc.Should().Be(new DateTimeOffset(2026, 10, 29, 21, 0, 0, TimeSpan.Zero), "and ends at the next local 00:00, excluded");

        var afterMidnight = beforeMidnight.AddSeconds(1);
        AgendaCalendar.Days(afterMidnight, istanbul, 0, 1)[0].Date.Should().Be(new DateOnly(2026, 10, 29), "the UTC date is still the 28th");
        afterMidnight.UtcDateTime.Day.Should().Be(28);
    }

    [Fact]
    public void Today_plus_tomorrow_is_two_calendar_days_not_48_hours()
    {
        var istanbul = Zone("Europe/Istanbul");
        var now = new DateTimeOffset(2026, 10, 28, 18, 0, 0, TimeSpan.Zero); // 21:00 local
        var days = AgendaCalendar.Days(now, istanbul, 0, 2);
        (days[^1].EndUtc - now).Should().Be(TimeSpan.FromHours(27), "the window ends at the end of tomorrow, not now + 48 h");
    }

    [Fact]
    public void A_dst_day_is_23_or_25_hours_long()
    {
        var berlin = Zone("Europe/Berlin");
        var autumn = AgendaCalendar.Days(new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero), berlin, 0, 1)[0];
        (autumn.EndUtc - autumn.StartUtc).Should().Be(TimeSpan.FromHours(25));
        var spring = AgendaCalendar.Days(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.Zero), berlin, 0, 1)[0];
        (spring.EndUtc - spring.StartUtc).Should().Be(TimeSpan.FromHours(23));
    }

    [Theory]
    [InlineData("America/Santiago")]
    [InlineData("America/Sao_Paulo")]
    [InlineData("Europe/Istanbul")]
    [InlineData("Australia/Sydney")]
    public void Days_are_contiguous_all_year_even_where_midnight_is_skipped(string zoneId)
    {
        var zone = Zone(zoneId);
        var days = AgendaCalendar.Days(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero), zone, 0, 366);
        for (var i = 0; i < days.Count; i++)
        {
            (days[i].EndUtc - days[i].StartUtc).TotalHours.Should().BeInRange(23, 25);
            if (i > 0)
                days[i].StartUtc.Should().Be(days[i - 1].EndUtc);
        }
    }

    [Fact]
    public void Periods_count_local_days_today_included_across_month_and_year_ends()
    {
        var istanbul = Zone("Europe/Istanbul");
        var now = new DateTimeOffset(2026, 12, 27, 10, 0, 0, TimeSpan.Zero);
        AgendaOptions.Days(AgendaView.Agenda, AgendaPeriod.SevenDays).Should().Be((0, 7));
        AgendaOptions.Days(AgendaView.Agenda, AgendaPeriod.FourteenDays).Should().Be((0, 14));
        AgendaOptions.Days(AgendaView.Agenda, AgendaPeriod.Tomorrow).Should().Be((1, 1));
        AgendaOptions.Days(AgendaView.Digest, AgendaPeriod.SevenDays).Should().Be((0, 2), "/gundem is always today + tomorrow");
        var week = AgendaCalendar.Days(now, istanbul, 0, 7);
        week[0].Date.Should().Be(new DateOnly(2026, 12, 27));
        week[^1].Date.Should().Be(new DateOnly(2027, 1, 2));
    }

    [Fact]
    public void Options_map_to_the_documented_defaults_and_agenda_never_reads_lfg()
    {
        AgendaOptions.Sport(null).Should().Be(AgendaSport.All);
        AgendaOptions.Sport("bogus").Should().Be(AgendaSport.All);
        AgendaOptions.Period(null).Should().Be(AgendaPeriod.SevenDays);
        AgendaOptions.Period("14-gun").Should().Be(AgendaPeriod.FourteenDays);
        foreach (var sport in Enum.GetValues<AgendaSport>())
            AgendaOptions.Kinds(AgendaView.Agenda, sport).Should().NotContain(AgendaKind.Lfg).And.NotContain(AgendaKind.Predictions);
        AgendaOptions.Kinds(AgendaView.Digest, AgendaSport.All).Should().Equal(AgendaKind.Formula1, AgendaKind.Esports, AgendaKind.Volleyball, AgendaKind.Lfg, AgendaKind.Predictions);
        AgendaOptions.Kinds(AgendaView.Agenda, AgendaSport.Cs2).Should().Equal(AgendaKind.Esports);
    }

    // ---------------------------------------------------------------- button ids

    [Fact]
    public void Button_ids_round_trip_and_reject_anything_else()
    {
        var request = new AgendaRequest(Guild, Here, new UserId(ulong.MaxValue), AgendaView.Agenda, AgendaSport.Volleyball, AgendaPeriod.FourteenDays);
        var id = AgendaCustomId.Format(AgendaCustomId.Navigate, request, 12);
        id.Length.Should().BeLessThanOrEqualTo(100);
        AgendaCustomId.TryParse(id, out var button).Should().BeTrue();
        button.Should().Be(new AgendaButton('n', AgendaView.Agenda, AgendaSport.Volleyball, AgendaPeriod.FourteenDays, ulong.MaxValue, 12));

        foreach (var bad in new[]
                 {
                     null, "", "tsq:agenda:x:a:0:0:1:0", "tsq:agenda:n:z:0:0:1:0", "tsq:agenda:n:a:7:0:1:0", "tsq:agenda:n:a:0:9:1:0",
                     "tsq:agenda:n:a:0:0:abc:0", "tsq:agenda:n:a:0:0:1:1234", "tsq:agenda:n:a:0:0:1:0:extra", "tsq:agenda:n:a:0:0:0:0",
                     "tsq:agenda:n:a:0:0:-1:0", "tsq:agenda:n:a:0:0:1:-1", " tsq:agenda:n:a:0:0:1:0",
                 })
            AgendaCustomId.TryParse(bad, out _).Should().BeFalse(bad ?? "null");
    }

    // ---------------------------------------------------------------- status rules and days

    private static AgendaItem Item(AgendaKind kind, string id, DateTimeOffset? start, AgendaStatus status = AgendaStatus.Scheduled, DateOnly? date = null,
        ChannelId? audience = null, string? title = null, AgendaLink? link = null) =>
        new(kind, id, title ?? id, null, start, date, status, link, AudienceChannel: audience);

    [Fact]
    public void Old_data_never_says_running_and_a_passed_start_is_not_guessed()
    {
        var now = TestHost.T0;
        AgendaService.Normalize(Item(AgendaKind.Esports, "a", now.AddHours(-1), AgendaStatus.InProgress), AgendaSourceState.Stale, now).Status
            .Should().Be(AgendaStatus.StartPassed);
        AgendaService.Normalize(Item(AgendaKind.Esports, "a", now.AddHours(-1), AgendaStatus.InProgress), AgendaSourceState.Ready, now).Status
            .Should().Be(AgendaStatus.InProgress);
        AgendaService.Normalize(Item(AgendaKind.Esports, "b", now.AddMinutes(-5)), AgendaSourceState.Ready, now).Status
            .Should().Be(AgendaStatus.StartPassed, "a planned time in the past is not 'live'");
        AgendaService.Normalize(Item(AgendaKind.Esports, "c", now.AddMinutes(5)), AgendaSourceState.Ready, now).Status.Should().Be(AgendaStatus.Scheduled);
        AgendaService.Normalize(Item(AgendaKind.Esports, "d", now.AddMinutes(-5), AgendaStatus.Cancelled), AgendaSourceState.Ready, now).Status
            .Should().Be(AgendaStatus.Cancelled);
    }

    [Fact]
    public void Items_land_on_their_local_day_and_today_takes_the_now_listings()
    {
        var istanbul = Zone("Europe/Istanbul");
        var now = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero); // 12:00 local
        var days = AgendaCalendar.Days(now, istanbul, 0, 2);
        var items = new[]
        {
            Item(AgendaKind.Formula1, "late-today", new DateTimeOffset(2026, 10, 28, 20, 59, 0, TimeSpan.Zero)), // 23:59 local
            Item(AgendaKind.Formula1, "tomorrow-midnight", new DateTimeOffset(2026, 10, 28, 21, 0, 0, TimeSpan.Zero)), // 00:00 local tomorrow
            Item(AgendaKind.Esports, "day-after", new DateTimeOffset(2026, 10, 29, 21, 0, 0, TimeSpan.Zero)), // excluded end
            Item(AgendaKind.Esports, "date-only", null, date: new DateOnly(2026, 10, 29)),
            Item(AgendaKind.Volleyball, "running-since-yesterday", new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero), AgendaStatus.InProgress),
            Item(AgendaKind.Volleyball, "old-not-running", new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero), AgendaStatus.StartPassed),
            Item(AgendaKind.Lfg, "now", null, AgendaStatus.LookingNow),
            Item(AgendaKind.Lfg, "open-since-yesterday", new DateTimeOffset(2026, 10, 27, 18, 0, 0, TimeSpan.Zero), AgendaStatus.ListingStartPassed),
        };

        var groups = AgendaService.Group(items, days, now);

        groups[0].Now.Select(i => i.SourceId).Should().Equal("open-since-yesterday", "now");
        groups[0].Items.Select(i => i.SourceId).Should().Equal("running-since-yesterday", "late-today");
        groups[1].Items.Select(i => i.SourceId).Should().Equal("tomorrow-midnight", "date-only");
        groups.SelectMany(g => g.Items.Concat(g.Now)).Select(i => i.SourceId).Should().NotContain(["day-after", "old-not-running"]);

        var tomorrowOnly = AgendaService.Group(items, AgendaCalendar.Days(now, istanbul, 1, 1), now);
        tomorrowOnly.Single().Now.Should().BeEmpty("'now' belongs to today only");
    }

    [Fact]
    public void Equal_times_keep_a_stable_order_and_similar_titles_stay_separate()
    {
        var istanbul = Zone("Europe/Istanbul");
        var now = TestHost.T0;
        var start = now.AddHours(2);
        var items = new[]
        {
            Item(AgendaKind.Esports, "pandascore:2", start, title: "A — B"),
            Item(AgendaKind.Formula1, "2026-05-quali", start, title: "GP — Sıralama"),
            Item(AgendaKind.Esports, "pandascore:1", start, title: "A — B"),
        };
        var group = AgendaService.Group(items, AgendaCalendar.Days(now, istanbul, 0, 1), now)[0];
        group.Items.Select(i => i.SourceId).Should().Equal("2026-05-quali", "pandascore:1", "pandascore:2");
    }

    // ---------------------------------------------------------------- service

    private sealed class FakeSource(AgendaKind kind, ModuleId module, Func<AgendaQuery, AgendaSourceResult> read) : IAgendaSource
    {
        public int Calls { get; private set; }
        public AgendaQuery? LastQuery { get; private set; }

        public AgendaKind Kind => kind;
        public ModuleId Module => module;

        public Task<AgendaSourceResult> ReadAsync(AgendaQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            LastQuery = query;
            return Task.FromResult(read(query));
        }
    }

    private sealed class FakeGate(params string[] off) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(GuildId guild, ModuleId module, CancellationToken cancellationToken) => Task.FromResult(!off.Contains(module.Value));
    }

    private sealed class FakeSettings(string zone = "Europe/Istanbul", string language = "tr") : IGuildSettingsStore
    {
        public Task<GuildSettings> GetAsync(GuildId guild, CancellationToken cancellationToken) => Task.FromResult(new GuildSettings(guild, language, zone, true));
        public Task SaveAsync(GuildSettings settings, UserId changedBy, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static AgendaService Service(IEnumerable<IAgendaSource> sources, IModuleGate? gate = null, FakeGuildGateway? guilds = null, string zone = "Europe/Istanbul",
        DateTimeOffset? now = null) =>
        new(sources, gate ?? new FakeGate(), new FakeSettings(zone), guilds ?? new FakeGuildGateway(), new FakeTimeProvider(now ?? TestHost.T0),
            NullLogger<AgendaService>.Instance);

    private static AgendaRequest Digest() => new(Guild, Here, Owner, AgendaView.Digest, AgendaSport.All, AgendaPeriod.Today);

    [Fact]
    public async Task A_module_that_is_off_is_not_read_and_a_failing_source_does_not_hide_the_others()
    {
        var now = TestHost.T0;
        var f1 = new FakeSource(AgendaKind.Formula1, new ModuleId("formula1"), _ => throw new InvalidOperationException("boom"));
        var cs2 = new FakeSource(AgendaKind.Esports, new ModuleId("esports"), _ => new AgendaSourceResult(AgendaSourceState.Ready, [Item(AgendaKind.Esports, "m1", now.AddHours(1))], now));
        var vb = new FakeSource(AgendaKind.Volleyball, new ModuleId("volleyball"), _ => new AgendaSourceResult(AgendaSourceState.Ready, [Item(AgendaKind.Volleyball, "v", now.AddHours(1))], now));
        var lfg = new FakeSource(AgendaKind.Lfg, new ModuleId("lfg"), _ => new AgendaSourceResult(AgendaSourceState.Ready, []));

        var snapshot = await Service([f1, cs2, vb, lfg], new FakeGate("volleyball")).BuildAsync(Digest(), CancellationToken.None);

        vb.Calls.Should().Be(0, "a module that is off in the guild is not read, whatever its cache holds");
        snapshot.Sources.Select(s => (s.Kind, s.Result.State)).Should().Equal(
            (AgendaKind.Formula1, AgendaSourceState.Failed), (AgendaKind.Esports, AgendaSourceState.Ready),
            (AgendaKind.Volleyball, AgendaSourceState.Disabled), (AgendaKind.Lfg, AgendaSourceState.Ready), (AgendaKind.Predictions, AgendaSourceState.Disabled));
        snapshot.Days.SelectMany(d => d.Items).Select(i => i.SourceId).Should().Equal("m1");
    }

    [Fact]
    public async Task One_now_and_the_guild_zone_go_to_every_source_and_agenda_never_asks_lfg()
    {
        var now = new DateTimeOffset(2026, 10, 28, 22, 30, 0, TimeSpan.Zero); // 01:30 on the 29th in Istanbul
        var sources = Enum.GetValues<AgendaKind>().Select(k => new FakeSource(k, new ModuleId(k.ToString().ToLowerInvariant()), _ => new AgendaSourceResult(AgendaSourceState.Ready, []))).ToList();

        var snapshot = await Service(sources, now: now).BuildAsync(new AgendaRequest(Guild, Here, Owner, AgendaView.Agenda, AgendaSport.All, AgendaPeriod.SevenDays), CancellationToken.None);

        sources.Single(s => s.Kind == AgendaKind.Lfg).Calls.Should().Be(0);
        var queries = sources.Where(s => s.Calls > 0).Select(s => s.LastQuery!).ToList();
        queries.Should().HaveCount(3).And.OnlyContain(q => q.Now == now && q.FromDate == new DateOnly(2026, 10, 29) && q.ToDate == new DateOnly(2026, 11, 5));
        snapshot.TimeZoneId.Should().Be("Europe/Istanbul");
    }

    [Fact]
    public async Task An_unknown_guild_zone_falls_back_to_the_repository_default()
    {
        var source = new FakeSource(AgendaKind.Formula1, new ModuleId("formula1"), _ => new AgendaSourceResult(AgendaSourceState.Ready, []));
        var snapshot = await Service([source], zone: "Mars/Olympus").BuildAsync(new AgendaRequest(Guild, Here, Owner, AgendaView.Agenda, AgendaSport.Formula1, AgendaPeriod.Today), CancellationToken.None);
        snapshot.TimeZoneId.Should().Be(GuildSettings.DefaultTimeZoneId);
    }

    [Fact]
    public async Task Lfg_from_another_channel_is_shown_only_when_that_channel_is_open_to_everyone()
    {
        var now = TestHost.T0;
        var guilds = new FakeGuildGateway();
        ChannelId Ch(ulong id) => new(id);
        guilds.SetChannelAudience(Guild, Ch(10), ChannelAudience.Open);
        guilds.SetChannelAudience(Guild, Ch(11), ChannelAudience.Open with { Narrowed = true }); // a role or member loses View Channel
        guilds.SetChannelAudience(Guild, Ch(12), ChannelAudience.Open with { IsAgeRestricted = true });
        guilds.SetChannelAudience(Guild, Ch(13), new ChannelAudience(true, false, false, true, false)); // thread
        guilds.SetChannelAudience(Guild, Ch(14), ChannelAudience.Open with { EveryoneCanView = false }); // private channel
        // 15: unknown (not in the cache)
        var listings = new[]
        {
            Item(AgendaKind.Lfg, "here", null, AgendaStatus.LookingNow, audience: Here, title: "Bu kanal"),
            Item(AgendaKind.Lfg, "open", null, AgendaStatus.LookingNow, audience: Ch(10), title: "Açık kanal"),
            Item(AgendaKind.Lfg, "narrowed", null, AgendaStatus.LookingNow, audience: Ch(11), title: "Gizli-1"),
            Item(AgendaKind.Lfg, "nsfw", null, AgendaStatus.LookingNow, audience: Ch(12), title: "Gizli-2"),
            Item(AgendaKind.Lfg, "thread", null, AgendaStatus.LookingNow, audience: Ch(13), title: "Gizli-3"),
            Item(AgendaKind.Lfg, "private", null, AgendaStatus.LookingNow, audience: Ch(14), title: "Gizli-4"),
            Item(AgendaKind.Lfg, "unknown", null, AgendaStatus.LookingNow, audience: Ch(15), title: "Gizli-5"),
        };
        var lfg = new FakeSource(AgendaKind.Lfg, new ModuleId("lfg"), _ => new AgendaSourceResult(AgendaSourceState.Ready, listings));

        var snapshot = await Service([lfg], guilds: guilds).BuildAsync(Digest(), CancellationToken.None);

        snapshot.Days[0].Now.Select(i => i.SourceId).Should().Equal("here", "open");
        var card = Renderer().Render(snapshot, 0);
        var text = card.Embed.Description + string.Join("\n", card.Embed.Fields.Select(f => f.Value));
        text.Should().NotContain("Gizli").And.Contain("Açık kanal");
    }

    [Fact]
    public async Task Many_channels_private_ones_first_never_hide_a_later_public_listing_and_each_channel_is_checked_once()
    {
        var now = TestHost.T0;
        var guilds = new FakeGuildGateway();
        ChannelId Ch(int i) => new((ulong)(100 + i));
        var listings = new List<AgendaItem>();
        // 14 other channels; the 10 checked first (earliest listings) are private or role-restricted, then public ones.
        for (var i = 1; i <= 14; i++)
        {
            guilds.SetChannelAudience(Guild, Ch(i), i <= 10 ? ChannelAudience.Open with { Narrowed = i % 2 == 0, EveryoneCanView = i % 2 == 0 } : ChannelAudience.Open);
            listings.Add(Item(AgendaKind.Lfg, "l" + i, now.AddMinutes(i), audience: Ch(i), title: (i <= 10 ? "Gizli " : "Açık ") + i,
                link: AgendaLink.ToCard(Ch(i), new MessageId((ulong)(5000 + i)), AgendaLinkKind.Listing)));
        }

        listings.Add(Item(AgendaKind.Lfg, "l12-b", now.AddMinutes(30), audience: Ch(12), title: "Açık 12 ikinci")); // two listings in one channel
        listings.Add(Item(AgendaKind.Lfg, "l3-b", now.AddMinutes(31), audience: Ch(3), title: "Gizli 3 ikinci"));
        var lfg = new FakeSource(AgendaKind.Lfg, new ModuleId("lfg"), _ => new AgendaSourceResult(AgendaSourceState.Ready, listings));
        var service = Service([lfg], guilds: guilds);

        var snapshot = await service.BuildAsync(Digest(), CancellationToken.None);

        snapshot.Days[0].Items.Select(i => i.Title).Should().Equal("Açık 11", "Açık 12", "Açık 13", "Açık 14", "Açık 12 ikinci");
        guilds.AudienceLookups.Should().Be(14, "one check per distinct channel in a build — the listing links reuse the verdict");
        snapshot.Sources.Single(s => s.Kind == AgendaKind.Lfg).Unchecked.Should().BeFalse("every channel was checked: private ones are simply not shown");
        var text = Text(Renderer().Render(snapshot, 0));
        text.Should().NotContain("Gizli").And.NotContain("/5001)").And.NotContain("/5010)").And.Contain("/5014)");
        text.Should().NotContain("doğrulanamadı");

        // A new build checks again: a public channel turned private disappears, a private one opened appears.
        guilds.SetChannelAudience(Guild, Ch(11), ChannelAudience.Open with { EveryoneCanView = false });
        guilds.SetChannelAudience(Guild, Ch(2), ChannelAudience.Open);
        var again = await service.BuildAsync(Digest(), CancellationToken.None);
        guilds.AudienceLookups.Should().Be(28);
        again.Days[0].Items.Select(i => i.Title).Should().Equal("Gizli 2", "Açık 12", "Açık 13", "Açık 14", "Açık 12 ikinci");
    }

    [Fact]
    public async Task The_same_channel_shows_its_own_listing_even_when_private_and_checks_nothing()
    {
        var now = TestHost.T0;
        var guilds = new FakeGuildGateway();
        var hidden = new ChannelId(77);
        guilds.SetChannelAudience(Guild, hidden, ChannelAudience.Open with { EveryoneCanView = false });
        var lfg = new FakeSource(AgendaKind.Lfg, new ModuleId("lfg"), _ => new AgendaSourceResult(AgendaSourceState.Ready,
            [Item(AgendaKind.Lfg, "own", now.AddHours(1), audience: hidden, title: "Ekip odası", link: AgendaLink.ToCard(hidden, new MessageId(1), AgendaLinkKind.Listing))]));

        var inside = await Service([lfg], guilds: guilds).BuildAsync(Digest() with { Channel = hidden }, CancellationToken.None);
        var outside = await Service([lfg], guilds: guilds).BuildAsync(Digest(), CancellationToken.None);

        inside.Days[0].Items.Single().Link.Should().NotBeNull("the card is read by the same channel's readers");
        outside.Days[0].Items.Should().BeEmpty();
        guilds.AudienceLookups.Should().Be(1, "only the build in another channel had to check");
    }

    [Fact]
    public async Task Audience_checks_are_bounded_and_an_unchecked_channel_marks_the_source_incomplete_without_a_count()
    {
        var guilds = new FakeGuildGateway();
        var count = AgendaService.MaxAudienceLookups + 10;
        var listings = Enumerable.Range(1, count).Select(i =>
        {
            guilds.SetChannelAudience(Guild, new ChannelId((ulong)(1000 + i)), ChannelAudience.Open);
            return Item(AgendaKind.Lfg, "l" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), null, AgendaStatus.LookingNow, audience: new ChannelId((ulong)(1000 + i)));
        }).ToList();
        listings.Add(Item(AgendaKind.Lfg, "a-unknown", null, AgendaStatus.LookingNow, audience: new ChannelId(9))); // checked first: not in the cache
        var lfg = new FakeSource(AgendaKind.Lfg, new ModuleId("lfg"), _ => new AgendaSourceResult(AgendaSourceState.Ready, listings));

        var snapshot = await Service([lfg], guilds: guilds).BuildAsync(Digest(), CancellationToken.None);

        guilds.AudienceLookups.Should().Be(AgendaService.MaxAudienceLookups);
        snapshot.Days[0].Now.Should().HaveCount(AgendaService.MaxAudienceLookups - 1, "the unknown channel and everything beyond the bound are left out, never shown unchecked");
        snapshot.Sources.Single(s => s.Kind == AgendaKind.Lfg).Unchecked.Should().BeTrue();
        var status = Renderer().Render(snapshot, 0).Embed.Fields.Single().Value;
        status.Should().Contain("LFG: bu kanaldaki ve herkese açık kanallardaki ilanlar · ⚠️ bazı kayıtlar bu kartta doğrulanamadı; liste eksik olabilir");
        System.Text.RegularExpressions.Regex.Match(status, "LFG: [^\n]*").Value.Should().NotMatchRegex(@"\d", "no count of what was left out");

        // A truncated source read says the same, and an empty day is then not called free.
        var truncated = new FakeSource(AgendaKind.Lfg, new ModuleId("lfg"), _ => new AgendaSourceResult(AgendaSourceState.Ready, [], Truncated: true));
        var card = Renderer().Render(await Service([truncated]).BuildAsync(Digest(), CancellationToken.None), 0);
        card.Embed.Fields.Single().Value.Should().Contain("doğrulanamadı");
        card.Embed.Description.Should().Contain("bazı veriler eksik").And.NotContain("Bu gün için gösterilecek etkinlik yok.");
    }

    [Fact]
    public async Task A_card_link_is_kept_only_for_a_readable_channel_and_otherwise_gives_way_to_the_official_page_or_the_command()
    {
        var now = TestHost.T0;
        var guilds = new FakeGuildGateway();
        ChannelId open = new(20), hidden = new(21), gone = new(22);
        guilds.SetChannelAudience(Guild, open, ChannelAudience.Open);
        guilds.SetChannelAudience(Guild, hidden, ChannelAudience.Open with { Narrowed = true });
        var page = AgendaLink.ToPage("https://www.hltv.org/matches/1/a-vs-b");
        AgendaItem Match(string id, ChannelId channel, AgendaLink? fallback) =>
            new(AgendaKind.Esports, id, id, null, now.AddHours(1), null, AgendaStatus.Scheduled, AgendaLink.ToCard(channel, new MessageId(1)), Fallback: fallback);
        var cs2 = new FakeSource(AgendaKind.Esports, new ModuleId("esports"), _ => new AgendaSourceResult(AgendaSourceState.Ready,
            [Match("open", open, page), Match("here", Here, page), Match("hidden", hidden, page), Match("gone", gone, page), Match("hidden-no-page", hidden, null)], now));

        var items = (await Service([cs2], guilds: guilds).BuildAsync(Digest(), CancellationToken.None)).Days[0].Items.ToDictionary(i => i.SourceId);

        items["open"].Link!.Kind.Should().Be(AgendaLinkKind.Card);
        items["here"].Link!.Kind.Should().Be(AgendaLinkKind.Card, "a card in the agenda card's own channel");
        items["hidden"].Link.Should().Be(page, "a role-restricted card channel: the official page instead");
        items["gone"].Link.Should().Be(page, "a channel that no longer exists");
        items["hidden-no-page"].Link.Should().BeNull();
        guilds.AudienceLookups.Should().Be(3, "open, hidden and gone, each once");
        var text = Text(Renderer().Render(Snapshot(items.Values.ToList()), 0));
        text.Should().Contain("/20/1)").And.Contain("/901/1)").And.NotContain("/21/1)").And.NotContain("/22/1)").And.Contain("`/esports matches`");
    }

    // ---------------------------------------------------------------- card

    private static AgendaRenderer Renderer() =>
        new(new LocalizationCatalog([new LocalizationSource(typeof(AgendaModule).Assembly, "ToroSquad.Modules.Agenda.Localization")]));

    private static AgendaSnapshot Snapshot(IReadOnlyList<AgendaItem> items, AgendaView view = AgendaView.Digest, params AgendaSourceView[] sources)
    {
        var zone = Zone("Europe/Istanbul");
        var now = TestHost.T0;
        var days = AgendaCalendar.Days(now, zone, 0, view == AgendaView.Digest ? 2 : 7);
        var request = new AgendaRequest(Guild, Here, Owner, view, AgendaSport.All, AgendaPeriod.SevenDays);
        return new AgendaSnapshot(request, "tr", "Europe/Istanbul", now, AgendaService.Group(items, days, now),
            sources.Length > 0 ? sources : [new AgendaSourceView(AgendaKind.Esports, new AgendaSourceResult(AgendaSourceState.Ready, [], now), false)]);
    }

    private static string Text(AgendaCard card) => card.Embed.Description + "\n" + string.Join("\n", card.Embed.Fields.Select(f => f.Value));

    private static void ShouldFitDiscord(AgendaCard card)
    {
        var e = card.Embed;
        (e.Title ?? "").Length.Should().BeLessThanOrEqualTo(DiscordLimits.EmbedTitleMax);
        (e.Description ?? "").Length.Should().BeLessThanOrEqualTo(DiscordLimits.EmbedDescriptionMax);
        e.Fields.Count.Should().BeLessThanOrEqualTo(DiscordLimits.EmbedFieldsMax);
        e.Fields.Should().OnlyContain(f => f.Value.Length <= DiscordLimits.EmbedFieldValueMax && f.Name.Length <= DiscordLimits.EmbedFieldNameMax);
        (e.Footer ?? "").Length.Should().BeLessThanOrEqualTo(DiscordLimits.EmbedFooterMax);
        ((e.Title ?? "").Length + (e.Description ?? "").Length + (e.Footer ?? "").Length + e.Fields.Sum(f => f.Name.Length + f.Value.Length)).Should().BeLessThanOrEqualTo(6000);
        card.Buttons.Should().OnlyContain(b => b.CustomId != null && b.CustomId.Length <= 100 && b.Label.Length <= 80);
        card.Buttons.Select(b => b.CustomId).Should().OnlyHaveUniqueItems();
        var act = () => DiscordConversions.ToEmbed(e);
        act.Should().NotThrow();
    }

    [Fact]
    public void A_long_programme_is_paged_in_one_card_and_no_item_is_lost()
    {
        var now = TestHost.T0;
        var long_ = new string('Ş', 150);
        var items = Enumerable.Range(0, 37).Select(i =>
            new AgendaItem(AgendaKind.Esports, "m" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), "Takım " + i + " — " + long_, "Turnuva " + long_,
                now.AddMinutes(10 + i * 50), null, AgendaStatus.Scheduled, AgendaLink.ToCard(new ChannelId(5), new MessageId((ulong)(1000 + i)))))
            .ToList();
        var snapshot = Snapshot(items);
        var renderer = Renderer();

        var first = renderer.Render(snapshot, 0);
        first.Pages.Should().BeGreaterThan(1);
        var all = new List<string>();
        for (var p = 0; p < first.Pages; p++)
        {
            var card = renderer.Render(snapshot, p);
            ShouldFitDiscord(card);
            card.Embed.Footer.Should().StartWith($"Sayfa {p + 1}/{first.Pages}");
            all.Add(card.Embed.Description!);
        }

        var joined = string.Join("\n", all);
        foreach (var i in Enumerable.Range(0, 37))
            System.Text.RegularExpressions.Regex.Count(joined, $"/1{i:000}\\)").Should().Be(1, $"item {i} appears exactly once (its card link)");
        joined.Should().Contain("(devam)", "a day that continues on the next page repeats its heading");
        renderer.Render(snapshot, 999).Page.Should().Be(first.Pages - 1, "a page beyond the end is clamped");
        first.Buttons.Select(b => (b.Label, b.Disabled)).Should().Equal(("◀ Önceki", true), ("Sonraki ▶", false), ("🔄 Yenile", false));
    }

    [Fact]
    public void Empty_days_say_so_and_a_data_gap_is_not_called_a_free_day()
    {
        var renderer = Renderer();
        var free = renderer.Render(Snapshot([]), 0).Embed.Description!;
        free.Should().Contain("**Bugün**").And.Contain("**Yarın**").And.Contain("Bu gün için gösterilecek etkinlik yok.");

        var broken = renderer.Render(Snapshot([], AgendaView.Digest, new AgendaSourceView(AgendaKind.Esports, AgendaSourceResult.Failed(), false)), 0);
        broken.Embed.Description.Should().Contain("bazı veriler eksik");
        broken.Embed.Fields.Single().Value.Should().Contain("CS2: ⚠️ veriye şu an ulaşılamıyor");

        var agenda = renderer.Render(Snapshot([], AgendaView.Agenda), 0).Embed.Description;
        agenda.Should().Be("Bu dönemde gösterilecek etkinlik yok.");
    }

    [Fact]
    public void Status_lines_tell_off_old_partial_and_unknown_data_apart()
    {
        var now = TestHost.T0;
        var card = Renderer().Render(Snapshot([], AgendaView.Digest,
            new AgendaSourceView(AgendaKind.Formula1, AgendaSourceResult.Disabled(), false),
            new AgendaSourceView(AgendaKind.Esports, new AgendaSourceResult(AgendaSourceState.Ready, [], now.AddMinutes(-3), now.AddHours(40), Unverifiable: 2), true),
            new AgendaSourceView(AgendaKind.Volleyball, new AgendaSourceResult(AgendaSourceState.Stale, [], now.AddDays(-2), Demo: true, Undated: 1), false),
            new AgendaSourceView(AgendaKind.Lfg, new AgendaSourceResult(AgendaSourceState.Ready, []), false)), 0);
        var status = card.Embed.Fields.Single().Value;
        status.Should().Contain("F1: modül bu sunucuda kapalı")
            .And.Contain("CS2: son güncelleme <t:").And.Contain("sonrasını kapsamıyor").And.Contain("2 maç gösterilmedi")
            .And.Contain("Voleybol: ⚠️ veri eski").And.Contain("TEST/DEMO").And.Contain("tarihi açıklanmamış 1 kayıt")
            .And.Contain("LFG: bu kanaldaki ve herkese açık kanallardaki ilanlar");
        card.Embed.Footer.Should().Be("Sayfa 1/1 · Günler: Europe/Istanbul · Saatler sizin yerel saatinizde");
    }

    [Fact]
    public void Text_is_defused_and_links_come_only_from_verified_ids_or_safe_urls()
    {
        var now = TestHost.T0;
        var items = new[]
        {
            new AgendaItem(AgendaKind.Lfg, "x", "@everyone <@&1> **kalın** [tıkla](https://evil.example)", null, now.AddHours(1), null, AgendaStatus.Scheduled,
                AgendaLink.ToCard(new ChannelId(77), new MessageId(88), AgendaLinkKind.Listing), new AgendaSlots(6, 6, true), Here),
            new AgendaItem(AgendaKind.Esports, "y", "A — B", null, now.AddHours(2), null, AgendaStatus.Scheduled, AgendaLink.ToPage("https://www.hltv.org/matches/1/a-vs-b")),
            new AgendaItem(AgendaKind.Esports, "z", "C — D", null, now.AddHours(3), null, AgendaStatus.Scheduled, AgendaLink.ToPage("javascript:alert(1)")),
            new AgendaItem(AgendaKind.Volleyball, "v", "Türkiye — Polonya", "VNL", null, new DateOnly(2026, 9, 25), AgendaStatus.Scheduled),
            new AgendaItem(AgendaKind.Lfg, "w", "Valheim", null, now.AddHours(4), null, AgendaStatus.Scheduled, null, new AgendaSlots(3, 6, false), Here),
        };
        var text = Renderer().Render(Snapshot(items), 0).Embed.Description!;

        text.Should().NotContain("@everyone").And.NotContain("<@&").And.NotContain("](https://evil");
        text.Should().Contain("[İlanı aç](https://discord.com/channels/900/77/88)", "the guild is the card's own guild");
        text.Should().Contain("6/6 · Dolu / bekleme listesi").And.Contain("3/6 kişi");
        text.Should().Contain("[Maç sayfası — sonuç içerebilir](https://www.hltv.org/matches/1/a-vs-b)");
        text.Should().NotContain("javascript").And.Contain("`/esports matches`", "no safe link: the related command instead");
        text.Should().Contain("25 Eylül · Saat açıklanmadı").And.NotContain("00:00");
    }

    // ---------------------------------------------------------------- /gundem first page

    private static readonly DateTimeOffset TomorrowStart = new(2026, 9, 24, 21, 0, 0, TimeSpan.Zero); // 25 Sep 00:00 in Istanbul

    /// <summary>Every page of a card: page texts, and per page the item ids in the order shown.</summary>
    private static (List<string> Pages, List<List<string>> Ids) Pages(AgendaSnapshot snapshot)
    {
        var renderer = Renderer();
        var first = renderer.Render(snapshot, 0);
        var texts = new List<string>();
        var ids = new List<List<string>>();
        var all = snapshot.Days.SelectMany(d => d.Now.Concat(d.Items)).ToList();
        for (var p = 0; p < first.Pages; p++)
        {
            var card = renderer.Render(snapshot, p);
            ShouldFitDiscord(card);
            texts.Add(card.Embed.Description!);
            ids.Add(System.Text.RegularExpressions.Regex.Matches(card.Embed.Description!, @"\*\*[^*]+ · (?<id>[^*]+)\*\*").Select(m => m.Groups["id"].Value).ToList());
        }

        // Every item exactly once, each day in time order with a stable order for equal times.
        ids.SelectMany(x => x).Should().BeEquivalentTo(all.Select(i => i.Title), o => o.WithoutStrictOrdering());
        ids.SelectMany(x => x).Should().OnlyHaveUniqueItems();
        foreach (var day in snapshot.Days)
        {
            var expected = day.Now.Concat(day.Items).Select(i => i.Title).ToList();
            ids.SelectMany(x => x).Where(expected.Contains).Should().Equal(expected);
        }

        return (texts, ids);
    }

    private static AgendaItem Planned(string id, DateTimeOffset start, string? context = null, AgendaKind kind = AgendaKind.Formula1) =>
        new(kind, id, id, context, start, null, AgendaStatus.Scheduled, AgendaLink.ToCard(new ChannelId(5), new MessageId((ulong)(100_000 + Math.Abs(id.GetHashCode(StringComparison.Ordinal)) % 100_000))));

    private static List<AgendaItem> Today(int n, string prefix = "bugün ") =>
        Enumerable.Range(0, n).Select(i => Planned(prefix + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), TestHost.T0.AddMinutes(5 + i * 30))).ToList();

    private static List<AgendaItem> Tomorrow(int n, string prefix = "yarın ") =>
        Enumerable.Range(0, n).Select(i => Planned(prefix + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), TomorrowStart.AddHours(9 + i))).ToList();

    [Fact]
    public void A_busy_today_still_leaves_tomorrow_on_the_first_page()
    {
        var (pages, ids) = Pages(Snapshot([.. Today(15), .. Tomorrow(4)]));

        pages.Should().HaveCount(2);
        ids[0].Should().Equal("bugün 00", "bugün 01", "bugün 02", "bugün 03", "bugün 04", "bugün 05", "bugün 06", "yarın 00", "yarın 01", "yarın 02");
        pages[0].Should().Contain("📆 **Bugün** · 24 Eylül Perşembe").And.Contain("📆 **Yarın** · 25 Eylül Cuma");
        pages[0].Should().Contain("➕ Sonraki sayfalarda 9 etkinlik daha: Bugün 8 · Yarın 1");
        pages[1].Should().StartWith("📆 **Bugün** · 24 Eylül Perşembe (devam)").And.Contain("📆 **Yarın** · 25 Eylül Cuma (devam)");
        pages.Should().NotContain(t => t.Contains("etkinlik yok"));
    }

    [Fact]
    public void An_empty_today_says_so_once_and_tomorrow_follows_on_the_same_page()
    {
        var (pages, ids) = Pages(Snapshot(Tomorrow(12)));

        pages.Should().HaveCount(2);
        pages[0].Should().StartWith("📆 **Bugün** · 24 Eylül Perşembe\nBu gün için gösterilecek etkinlik yok.");
        ids[0].Should().HaveCount(10).And.StartWith("yarın 00");
        pages[1].Should().NotContain("Bugün").And.Contain("(devam)");
    }

    [Fact]
    public void A_busy_today_and_an_empty_tomorrow_still_name_tomorrow_on_the_first_page()
    {
        var (pages, ids) = Pages(Snapshot(Today(14)));

        pages.Should().HaveCount(2);
        ids[0].Should().HaveCount(10);
        pages[0].Should().Contain("📆 **Yarın** · 25 Eylül Cuma\nBu gün için gösterilecek etkinlik yok.");
        pages[0].Should().Contain("➕ Sonraki sayfalarda 4 etkinlik daha: Bugün 4");
        pages[1].Should().StartWith("📆 **Bugün** · 24 Eylül Perşembe (devam)").And.NotContain("etkinlik yok");
    }

    [Fact]
    public void Long_titles_on_both_days_keep_both_days_on_page_one_without_cutting_a_link()
    {
        var long_ = new string('Ş', 200);
        var items = Today(9).Select(i => i with { Title = i.Title + " " + long_, Context = "Pist " + long_ })
            .Concat(Tomorrow(9).Select(i => i with { Title = i.Title + " " + long_, Context = "Pist " + long_ })).ToList();

        var snapshot = Snapshot(items);
        var renderer = Renderer();
        var first = renderer.Render(snapshot, 0);
        var all = Enumerable.Range(0, first.Pages).Select(p => renderer.Render(snapshot, p)).ToList();

        all.ForEach(ShouldFitDiscord);
        first.Embed.Description.Should().Contain("**Bugün**").And.Contain("**Yarın**");
        var links = string.Join("\n", all.Select(c => c.Embed.Description));
        System.Text.RegularExpressions.Regex.Count(links, @"\[Kartı aç\]\(https://discord\.com/channels/900/5/\d+\)").Should().Be(18, "every link complete, once");
        System.Text.RegularExpressions.Regex.Count(links, @"<t:\d+:f> · <t:\d+:R>").Should().Be(18, "every timestamp pair complete, once");
    }

    [Fact]
    public void Players_looking_now_today_and_planned_events_tomorrow_share_the_first_page()
    {
        var now = TestHost.T0;
        var looking = Enumerable.Range(0, 12).Select(i =>
            new AgendaItem(AgendaKind.Lfg, "now-" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture), "ekip " + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
                null, null, null, AgendaStatus.LookingNow, null, new AgendaSlots(1, 4, false), Here)).ToList();

        var (pages, ids) = Pages(Snapshot([.. looking, .. Tomorrow(3)]));

        ids[0].Should().HaveCount(10).And.EndWith(["yarın 00", "yarın 01", "yarın 02"]);
        pages[0].Should().Contain("👥 Şimdi ekip arayanlar").And.Contain("**Yarın**");
        pages[1].Should().StartWith("📆 **Bugün** · 24 Eylül Perşembe (devam)\n👥 Şimdi ekip arayanlar");
        now.Should().Be(TestHost.T0);
    }

    [Fact]
    public void A_failing_source_leaves_the_others_on_both_days_and_no_day_is_called_free()
    {
        var now = TestHost.T0;
        var snapshot = Snapshot([.. Today(12), .. Tomorrow(2)], AgendaView.Digest,
            new AgendaSourceView(AgendaKind.Formula1, new AgendaSourceResult(AgendaSourceState.Ready, [], now), false),
            new AgendaSourceView(AgendaKind.Esports, AgendaSourceResult.Failed(), false));

        var (pages, ids) = Pages(snapshot);

        ids[0].Should().Contain("yarın 00").And.Contain("bugün 00");
        Renderer().Render(snapshot, 0).Embed.Fields.Single().Value.Should().Contain("CS2: ⚠️ veriye şu an ulaşılamıyor");
        pages.Should().NotContain(t => t.Contains("etkinlik yok"));
    }

    [Fact]
    public void Ajanda_keeps_its_plain_day_order()
    {
        var (pages, ids) = Pages(Snapshot([.. Today(15), .. Tomorrow(4)], AgendaView.Agenda));

        ids[0].Should().HaveCount(10).And.OnlyContain(id => id.StartsWith("bugün ", StringComparison.Ordinal), "7/14 days are not squeezed onto page 1");
        pages[1].Should().Contain("**Yarın**");
    }

    // ---------------------------------------------------------------- predictions

    [Fact]
    public void A_prediction_closes_at_its_closing_time_and_one_without_a_closing_time_is_open_now()
    {
        var now = TestHost.T0;
        AgendaService.Normalize(Item(AgendaKind.Predictions, "p", now.AddMinutes(-1), AgendaStatus.EntriesOpen), AgendaSourceState.Ready, now)
            .Status.Should().Be(AgendaStatus.EntriesClosed, "no entry is taken after the closing time, whether the sweep ran or not");
        AgendaService.Normalize(Item(AgendaKind.Predictions, "p", now.AddMinutes(1), AgendaStatus.EntriesOpen), AgendaSourceState.Stale, now)
            .Status.Should().Be(AgendaStatus.EntriesOpen);

        var days = AgendaCalendar.Days(now, Zone("Europe/Istanbul"), 0, 2);
        var groups = AgendaService.Group(
        [
            Item(AgendaKind.Predictions, "no-deadline", null, AgendaStatus.EntriesOpen),
            Item(AgendaKind.Lfg, "lfg-now", null, AgendaStatus.LookingNow),
            Item(AgendaKind.Predictions, "closing-tomorrow", TomorrowStart.AddHours(5), AgendaStatus.EntriesOpen),
            Item(AgendaKind.Predictions, "locked-no-time", null, AgendaStatus.EntriesClosed),
        ], days, now);

        groups[0].Now.Select(i => i.SourceId).Should().Equal("lfg-now", "no-deadline");
        groups[1].Items.Select(i => i.SourceId).Should().Equal("closing-tomorrow");
        groups.SelectMany(g => g.Now.Concat(g.Items)).Should().NotContain(i => i.SourceId == "locked-no-time", "a closed prediction without a time has no place");
    }

    [Fact]
    public async Task A_prediction_and_its_card_link_are_kept_whatever_the_channels_overwrites_say()
    {
        var now = TestHost.T0;
        var guilds = new FakeGuildGateway();
        var channel = new ChannelId(70);
        guilds.SetChannelReach(Guild, Here, channel, ChannelReach.NotContained);
        var source = new FakeSource(AgendaKind.Predictions, new ModuleId("predictions"), _ => new AgendaSourceResult(AgendaSourceState.Ready,
            [Item(AgendaKind.Predictions, "p", now.AddHours(1), AgendaStatus.EntriesOpen, link: AgendaLink.ToCard(channel, new MessageId(71), AgendaLinkKind.Prediction))]));

        var snapshot = await Service([source], guilds: guilds).BuildAsync(Digest(), CancellationToken.None);

        snapshot.Days[0].Items.Single().Link!.Message.Should().Be(new MessageId(71));
        guilds.AudienceLookups.Should().Be(0, "no channel check for a prediction");
        snapshot.Sources.Single(s => s.Kind == AgendaKind.Predictions).Unchecked.Should().BeFalse();
    }

    [Fact]
    public void Predictions_render_as_open_or_closed_with_their_own_heading_and_never_an_outcome()
    {
        var now = TestHost.T0;
        var card = Renderer().Render(Snapshot(
        [
            Item(AgendaKind.Predictions, "a", null, AgendaStatus.EntriesOpen, title: "Derbi kim kazanır?", link: AgendaLink.ToCard(new ChannelId(5), new MessageId(61), AgendaLinkKind.Prediction)),
            Item(AgendaKind.Predictions, "b", now.AddHours(2), AgendaStatus.EntriesOpen, title: "İlk golü kim atar?"),
            Item(AgendaKind.Predictions, "c", now.AddHours(-1), AgendaStatus.EntriesClosed, title: "Maçta kaç gol olur?"),
            Item(AgendaKind.Lfg, "l", null, AgendaStatus.LookingNow, title: "Valheim"),
        ], AgendaView.Digest, new AgendaSourceView(AgendaKind.Predictions, new AgendaSourceResult(AgendaSourceState.Ready, []), false)), 0);
        var text = Text(card);

        text.Should().Contain("👥 Şimdi ekip arayanlar\n👥 **LFG · Valheim**");
        text.Should().Contain("🔮 Şu an açık öngörüler\n🔮 **Öngörü · Derbi kim kazanır?**\n🟢 Tahminler açık · [Kartı aç](https://discord.com/channels/900/5/61)");
        text.Should().MatchRegex(@"🔮 \*\*Öngörü · İlk golü kim atar\?\*\*\n<t:\d+:f> · <t:\d+:R> · 🟢 Son tahmin saati");
        text.Should().MatchRegex(@"🔮 \*\*Öngörü · Maçta kaç gol olur\?\*\*\n<t:\d+:f> · <t:\d+:R> · 🔒 Tahminler kapandı · sonuç bekleniyor");
        text.Should().Contain("🔮 Öngörü: bu sunucunun açık öngörüleri; sonuç gösterilmez");
        text.Should().NotContainAny("Coin", "kazandı", "/f1", "/esports", "/volleyball");
    }

    [Fact]
    public void Single_page_cards_only_offer_refresh()
    {
        var card = Renderer().Render(Snapshot([Item(AgendaKind.Formula1, "s", TestHost.T0.AddHours(1))]), 0);
        card.Buttons.Select(b => b.Label).Should().Equal("🔄 Yenile");
        AgendaCustomId.TryParse(card.Buttons[0].CustomId, out var button).Should().BeTrue();
        (button.Action, button.Owner, button.View).Should().Be((AgendaCustomId.Refresh, Owner.Value, AgendaView.Digest));
    }
}
