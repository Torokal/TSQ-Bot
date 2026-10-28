using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Agenda.Application;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Modules.Lfg.Domain;
using ToroSquad.Modules.Lfg.Persistence;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// TSQ Ajanda end to end with the production wiring and the real source adapters (F1, CS2, volleyball, LFG) on SQLite: what
/// each source contributes and leaves out, links only to really delivered cards of this guild, LFG privacy, module gates,
/// the public card and its buttons — and that nothing calls a provider or writes anything. TestHost.T0 = Thursday
/// 2026-09-24 12:00 UTC (15:00 in Istanbul); today ends 21:00 UTC, tomorrow 2026-09-25 21:00 UTC.
/// </summary>
public sealed partial class AgendaTests : IAsyncLifetime
{
    private static readonly GuildId Guild = new(1_200_000_000_000_000_001);
    private static readonly GuildId OtherGuild = new(1_200_000_000_000_000_002);
    private static readonly ChannelId Here = new(1_200_000_000_000_000_010);
    private static readonly ChannelId OpenChannel = new(1_200_000_000_000_000_011);
    private static readonly ChannelId HiddenChannel = new(1_200_000_000_000_000_012);
    private static readonly ChannelId CardChannel = new(555); // where the sports cards were delivered (open to everyone)
    private static readonly UserId Owner = new(1_200_000_000_000_000_020);
    private static readonly DateTimeOffset T0 = TestHost.T0;

    private TestHost _host = null!;
    private F1FakeProviders _f1 = null!;
    private readonly CountingEsports _esports = new();
    private readonly CountingVolleyball _volleyball = new();

    public async ValueTask InitializeAsync()
    {
        (_host, _f1) = await F1TestHostExtensions.CreateF1HostAsync(extra: s =>
        {
            s.AddSingleton<IEsportsDataProvider>(_esports);
            s.AddSingleton<IVolleyballDataProvider>(_volleyball);
        });
        foreach (var module in new[] { "agenda", "formula1", "esports", "volleyball", "lfg" })
            await SetModuleAsync(module, true);
        _host.Guilds.SetChannelAudience(Guild, OpenChannel, ChannelAudience.Open);
        _host.Guilds.SetChannelAudience(Guild, CardChannel, ChannelAudience.Open);
        _host.Guilds.SetChannelAudience(Guild, HiddenChannel, ChannelAudience.Open with { EveryoneCanView = false });
        SeedCaches();
        await SeedDatabaseAsync();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private Task SetModuleAsync(string module, bool on) => _host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), module, on, CancellationToken.None)).Succeeded.Should().BeTrue());

    // ---------------------------------------------------------------- data

    private const string Meeting = "2026-18";

    private static F1SessionView Session(F1SessionType type, DateTimeOffset start, F1SessionState state = F1SessionState.Scheduled, DateTimeOffset? lifecycleAt = null) =>
        new(new F1Session(2026, 18, type, start), "Azerbaycan GP", "Baku City Circuit", "Azerbaijan", "Baku", state, null, null, null, null, lifecycleAt, false);

    private static EsportsMatch Match(string id, string a, string b, DateTimeOffset? start, MatchStatus status = MatchStatus.Scheduled, bool exact = true, int? scoreA = null) =>
        new(new MatchKey("pandascore", id), new TournamentRef("pandascore", "t1", "IEM Örnek Kupası", "1", null, null, null), start, exact, 3, status, "test",
            new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", "team-" + a, a, null), scoreA, OpponentResult.None),
            new MatchOpponent(OpponentKind.Team, new TeamRef("pandascore", "team-" + b, b, null), null, OpponentResult.None),
            null, false, false, [], null, null, []);

    private static VbMatchView Volley(string key, DateTimeOffset? start, VolleyballMatchStatus status = VolleyballMatchStatus.Scheduled, bool finished = false) =>
        new(key, CountingVolleyball.ProviderId, "Milletler Ligi", "Grup", null, start, FollowedSide.Home, "Türkiye", "TUR", "Polonya", "POL", null, null, null, null, [],
            status, finished ? 3 : 0, finished ? 1 : 0, [], null, null, null, finished, finished, false, false, T0.AddMinutes(-5));

    private void SeedCaches()
    {
        var f1 = _host.Services.GetRequiredService<Formula1Cache>();
        f1.SetSchedule(new F1Feed<IReadOnlyList<F1SeasonSchedule>>([new F1SeasonSchedule(2026, "test", [])], T0.AddHours(-1), F1ProviderOutcome.Success, null, T0.AddHours(-1), 0));
        f1.SetSessions(
        [
            Session(F1SessionType.Practice1, T0.AddHours(2)),
            Session(F1SessionType.Practice2, T0.AddHours(-1), F1SessionState.Suspended, T0.AddMinutes(-2)), // red flag right now
            Session(F1SessionType.Sprint, T0.AddHours(-4), F1SessionState.Finalised, T0.AddHours(-3)),
            Session(F1SessionType.SprintQualifying, T0.AddHours(3), F1SessionState.Cancelled),
            Session(F1SessionType.Qualifying, T0.AddHours(22)),
            Session(F1SessionType.Race, T0.AddHours(26)),
        ], new Dictionary<string, F1CachedResult>());

        _host.Services.GetRequiredService<EsportsCache>().Restore(
        [
            Match("1", "Aurora", "Vitality", T0.AddHours(1)),
            Match("2", "FaZe", "NaVi", T0.AddMinutes(-30), MatchStatus.Live),
            Match("3", "G2", "MOUZ", T0.AddHours(-3), MatchStatus.Finished, scoreA: 2),
            Match("4", "Spirit", "Falcons", new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero), exact: false),
            Match("5", "Liquid", "Astralis", null),
            Match("6", "Heroic", "Furia", T0.AddHours(40)),
        ], T0.AddMinutes(-2), null, null, null, []);

        var vb = _host.Services.GetRequiredService<VolleyballCache>();
        var feed = new VbFeed(T0.AddHours(-1), VbProviderOutcome.Success, null, T0.AddHours(-1), 0);
        vb.Restore(feed, feed);
        vb.SetMatches([Volley("fivb:1", T0.AddHours(27)), Volley("fivb:2", T0.AddHours(-5), VolleyballMatchStatus.Finished, true), Volley("fivb:3", null)]);
    }

    private static OutboxMessageEntity Card(GuildId guild, string module, string sourceKey, string kind, ulong? message, OutboxStatus status = OutboxStatus.Sent, bool dry = false,
        ulong channel = 555, DateTimeOffset? sentAt = null, string? lastError = null) => new()
        {
            LogicalKey = Guid.NewGuid().ToString("N"),
            GuildId = guild.Value,
            ModuleId = module,
            ChannelId = channel,
            Kind = kind,
            SourceKey = sourceKey,
            IsDryRun = dry,
            PayloadJson = "{}",
            PayloadHash = "h",
            Status = status,
            CreatedAt = T0.AddHours(-2),
            UpdatedAt = T0.AddHours(-2),
            ExpiresAt = T0.AddDays(2),
            SentAt = status == OutboxStatus.Sent ? sentAt ?? T0.AddHours(-2) : null,
            DiscordMessageId = message,
            LastError = lastError,
        };


    private Task SeedDatabaseAsync() => _host.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        db.Outbox.AddRange(
            Card(Guild, "formula1", Meeting, "weekend_schedule", 5001),
            Card(Guild, "formula1", "2026-18-race", "race_reminder", 5002, OutboxStatus.Simulated, dry: true),
            Card(Guild, "formula1", "2026-18-race", "race_reminder", null, OutboxStatus.Pending),
            Card(OtherGuild, "formula1", Meeting, "weekend_schedule", 5999),
            Card(Guild, "esports", "pandascore:1", "reminder", 6001),
            Card(Guild, "esports", "pandascore:1", "result", 6002),
            Card(Guild, "esports", "pandascore:2", "reminder", 6003, OutboxStatus.Failed),
            Card(Guild, "volleyball", "fivb:1", "reminder:20260925", 7001),
            Card(Guild, "volleyball", "fivb:1", "set:1", 7002));

        LfgListingEntity Listing(GuildId guild, ChannelId channel, string game, LfgStatus status, DateTimeOffset? eventAt, int max, ulong? message, DateTimeOffset? expires = null) => new()
        {
            GuildId = guild.Value,
            ChannelId = channel.Value,
            MessageId = message,
            OwnerUserId = 42,
            GameName = game,
            Details = "gizli ayrıntı",
            MaxPlayers = max,
            Status = status,
            CreatedAt = T0.AddHours(-6),
            EventAt = eventAt,
            ExpiresAt = expires ?? T0.AddHours(8),
            VoiceChannelId = 999,
        };

        var planned = Listing(Guild, Here, "Valheim", LfgStatus.Open, T0.AddHours(4), 6, 8001);
        var full = Listing(Guild, Here, "Lethal Company", LfgStatus.Full, null, 2, 8002);
        var passed = Listing(Guild, Here, "Phasmophobia", LfgStatus.Open, T0.AddHours(-1), 4, 8003);
        db.Set<LfgListingEntity>().AddRange(planned, full, passed,
            Listing(Guild, Here, "Süresi Geçmiş", LfgStatus.Open, null, 4, 8004, expires: T0.AddMinutes(-1)),
            Listing(Guild, Here, "Kapanmış", LfgStatus.Closed, null, 4, 8005),
            Listing(Guild, Here, "Kayıp Kart", LfgStatus.Orphaned, null, 4, 8006),
            Listing(OtherGuild, Here, "Başka Sunucu", LfgStatus.Open, null, 4, 8007),
            Listing(Guild, OpenChannel, "Açık Kanal Oyunu", LfgStatus.Open, T0.AddHours(5), 5, 8008),
            Listing(Guild, HiddenChannel, "Gizli Kanal Oyunu", LfgStatus.Open, T0.AddHours(5), 5, 8009),
            Listing(Guild, Here, "Linksiz", LfgStatus.Open, T0.AddHours(6), 3, null));
        await db.SaveChangesAsync();

        LfgParticipantEntity P(LfgListingEntity l, ulong user, LfgResponse response) => new() { ListingId = l.Id, UserId = user, Response = response, JoinedAt = T0.AddHours(-5) };
        db.Set<LfgParticipantEntity>().AddRange(
            P(planned, 42, LfgResponse.Joined), P(planned, 43, LfgResponse.Joined), P(planned, 44, LfgResponse.Joined),
            P(planned, 45, LfgResponse.Maybe), P(planned, 46, LfgResponse.Waitlisted),
            P(full, 42, LfgResponse.Joined), P(full, 43, LfgResponse.Joined), P(full, 44, LfgResponse.Waitlisted));
        await db.SaveChangesAsync();
        return 0;
    });

    // ---------------------------------------------------------------- helpers

    private sealed class FakeResponder : IAgendaResponder
    {
        public List<string> Calls { get; } = [];
        public List<AgendaCard> Cards { get; } = [];
        public List<string> Private { get; } = [];

        public Task DeferPublicAsync() { Calls.Add("defer-public"); return Task.CompletedTask; }
        public Task ReplyPublicAsync(AgendaCard card) { Calls.Add("public"); Cards.Add(card); return Task.CompletedTask; }
        public Task ReplyPrivateAsync(string text) { Calls.Add("private"); Private.Add(text); return Task.CompletedTask; }
        public Task DeferUpdateAsync() { Calls.Add("defer-update"); return Task.CompletedTask; }
        public Task UpdateAsync(AgendaCard card) { Calls.Add("update"); Cards.Add(card); return Task.CompletedTask; }
    }

    private async Task<FakeResponder> RunAsync(AgendaView view, AgendaSport sport = AgendaSport.All, AgendaPeriod period = AgendaPeriod.SevenDays)
    {
        var responder = new FakeResponder();
        await _host.InScopeAsync(sp => sp.GetRequiredService<AgendaFlow>().CommandAsync(new AgendaRequest(Guild, Here, Owner, view, sport, period), responder, CancellationToken.None));
        return responder;
    }

    private async Task<FakeResponder> ClickAsync(string? customId, UserId clicker, ulong message, DateTimeOffset createdAt)
    {
        var responder = new FakeResponder();
        await _host.InScopeAsync(sp => sp.GetRequiredService<AgendaFlow>().ButtonAsync(new AgendaClick(Guild, Here, clicker, new MessageId(message), createdAt, customId), responder, CancellationToken.None));
        return responder;
    }

    /// <summary>Every page of a card, as its buttons would show them one by one.</summary>
    private Task<string> AllPagesAsync(AgendaView view, AgendaSport sport = AgendaSport.All, AgendaPeriod period = AgendaPeriod.SevenDays) => _host.InScopeAsync(async sp =>
    {
        var snapshot = await sp.GetRequiredService<AgendaService>().BuildAsync(new AgendaRequest(Guild, Here, Owner, view, sport, period), CancellationToken.None);
        var renderer = sp.GetRequiredService<AgendaRenderer>();
        var first = renderer.Render(snapshot, 0);
        return string.Join("\n", Enumerable.Range(0, first.Pages).Select(p => Text(renderer.Render(snapshot, p))));
    });

    private static string Text(AgendaCard card) => card.Embed.Description + "\n" + string.Join("\n", card.Embed.Fields.Select(f => f.Value));

    /// <summary>The lines under one day's headings, across all pages (a day may continue on the next page).</summary>
    private static string DayText(string text, string day)
    {
        var lines = new List<string>();
        var inDay = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("📆 **", StringComparison.Ordinal))
                inDay = line.StartsWith("📆 **" + day + "**", StringComparison.Ordinal);
            else if (line.StartsWith('➕') || line.StartsWith("🏎️ F1:", StringComparison.Ordinal))
                inDay = false; // the "more" line and the data-status field
            if (inDay)
                lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    private Task<(int Outbox, int Listings, int Participants, int Open)> CountsAsync() => _host.InScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<ToroDbContext>();
        return (await db.Outbox.CountAsync(), await db.Set<LfgListingEntity>().CountAsync(), await db.Set<LfgParticipantEntity>().CountAsync(),
            await db.Set<LfgListingEntity>().CountAsync(l => l.Status == LfgStatus.Open));
    });

    // ---------------------------------------------------------------- /gundem

    [Fact]
    public async Task Gundem_shows_today_and_tomorrow_from_every_source_in_one_public_card()
    {
        var before = await CountsAsync();

        var responder = await RunAsync(AgendaView.Digest);

        responder.Calls.Should().Equal("defer-public", "public");
        var card = responder.Cards.Single();
        card.Pages.Should().Be(2, "16 items: one card, two pages of the same message");
        card.Embed.Description.Should().Contain("➕ Sonraki sayfalarda").And.Contain("Yarın", "page 1 says what is still to come tomorrow");
        var text = await AllPagesAsync(AgendaView.Digest);
        var today = DayText(text, "Bugün");
        var tomorrow = DayText(text, "Yarın");
        card.Embed.Description.Should().Contain("📆 **Bugün**").And.Contain("📆 **Yarın**", "page 1 shows both days");

        // F1: sessions, not weekends; a red-flagged session is just "in progress"; finished and result-bearing data stay out.
        today.Should().Contain("Azerbaycan GP — 1. Antrenman").And.Contain("Azerbaycan GP — 2. Antrenman").And.Contain("Azerbaycan GP — Sprint Sıralaması");
        text.Should().NotContain("Azerbaycan GP — Sprint**", "the finished sprint is not listed");
        tomorrow.Should().Contain("Azerbaycan GP — Sıralama").And.Contain("Azerbaycan GP — Yarış");
        text.Should().Contain("🔴 Devam ediyor").And.Contain("İptal edildi");
        text.Should().NotContainAny("KIRMIZI", "kırmızı", "bayrak", "SAFETY", "Suspended", "DİSKALİFİYE");
        text.Should().Contain("(https://discord.com/channels/" + Guild.Value + "/555/5001)", "the weekend card of this guild");
        text.Should().NotContain("/5002)").And.NotContain("/5999)", "no dry-run, pending or other-guild card");

        // CS2: live from current data, date-only without a time, finished and undated left out; only programme cards linked.
        today.Should().Contain("Aurora — Vitality").And.Contain("FaZe — NaVi");
        text.Should().NotContain("G2").And.NotContain("Heroic", "a finished match and a match after tomorrow are not listed");
        tomorrow.Should().Contain("Spirit — Falcons").And.Contain("25 Eylül · Saat açıklanmadı");
        text.Should().Contain("/6001)").And.NotContain("/6002)", "the reminder card, never the result card").And.NotContain("/6003)");
        text.Should().Contain("CS2: son güncelleme").And.Contain("tarihi açıklanmamış 1 kayıt");

        // Volleyball: only the followed team; finished match out; programme card, not the set card.
        tomorrow.Should().Contain("Türkiye — Polonya").And.Contain("/7001)");
        text.Should().NotContain("/7002)");

        // LFG: active listings of this channel and of an open channel; joined players only; nothing private.
        today.Should().Contain("👥 Şimdi ekip arayanlar").And.Contain("Lethal Company").And.Contain("2/2 · Dolu / bekleme listesi");
        today.Should().Contain("Valheim").And.Contain("3/6 kişi").And.Contain("/8001)");
        today.Should().Contain("Phasmophobia").And.Contain("Başlangıç saati geçti · ilan açık");
        today.Should().Contain("Açık Kanal Oyunu").And.Contain("Linksiz");
        text.Should().NotContainAny("Gizli Kanal", "Süresi Geçmiş", "Kapanmış", "Kayıp Kart", "Başka Sunucu", "gizli ayrıntı", "<@");
        card.Embed.Footer.Should().Be("Sayfa 1/2 · Günler: Europe/Istanbul · Saatler sizin yerel saatinizde");

        // Read-only: no provider call, nothing written, nothing expired or staged.
        (_f1.ScheduleCalls, _f1.LifecycleCalls, _f1.ResultCalls, _f1.StandingsCalls, _esports.Calls, _volleyball.Calls).Should().Be((0, 0, 0, 0, 0, 0));
        (await CountsAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Ajanda_filters_by_sport_never_lists_lfg_and_applies_the_server_filters()
    {
        var all = await AllPagesAsync(AgendaView.Agenda);
        all.Should().Contain("Heroic — Furia", "seven days reach past tomorrow").And.NotContain("Valheim").And.NotContain("LFG:");

        var f1 = Text((await RunAsync(AgendaView.Agenda, AgendaSport.Formula1)).Cards.Single());
        f1.Should().Contain("Azerbaycan GP").And.NotContain("Aurora").And.NotContain("Türkiye — Polonya").And.NotContain("CS2:");

        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Set<EsportsFilterEntity>().Add(new EsportsFilterEntity { GuildId = Guild.Value, Dimension = (int)FilterDimension.Team, Value = "team-Aurora", Label = "Aurora" });
            await db.SaveChangesAsync();
            return 0;
        });
        var filtered = Text((await RunAsync(AgendaView.Agenda, AgendaSport.Cs2)).Cards.Single());
        filtered.Should().Contain("Aurora — Vitality").And.NotContain("FaZe").And.NotContain("Spirit");

        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            var config = await db.Set<EsportsGuildConfigEntity>().FirstOrDefaultAsync(c => c.GuildId == Guild.Value);
            if (config is null)
                db.Set<EsportsGuildConfigEntity>().Add(config = new EsportsGuildConfigEntity { GuildId = Guild.Value });
            config.VrsTopN = 20;
            await db.SaveChangesAsync();
            return 0;
        });
        var vrs = Text((await RunAsync(AgendaView.Agenda, AgendaSport.Cs2)).Cards.Single());
        vrs.Should().NotContain("Aurora — Vitality", "VRS data is missing: the filter cannot judge, so nothing passes").And.Contain("maç gösterilmedi");
    }

    [Fact]
    public async Task A_source_module_that_is_off_is_not_shown_even_with_a_full_cache()
    {
        await SetModuleAsync("esports", false);
        await SetModuleAsync("lfg", false);

        var text = await AllPagesAsync(AgendaView.Digest);

        text.Should().NotContain("Aurora").And.NotContain("Valheim");
        text.Should().Contain("CS2: modül bu sunucuda kapalı").And.Contain("LFG: modül bu sunucuda kapalı");
        text.Should().Contain("Azerbaycan GP", "the other sources are unaffected");
    }

    [Fact]
    public async Task Old_data_is_marked_and_never_shown_as_running()
    {
        _host.Clock.Advance(TimeSpan.FromHours(1)); // CS2 data is now 62 min old (stale after 30), the live match started 90 min ago

        var text = await AllPagesAsync(AgendaView.Digest);

        text.Should().Contain("CS2: ⚠️ veri eski");
        var faze = text[text.IndexOf("FaZe — NaVi", StringComparison.Ordinal)..];
        faze[..faze.IndexOf('\n', faze.IndexOf('\n') + 1)].Should().Contain("Başlangıç saati geçti; durum doğrulanamadı").And.NotContain("Devam ediyor");
    }

    // ---------------------------------------------------------------- buttons

    [Fact]
    public async Task Buttons_belong_to_the_card_owner_expire_cool_down_and_recompute_days_after_midnight()
    {
        var card = (await RunAsync(AgendaView.Digest)).Cards.Single();
        var refresh = card.Buttons.Single(b => b.Label == "🔄 Yenile").CustomId;

        var stranger = await ClickAsync(refresh, new UserId(77), 1, T0);
        stranger.Calls.Should().Equal("private");
        stranger.Private.Single().Should().Contain("/gundem");

        (await ClickAsync("tsq:agenda:r:g:0:0:" + Owner.Value + ":99999", Owner, 2, T0)).Calls.Should().Equal("private");
        (await ClickAsync(refresh, Owner, 3, T0 - AgendaFlow.CardLifetime - TimeSpan.FromSeconds(1))).Private.Single().Should().Contain("süresi doldu");

        var ok = await ClickAsync(refresh, Owner, 4, T0);
        ok.Calls.Should().Equal("defer-update", "update");

        // The open channel turns private: the next page or refresh drops its listing (checked again on every click).
        (await AllPagesAsync(AgendaView.Digest)).Should().Contain("Açık Kanal Oyunu");
        _host.Guilds.SetChannelAudience(Guild, OpenChannel, ChannelAudience.Open with { EveryoneCanView = false });
        (await AllPagesAsync(AgendaView.Digest)).Should().NotContain("Açık Kanal Oyunu");
        (await ClickAsync(refresh, Owner, 4, T0)).Private.Single().Should().Contain("Biraz bekle", "a second click on the same card within the cooldown");

        // 21:05 UTC = 00:05 on the 25th in Istanbul: "today" is now the 25th, headings and windows recomputed together.
        _host.Clock.Advance(TimeSpan.FromHours(9) + TimeSpan.FromMinutes(5));
        var after = await ClickAsync(refresh, Owner, 5, _host.Clock.GetUtcNow().AddMinutes(-1));
        var text = Text(after.Cards.Single());
        text.Should().Contain("📆 **Bugün** · 25 Eylül Cuma").And.Contain("📆 **Yarın** · 26 Eylül Cumartesi");
        text.Should().NotContain("Valheim", "yesterday's listing expired (expiry at T0 + 8 h)");
        (_f1.ScheduleCalls, _esports.Calls, _volleyball.Calls).Should().Be((0, 0, 0));
    }

    // ---------------------------------------------------------------- read-only proof

    /// <summary>
    /// Catches every database change during one phase of a test, whatever the path: (1) every SQL command EF Core sends to
    /// THIS test's database file — SaveChanges, ExecuteUpdate/ExecuteDelete, raw SQL — seen through EF's diagnostic events
    /// (this also sees a write that leaves every value as it was); (2) SQLite's own commit counter read from a separate
    /// connection (<c>PRAGMA data_version</c> moves whenever another connection, through EF or not, commits a change to the
    /// file); (3) the value of every column of every row of every table, before and after.
    /// </summary>
    private sealed class WriteProbe : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        private readonly string _path;
        private readonly SqliteConnection _observer;
        private readonly long _version;
        private readonly SortedDictionary<string, List<string>> _before;
        private readonly IDisposable _all;
        private readonly List<IDisposable> _listeners = [];
        private readonly ConcurrentQueue<string> _writes = new();
        private int _reads;

        private WriteProbe(string path)
        {
            _path = Path.GetFullPath(path);
            _observer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            _observer.Open();
            _version = DataVersion();
            _before = Rows();
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public static WriteProbe Start(TestHost host) => new(host.Services.GetRequiredService<DatabaseLocation>().Path);

        /// <summary>What changed since the start (empty = nothing).</summary>
        public List<string> Changes()
        {
            var changes = _writes.Select(w => "sql: " + w).ToList();
            if (DataVersion() != _version)
                changes.Add("commit: data_version moved");
            var after = Rows();
            foreach (var table in _before.Keys.Union(after.Keys))
            {
                if (!_before.TryGetValue(table, out var old) || !after.TryGetValue(table, out var now) || !old.SequenceEqual(now))
                    changes.Add("rows: " + table);
            }

            return changes;
        }

        public void ShouldBeUnchanged(string phase)
        {
            _reads.Should().BePositive($"{phase}: the probe sees this database's commands (otherwise the SQL check proves nothing)");
            _before.Should().NotBeEmpty();
            Changes().Should().BeEmpty($"{phase} must not change the database");
        }

        private long DataVersion()
        {
            using var command = _observer.CreateCommand();
            command.CommandText = "PRAGMA data_version";
            return (long)command.ExecuteScalar()!;
        }

        private SortedDictionary<string, List<string>> Rows()
        {
            var tables = new List<string>();
            using (var command = _observer.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }

            var rows = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var table in tables)
            {
                using var command = _observer.CreateCommand();
                command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                using var reader = command.ExecuteReader();
                var list = new List<string>();
                while (reader.Read())
                {
                    list.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetName(i) + "=" + (reader.IsDBNull(i) ? "<null>"
                        : reader.GetValue(i) is byte[] bytes ? Convert.ToBase64String(bytes) : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)))));
                }

                list.Sort(StringComparer.Ordinal);
                rows[table] = list;
            }

            return rows;
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
            {
                lock (_listeners)
                    _listeners.Add(value.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != RelationalEventId.CommandExecuting.Name || value.Value is not CommandEventData data ||
                data.Command.Connection?.ConnectionString is not { Length: > 0 } connection ||
                !string.Equals(Path.GetFullPath(new SqliteConnectionStringBuilder(connection).DataSource), _path, StringComparison.OrdinalIgnoreCase))
                return;
            var sql = data.Command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && !WriteVerb().IsMatch(sql))
                Interlocked.Increment(ref _reads);
            else
                _writes.Enqueue(sql.Length > 120 ? sql[..120] : sql);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            _all.Dispose();
            lock (_listeners)
                _listeners.ForEach(l => l.Dispose());
            _observer.Dispose();
        }
    }

    [GeneratedRegex(@"\b(INSERT|UPDATE|DELETE|REPLACE|UPSERT|CREATE|DROP|ALTER|PRAGMA|VACUUM|ATTACH)\b")]
    private static partial Regex WriteVerb();

    [Fact]
    public async Task First_cards_page_changes_and_refresh_change_nothing_in_the_database()
    {
        FakeResponder digest;
        using (var probe = WriteProbe.Start(_host))
        {
            digest = await RunAsync(AgendaView.Digest);
            await RunAsync(AgendaView.Agenda);
            await RunAsync(AgendaView.Agenda, AgendaSport.Cs2, AgendaPeriod.FourteenDays);
            probe.ShouldBeUnchanged("the first /gundem and /ajanda cards");
        }

        var card = digest.Cards.Single();
        card.Pages.Should().Be(2);
        using (var probe = WriteProbe.Start(_host))
        {
            var page = await ClickAsync(card.Buttons.Single(b => b.Label == "Sonraki ▶").CustomId, Owner, 10, T0);
            page.Calls.Should().Equal("defer-update", "update");
            page.Cards.Single().Page.Should().Be(1);
            var back = await ClickAsync(page.Cards.Single().Buttons.Single(b => b.Label == "◀ Önceki").CustomId, Owner, 11, T0);
            back.Cards.Single().Page.Should().Be(0);
            probe.ShouldBeUnchanged("page changes");
        }

        using (var probe = WriteProbe.Start(_host))
        {
            var refresh = await ClickAsync(card.Buttons.Single(b => b.Label == "🔄 Yenile").CustomId, Owner, 12, T0);
            refresh.Calls.Should().Equal("defer-update", "update");
            (await ClickAsync(card.Buttons.Single(b => b.Label == "🔄 Yenile").CustomId, Owner, 12, T0)).Calls.Should().Equal(["private"], "the cooldown refusal");
            (await ClickAsync(card.Buttons.Single(b => b.Label == "🔄 Yenile").CustomId, new UserId(77), 13, T0)).Calls.Should().Equal(["private"], "a stranger's click");
            probe.ShouldBeUnchanged("refresh and refused clicks");
        }

        (_f1.ScheduleCalls, _f1.LifecycleCalls, _f1.ResultCalls, _f1.StandingsCalls, _esports.Calls, _volleyball.Calls).Should().Be((0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task The_write_probe_catches_a_write_on_every_path_even_one_that_keeps_the_value()
    {
        using (var probe = WriteProbe.Start(_host))
        {
            await _host.InScopeAsync(async sp =>
            {
                var db = sp.GetRequiredService<ToroDbContext>();
                var listing = await db.Set<LfgListingEntity>().FirstAsync(l => l.GameName == "Valheim");
                listing.MaxPlayers = 7;
                await db.SaveChangesAsync();
            });
            probe.Changes().Should().Contain(c => c.StartsWith("sql: UPDATE", StringComparison.Ordinal)).And.Contain("commit: data_version moved").And.Contain("rows: lfg_listing");
        }

        using (var probe = WriteProbe.Start(_host))
        {
            await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.Where(o => o.DiscordMessageId == 5001)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.LastError, "x")));
            probe.Changes().Should().Contain(c => c.StartsWith("sql: UPDATE", StringComparison.Ordinal)).And.Contain("rows: outbox");
        }

        using (var probe = WriteProbe.Start(_host))
        {
            await _host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Database.ExecuteSqlRawAsync("UPDATE \"guild_module_state\" SET \"Enabled\" = \"Enabled\""));
            probe.Changes().Should().Equal(["sql: UPDATE \"guild_module_state\" SET \"Enabled\" = \"Enabled\""],
                "a write that keeps every value leaves the file untouched (SQLite commits nothing) — the SQL check still sees the attempt");
        }

        using (var probe = WriteProbe.Start(_host))
        {
            var path = _host.Services.GetRequiredService<DatabaseLocation>().Path;
            using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                raw.Open();
                using var command = raw.CreateCommand();
                command.CommandText = "UPDATE \"lfg_participant\" SET \"Response\" = \"Response\" + 100";
                command.ExecuteNonQuery().Should().BePositive();
            }

            probe.Changes().Should().Equal(["commit: data_version moved", "rows: lfg_participant"], "a write outside EF is invisible to EF but not to SQLite");
        }
    }

    // ---------------------------------------------------------------- LFG across many channels

    [Fact]
    public async Task Lfg_listings_in_many_channels_keep_private_ones_out_and_the_public_one_in_on_every_page_and_refresh()
    {
        // 12 more channels: the first 10 (earliest listings) private or role-restricted, then two open ones; channel 3 and
        // channel 12 hold two listings each.
        ChannelId Ch(int i) => new((ulong)(1_200_000_000_000_001_000 + i));
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            for (var i = 1; i <= 12; i++)
            {
                _host.Guilds.SetChannelAudience(Guild, Ch(i), i <= 10 ? ChannelAudience.Open with { EveryoneCanView = i % 2 == 1, Narrowed = i % 2 == 1 } : ChannelAudience.Open);
                db.Set<LfgListingEntity>().Add(Listing(Ch(i), (i <= 10 ? "Özel Oda " : "Herkese Açık ") + i, T0.AddMinutes(5 + i), 9100UL + (ulong)i));
            }

            db.Set<LfgListingEntity>().AddRange(Listing(Ch(3), "Özel Oda 3b", T0.AddMinutes(40), 9200), Listing(Ch(12), "Herkese Açık 12b", T0.AddMinutes(41), 9201));
            await db.SaveChangesAsync();
        });

        var lookups = _host.Guilds.AudienceLookups;
        var first = await RunAsync(AgendaView.Digest);
        (_host.Guilds.AudienceLookups - lookups).Should().Be(15, "one check per distinct other channel: 12 new, the open and the hidden LFG channel, the card channel");

        var card = first.Cards.Single();
        var pages = new List<string> { Text(card) };
        var next = card;
        for (var p = 1; p < card.Pages; p++)
        {
            next = (await ClickAsync(next.Buttons.Single(b => b.Label == "Sonraki ▶").CustomId, Owner, 100UL + (ulong)p, T0)).Cards.Single();
            _host.Clock.Advance(AgendaCooldown.Window); // a new card id per click anyway; keep "now" honest
            pages.Add(Text(next));
        }

        var all = string.Join("\n", pages);
        all.Should().Contain("Herkese Açık 11").And.Contain("Herkese Açık 12").And.Contain("Herkese Açık 12b").And.Contain("/9112)");
        all.Should().NotContain("Özel Oda").And.NotContain("/9101)").And.NotContain("/9200)").And.NotContain("doğrulanamadı", "every channel was checked");
        pages.Should().NotContain(t => t.Contains("etkinlik yok"));

        // Permissions change between clicks: refresh and paging check again.
        _host.Guilds.SetChannelAudience(Guild, Ch(11), ChannelAudience.Open with { Narrowed = true });
        _host.Guilds.SetChannelAudience(Guild, Ch(2), ChannelAudience.Open);
        var refreshed = (await ClickAsync(card.Buttons.Single(b => b.Label == "🔄 Yenile").CustomId, Owner, 200, _host.Clock.GetUtcNow())).Cards.Single();
        var after = new List<string> { Text(refreshed) };
        for (var p = 1; p < refreshed.Pages; p++)
            after.Add(Text((await ClickAsync(AgendaCustomId.Format(AgendaCustomId.Navigate, new AgendaRequest(Guild, Here, Owner, AgendaView.Digest, AgendaSport.All, AgendaPeriod.SevenDays), p),
                Owner, 300UL + (ulong)p, _host.Clock.GetUtcNow())).Cards.Single()));
        var now = string.Join("\n", after);
        now.Should().NotContain("Herkese Açık 11").And.Contain("Özel Oda 2").And.Contain("Herkese Açık 12b").And.NotContain("Özel Oda 3");

        // A channel that cannot be checked (gone from the cache) hides its listing and says the list may be incomplete — no count.
        _host.Guilds.SetChannelAudience(Guild, Ch(12), ChannelAudience.Unknown);
        var unknown = await AllPagesAsync(AgendaView.Digest);
        unknown.Should().NotContain("Herkese Açık 12").And.Contain("LFG: bu kanaldaki ve herkese açık kanallardaki ilanlar · ⚠️ bazı kayıtlar bu kartta doğrulanamadı; liste eksik olabilir");
        Regex.Match(unknown, "LFG: [^\n]*").Value.Should().NotMatchRegex(@"\d");
    }

    private static LfgListingEntity Listing(ChannelId channel, string game, DateTimeOffset? eventAt, ulong? message) => new()
    {
        GuildId = Guild.Value,
        ChannelId = channel.Value,
        MessageId = message,
        OwnerUserId = 42,
        GameName = game,
        Details = "gizli ayrıntı",
        MaxPlayers = 4,
        Status = LfgStatus.Open,
        CreatedAt = T0.AddHours(-1),
        EventAt = eventAt,
        ExpiresAt = T0.AddHours(8),
    };

    // ---------------------------------------------------------------- card links

    /// <summary>The item's lines ("**Kind · title**" and the line below it).</summary>
    private static string ItemLines(string text, string title)
    {
        var at = text.IndexOf(" · " + title + "**", StringComparison.Ordinal);
        at.Should().BeGreaterThan(-1, title + " is listed");
        var start = text.LastIndexOf('\n', at) + 1;
        var end = text.IndexOf('\n', text.IndexOf('\n', at) + 1);
        return end < 0 ? text[start..] : text[start..end];
    }

    [Fact]
    public async Task Card_links_use_the_newest_delivered_card_of_this_guild_only_while_it_is_known_to_exist_in_a_readable_channel()
    {
        ChannelId restricted = new(556), gone = new(557);
        _host.Guilds.SetChannelAudience(Guild, restricted, ChannelAudience.Open with { Narrowed = true });
        // 557: not in the gateway cache (deleted channel).
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            // Aurora — Vitality: its reminder was deleted in Discord (the outbox recorded it on an edit).
            var reminder = await db.Outbox.SingleAsync(o => o.DiscordMessageId == 6001);
            reminder.LastError = SentCards.DeletedMarker;
            // FaZe — NaVi: a start card whose channel was found gone on an edit, plus deliveries never confirmed.
            db.Outbox.AddRange(
                Card(Guild, "esports", "pandascore:2", "started", 6004, lastError: "edit_failed UnknownChannel: Unknown Channel"),
                Card(Guild, "esports", "pandascore:2", "reminder", 6005, OutboxStatus.InFlight),
                Card(Guild, "esports", "pandascore:2", "reminder", 6006, OutboxStatus.DeliveryUnknown),
                // Spirit — Falcons: a reminder in a deleted channel.
                Card(Guild, "esports", "pandascore:4", "reminder", 6007, channel: gone.Value),
                // Türkiye — Polonya: a newer reminder (time changed) replaces the old one; later a final card (never linked).
                Card(Guild, "volleyball", "fivb:1", "reminder:20260926", 7003, sentAt: T0.AddHours(-1)),
                Card(Guild, "volleyball", "fivb:1", "final", 7004, sentAt: T0.AddMinutes(-1)),
                Card(OtherGuild, "volleyball", "fivb:1", "reminder:20260927", 7999, sentAt: T0));
            await db.SaveChangesAsync();
        });

        var text = await AllPagesAsync(AgendaView.Digest);

        ItemLines(text, "Aurora — Vitality").Should().NotContain("/6001)").And.EndWith("`/esports matches`", "a known deleted card is not linked");
        ItemLines(text, "FaZe — NaVi").Should().NotContainAny("/6003)", "/6004)", "/6005)", "/6006)").And.EndWith("`/esports matches`");
        ItemLines(text, "Spirit — Falcons").Should().NotContain("/6007)").And.EndWith("`/esports matches`", "the card's channel no longer exists");
        ItemLines(text, "Türkiye — Polonya").Should().Contain("[Kartı aç](https://discord.com/channels/" + Guild.Value + "/555/7003)", "the newest programme card");
        text.Should().NotContainAny("/7001)", "/7002)", "/7004)", "/7999)");
        ItemLines(text, "Azerbaycan GP — Yarış").Should().Contain("/555/5001)");

        // The newest card's channel becomes role-restricted: no link to it — and not to the outdated older card either.
        await _host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            (await db.Outbox.SingleAsync(o => o.DiscordMessageId == 7003)).ChannelId = restricted.Value;
            await db.SaveChangesAsync();
        });
        ItemLines(await AllPagesAsync(AgendaView.Digest), "Türkiye — Polonya").Should().NotContainAny("/7003)", "/7001)").And.EndWith("`/volleyball schedule`");

        // The weekend card's channel turns private: F1 items fall back to the command.
        _host.Guilds.SetChannelAudience(Guild, CardChannel, ChannelAudience.Open with { EveryoneCanView = false });
        ItemLines(await AllPagesAsync(AgendaView.Digest), "Azerbaycan GP — Yarış").Should().NotContain("/5001)").And.EndWith("`/f1 next`");
    }

    // ---------------------------------------------------------------- fakes

    private sealed class CountingEsports : IEsportsDataProvider
    {
        public int Calls { get; private set; }
        public string Id => "pandascore";
        public ProviderCapability Capabilities => ProviderCapability.Fixtures;
        public bool IsConfigured => true;

        public Task<ProviderResult<IReadOnlyList<EsportsMatch>>> GetMatchesAsync(MatchWindow window, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("no provider call on the agenda path");
        }

        public Task<ProviderResult<IReadOnlyList<EsportsEvent>>> GetEventsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("no provider call on the agenda path");
        }
    }

    private sealed class CountingVolleyball : IVolleyballDataProvider
    {
        public const string ProviderId = "fivb";
        public int Calls { get; private set; }
        public string Id => ProviderId;
        public string AttributionKey => "vb.attr.fivb";
        public bool IsConfigured => true;
        public VbCapabilities Capabilities => VbCapabilities.Fixtures;

        public Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetMatchesAsync(TrackedTeamIdentity team, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("no provider call on the agenda path");
        }

        public Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetLiveStateAsync(IReadOnlyCollection<string> providerMatchIds, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("no provider call on the agenda path");
        }
    }
}
