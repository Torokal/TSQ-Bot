using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Discord.Transport;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Live.Application;
using ToroSquad.Modules.Live.Domain;
using ToroSquad.Modules.Live.Providers;

namespace ToroSquad.Tests.Support;

public sealed record FakeStream(string Id, DateTimeOffset StartedAt, string Title, string? Category = null);

/// <summary>
/// Scriptable official-API provider: tests set the "real world" (which channel is live, with which title); the provider
/// answers exactly like a batched provider call (or a scripted failure) and counts calls.
/// </summary>
public sealed class LiveFakeProvider(LivePlatform platform, TimeProvider clock) : ILiveStatusProvider
{
    public Dictionary<string, FakeStream> Live { get; } = new(StringComparer.Ordinal);
    public LiveProviderOutcome? Failure { get; set; }

    /// <summary>Channels the provider answer does not describe (unknown slug, malformed entry): no observation at all.</summary>
    public HashSet<string> Undescribed { get; } = new(StringComparer.Ordinal);
    public bool Configured { get; set; } = true;
    public int Calls { get; private set; }

    public LivePlatform Platform => platform;
    public bool IsConfigured => Configured;
    public LiveAuthState Auth => new(Failure is { } f && f == LiveProviderOutcome.AuthFailed ? f : LiveProviderOutcome.Ok, null, null);

    public void GoLive(string login, FakeStream stream) => Live[login] = stream;

    public void GoOffline(string login) => Live.Remove(login);

    public Task<LiveProviderResult> GetStatusAsync(IReadOnlyCollection<string> logins, CancellationToken cancellationToken)
    {
        Calls++;
        var at = clock.GetUtcNow();
        if (Failure is { } failure)
            return Task.FromResult(LiveProviderResult.Fail(failure, "scripted", at));
        var observations = logins.Where(l => !Undescribed.Contains(l)).Select(l => Live.TryGetValue(l, out var s)
                ? new LiveObservation(platform, l, ObservationKind.Status, true, at, s.Id, s.StartedAt, s.Title, s.Category)
                : new LiveObservation(platform, l, ObservationKind.Status, false, at))
            .ToList();
        return Task.FromResult(LiveProviderResult.Ok(observations, at));
    }
}

/// <summary>
/// A TSQ Live test bed: the production registration (ToroHost) on a real SQLite file, fake Twitch/Kick providers, fake
/// Discord transport, fake clock, the guild's announcement channel usable (incl. Mention Everyone) and the module enabled.
/// </summary>
public sealed class LiveBed : IAsyncDisposable
{
    public static readonly GuildId Guild = new(55);
    public static readonly ChannelId Channel = new(700);
    public const string Toro = "lordtoro";
    public const string Nasil = "nasilyani69";

    public static readonly GuildPermission ChannelPermissions =
        GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory | GuildPermission.MentionEveryone;

    private readonly bool _ownsHost;

    private LiveBed(TestHost host, bool ownsHost)
    {
        Host = host;
        _ownsHost = ownsHost;
    }

    public TestHost Host { get; }
    public LiveFakeProvider Twitch => Host.Services.GetServices<ILiveStatusProvider>().OfType<LiveFakeProvider>().Single(p => p.Platform == LivePlatform.Twitch);
    public LiveFakeProvider Kick => Host.Services.GetServices<ILiveStatusProvider>().OfType<LiveFakeProvider>().Single(p => p.Platform == LivePlatform.Kick);
    public FakeMessageTransport Transport => Host.Services.GetRequiredService<FakeMessageTransport>();
    public DateTimeOffset Now => Host.Clock.GetUtcNow();

    public IReadOnlyList<FakeMessageTransport.FakeMessage> Messages => Transport.Messages.Where(m => m.Channel == Channel).ToList();

    /// <summary>Messages that really pinged @everyone (allowed_mentions opt-in AND the text).</summary>
    public int EveryonePings => Messages.Count(m => m.Message.Mentions.Everyone && m.Message.Content?.Contains("@everyone", StringComparison.Ordinal) == true);

    public static Dictionary<string, string?> Settings(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Live:Enabled"] = "true",
            ["Live:DiscordChannelId"] = Channel.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Live:GuildId"] = Guild.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Live:ReconciliationIntervalSeconds"] = "30",
            ["Live:ReconnectGraceSeconds"] = "120",
            ["Live:Creators:lordtoro:DisplayName"] = "LORDTORO",
            ["Live:Creators:lordtoro:Twitch"] = Toro,
            ["Live:Creators:lordtoro:Kick"] = Toro,
            ["Live:Creators:nasilyani69:DisplayName"] = "NASILYANI69",
            ["Live:Creators:nasilyani69:Twitch"] = Nasil,
            ["Live:Creators:nasilyani69:Kick"] = Nasil,
        };
        foreach (var (k, v) in overrides ?? [])
            settings[k] = v;
        return settings;
    }

    public static async Task<LiveBed> CreateAsync(Dictionary<string, string?>? overrides = null, bool enableModule = true, DateTimeOffset? start = null,
        LiveBed? restartOf = null, Action<IServiceCollection>? extra = null)
    {
        var settings = Settings(overrides);
        if (restartOf is not null)
            settings["Bot:DataDirectory"] = restartOf.Host.Directory;
        var host = await TestHost.CreateAsync(settings, start, services =>
        {
            services.RemoveAll<ILiveStatusProvider>();
            services.AddSingleton<ILiveStatusProvider>(sp => new LiveFakeProvider(LivePlatform.Twitch, sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ILiveStatusProvider>(sp => new LiveFakeProvider(LivePlatform.Kick, sp.GetRequiredService<TimeProvider>()));
            if (restartOf is not null)
            {
                // Same Discord channel (the first process's messages are still there).
                services.AddSingleton(restartOf.Transport);
                services.AddSingleton<IMessageTransport>(restartOf.Transport);
            }

            extra?.Invoke(services);
        });
        var bed = new LiveBed(host, ownsHost: true);
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(Guild));
        host.Guilds.SetChannel(Guild, Channel, new BotChannelAccess(true, true, ChannelPermissions));
        if (enableModule && restartOf is null)
            await bed.SetModuleAsync(true);
        if (restartOf is not null)
        {
            foreach (var (login, stream) in restartOf.Twitch.Live)
                bed.Twitch.GoLive(login, stream);
            foreach (var (login, stream) in restartOf.Kick.Live)
                bed.Kick.GoLive(login, stream);
            await host.Services.GetRequiredService<LivePoller>().WarmUpAsync(CancellationToken.None);
        }

        return bed;
    }

    public Task SetModuleAsync(bool enabled) => Host.InScopeAsync(async sp =>
        (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(TestHost.Admin(Guild), "live", enabled, CancellationToken.None))
        .Succeeded.Should().BeTrue());

    /// <summary>One reconciliation round (due providers) + one outbox dispatch round.</summary>
    public async Task PollAsync()
    {
        await Host.Services.GetRequiredService<LivePoller>().TickAsync(CancellationToken.None);
        await DeliverAsync();
    }

    public async Task DeliverAsync()
    {
        var processor = Host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessOnceAsync(CancellationToken.None) > 0)
        {
        }
    }

    /// <summary>Advance the clock, then poll and deliver.</summary>
    public async Task StepAsync(TimeSpan advance)
    {
        Host.Clock.Advance(advance);
        await PollAsync();
    }

    /// <summary>Apply observations directly (event-shaped input: metadata updates, duplicates, out-of-order), then deliver.</summary>
    public async Task ApplyAsync(params LiveObservation[] observations)
    {
        await Host.Services.GetRequiredService<LiveCoordinator>().ApplyAsync(observations, CancellationToken.None);
        await DeliverAsync();
    }

    public Task<CreatorState> CreatorAsync(string key) => Host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<CreatorState>().AsNoTracking().SingleAsync(s => s.CreatorKey == key));

    public Task<PlatformState> PlatformAsync(string key, LivePlatform platform) => Host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Set<PlatformState>().AsNoTracking().SingleAsync(s => s.CreatorKey == key && s.Platform == platform));

    public Task<List<OutboxMessageEntity>> OutboxAsync() => Host.InScopeAsync(async sp =>
        await sp.GetRequiredService<ToroDbContext>().Outbox.AsNoTracking().Where(o => o.ModuleId == "live").OrderBy(o => o.Id).ToListAsync());

    public async ValueTask DisposeAsync()
    {
        if (_ownsHost)
            await Host.DisposeAsync();
    }

    public static FakeStream Stream(string id, DateTimeOffset startedAt, string title, string? category = null) => new(id, startedAt, title, category);

    /// <summary>All texts a Discord user would see in a message (content, title, description, fields, button labels).</summary>
    public static string Visible(OutgoingMessage m) =>
        string.Join("\n", new[] { m.Content, m.Embed?.Title, m.Embed?.Description, m.Embed?.Footer }
            .Concat(m.Embed?.Fields.Select(f => f.Value) ?? [])
            .Concat(m.Buttons?.Select(b => b.Label) ?? []));

    public static OutboxStatus[] Terminal => [OutboxStatus.Failed, OutboxStatus.Cancelled, OutboxStatus.Expired];
}
