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
/// Every click, select and form submit of TSQ Öngörü: the creation form, and the card's buttons (🎯 Tahmin Yap, 🔒 Kilitle,
/// ✅ Sonuçlandır, ↩️ İptal / İade) with their private follow-ups. The card's custom ids carry only the prediction number,
/// so they keep working after a restart; each click reloads the prediction and is re-checked by the services (guild,
/// channel, the card's own message, module, manager, stored state) — a custom id carries no authority and the button's own
/// state is never trusted. Management answers and confirmations are private; the public card is only ever edited by the
/// card sync, never from here. Every message and edit goes out with allowed_mentions = none.
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
        await RespondWithModalAsync(PredictionFormUi.CreateModal(draftId, draft.Values, predictions.DefaultOddsText, key => Localizer.Get(language, key)));
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

    // ---- 🎯 Tahmin Yap ----

    /// <summary>
    /// 🎯 Tahmin Yap on the public card: every check first, then the entry form (outcome select + amount). No coin moves
    /// here; closing the form changes nothing, and pressing the button again always starts afresh.
    /// </summary>
    [ComponentInteraction(PredictionCards.EnterPrefix + "*", ignoreGroupNames: true)]
    public async Task EnterAsync(string id)
    {
        if (await RefuseBotAsync())
            return;
        if (await CardPredictionAsync(id) is not { } predictionId)
            return;
        var (refusal, info) = await predictions.StartEntryAsync(Actor, Here, predictionId, CardMessage, CancellationToken.None);
        await OpenEntryFormAsync(refusal, info);
    }

    /// <summary>✏️ Düzenle on the entry preview: the form again with the earlier choice and amount.</summary>
    [ComponentInteraction(PredictionMessages.EntryEditPrefix + "*", ignoreGroupNames: true)]
    public async Task EditEntryAsync(string token)
    {
        if (await RefuseBotAsync())
            return;
        var (refusal, info) = await predictions.EditEntryAsync(Actor, Here, token, CancellationToken.None);
        await OpenEntryFormAsync(refusal, info);
    }

    [ModalInteraction(PredictionMessages.StakeModalPrefix + "*", ignoreGroupNames: true)]
    public async Task SubmitEntryAsync(string value, PredictionStakeModal modal)
    {
        if (await RefuseBotAsync())
            return;
        var parts = value.Split(PredictionMessages.Separator, 2);
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var predictionId))
        {
            await ReplyResultAsync(PredictionService.NotFound());
            return;
        }

        var outcome = PredictionFormUi.ReadValue(((IModalInteraction)Context.Interaction).Data.Components, PredictionFormUi.OutcomeField);
        var reply = await predictions.PreviewEntryAsync(Actor, Here, predictionId, outcome, modal.Amount, parts.Length > 1 ? parts[1] : null, CancellationToken.None);
        await ShowFromModalAsync(reply);
    }

    [ComponentInteraction(PredictionMessages.EntryConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmEntryAsync(string token)
    {
        if (await RefuseBotAsync())
            return;
        await DeferEphemeralAsync();
        await ReplaceAsync(await predictions.ConfirmEntryAsync(Actor, Here, token, DisplayName(), CancellationToken.None));
    }

    // ---- 🔒 Kilitle ----

    [ComponentInteraction(PredictionCards.LockPrefix + "*", ignoreGroupNames: true)]
    public async Task LockAsync(string id)
    {
        await DeferEphemeralAsync();
        if (await CardPredictionAsync(id) is { } predictionId)
            await ReplyViewAsync(await predictions.PromptLockAsync(Actor, Here, predictionId, CardMessage, CancellationToken.None));
    }

    [ComponentInteraction(PredictionMessages.LockConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmLockAsync(string id)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(TryId(id, out var predictionId)
            ? await predictions.LockAsync(Actor, Here, predictionId, CancellationToken.None)
            : PredictionService.NotFound());
    }

    // ---- ✅ Sonuçlandır ----

    [ComponentInteraction(PredictionCards.SettlePrefix + "*", ignoreGroupNames: true)]
    public async Task SettleAsync(string id)
    {
        await DeferEphemeralAsync();
        if (await CardPredictionAsync(id) is { } predictionId)
            await ReplyViewAsync(await predictions.StartSettleAsync(Actor, Here, predictionId, CardMessage, CancellationToken.None));
    }

    [ComponentInteraction(PredictionMessages.SettlePickPrefix + "*", ignoreGroupNames: true)]
    public async Task PickWinnerAsync(string id, string[] values)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(TryId(id, out var predictionId)
            ? await predictions.PreviewSettleAsync(Actor, Here, predictionId, values.FirstOrDefault(), CancellationToken.None)
            : PredictionService.NotFound());
    }

    [ComponentInteraction(PredictionMessages.SettleConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmSettleAsync(string value)
    {
        await DeferEphemeralAsync();
        var parts = value.Split(PredictionMessages.Separator);
        await ReplaceAsync(parts.Length == 2 && TryId(parts[0], out var predictionId) && TryId(parts[1], out var outcomeId)
            ? await predictions.ConfirmSettleAsync(Actor, Here, predictionId, outcomeId, CancellationToken.None)
            : PredictionService.NotFound());
    }

    // ---- ↩️ İptal / İade ----

    [ComponentInteraction(PredictionCards.CancelPrefix + "*", ignoreGroupNames: true)]
    public async Task CancelAsync(string id)
    {
        if (await CardPredictionAsync(id) is not { } predictionId)
            return;
        if (await predictions.StartCancelAsync(Actor, Here, predictionId, CardMessage, CancellationToken.None) is { } refusal)
        {
            await ReplyResultAsync(refusal);
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(PredictionFormUi.CancelModal(predictionId, key => Localizer.Get(language, key)));
    }

    [ModalInteraction(PredictionMessages.CancelReasonModalPrefix + "*", ignoreGroupNames: true)]
    public async Task SubmitCancelReasonAsync(string id, PredictionCancelModal modal)
    {
        var reply = TryId(id, out var predictionId)
            ? await predictions.PreviewCancelAsync(Actor, Here, predictionId, modal.Reason, CancellationToken.None)
            : PredictionService.NotFound();
        await ShowFromModalAsync(reply);
    }

    [ComponentInteraction(PredictionMessages.CancelConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmCancelAsync(string token)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(await predictions.ConfirmCancelAsync(Actor, Here, token, CancellationToken.None));
    }

    // ---- tournament end, dismiss, paging ----

    [ComponentInteraction(PredictionMessages.EndConfirmPrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmEndAsync(string token)
    {
        await DeferEphemeralAsync();
        await ReplaceAsync(await economy.ConfirmTournamentEndAsync(Actor, Here, token, CancellationToken.None));
    }

    /// <summary>Vazgeç on any private step: its pending state (if any) is dropped, nothing changes.</summary>
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

    /// <summary>The message a card button was clicked on (the services check it is the prediction's own card).</summary>
    private MessageId? CardMessage => Context.Interaction is IComponentInteraction { Message: { } message } ? new MessageId(message.Id) : null;

    /// <summary>
    /// The prediction number of a card button. A click on one of the bot's own cards whose post confirmation was lost records
    /// the card first. An unreadable number is answered privately as "not found".
    /// </summary>
    private async Task<long?> CardPredictionAsync(string id)
    {
        if (!TryId(id, out var predictionId))
        {
            await ReplyResultAsync(PredictionService.NotFound());
            return null;
        }

        if (Context.Interaction is IComponentInteraction { Message: { } message } && message.Author.Id == Context.Client.CurrentUser.Id)
            await predictions.AttachFromCardAsync(predictionId, Actor.GuildId, Here, new MessageId(message.Id), CancellationToken.None);
        return predictionId;
    }

    private static bool TryId(string text, out long id) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;

    private async Task OpenEntryFormAsync(OperationResult? refusal, EntryFormInfo? info)
    {
        if (refusal is not null || info is null)
        {
            await ReplyResultAsync(refusal ?? PredictionService.NotFound());
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(PredictionFormUi.StakeModal(info, Coins.Format(info.AvailableMinor, language), key => Localizer.Get(language, key)));
    }

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
