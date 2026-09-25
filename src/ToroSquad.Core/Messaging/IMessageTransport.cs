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
    /// Bounded reconciliation for ambiguous deliveries: scan the last <paramref name="scanLimit"/> messages authored by
    /// the bot for the one described by <paramref name="probe"/>. Nothing internal is shown to users for this.
    /// </summary>
    Task<ReconcileOutcome> FindRecentAsync(ChannelId channel, DeliveryProbe probe, int scanLimit, CancellationToken cancellationToken);
}

/// <summary>
/// What an ambiguous delivery looked like: the <see cref="MessageFingerprint"/> of exactly the message that was sent, the
/// earliest time it can have been created, and messages already owned by other deliveries (never matched). Messages sent
/// before the footer reference was removed still carry "ref &lt;marker&gt;" in the footer; <see cref="LegacyMarker"/>
/// finds those.
/// </summary>
public sealed record DeliveryProbe(string Fingerprint, DateTimeOffset NotBefore, IReadOnlySet<MessageId> Exclude, string? LegacyMarker = null)
{
    public bool MatchesLegacyFooter(string? footer) =>
        LegacyMarker is { Length: > 0 } marker && footer?.Contains("ref " + marker, StringComparison.Ordinal) == true;
}

/// <summary>
/// Content fingerprint of a message as Discord stores it (content, title, description, footer, timestamp to the second,
/// colour, fields). Used instead of a visible reference to recognise our own message after an ambiguous send.
/// </summary>
public static class MessageFingerprint
{
    public static string Of(OutgoingMessage message) => Compute(
        message.Content, message.Embed?.Title, message.Embed?.Description, message.Embed?.Footer, message.Embed?.Timestamp,
        message.Embed?.Color, message.Embed?.Fields.Select(f => (f.Name, f.Value)) ?? []);

    public static string Compute(string? content, string? title, string? description, string? footer, DateTimeOffset? timestamp, uint? color,
        IEnumerable<(string Name, string Value)> fields)
    {
        var text = new System.Text.StringBuilder();
        void Add(string? value) => text.Append((value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim()).Append('\u001F');
        Add(content);
        Add(title);
        Add(description);
        Add(footer);
        Add(timestamp?.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(color?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (name, value) in fields)
        {
            Add(name);
            Add(value);
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }
}
