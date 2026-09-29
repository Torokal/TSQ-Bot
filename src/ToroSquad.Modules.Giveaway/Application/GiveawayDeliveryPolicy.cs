using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>
/// A started giveaway is a promise to the members who entered: /modules disable giveaway stops new giveaways and the
/// commands, never a draw that is already due. So the winner announcements (every giveaway kind) are delivered even while
/// the module is disabled; everything else about the outbox (allow-list, at most once, never late) is unchanged.
/// </summary>
public sealed class GiveawayDeliveryPolicy(ILogger<GiveawayDeliveryPolicy> logger) : IDeliveryPolicy
{
    public ModuleId Module => GiveawayModule.ModuleIdTyped;

    public Task<DeliveryDecision> CanDeliverAsync(GuildId guild, ChannelId channel, string kind, CancellationToken cancellationToken) =>
        Task.FromResult(DeliveryDecision.Allowed);

    public bool DeliversWhileModuleDisabled(string kind) => kind.StartsWith("giveaway-", StringComparison.Ordinal);

    public Task ReportChannelProblemAsync(GuildId guild, ChannelId channel, PermanentFailureKind kind, CancellationToken cancellationToken)
    {
        logger.LogWarning("Giveaway announcement: channel {Channel} in guild {Guild} is unusable ({Kind})", channel, guild, kind);
        return Task.CompletedTask;
    }
}
