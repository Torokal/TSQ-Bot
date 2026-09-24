namespace ToroSquad.Core.Messaging;

public enum PermanentFailureKind
{
    MissingPermissions = 1,
    UnknownChannel = 2,
    UnknownMessage = 3,
    InvalidPayload = 4,
    MissingAccess = 5,
    Other = 99,
}

/// <summary>
/// Result of a send/edit. <see cref="Ambiguous"/> means Discord may or may not have accepted the message
/// (e.g. HTTP timeout after the request was written) — callers must not blindly resend.
/// </summary>
public abstract record SendOutcome
{
    public sealed record Sent(MessageId MessageId) : SendOutcome;
    public sealed record RateLimited(TimeSpan RetryAfter) : SendOutcome;
    public sealed record Transient(string Reason) : SendOutcome;
    public sealed record Permanent(PermanentFailureKind Kind, string Reason) : SendOutcome;
    public sealed record Ambiguous(string Reason) : SendOutcome;
}

public abstract record ReconcileOutcome
{
    public sealed record Found(MessageId MessageId) : ReconcileOutcome;
    public sealed record NotFound : ReconcileOutcome;
    public sealed record NotPossible(string Reason) : ReconcileOutcome;
}

/// <summary>
/// Delivery boundary to Discord. Implementations: Discord.Net (real), Fake (tests/dev), DryRun.
/// Implementations MUST honour <see cref="OutgoingMessage.Mentions"/> as the complete allow-list and MUST
/// never ping on edits.
/// </summary>
public interface IMessageTransport
{
    string Name { get; }

    Task<SendOutcome> SendAsync(ChannelId channel, OutgoingMessage message, CancellationToken cancellationToken);

    Task<SendOutcome> EditAsync(ChannelId channel, MessageId message, OutgoingMessage content, CancellationToken cancellationToken);

    /// <summary>
    /// Bounded reconciliation for ambiguous deliveries: scan the last <paramref name="scanLimit"/> messages
    /// authored by the bot for the marker (we put it in the embed footer).
    /// </summary>
    Task<ReconcileOutcome> FindRecentByMarkerAsync(ChannelId channel, string marker, int scanLimit, CancellationToken cancellationToken);
}
