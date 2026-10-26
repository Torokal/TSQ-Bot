using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>The two ways /ongoru gunluk answers the member (implemented by the Discord command class; faked in tests).</summary>
public interface IPredictionDailyResponder
{
    /// <summary>Private "thinking…" acknowledgement inside Discord's 3-second window; every interaction reply after it is private too.</summary>
    Task DeferPrivateAsync();

    /// <summary>Only the member who ran the command sees it; never pings.</summary>
    Task ReplyPrivateAsync(OperationResult result);
}

/// <summary>
/// /ongoru gunluk end to end. The interaction is acknowledged privately first (a private acknowledgement can never become
/// a public reply, and the claim must not race Discord's 3-second window); then the claim runs
/// (<see cref="PredictionEconomy.ClaimDailyAsync"/>: channel, module, once per Türkiye day, atomic). A refusal, a repeat
/// and the ceiling are answered privately and nothing is posted. A new claim is announced with ONE public bot message in
/// the channel it was used in (no mentions, the name as untrusted text) — sent once, never retried, an ambiguous send is
/// only looked for — and the member gets the private details. The coins are already committed before anything is posted:
/// when the announcement fails the member is told privately that the coins were added anyway, and nothing is credited
/// again.
/// </summary>
public sealed class PredictionDailyFlow(PredictionEconomy economy, IMessageTransport transport, TimeProvider clock, ILogger<PredictionDailyFlow> logger)
{
    /// <summary>How many of the bot's latest messages an ambiguous announcement is looked for in.</summary>
    public const int RecentMessages = 10;

    public async Task RunAsync(ActorContext actor, ChannelId here, string displayName, IPredictionDailyResponder responder, CancellationToken ct)
    {
        await responder.DeferPrivateAsync();
        var reply = await economy.ClaimDailyAsync(actor, here, displayName, ct);
        if (!reply.Result.Succeeded || reply.Announcement is not { } announcement)
        {
            await responder.ReplyPrivateAsync(reply.Result);
            return;
        }

        if (await AnnounceAsync(actor, here, announcement, ct))
        {
            await responder.ReplyPrivateAsync(reply.Result);
            return;
        }

        await responder.ReplyPrivateAsync(reply.Result with { MessageKey = "predictions.daily.done_unannounced" });
    }

    private async Task<bool> AnnounceAsync(ActorContext actor, ChannelId here, OutgoingMessage announcement, CancellationToken ct)
    {
        var attempt = clock.GetUtcNow();
        try
        {
            var outcome = await transport.SendAsync(here, announcement, ct);
            if (outcome is SendOutcome.Sent)
                return true;
            if (outcome is SendOutcome.Ambiguous)
            {
                var probe = new DeliveryProbe(MessageFingerprint.Of(announcement), attempt, new HashSet<MessageId>());
                if (await transport.FindRecentAsync(here, probe, RecentMessages, ct) is ReconcileOutcome.Found)
                    return true;
            }

            logger.LogWarning("prediction_daily_announce_failed guild={Guild} user={User} channel={Channel} outcome={Outcome}",
                actor.GuildId, actor.UserId, here, outcome.GetType().Name);
            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("prediction_daily_announce_failed guild={Guild} user={User} channel={Channel} outcome={Outcome}",
                actor.GuildId, actor.UserId, here, e.GetType().Name);
            return false;
        }
    }
}
