using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Tests.Support;

/// <summary>
/// Scriptable Formula 1 providers (all four capabilities) for integration tests. Lifecycle events are only "visible"
/// once the fake clock has reached their provider timestamp, exactly like a real provider.
/// </summary>
public sealed class F1FakeProviders(TimeProvider clock) : IF1ScheduleProvider, IF1LifecycleProvider, IF1ResultsProvider, IF1StandingsProvider
{
    public const string LifecycleId = "fakelive";
    public const string ResultsId = "fakeresults";

    public List<F1Meeting> Meetings { get; } = [];
    public List<F1ProviderSession> ProviderSessions { get; } = [];
    public Dictionary<string, List<F1LifecycleEvent>> Events { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, F1SessionResult?> ResultsByRef { get; } = new(StringComparer.Ordinal);
    public F1StandingsSnapshot? Drivers { get; set; }
    public F1StandingsSnapshot? Constructors { get; set; }

    public bool LifecycleConfigured { get; set; } = true;
    public F1ProviderOutcome? ScheduleFailure { get; set; }
    public F1ProviderOutcome? LifecycleFailure { get; set; }
    public F1ProviderOutcome? ResultsFailure { get; set; }
    public F1ProviderOutcome? StandingsFailure { get; set; }

    public int ScheduleCalls { get; private set; }
    public int LifecycleCalls { get; private set; }
    public int ResultCalls { get; private set; }
    public int StandingsCalls { get; private set; }

    string IF1ScheduleProvider.Id => "fakeschedule";
    string IF1LifecycleProvider.Id => LifecycleId;
    string IF1ResultsProvider.Id => ResultsId;
    string IF1StandingsProvider.Id => "fakestandings";
    string IF1ScheduleProvider.AttributionKey => "f1.source.jolpica";
    string IF1LifecycleProvider.AttributionKey => "f1.source.openf1";
    string IF1ResultsProvider.AttributionKey => "f1.source.openf1";
    string IF1StandingsProvider.AttributionKey => "f1.source.jolpica";
    bool IF1ScheduleProvider.IsConfigured => true;
    bool IF1LifecycleProvider.IsConfigured => LifecycleConfigured;
    bool IF1ResultsProvider.IsConfigured => true;
    bool IF1StandingsProvider.IsConfigured => true;
    public TimeSpan AvailabilityDelay => TimeSpan.Zero;

    /// <summary>Copies the provider-side state (a "restart" gets a new process but the same real world).</summary>
    public void CopyFrom(F1FakeProviders other)
    {
        Meetings.AddRange(other.Meetings);
        ProviderSessions.AddRange(other.ProviderSessions);
        foreach (var (key, list) in other.Events)
            Events[key] = [.. list];
        foreach (var (key, result) in other.ResultsByRef)
            ResultsByRef[key] = result;
        Drivers = other.Drivers;
        Constructors = other.Constructors;
        LifecycleConfigured = other.LifecycleConfigured;
    }

    public void AddEvent(string providerRef, F1LifecycleSignal signal, DateTimeOffset at, int? phase = null)
    {
        if (!Events.TryGetValue(providerRef, out var list))
            Events[providerRef] = list = [];
        list.Add(new F1LifecycleEvent(LifecycleId, providerRef, signal, at, phase));
    }

    /// <summary>Adds a meeting and matching provider sessions (ref = "P-" + session key).</summary>
    public F1Meeting AddMeeting(int season, int round, params (F1SessionType Type, DateTimeOffset Start)[] sessions)
    {
        var meeting = new F1Meeting(season, round, "Test Grand Prix " + round, null, "Test Circuit", "Testland", "Test City", "test-" + round,
            sessions.Select(s => new F1Session(season, round, s.Type, s.Start)).ToList());
        Meetings.Add(meeting);
        foreach (var s in meeting.Sessions)
            ProviderSessions.Add(new F1ProviderSession(LifecycleId, Ref(s), season, s.Type, s.ScheduledStartUtc, s.PlannedEndUtc, false));
        return meeting;
    }

    public static string Ref(F1Session session) => "P-" + session.Key;

    public Task<F1ProviderResult<F1SeasonSchedule>> GetScheduleAsync(int season, CancellationToken cancellationToken)
    {
        ScheduleCalls++;
        if (ScheduleFailure is { } f)
            return Task.FromResult(F1ProviderResult<F1SeasonSchedule>.Fail(f, "scripted", clock.GetUtcNow()));
        return Task.FromResult(F1ProviderResult<F1SeasonSchedule>.Ok(new F1SeasonSchedule(season, "fakeschedule", Meetings.Where(m => m.Season == season).ToList()), clock.GetUtcNow()));
    }

    public Task<F1ProviderResult<IReadOnlyList<F1ProviderSession>>> GetSessionsAsync(int season, CancellationToken cancellationToken) =>
        Task.FromResult(F1ProviderResult<IReadOnlyList<F1ProviderSession>>.Ok(ProviderSessions.Where(s => s.Season == season).ToList(), clock.GetUtcNow()));

    public Task<F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>> GetLifecycleEventsAsync(string providerSessionRef, CancellationToken cancellationToken)
    {
        LifecycleCalls++;
        if (LifecycleFailure is { } f)
            return Task.FromResult(F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>.Fail(f, "scripted", clock.GetUtcNow()));
        var now = clock.GetUtcNow();
        IReadOnlyList<F1LifecycleEvent> visible = Events.GetValueOrDefault(providerSessionRef)?.Where(e => e.OccurredAt <= now).OrderBy(e => e.OccurredAt).ToList() ?? [];
        return Task.FromResult(F1ProviderResult<IReadOnlyList<F1LifecycleEvent>>.Ok(visible, now));
    }

    public Task<F1ProviderResult<F1SessionResult>> GetResultAsync(F1Session session, string providerSessionRef, CancellationToken cancellationToken)
    {
        ResultCalls++;
        if (ResultsFailure is { } f)
            return Task.FromResult(F1ProviderResult<F1SessionResult>.Fail(f, "scripted", clock.GetUtcNow(), TimeSpan.FromMinutes(1)));
        return Task.FromResult(F1ProviderResult<F1SessionResult>.Ok(ResultsByRef.GetValueOrDefault(providerSessionRef), clock.GetUtcNow()));
    }

    public Task<F1ProviderResult<F1StandingsSnapshot>> GetStandingsAsync(F1StandingsKind kind, int season, CancellationToken cancellationToken)
    {
        StandingsCalls++;
        if (StandingsFailure is { } f)
            return Task.FromResult(F1ProviderResult<F1StandingsSnapshot>.Fail(f, "scripted", clock.GetUtcNow()));
        var value = kind == F1StandingsKind.Drivers ? Drivers : Constructors;
        value ??= new F1StandingsSnapshot(kind, season, null, "fakestandings", [], []);
        return Task.FromResult(F1ProviderResult<F1StandingsSnapshot>.Ok(value, clock.GetUtcNow()));
    }

    /// <summary>A classification of <paramref name="entries"/> drivers; the session roster has <paramref name="roster"/> drivers (default: the same).</summary>
    public static F1SessionResult Result(F1Session session, int entries = 20, int swapFirstTwo = 0, int? roster = null)
    {
        var practice = F1SessionTypes.IsPractice(session.Type);
        var rows = Enumerable.Range(1, entries).Select(i => new F1DriverResult(i, i, "Driver " + i, "D" + i, "Team " + ((i + 1) / 2), F1ResultStatus.Classified, 50,
            practice ? 90 + (0.1 * i) : 5400 + i, i == 1 ? 0 : i * 0.5, null, null)).ToList();
        if (swapFirstTwo > 0)
        {
            // A steward penalty: the winner drops to second.
            rows[0] = rows[0] with { Position = 2, GapSeconds = 1.0 };
            rows[1] = rows[1] with { Position = 1, GapSeconds = 0 };
        }

        return new F1SessionResult(session.Key, session.Type, ResultsId, rows, Enumerable.Range(1, roster ?? entries).ToList());
    }

    public static F1StandingsSnapshot DriverTable(int season, int? round, params (string Id, decimal Points)[] rows) =>
        new(F1StandingsKind.Drivers, season, round, "fakestandings",
            rows.Select((r, i) => new F1DriverStanding(i + 1, r.Id, "Driver " + r.Id, null, "Team", r.Points, 0)).ToList(), []);

    public static F1StandingsSnapshot ConstructorTable(int season, int? round, params (string Id, decimal Points)[] rows) =>
        new(F1StandingsKind.Constructors, season, round, "fakestandings", [],
            rows.Select((r, i) => new F1ConstructorStanding(i + 1, r.Id, "Team " + r.Id, r.Points, 0)).ToList());
}

/// <summary>Live transport fake: tests push events, force disconnects and auth failures, and count connections.</summary>
public sealed class FakeLiveTransport : IF1LiveTransport
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _drop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<F1LifecycleEvent, CancellationToken, Task>? _onEvent;

    public string Id => F1FakeProviders.LifecycleId;
    public bool IsConfigured { get; set; } = true;
    public int Connections { get; private set; }
    public int Active { get; private set; }

    /// <summary>Simulates a stalled network path: the connection ignores cancellation until <see cref="Drop"/> is called.</summary>
    public bool IgnoreCancellation { get; set; }
    public Queue<Exception> ConnectFailures { get; } = new();

    public async Task RunConnectionAsync(Func<F1LifecycleEvent, CancellationToken, Task> onEvent, Action onConnected, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Connections++;
            if (ConnectFailures.TryDequeue(out var failure))
                throw failure;
            _onEvent = onEvent;
            _drop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Active++;
        }

        try
        {
            onConnected();
            if (IgnoreCancellation)
                await _drop.Task;
            else
                await _drop.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                Active--;
                _onEvent = null;
            }
        }
    }

    public Task PushAsync(F1LifecycleEvent e) => (_onEvent ?? throw new InvalidOperationException("not connected"))(e, CancellationToken.None);

    /// <summary>Ends the current connection as if the server closed it.</summary>
    public void Drop() => _drop.TrySetResult();
}

public static class F1TestHostExtensions
{
    public static readonly GuildPermission ChannelPermissions =
        GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory;

    /// <summary>Test host whose four F1 providers are one scriptable fake (plus an optional live transport).</summary>
    public static async Task<(TestHost Host, F1FakeProviders Fake)> CreateF1HostAsync(Dictionary<string, string?>? overrides = null, DateTimeOffset? start = null,
        IF1LiveTransport? live = null, F1FakeProviders? copyFrom = null, Action<IServiceCollection>? extra = null)
    {
        F1FakeProviders? fake = null;
        var host = await TestHost.CreateAsync(overrides, start, services =>
        {
            services.AddSingleton(sp => fake ??= new F1FakeProviders(sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IF1ScheduleProvider>(sp => sp.GetRequiredService<F1FakeProviders>());
            services.AddSingleton<IF1LifecycleProvider>(sp => sp.GetRequiredService<F1FakeProviders>());
            services.AddSingleton<IF1ResultsProvider>(sp => sp.GetRequiredService<F1FakeProviders>());
            services.AddSingleton<IF1StandingsProvider>(sp => sp.GetRequiredService<F1FakeProviders>());
            if (live is not null)
                services.AddSingleton(live);
            extra?.Invoke(services);
        });
        var created = host.Services.GetRequiredService<F1FakeProviders>();
        if (copyFrom is not null)
            created.CopyFrom(copyFrom);
        return (host, created);
    }

    /// <summary>Formula 1 guild: usable channel, optional ping role, module enabled.</summary>
    public static async Task SetUpF1GuildAsync(this TestHost host, GuildId guild, ChannelId channel, RoleId? pingRole = null)
    {
        var roles = pingRole is { } r ? new[] { new RoleInfo(r, "F1 fans", 3, GuildPermission.None, false, false, true) } : [];
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild, roles));
        host.Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true, ChannelPermissions));
        await host.InScopeAsync(async sp =>
        {
            var admin = TestHost.Admin(guild);
            var config = sp.GetRequiredService<Formula1ConfigService>();
            (await config.SetChannelAsync(admin, channel.Value, CancellationToken.None)).Succeeded.Should().BeTrue();
            if (pingRole is { } role)
                (await config.SetRoleAsync(admin, role.Value, pingOnStarts: true, pingOnResults: false, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "formula1", true, CancellationToken.None)).Succeeded.Should().BeTrue();
        });
    }
}
