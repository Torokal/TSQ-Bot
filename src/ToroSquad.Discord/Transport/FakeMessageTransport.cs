using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;

namespace ToroSquad.Discord.Transport;

/// <summary>
/// In-process transport for local development and tests. Nothing leaves the machine. Outcomes can be scripted to
/// simulate 403/404/429/timeouts/crashes. It is never presented as a live Discord delivery.
/// </summary>
public sealed class FakeMessageTransport(ILogger<FakeMessageTransport>? logger = null) : IMessageTransport
{
    private readonly ILogger _logger = logger ?? NullLogger<FakeMessageTransport>.Instance;
    private readonly ConcurrentQueue<Func<SendOutcome>> _scriptedSends = new();
    private readonly ConcurrentQueue<Func<SendOutcome>> _scriptedEdits = new();
    private readonly List<FakeMessage> _messages = [];
    private readonly object _gate = new();
    private long _nextId = 1_000_000_000_000_000;

    public sealed record FakeMessage(ChannelId Channel, MessageId Id, OutgoingMessage Message, bool Pinged, List<OutgoingMessage> Edits);

    public string Name => "fake";

    public ReconcileOutcome? ScriptedReconcile { get; set; }

    public IReadOnlyList<FakeMessage> Messages
    {
        get
        {
            lock (_gate)
                return _messages.ToList();
        }
    }

    public int SendCalls { get; private set; }
    public int EditCalls { get; private set; }

    /// <summary>Queue an outcome for the next send. <see cref="SendOutcome.Sent"/> placeholders are replaced by real ids.</summary>
    public void ScriptSend(Func<SendOutcome> outcome) => _scriptedSends.Enqueue(outcome);

    /// <summary>Simulates "Discord accepted the message but the HTTP response was lost" (ambiguous timeout).</summary>
    public void ScriptAcceptedButTimedOut() => _scriptedSends.Enqueue(() => AcceptedButLost());

    public void ScriptEdit(Func<SendOutcome> outcome) => _scriptedEdits.Enqueue(outcome);

    public Task<SendOutcome> SendAsync(ChannelId channel, OutgoingMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SendCalls++;
        _pendingChannel = channel;
        _pendingMessage = message;
        if (_scriptedSends.TryDequeue(out var scripted))
        {
            var outcome = scripted();
            if (outcome is not SendOutcome.Sent)
                return Task.FromResult(outcome);
        }

        var id = Store(channel, message);
        _logger.LogInformation("[FAKE-TRANSPORT] sent to {Channel}: {Title} (roles pinged: {Roles})", channel, message.Embed?.Title ?? message.Content, message.Mentions.Roles.Count);
        return Task.FromResult<SendOutcome>(new SendOutcome.Sent(id));
    }

    public Task<SendOutcome> EditAsync(ChannelId channel, MessageId message, OutgoingMessage content, CancellationToken cancellationToken)
    {
        EditCalls++;
        if (_scriptedEdits.TryDequeue(out var scripted))
        {
            var outcome = scripted();
            if (outcome is not SendOutcome.Sent)
                return Task.FromResult(outcome);
        }

        lock (_gate)
        {
            var existing = _messages.FirstOrDefault(m => m.Id == message && m.Channel == channel);
            if (existing is null)
                return Task.FromResult<SendOutcome>(new SendOutcome.Permanent(PermanentFailureKind.UnknownMessage, "Unknown Message"));
            existing.Edits.Add(content);
        }

        return Task.FromResult<SendOutcome>(new SendOutcome.Sent(message));
    }

    /// <summary>Same matching as the Discord transport (fake ids carry no creation time, so NotBefore is not checked).</summary>
    public Task<ReconcileOutcome> FindRecentAsync(ChannelId channel, DeliveryProbe probe, int scanLimit, CancellationToken cancellationToken)
    {
        if (ScriptedReconcile is not null)
            return Task.FromResult(ScriptedReconcile);
        lock (_gate)
        {
            var match = _messages
                .Where(m => m.Channel == channel)
                .TakeLast(scanLimit)
                .Where(m => !probe.Exclude.Contains(m.Id))
                .LastOrDefault(m => MessageFingerprint.Of(m.Message) == probe.Fingerprint || probe.MatchesLegacyFooter(m.Message.Embed?.Footer));
            return Task.FromResult<ReconcileOutcome>(match is null ? new ReconcileOutcome.NotFound() : new ReconcileOutcome.Found(match.Id));
        }
    }

    /// <summary>Test helper: delete a message as a moderator would.</summary>
    public void DeleteMessage(MessageId id)
    {
        lock (_gate)
            _messages.RemoveAll(m => m.Id == id);
    }

    private ChannelId _pendingChannel;
    private OutgoingMessage? _pendingMessage;

    private SendOutcome AcceptedButLost()
    {
        Store(_pendingChannel, _pendingMessage!);
        return new SendOutcome.Ambiguous("simulated timeout after Discord accepted the message");
    }

    private MessageId Store(ChannelId channel, OutgoingMessage message)
    {
        lock (_gate)
        {
            var id = new MessageId((ulong)Interlocked.Increment(ref _nextId));
            _messages.Add(new FakeMessage(channel, id, message, message.Mentions.PingsAnything, []));
            return id;
        }
    }
}
