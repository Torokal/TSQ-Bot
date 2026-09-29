using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Commands;

/// <summary>
/// Every click, select and form submit of TSQ Öngörü. Each one is re-checked by the services (channel, module, role or
/// manager, token owner, stored state) — a custom id carries no authority and the button's own state is never trusted.
/// Private steps are updated in place (the same private message moves from preview to receipt); the public card is only
/// ever edited by the card sync, never from here. Every message and edit goes out with allowed_mentions = none.
/// </summary>
[ToroModule(PredictionsModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class PredictionComponents(
    InteractionServices services,
    PredictionService predictions,
    PredictionEconomy economy,
    ILogger<PredictionComponents> logger) : PredictionInteractionModule(services)
{
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    // ---- creation form ----

    [ModalInteraction(PredictionMessages.FormModalPrefix + "*", ignoreGroupNames: true)]
    public async Task SubmitFormAsync(string draftId, PredictionFormModal modal)
    {
        if (await RefuseBotAsync())
            return;
        var reply = await predictions.SubmitFormAsync(Actor, Here, draftId, modal.ToValues(), DisplayName(), CancellationToken.None);
        await ShowFromModalAsync(reply);
    }

    /// <summary>Düzenle: the same form again, filled with everything typed so far.</summary>
    [ComponentInteraction(PredictionMessages.EditPrefix + "*", ignoreGroupNames: true)]
    public async Task EditFormAsync(string draftId)
    {
        if (predictions.Draft(draftId, Actor) is not { } draft)
        {
            await ReplyTextAsync("predictions.form.expired");
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(PredictionFormUi.CreateModal(draftId, draft.Values, key => Localizer.Get(language, key)));
    }

    [ComponentInteraction(PredictionMessages.PublishPrefix + "*", ignoreGroupNames: true)]
    public async Task PublishAsync(string draftId)
    {
        if (await RefuseBotAsync())
            return;
        await DeferEphemeralAsync();
        var reply = await predictions.PublishAsync(Actor, Here, draftId, DisplayName(), CancellationToken.None);
        if (reply.View is not null)
        {
            await ReplaceAsync(reply); // the form became invalid (e.g. the lock time passed): the errors and the way back
            return;
        }

        if (reply.Result.Succeeded || reply.Result.MessageKey is "predictions.publish.uncertain" or "predictions.form.tournament_changed")
            await ReplaceAsync(reply); // done (or nothing left to click): the private preview becomes the final line
        else
            await ReplyResultAsync(reply.Result); // refused: the preview stays usable
    }

    [ComponentInteraction(PredictionMessages.DiscardPrefix + "*", ignoreGroupNames: true)]
    public async Task DiscardFormAsync(string draftId)
    {
        await DeferEphemeralAsync();
        predictions.Discard(draftId, Actor);
        await ReplaceAsync(OperationResult.Ok("predictions.form.discarded"));
    }

    // ---- entry ----

    /// <summary>A pick on the public card: every check first, then the amount form (no coin moves here).</summary>
    [ComponentInteraction(PredictionCards.PickPrefix + "*", ignoreGroupNames: true)]
    public async Task PickAsync(string id, string[] values)
    {
        if (await RefuseBotAsync())
            return;
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var predictionId))
        {
            await ReplyResultAsync(PredictionService.NotFound());
            return;
        }

        // Only the bot's own card carries this select: a lost post confirmation is recovered from the click.
        if (Context.Interaction is IComponentInteraction { Message: { } message } && message.Author.Id == Context.Client.CurrentUser.Id)
            await predictions.AttachFromCardAsync(predictionId, Actor.GuildId, Here, new MessageId(message.Id), CancellationToken.None);

        var (refusal, info) = await predictions.StartEntryAsync(Actor, Here, predictionId, values.FirstOrDefault(), CancellationToken.None);
        if (refusal is not null || info is null)
        {
            await ReplyResultAsync(refusal ?? PredictionService.NotFound());
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(PredictionFormUi.StakeModal(info, Coins.Format(info.AvailableMinor, language), key => Localizer.Get(language, key)));
    }

    [ModalInteraction(PredictionMessages.StakeModalPrefix + "*:*", ignoreGroupNames: true)]
    public async Task SubmitStakeAsync(string predictionId, string outcomeId, PredictionStakeModal modal)
    {
        if (await RefuseBotAsync())
            return;
        await DeferEphemeralAsync();
        if (!long.TryParse(predictionId, NumberStyles.None, CultureInfo.InvariantCulture, out var prediction) ||
            !long.TryParse(outcomeId, NumberStyles.None, CultureInfo.InvariantCulture, out var outcome))
        {
            await ReplyResultAsync(PredictionService.NotFound());
            return;
        }

        await ReplyViewAsync(await predictions.PreviewEntryAsync(Actor, Here, prediction, outcome, modal.Amount, CancellationToken.None));
    }

    [ComponentInteraction(PredictionMessages.EntryConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmEntryAsync(string token)
    {
        if (await RefuseBotAsync())
            return;
        await DeferEphemeralAsync();
        await ReplaceAsync(await predictions.ConfirmEntryAsync(Actor, Here, token, CancellationToken.None));
    }

    // ---- settle / cancel / tournament end ----

    [ComponentInteraction(PredictionMessages.SettlePickPrefix + "*", ignoreGroupNames: true)]
    public async Task PickWinnerAsync(string token, string[] values)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(await predictions.PreviewSettleAsync(Actor, Here, token, values.FirstOrDefault(), CancellationToken.None));
    }

    [ComponentInteraction(PredictionMessages.SettleConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmSettleAsync(string token)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(await predictions.ConfirmSettleAsync(Actor, Here, token, CancellationToken.None));
    }

    [ComponentInteraction(PredictionMessages.CancelConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmCancelAsync(string token)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(await predictions.ConfirmCancelAsync(Actor, Here, token, CancellationToken.None));
    }

    [ComponentInteraction(PredictionMessages.EndConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmEndAsync(string token)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(await economy.ConfirmTournamentEndAsync(Actor, Here, token, CancellationToken.None));
    }

    /// <summary>Vazgeç on any confirmation: the pending step is dropped, nothing changes.</summary>
    [ComponentInteraction(PredictionMessages.DismissPrefix + "*", ignoreGroupNames: true)]
    public async Task DismissAsync(string token)
    {
        await DeferEphemeralAsync();
        predictions.Discard(token, Actor);
        await ReplaceAsync(OperationResult.Ok("predictions.dismissed"));
    }

    [ComponentInteraction(PredictionMessages.MinePagePrefix + "*", ignoreGroupNames: true)]
    public async Task MinePageAsync(string page)
    {
        await DeferEphemeralAsync();
        _ = int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out var number);
        await ReplaceAsync(await economy.MyEntriesAsync(Actor, Here, number, CancellationToken.None));
    }

    // ---- helpers ----

    /// <summary>
    /// The answer to a form submit: in place when the form was reopened from a private message (Düzenle), otherwise a new
    /// private message. Never the public card.
    /// </summary>
    private async Task ShowFromModalAsync(PredictionReply reply)
    {
        var (content, embed, components) = await RenderAsync(reply);
        if (Context.Interaction is SocketModal { Message: { } source } modal && source.Flags is { } flags && flags.HasFlag(MessageFlags.Ephemeral))
        {
            await modal.UpdateAsync(m =>
            {
                m.Content = content ?? "";
                m.Embed = embed;
                m.Components = components ?? new ComponentBuilder().Build();
                m.AllowedMentions = NoPings;
            });
            return;
        }

        await SendAsync(content, embed, components, ephemeral: true);
    }

    /// <summary>Replaces the private message the click came from (after the click was acknowledged); a new private message if that fails.</summary>
    private async Task ReplaceAsync(PredictionReply reply)
    {
        var (content, embed, components) = await RenderAsync(reply);
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = content ?? "";
                m.Embed = embed;
                m.Components = components ?? new ComponentBuilder().Build();
                m.AllowedMentions = NoPings;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TSQ Öngörü: could not update a private message; answering with a new one");
            await SendAsync(content, embed, components, ephemeral: true);
        }
    }

    private async Task<(string? Content, Embed? Embed, MessageComponent? Components)> RenderAsync(PredictionReply reply)
    {
        if (reply.View is { } view)
            return (view.Content, DiscordConversions.ToEmbed(view.Embed), DiscordConversions.ToComponents(view));
        return (await T(reply.Result.MessageKey, [.. reply.Result.Args]) + await TraceLineAsync(reply.Result), null, null);
    }
}
