using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Persistence;
using ToroSquad.Modules.Updates.Providers;

namespace ToroSquad.Tests.Support;

/// <summary>
/// One synthetic Steam news post (never real Steam content: made-up ids, titles and text). The defaults are the observed
/// shape of an official announcement; every field can be overridden to build a broken or foreign post.
/// </summary>
public sealed record SteamPost(
    string Gid,
    string Title,
    DateTimeOffset? Published = null,
    string? Contents = null,
    string[]? Tags = null,
    string? Url = null,
    string Feed = SteamNewsParser.AnnouncementFeed,
    uint? AppId = 730,
    bool External = true);

/// <summary>Builds ISteamNews/GetNewsForApp v2 answers in the observed shape.</summary>
public static class SteamNews
{
    /// <summary>Patch notes in Valve's layout: "[ SECTION ]" headers and list items, made-up lines.</summary>
    public const string PatchNotes = "[p]\\[ MAPS ][/p][list][*][p]Synthetic change one[/p][/*][*][p]Synthetic change two[/p][/*][/list][p]\\[ MISC ][/p][list][*][p]Synthetic fix[/p][/*][/list]";

    public const string Prose = "A synthetic announcement about something that is not a patch. Nothing was changed in the game today.";

    public static readonly string[] PatchTag = ["patchnotes"];

    public static string CdnUrl(string gid) => "https://steamstore-a.akamaihd.net/news/externalpost/steam_community_announcements/" + gid;

    public static string CardUrl(string gid) => "https://store.steampowered.com/news/externalpost/steam_community_announcements/" + gid;

    /// <summary>A tagged "Counter-Strike 2 Update" post with patch notes.</summary>
    public static SteamPost Update(long gid, DateTimeOffset published, string title = "Counter-Strike 2 Update") =>
        new(gid.ToString(CultureInfo.InvariantCulture), title, published, PatchNotes, PatchTag);

    /// <summary>An untagged announcement in prose.</summary>
    public static SteamPost Announcement(long gid, DateTimeOffset published, string? title = null) =>
        new(gid.ToString(CultureInfo.InvariantCulture), title ?? $"Synthetic announcement {gid}", published, Prose);

    public static string Json(IEnumerable<SteamPost> posts, uint appId = 730)
    {
        var items = new JsonArray();
        foreach (var post in posts)
        {
            var item = new JsonObject
            {
                ["gid"] = post.Gid,
                ["title"] = post.Title,
                ["url"] = post.Url ?? CdnUrl(post.Gid),
                ["is_external_url"] = post.External,
                ["author"] = "synthetic",
                ["contents"] = post.Contents ?? Prose,
                ["feedlabel"] = "Community Announcements",
                ["feedname"] = post.Feed,
                ["feed_type"] = 1,
            };
            if (post.Published is { } published)
                item["date"] = published.ToUnixTimeSeconds();
            if (post.AppId is { } own)
                item["appid"] = own;
            if (post.Tags is { } tags)
                item["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray());
            items.Add(item);
        }

        return new JsonObject { ["appnews"] = new JsonObject { ["appid"] = appId, ["newsitems"] = items, ["count"] = items.Count } }.ToJsonString();
    }

    public static HttpResponseMessage Ok(string json, string contentType = "application/json", TimeSpan? expiresIn = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, contentType) };
        if (expiresIn is { } lifetime)
        {
            var date = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
            response.Headers.Date = date;
            response.Content.Headers.Expires = date + lifetime;
        }

        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (retryAfter is { } r)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(r);
        return response;
    }

    public static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}

/// <summary>
/// The scripted "Steam" of a test: every request is recorded and answered by the current responder. Registered as the
/// primary handler of the Steam news HTTP client, so the production provider code runs unchanged.
/// </summary>
public sealed class SteamNewsServer : HttpMessageHandler
{
    private readonly Dictionary<uint, Func<HttpResponseMessage>> _apps = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => SteamNews.Status(HttpStatusCode.ServiceUnavailable);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// Serves these posts as the feed of <paramref name="appId"/> (Counter-Strike 2 by default); a request for an app without
    /// a feed is answered 503. <paramref name="expiresIn"/> adds the cache lifetime header pair (none by default).
    /// </summary>
    public void Serve(IEnumerable<SteamPost> posts, TimeSpan? expiresIn = null, uint appId = 730)
    {
        var json = SteamNews.Json(posts, appId);
        _apps[appId] = () => SteamNews.Ok(json, expiresIn: expiresIn);
        Respond = request => _apps.TryGetValue(AppIdOf(request), out var feed) ? feed() : SteamNews.Status(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>The <c>appid</c> a request asks for (0 when it names none).</summary>
    public static uint AppIdOf(HttpRequestMessage request) =>
        uint.TryParse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["appid"], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;

    public int RequestsFor(uint appId) => Requests.Count(r => AppIdOf(r) == appId);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);
        return Task.FromResult(Respond(request));
    }
}

/// <summary>
/// A second, made-up provider with one made-up game: proves that a game and a provider can be added by registration alone.
/// Its classifier calls a post an update when the title starts with "Patch ".
/// </summary>
public sealed class FakeUpdateProvider : IGameUpdateProvider
{
    public const string ProviderId = "fakestore";
    public const string GameKey = "fakegame";

    public static GameUpdateDefinition Game { get; } = new(GameKey, "Fake Game", "FG", ProviderId, "g-1", new PrefixClassifier());

    public List<GameUpdateCandidate> Posts { get; } = [];

    public UpdateFetchResult? Next { get; set; }

    public Exception? Throw { get; set; }

    public int Requests { get; private set; }

    public string Provider => ProviderId;

    public string DisplayName => "FakeStore";

    public string ReadLinkKey => "updates.card.read_steam";

    public static string Url(string id) => "https://fakestore.example/notes/" + id;

    public bool IsCanonicalUrl(string url) => url.StartsWith("https://fakestore.example/notes/", StringComparison.Ordinal);

    public void Add(string id, string title, DateTimeOffset published) =>
        Posts.Add(new GameUpdateCandidate(ProviderId, GameKey, id, title, Url(id), published, [], ""));

    public Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, CancellationToken cancellationToken)
    {
        Requests++;
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Next ?? new UpdateFetchResult(Posts.Count == 0 ? UpdateFetchOutcome.Empty : UpdateFetchOutcome.Ok, 200, Posts.ToList(), 0, null, null, null));
    }

    private sealed class PrefixClassifier : IGameUpdateClassifier
    {
        public UpdateClassificationResult Classify(GameUpdateCandidate candidate) =>
            candidate.Title.StartsWith("Patch ", StringComparison.Ordinal) ? new(UpdateClassification.Update, "patch_prefix") : new(UpdateClassification.NotUpdate, "no_prefix");
    }
}

/// <summary>
/// A production-wired host for TSQ Bot Updates: real SQLite, real outbox, fake clock, scripted Steam. With
/// <c>shareWith</c> it reopens another rig's database at that rig's time (a restart, possibly in another mode). Like the
/// real host at startup, a new rig records the running mode before anything else (<c>enterMode</c>).
/// </summary>
public sealed class UpdatesRig : IAsyncDisposable
{
    public static readonly GuildId Guild = new(970);
    public static readonly GuildId OtherGuild = new(971);
    public static readonly ChannelId Channel = new(9701);
    public static readonly ChannelId Channel2 = new(9702);
    public static readonly ChannelId OtherChannel = new(9711);
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private UpdatesRig(TestHost host, SteamNewsServer steam, WowForum forum, FakeUpdateProvider? fake, string dataDirectory)
    {
        Host = host;
        Steam = steam;
        Forum = forum;
        Fake = fake;
        DataDirectory = dataDirectory;
    }

    /// <summary>The scripted Blizzard forum (World of Warcraft: Forever).</summary>
    public WowForum Forum { get; }

    /// <summary>Where the database lives: this host's own folder, or the first rig's when the database is shared.</summary>
    public string DataDirectory { get; }

    public TestHost Host { get; }
    public SteamNewsServer Steam { get; }

    /// <summary>The second provider (only with <c>fakeGame</c>).</summary>
    public FakeUpdateProvider? Fake { get; }

    public DateTimeOffset Now => Host.Clock.GetUtcNow();

    public static async Task<UpdatesRig> CreateAsync(string mode = "Live", Dictionary<string, string?>? extra = null, bool configure = true, bool fakeGame = false,
        UpdatesRig? shareWith = null, Action<IServiceCollection>? replace = null, bool enterMode = true)
    {
        var steam = new SteamNewsServer();
        var forum = new WowForum();
        var fake = fakeGame ? new FakeUpdateProvider() : null;
        var overrides = new Dictionary<string, string?> { ["Updates:Mode"] = mode };
        foreach (var (k, v) in extra ?? [])
            overrides[k] = v;
        if (shareWith is not null)
            overrides["Bot:DataDirectory"] = shareWith.DataDirectory;
        var host = await TestHost.CreateAsync(overrides, shareWith?.Now ?? Start, services =>
        {
            services.AddHttpClient(SteamNewsUpdateProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(steam));
            services.AddHttpClient(BlizzardForumUpdateProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(forum));
            if (fake is not null)
            {
                // Exactly what adding a game takes: one definition and (for a new source) one provider.
                services.AddSingleton(FakeUpdateProvider.Game);
                services.AddSingleton<IGameUpdateProvider>(fake);
            }

            replace?.Invoke(services);
        });
        var rig = new UpdatesRig(host, steam, forum, fake, shareWith?.DataDirectory ?? host.Directory);
        rig.AllowChannel(Guild, Channel);
        if (enterMode)
            await rig.Poller.EnterModeAsync(Ct);
        if (configure && shareWith is null)
            await rig.ConfigureGuildAsync(Guild, Channel);
        return rig;
    }

    public void AllowChannel(GuildId guild, ChannelId channel)
    {
        Host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild));
        Host.Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true,
            GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory));
    }

    /// <summary>Channel, module on, and the given games on (CS2 by default).</summary>
    public async Task ConfigureGuildAsync(GuildId guild, ChannelId channel, bool enableModule = true, params string[] games)
    {
        AllowChannel(guild, channel);
        await Host.InScopeAsync(async sp =>
        {
            var config = sp.GetRequiredService<UpdatesConfigService>();
            (await config.SetChannelAsync(TestHost.Admin(guild), channel.Value, Ct)).Succeeded.Should().BeTrue();
            if (enableModule)
                (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(guild), "updates", true, Ct)).Succeeded.Should().BeTrue();
            foreach (var game in games.Length == 0 ? ["cs2"] : games)
                (await config.SetGameEnabledAsync(TestHost.Admin(guild), game, true, Ct)).Succeeded.Should().BeTrue();
        });
    }

    public Task<T> ConfigAsync<T>(Func<UpdatesConfigService, Task<T>> action) => Host.InScopeAsync(sp => action(sp.GetRequiredService<UpdatesConfigService>()));

    public UpdatesPoller Poller => Host.Services.GetRequiredService<UpdatesPoller>();

    /// <summary>Moves the clock to the latest due poll (never backwards), optionally further, and runs one tick.</summary>
    public async Task<int> PollAsync(TimeSpan? after = null, UpdatesPoller? poller = null)
    {
        var due = (await StatesAsync()).Where(s => s.NextPollAt is not null).Select(s => s.NextPollAt!.Value).DefaultIfEmpty(Now).Max();
        if (due > Now)
            Host.Clock.SetUtcNow(due);
        if (after is { } a)
            Host.Clock.Advance(a);
        return await (poller ?? Poller).TickAsync(Ct);
    }

    public async Task DeliverAsync()
    {
        var processor = Host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(Ct) > 0)
        {
        }
    }

    public Task<List<UpdatesSourceStateEntity>> StatesAsync() =>
        Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<UpdatesSourceStateEntity>().AsNoTracking().OrderBy(s => s.GameKey).ToListAsync(Ct));

    public async Task<UpdatesSourceStateEntity?> StateAsync(string game = "cs2") => (await StatesAsync()).FirstOrDefault(s => s.GameKey == game);

    public Task<List<OutboxMessageEntity>> OutboxAsync() =>
        Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "updates").OrderBy(o => o.Id).ToListAsync(Ct));

    public Task<List<UpdatesItemEntity>> ItemsAsync() =>
        Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<UpdatesItemEntity>().AsNoTracking().OrderBy(a => a.GameKey).ThenBy(a => a.ExternalId).ToListAsync(Ct));

    /// <summary>One game's status as the guild's admin sees it.</summary>
    public async Task<UpdatesGameStatus> GameStatusAsync(GuildId guild, string game = "cs2") =>
        (await ConfigAsync(c => c.StatusAsync(TestHost.Admin(guild), Ct))).Status!.Games.Single(g => g.Game.Key == game);

    public Task<UpdatesGuildConfigEntity?> GuildConfigAsync(GuildId guild) =>
        Host.InScopeAsync(sp => sp.GetRequiredService<ToroDbContext>().Set<UpdatesGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guild.Value, Ct));

    public int SteamRequests => Steam.Requests.Count;

    /// <summary>The descriptions of the cards that reached the (fake) Discord, in send order.</summary>
    public IReadOnlyList<string> CardTexts => Host.Transport.Messages.Select(m => m.Message.Embed!.Description!).ToList();

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
