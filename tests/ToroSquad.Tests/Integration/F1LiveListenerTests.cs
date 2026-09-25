using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;
using ToroSquad.Tests.Support;
using static ToroSquad.Tests.Integration.F1LifecycleIntegrationTests;

namespace ToroSquad.Tests.Integration;

/// <summary>Live lifecycle connection resilience: reconnects, backoff, duplicates, cancellation and host shutdown.</summary>
public sealed class F1LiveListenerTests
{
    private static readonly DateTimeOffset At = new(2030, 6, 9, 7, 0, 0, TimeSpan.Zero);

    private static (Formula1LiveListener Listener, FakeLiveTransport Transport, FakeTimeProvider Clock, Formula1Cache Cache) Create(bool configured = true)
    {
        var clock = new FakeTimeProvider(At);
        var transport = new FakeLiveTransport { IsConfigured = configured };
        var cache = new Formula1Cache();
        return (new Formula1LiveListener(transport, cache, clock, NullLogger<Formula1LiveListener>.Instance), transport, clock, cache);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Action? tick = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not reached");
            tick?.Invoke();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static F1LifecycleEvent E(F1LifecycleSignal signal, int minute) => new(F1FakeProviders.LifecycleId, "P-1", signal, At.AddMinutes(minute));

    private static List<F1LifecycleEvent> Drain(Formula1LiveListener listener)
    {
        var list = new List<F1LifecycleEvent>();
        while (listener.Events.TryRead(out var e))
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task Connects_once_signals_reconciliation_and_suppresses_duplicate_events()
    {
        var (listener, transport, _, cache) = Create();
        await using var _ = listener;
        listener.EnsureRunning(CancellationToken.None);
        listener.EnsureRunning(CancellationToken.None);
        await WaitUntilAsync(() => transport.Active == 1);
        transport.Connections.Should().Be(1, "never a second listener");
        cache.Live.State.Should().Be(F1LiveState.Connected);
        listener.ConsumeReconnectSignal().Should().BeTrue();
        listener.ConsumeReconnectSignal().Should().BeFalse("signalled once per connection");

        await transport.PushAsync(E(F1LifecycleSignal.Started, 3));
        await transport.PushAsync(E(F1LifecycleSignal.Started, 3));
        await transport.PushAsync(E(F1LifecycleSignal.Suspended, 40));
        Drain(listener).Select(e => e.Signal).Should().Equal(F1LifecycleSignal.Started, F1LifecycleSignal.Suspended);
    }

    [Fact]
    public async Task A_dropped_connection_reconnects_and_asks_for_reconciliation()
    {
        var (listener, transport, clock, cache) = Create();
        await using var _ = listener;
        listener.EnsureRunning(CancellationToken.None);
        await WaitUntilAsync(() => transport.Active == 1);
        listener.ConsumeReconnectSignal();

        transport.Drop();
        await WaitUntilAsync(() => transport.Connections == 2 && transport.Active == 1, () => clock.Advance(TimeSpan.FromSeconds(1)));
        listener.ConsumeReconnectSignal().Should().BeTrue("events may have been missed while disconnected");
        cache.Live.Reconnects.Should().Be(1);
        cache.Live.State.Should().Be(F1LiveState.Connected);

        // An event replayed after the reconnect is still delivered once only.
        await transport.PushAsync(E(F1LifecycleSignal.Started, 3));
        await transport.PushAsync(E(F1LifecycleSignal.Started, 3));
        Drain(listener).Should().ContainSingle();
    }

    [Fact]
    public async Task Transient_failures_back_off_exponentially_and_recover()
    {
        var (listener, transport, clock, cache) = Create();
        await using var _ = listener;
        transport.ConnectFailures.Enqueue(new HttpRequestException("provider down"));
        transport.ConnectFailures.Enqueue(new TimeoutException());
        listener.EnsureRunning(CancellationToken.None);
        await WaitUntilAsync(() => cache.Live.State == F1LiveState.BackingOff);
        cache.Live.LastError.Should().Be(nameof(HttpRequestException));
        await WaitUntilAsync(() => transport.Active == 1, () => clock.Advance(TimeSpan.FromSeconds(2)));
        transport.Connections.Should().Be(3);
        cache.Live.State.Should().Be(F1LiveState.Connected);
    }

    [Fact]
    public async Task Authentication_failure_is_not_retried_aggressively()
    {
        var (listener, transport, clock, cache) = Create();
        await using var _ = listener;
        transport.ConnectFailures.Enqueue(new F1LiveAuthenticationException("bad token"));
        listener.EnsureRunning(CancellationToken.None);
        await WaitUntilAsync(() => cache.Live.State == F1LiveState.AuthFailed);
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(30)); // 10 minutes
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        transport.Connections.Should().Be(1, "no reconnect within the 15-minute auth backoff");
        await WaitUntilAsync(() => transport.Active == 1, () => clock.Advance(TimeSpan.FromMinutes(1)));
        transport.Connections.Should().Be(2);
    }

    [Fact]
    public async Task Stop_and_host_shutdown_close_the_connection_cleanly()
    {
        var (listener, transport, _, cache) = Create();
        listener.EnsureRunning(CancellationToken.None);
        await WaitUntilAsync(() => transport.Active == 1);
        await listener.StopAsync();
        transport.Active.Should().Be(0);
        listener.IsRunning.Should().BeFalse();
        cache.Live.State.Should().Be(F1LiveState.Idle);

        using var host = new CancellationTokenSource();
        listener.EnsureRunning(host.Token);
        await WaitUntilAsync(() => transport.Active == 1);
        await host.CancelAsync(); // host shutdown
        await WaitUntilAsync(() => transport.Active == 0 && !listener.IsRunning);
        await listener.DisposeAsync();
    }

    [Fact]
    public async Task Without_live_credentials_nothing_connects()
    {
        var (listener, transport, _, cache) = Create(configured: false);
        await using var _ = listener;
        listener.EnsureRunning(CancellationToken.None);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        transport.Connections.Should().Be(0);
        listener.IsRunning.Should().BeFalse();
        cache.Live.State.Should().Be(F1LiveState.NotConfigured);
    }

    [Fact]
    public async Task Poller_applies_live_events_opens_the_stream_only_around_sessions_and_never_duplicates_the_start()
    {
        var transport = new FakeLiveTransport();
        var (host, fake) = await F1TestHostExtensions.CreateF1HostAsync(Live, live: transport);
        await using var _ = host;
        var race = fake.AddMeeting(2026, 18, (F1SessionType.Race, T0.AddHours(3))).Sessions[0];
        await host.SetUpF1GuildAsync(Guild, Channel);

        await StepAsync(host, TimeSpan.Zero);
        transport.Connections.Should().Be(0, "no listener far from a session");

        await StepAsync(host, TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(30))); // 30 min before the start
        await WaitUntilAsync(() => transport.Active == 1);
        var listener = host.Services.GetRequiredService<Formula1LiveListener>();
        var restCallsBefore = fake.LifecycleCalls;

        var started = new F1LifecycleEvent(F1FakeProviders.LifecycleId, F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddHours(3).AddMinutes(2));
        await transport.PushAsync(started);
        await transport.PushAsync(started);
        host.Clock.Advance(TimeSpan.FromMinutes(33));
        await TickAsync(host);
        await DeliverAsync(host);
        (await OutboxAsync(host, "started:")).Should().ContainSingle();

        // Reconnect: REST reconciliation runs (it returns the same start — still one notification).
        fake.AddEvent(F1FakeProviders.Ref(race), F1LifecycleSignal.Started, T0.AddHours(3).AddMinutes(2));
        transport.Drop();
        await WaitUntilAsync(() => transport.Connections == 2 && transport.Active == 1, () => host.Clock.Advance(TimeSpan.FromSeconds(1)));
        await TickAsync(host);
        await DeliverAsync(host);
        fake.LifecycleCalls.Should().BeGreaterThan(restCallsBefore);
        (await OutboxAsync(host, "started:")).Should().ContainSingle();
        host.Transport.Messages.Should().ContainSingle();

        // Session finished and long over → the stream is closed.
        await transport.PushAsync(new F1LifecycleEvent(F1FakeProviders.LifecycleId, F1FakeProviders.Ref(race), F1LifecycleSignal.Finished, T0.AddHours(5)));
        await StepAsync(host, TimeSpan.FromHours(2));
        await StepAsync(host, TimeSpan.FromMinutes(1));
        await WaitUntilAsync(() => transport.Active == 0);
        listener.IsRunning.Should().BeFalse();
    }
}
