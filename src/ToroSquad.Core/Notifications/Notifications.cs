using System.Globalization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;

namespace ToroSquad.Core.Notifications;

/// <summary>
/// A notification a module wants delivered to a configured guild channel. The logical key
/// (guild + module + source item + channel + kind) is unique in the outbox, so re-polling, restarts and
/// "two followed teams in the same match" can never produce a second message for the same thing.
/// </summary>
public sealed record NotificationRequest(
    GuildId Guild,
    ModuleId Module,
    string SourceKey,
    ChannelId Channel,
    string Kind,
    OutgoingMessage Message,
    DateTimeOffset ExpiresAt,
    bool IsDryRun)
{
    public string LogicalKey => BuildLogicalKey(Guild, Module, SourceKey, Channel, Kind, IsDryRun);

    public static string BuildLogicalKey(GuildId guild, ModuleId module, string sourceKey, ChannelId channel, string kind, bool dryRun) =>
        string.Create(CultureInfo.InvariantCulture, $"{(dryRun ? "dry" : "live")}|{guild.Value}|{module.Value}|{sourceKey}|{channel.Value}|{kind}");
}

public enum StageOutcome
{
    Created = 0,
    UpdatedPending = 1,
    EditScheduled = 2,
    Unchanged = 3,
    IgnoredTerminal = 4,
}

public enum OutboxStatus
{
    Pending = 0,
    InFlight = 1,
    Sent = 2,
    Failed = 3,
    DeliveryUnknown = 4,
    Cancelled = 5,
    Expired = 6,
    Simulated = 7,
}

/// <summary>
/// Stages notifications into the persistent outbox within the caller's unit of work (same DB transaction as
/// the module's own state changes). The caller commits via <see cref="IUnitOfWork"/>.
/// </summary>
public interface INotificationOutbox
{
    Task<StageOutcome> StageAsync(NotificationRequest request, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public abstract record DeliveryDecision
{
    public sealed record Allow : DeliveryDecision;

    /// <summary>Do not deliver now or later (module disabled, paused, channel changed). Stored with a reason.</summary>
    public sealed record Cancel(string Reason) : DeliveryDecision;

    public static DeliveryDecision Allowed { get; } = new Allow();
}

/// <summary>
/// Module-specific last-moment check executed immediately before each send/edit (pause, channel still
/// configured, etc.). The module gate (enabled/disabled) is checked by the dispatcher itself.
/// </summary>
public interface IDeliveryPolicy
{
    ModuleId Module { get; }
    Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken);

    /// <summary>Called when Discord reports the channel unusable (403/404) so the module can surface it in doctor.</summary>
    Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken);
}
