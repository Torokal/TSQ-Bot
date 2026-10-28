using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Pfp.Application;

/// <summary>
/// What /pfp and /banner do, in one place: the fast checks first — the <c>tur</c> value, then the shared cooldown — whose
/// refusals are private and come before any acknowledgement; then ONE public acknowledgement (the REST reads may exceed
/// Discord's 3-second window), the resolution (<see cref="PfpResolver"/>) inside <see cref="LookupBudget"/>, and ONE public
/// completion of that same response with the card or a short notice. Every path after the acknowledgement completes it — a
/// failed or timed-out read becomes the "Profil bilgisi şu anda alınamadı" notice, so the response never stays "thinking".
/// No retries here (Discord.Net's own rate-limit handling is the only one). Logs carry ids and outcomes only.
/// </summary>
public sealed class PfpCommandFlow(PfpResolver resolver, PfpCooldown cooldown, PfpCards cards, TimeProvider clock, ILogger<PfpCommandFlow> logger)
{
    /// <summary>All Discord reads of one command together; the interaction token itself stays valid for 15 minutes.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(10);

    public async Task RunAsync(PfpRequest request, IPfpProfileSource source, IPfpResponder responder)
    {
        if (!PfpScopes.TryParse(request.Scope, out var scope))
        {
            await responder.ReplyPrivateAsync(cards.InvalidScope(request.Language));
            return;
        }

        if (!cooldown.TryEnter(request.GuildId, request.InvokerId, out var wait))
        {
            await responder.ReplyPrivateAsync(cards.Cooldown(request.Language, wait));
            return;
        }

        await responder.DeferPublicAsync();

        PfpResolution resolution;
        using (var budget = new CancellationTokenSource(LookupBudget, clock))
        {
            try
            {
                resolution = await resolver.ResolveAsync(source, request.GuildId, request.TargetId, request.Kind, scope, budget.Token);
            }
            catch (Exception ex)
            {
                // The Discord adapter maps the known failures itself; anything else must still complete the response.
                logger.LogWarning("Pfp: profile read failed unexpectedly ({Error}) guild={Guild} target={Target}", ex.GetType().Name, request.GuildId, request.TargetId);
                resolution = PfpResolution.Unavailable;
            }
        }

        logger.LogDebug("Pfp {Kind} guild={Guild} user={User} target={Target} scope={Scope} outcome={Outcome} source={Source}",
            request.Kind, request.GuildId, request.InvokerId, request.TargetId, scope, resolution.Kind, resolution.Source);
        await responder.ReplyPublicAsync(cards.Render(request.Language, resolution));
    }
}
