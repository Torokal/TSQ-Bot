using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

/// <summary>
/// Owns the (at most one) live lifecycle connection. Started only while a session is in its active window and stopped
/// afterwards; reconnects with bounded exponential backoff; authentication failures back off for a long time instead of
/// hammering the provider; never throws into the host. It only queues normalized events — the poller applies them to
/// persisted state, and only the planner + outbox ever lead to Discord messages.
/// <para>At most one connection, always: the listener never forgets a loop that is still running. A stop that does not
/// finish within <see cref="StopTimeout"/> leaves the loop registered in state <see cref="F1LiveState.Stopping"/> (visible in
/// doctor/health); <see cref="EnsureRunning"/> refuses to start another connection until that loop has really ended.
/// Stopping itself is bounded, so host shutdown never hangs on a stalled network path.</para>
/// </summary>
public sealed class Formula1LiveListener(IF1LiveTransport transport, Formula1Cache cache, TimeProvider clock, ILogger<Formula1LiveListener> logger) : IAsyncDisposable
{
    public static readonly TimeSpan AuthFailureBackoff = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private const int DedupeCapacity = 4096;

    private readonly Channel<F1LifecycleEvent> _events = Channel.CreateBounded<F1LifecycleEvent>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Lock _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _stopRequested;
    private int _reconnected;
    private F1LiveStatus _status = Publish(cache, F1LiveStatus.Initial(transport.IsConfigured));

    public ChannelReader<F1LifecycleEvent> Events => _events.Reader;

    public bool IsConfigured => transport.IsConfigured;

    /// <summary>How long <see cref="StopAsync"/> waits for the connection to close before reporting it as still stopping.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A connection loop is alive and not asked to stop.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _loop is { IsCompleted: false } && _cts is { IsCancellationRequested: false }; }
    }

    /// <summary>A stop was requested but the previous loop has not ended yet.</summary>
    public bool IsStopping
    {
        get { lock (_gate) return _loop is { IsCompleted: false } && _cts is { IsCancellationRequested: true }; }
    }

    public F1LiveStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>True once after every (re)connect: the caller then reconciles over REST to catch events missed while offline.</summary>
    public bool ConsumeReconnectSignal() => Interlocked.Exchange(ref _reconnected, 0) == 1;

    /// <summary>
    /// Starts the connection loop if none is alive. Never a second listener: while a previous loop is still running — or
    /// still stopping — nothing new is started.
    /// </summary>
    public void EnsureRunning(CancellationToken hostStopping)
    {
        lock (_gate)
        {
            if (!transport.IsConfigured)
            {
                SetStatus(_status with { State = F1LiveState.NotConfigured });
                return;
            }

            if (_loop is { IsCompleted: false })
            {
                if (_cts is { IsCancellationRequested: true })
                    logger.LogDebug("F1 live listener: previous connection still closing; not starting another one");
                return;
            }

            _cts?.Dispose();
            _stopRequested = false;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
            var token = _cts.Token;
            _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            logger.LogInformation("F1 live listener started ({Transport})", transport.Id);
        }
    }

    /// <summary>
    /// Requests the stop and waits at most <see cref="StopTimeout"/>. If the connection has not closed by then, the loop
    /// stays registered (state <see cref="F1LiveState.Stopping"/>) and becomes Idle only when it really ends — so no second
    /// connection can be opened meanwhile. Calling it again while stopping returns immediately (bounded shutdown).
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        bool alreadyStopping;
        lock (_gate)
        {
            loop = _loop;
            if (loop is null || loop.IsCompleted)
            {
                SetStatus(_status with { State = transport.IsConfigured ? F1LiveState.Idle : F1LiveState.NotConfigured });
                return;
            }

            // Host shutdown may already have cancelled the token; the first stop still waits (bounded) and reports.
            alreadyStopping = _stopRequested;
            _stopRequested = true;
            _cts?.Cancel();
        }

        if (alreadyStopping)
            return; // the first stop is already waiting for (or has given up on) this loop

        try
        {
            await loop.WaitAsync(StopTimeout);
        }
        catch (TimeoutException)
        {
            lock (_gate)
                SetStatus(_status with { State = F1LiveState.Stopping, LastError = "connection still closing after stop timeout" });
            logger.LogWarning("F1 live listener: connection did not close within {Timeout}; no new connection until it has", StopTimeout);
            _ = loop.ContinueWith(_ => MarkStopped(loop), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
            // the loop ended by cancellation
        }

        MarkStopped(loop);
        logger.LogInformation("F1 live listener stopped");
    }

    private void MarkStopped(Task loop)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_loop, loop))
                return;
            _loop = null;
            SetStatus(_status with { State = transport.IsConfigured ? F1LiveState.Idle : F1LiveState.NotConfigured });
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            Update(s => s with { State = F1LiveState.Connecting });
            TimeSpan wait;
            try
            {
                await transport.RunConnectionAsync(OnEventAsync, () =>
                {
                    failures = 0;
                    Interlocked.Exchange(ref _reconnected, 1);
                    Update(s => s with
                    {
                        State = F1LiveState.Connected,
                        LastConnectedAt = clock.GetUtcNow(),
                        Reconnects = everConnected ? s.Reconnects + 1 : s.Reconnects,
                        LastError = null,
                    });
                    if (everConnected)
                        logger.LogInformation("F1 live connection re-established");
                    else
                        logger.LogInformation("F1 live connection opened");
                    everConnected = true;
                }, ct);
                // Clean end (token rotation or server close): reconnect promptly but never in a tight loop.
                wait = TimeSpan.FromSeconds(2);
                Update(s => s with { State = F1LiveState.BackingOff, LastDisconnectedAt = clock.GetUtcNow() });
                logger.LogInformation("F1 live connection closed; reconnecting");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (F1LiveAuthenticationException ex)
            {
                wait = AuthFailureBackoff;
                Update(s => s with { State = F1LiveState.AuthFailed, LastDisconnectedAt = clock.GetUtcNow(), LastError = ex.Message });
                logger.LogWarning("F1 live connection: authentication failed; retrying in {Wait}", wait);
            }
            catch (Exception ex)
            {
                failures++;
#pragma warning disable CA5394 // jitter, not security relevant
                var seconds = Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures, 9))) * (0.8 + (Random.Shared.NextDouble() * 0.4));
#pragma warning restore CA5394
                wait = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, seconds));
                Update(s => s with { State = F1LiveState.BackingOff, LastDisconnectedAt = clock.GetUtcNow(), LastError = ex.GetType().Name });
                logger.LogWarning("F1 live connection failed ({Error}); backing off {Wait}", ex.GetType().Name, wait);
            }

            try
            {
                await Task.Delay(wait, clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Queues an event; exact repeats (same session, signal, provider time, segment) are dropped here already.</summary>
    public async Task OnEventAsync(F1LifecycleEvent e, CancellationToken ct)
    {
        var key = string.Join('|', e.ProviderId, e.ProviderSessionRef, (int)e.Signal, e.OccurredAt.UtcTicks, e.QualifyingPhase);
        lock (_gate)
        {
            if (!_seen.Add(key))
            {
                logger.LogDebug("F1 live: duplicate event suppressed");
                return;
            }

            _seenOrder.Enqueue(key);
            while (_seenOrder.Count > DedupeCapacity)
                _seen.Remove(_seenOrder.Dequeue());
        }

        await _events.Writer.WriteAsync(e, ct);
    }

    private static F1LiveStatus Publish(Formula1Cache cache, F1LiveStatus status)
    {
        cache.SetLive(status);
        return status;
    }

    private void Update(Func<F1LiveStatus, F1LiveStatus> change)
    {
        lock (_gate)
            SetStatus(change(_status));
    }

    private void SetStatus(F1LiveStatus status)
    {
        _status = status;
        cache.SetLive(status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (_gate)
        {
            // A loop that is still closing keeps using its token: never dispose it underneath.
            if (_loop is null or { IsCompleted: true })
                _cts?.Dispose();
        }
    }
}

/// <summary>Live transport used when no streaming provider is available (lifecycle is then REST-only or unavailable).</summary>
public sealed class NoLiveTransport : IF1LiveTransport
{
    public string Id => "none";
    public bool IsConfigured => false;

    public Task RunConnectionAsync(Func<F1LifecycleEvent, CancellationToken, Task> onEvent, Action onConnected, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No live transport configured.");
}
