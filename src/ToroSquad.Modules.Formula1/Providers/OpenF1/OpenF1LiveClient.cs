using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using ToroSquad.Modules.Formula1.Domain;

namespace ToroSquad.Modules.Formula1.Providers.OpenF1;

/// <summary>
/// One OpenF1 MQTT connection (documented backend channel: mqtt.openf1.org:8883, TLS, OAuth2 access token as the MQTT
/// password, topics = REST paths; we subscribe only to <see cref="OpenF1Topics.RaceControl"/>). The connection is ended
/// cleanly shortly before the token expires so the listener reconnects with a fresh token. Delivers normalized
/// lifecycle events only; it never talks to Discord and never decides notifications.
/// </summary>
public sealed class OpenF1LiveClient(OpenF1TokenProvider tokens, IOptions<OpenF1Options> options, TimeProvider clock, ILogger<OpenF1LiveClient> logger) : IF1LiveTransport
{
    public string Id => OpenF1Parser.Source;

    public bool IsConfigured => tokens.IsConfigured;

    public async Task RunConnectionAsync(Func<F1LifecycleEvent, CancellationToken, Task> onEvent, Action onConnected, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var token = await tokens.GetAsync(cancellationToken);
        if (token.Outcome == F1ProviderOutcome.AuthFailed)
            throw new F1LiveAuthenticationException("OpenF1 rejected the credentials (" + token.Detail + ")");
        if (!token.HasData)
            throw new InvalidOperationException("OpenF1 token unavailable: " + token.Outcome);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DisconnectedAsync += _ =>
        {
            ended.TrySetResult();
            return Task.CompletedTask;
        };
        client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                var (lifecycle, _) = OpenF1Parser.ParseMqttMessage(e.ApplicationMessage.Topic, payload);
                if (lifecycle is not null)
                    await onEvent(lifecycle, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad message never kills the connection.
                logger.LogWarning("OpenF1 MQTT message ignored: {Error}", ex.GetType().Name);
            }
        };

        var mqttOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(o.MqttHost, o.MqttPort)
            .WithTlsOptions(tls => tls.UseTls())
            .WithCredentials(o.Username, token.Value)
            .WithClientId("tsqbot-" + Guid.NewGuid().ToString("N")[..12])
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds))
            .Build();

        MqttClientConnectResult connect;
        try
        {
            // Bounded even if the network path stalls (e.g. a TLS handshake that never completes).
            connect = await RunBoundedAsync(t => client.ConnectAsync(mqttOptions, t), OperationTimeout(o), cancellationToken);
        }
        catch (MQTTnet.Adapter.MqttConnectingFailedException ex) when (IsAuthRefusal(ex.Message))
        {
            // MQTTnet reports the CONNACK reason in the message ("... failed (NotAuthorized)").
            tokens.Invalidate();
            throw new F1LiveAuthenticationException("OpenF1 MQTT refused the token");
        }

        if (connect.ResultCode is MqttClientConnectResultCode.NotAuthorized or MqttClientConnectResultCode.BadUserNameOrPassword)
        {
            tokens.Invalidate();
            throw new F1LiveAuthenticationException("OpenF1 MQTT refused the token (" + connect.ResultCode + ")");
        }

        if (connect.ResultCode != MqttClientConnectResultCode.Success)
            throw new InvalidOperationException("OpenF1 MQTT connect failed: " + connect.ResultCode);

        await RunBoundedAsync(t => client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder().WithTopicFilter(OpenF1Topics.RaceControl).Build(), t),
            OperationTimeout(o), cancellationToken);
        onConnected();

        // Reconnect with a fresh token before the current one expires.
        var lifetime = (tokens.ValidUntil ?? clock.GetUtcNow().AddMinutes(50)) - clock.GetUtcNow();
        if (lifetime < TimeSpan.FromMinutes(1))
            lifetime = TimeSpan.FromMinutes(1);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var expiry = Task.Delay(lifetime, clock, linked.Token);
        await Task.WhenAny(ended.Task, expiry);
        await linked.CancelAsync();
        if (client.IsConnected)
        {
            // Graceful disconnect is best effort and bounded: a stalled network never blocks stop or host shutdown.
            // Disposing the client afterwards closes the socket regardless.
            if (!await DisconnectBoundedAsync(t => client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), t), DisconnectTimeout))
                logger.LogWarning("OpenF1 MQTT disconnect did not complete within {Timeout}; closing the socket", DisconnectTimeout);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Upper bound for a graceful MQTT disconnect.</summary>
    public static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);

    private static TimeSpan OperationTimeout(OpenF1Options o) => TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds));

    /// <summary>
    /// Runs a network operation with a hard upper bound: its token is cancelled after <paramref name="timeout"/>, and even
    /// an operation that ignores its token no longer blocks the caller (TimeoutException; the abandoned task is observed).
    /// Caller cancellation is honoured immediately.
    /// </summary>
    public static async Task<T> RunBoundedAsync<T>(Func<CancellationToken, Task<T>> operation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var task = operation(cts.Token);
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = await Task.WhenAny(task, Task.Delay(timeout + TimeSpan.FromSeconds(1), guard.Token));
        if (finished != task)
        {
            Observe(task);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"MQTT operation did not complete within {timeout.TotalSeconds:0} s");
        }

        await guard.CancelAsync();
        try
        {
            return await task;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MQTT operation cancelled after {timeout.TotalSeconds:0} s");
        }
    }

    /// <summary>Bounded best-effort disconnect: true when it completed in time, false otherwise (never throws, never hangs).</summary>
    public static async Task<bool> DisconnectBoundedAsync(Func<CancellationToken, Task> disconnect, TimeSpan timeout)
    {
        try
        {
            await RunBoundedAsync(async t =>
            {
                await disconnect(t);
                return true;
            }, timeout, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static bool IsAuthRefusal(string message) =>
        message.Contains(nameof(MqttClientConnectResultCode.NotAuthorized), StringComparison.OrdinalIgnoreCase) ||
        message.Contains(nameof(MqttClientConnectResultCode.BadUserNameOrPassword), StringComparison.OrdinalIgnoreCase);
}
