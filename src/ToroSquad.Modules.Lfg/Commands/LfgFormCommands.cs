using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Commands;

/// <summary>
/// The listing form: /ekip (create) and ✏️ Düzenle on the card (edit, owner only) open the same modal; its submit is
/// checked with the service's own rules (nothing stored) and answered privately with the settings step; saving creates the
/// listing (its card is posted as a public follow-up that never pings) or edits it (its card is edited by the bot). A draft
/// lives only in memory between the steps. Every step re-checks the caller server-side; custom ids carry no authority.
/// </summary>
[ToroModule(LfgModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class LfgFormCommands(
    InteractionServices services,
    LfgService lfg,
    LfgFormDrafts drafts,
    LfgCardRenderer renderer,
    LfgCardSync cards,
    IOptions<LfgOptions> options,
    ILogger<LfgFormCommands> logger) : ToroInteractionModule(services)
{
    /// <summary>How far back a failed card post is looked for.</summary>
    private const int RecentMessages = 20;

    /// <summary>Bound for the at-limit card check: the form must still open within Discord's 3 seconds.</summary>
    private static readonly TimeSpan LimitRecheckBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>allowed_mentions = none on EVERY message and edit of the form and of the card.</summary>
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [SlashCommand("ekip", "Find players: open a group listing for any game")]
    public async Task OpenCreateFormAsync()
    {
        // Refusals that do not depend on the form (listing channel, active-listing limit): answered before anyone types.
        var refusal = await lfg.PrecheckCreateAsync(Actor, Here, CancellationToken.None);
        if (refusal?.MessageKey == "lfg.create.limit" && await OwnerCardWasDeletedAsync())
            refusal = await lfg.PrecheckCreateAsync(Actor, Here, CancellationToken.None);
        if (refusal is not null)
        {
            await ReplyResultAsync(refusal);
            return;
        }

        var draft = drafts.Open(Actor, Here, LfgFormKind.Create, null, LfgFormValues.Empty);
        await RespondWithModalAsync(LfgFormUi.Modal(draft, MaxPlayers, await TextAsync()));
    }

    /// <summary>✏️ Düzenle on the card: the owner gets the same form, filled with the listing as stored now.</summary>
    [ComponentInteraction(LfgCardRenderer.EditPrefix + "*", ignoreGroupNames: true)]
    public async Task OpenEditFormAsync(string id)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var listingId))
        {
            await ReplyTextAsync("lfg.not_found");
            return;
        }

        var opened = await lfg.OpenEditAsync(Actor, listingId, CancellationToken.None);
        if (!opened.Result.Succeeded || opened.Listing is not { } listing || opened.Prefill is not { } prefill)
        {
            await ReplyResultAsync(opened.Result);
            return;
        }

        var draft = drafts.Open(Actor, listing.Channel, LfgFormKind.Edit, listing.Id, prefill, listing.NotifyBeforeStart, listing.NotifyAtStart,
            opened.Voice, // a deleted voice channel is not offered again (keeping "none" then removes it)
            new LfgFormSettings(listing.NotifyBeforeStart, listing.NotifyAtStart, opened.Voice, listing.VoiceChannel));
        await RespondWithModalAsync(LfgFormUi.Modal(draft, MaxPlayers, await TextAsync()));
    }

    /// <summary>The main modal was submitted: checked with the service's rules (nothing stored), then the settings step.</summary>
    [ModalInteraction(LfgForm.ModalPrefix + "*", ignoreGroupNames: true)]
    public async Task SubmitFormAsync(string id, LfgFormModal modal)
    {
        var components = ((IModalInteraction)Context.Interaction).Data.Components;
        var (before, atStart) = LfgFormUi.ReadNotices(components);
        var draft = drafts.Update(id, Actor, d => LfgFormUi.WithModal(d, modal, LfgFormUi.ReadValue(components, LfgForm.PlayersField),
            LfgFormUi.ReadVoice(components), before, atStart));
        await CheckAndShowAsync(id, draft);
    }

    /// <summary>📝 Detay Ekle / Detayı Düzenle (settings message): the small details modal, filled with the draft's details.</summary>
    [ComponentInteraction(LfgFormUi.DetailsPrefix + "*", ignoreGroupNames: true)]
    public async Task OpenDetailsAsync(string id)
    {
        if (drafts.Get(id, Actor) is not { } draft)
        {
            await ReplyTextAsync("lfg.form.expired");
            return;
        }

        await RespondWithModalAsync(LfgFormUi.DetailsModal(draft, await TextAsync()));
    }

    /// <summary>The details modal was submitted (empty clears them): checked again, then back to the settings step.</summary>
    [ModalInteraction(LfgFormUi.DetailsModalPrefix + "*", ignoreGroupNames: true)]
    public async Task SubmitDetailsAsync(string id, LfgDetailsModal modal) =>
        await CheckAndShowAsync(id, drafts.Update(id, Actor, d => LfgFormUi.WithDetails(d, modal.Details)));

    /// <summary>The listing duration (settings step); the summary shows it at once, the save checks it.</summary>
    [ComponentInteraction(LfgFormUi.DurationPrefix + "*", ignoreGroupNames: true)]
    public async Task ChooseDurationAsync(string id, string[] values) =>
        await UpdateSettingsAsync(drafts.Update(id, Actor, d => LfgFormUi.WithDuration(d, values, DefaultMinutes)));

    /// <summary>Back into the same form, filled with what was typed.</summary>
    [ComponentInteraction(LfgFormUi.BackPrefix + "*", ignoreGroupNames: true)]
    public async Task ReopenFormAsync(string id)
    {
        if (drafts.Get(id, Actor) is not { } draft)
        {
            await ReplyTextAsync("lfg.form.expired");
            return;
        }

        await RespondWithModalAsync(LfgFormUi.Modal(draft, MaxPlayers, await TextAsync()));
    }

    [ComponentInteraction(LfgFormUi.CancelPrefix + "*", ignoreGroupNames: true)]
    public async Task CancelFormAsync(string id)
    {
        await DeferEphemeralAsync();
        drafts.Remove(id, Actor);
        await EditFormMessageAsync(await T("lfg.form.cancelled"));
    }

    /// <summary>
    /// Save: the draft is taken (a double click saves once) and handed to the service, which checks everything again on the
    /// stored state. Refused: the draft stays, the settings message stays usable and the reason is shown privately.
    /// </summary>
    [ComponentInteraction(LfgFormUi.SavePrefix + "*", ignoreGroupNames: true)]
    public async Task SaveFormAsync(string id)
    {
        await DeferEphemeralAsync();
        if (drafts.Take(id, Actor) is not { } draft)
        {
            // A second click while the first is saving (or an expired draft): the first click's answer stands; restart-lost
            // drafts are answered privately without touching the form message.
            await ReplyTextAsync("lfg.form.unavailable");
            return;
        }

        if (draft.Kind == LfgFormKind.Edit)
        {
            var edited = await lfg.EditAsync(Actor, draft.ListingId!.Value, draft.ToEditInput(), CancellationToken.None);
            if (!edited.Result.Succeeded)
            {
                drafts.Return(draft);
                await ReplyRefusalAsync(draft, edited.Result);
                return;
            }

            if (edited.RefreshCard)
                await RedrawCardAsync(draft.ListingId.Value); // edits the same card, never a new message
            await EditFormMessageAsync(await T(edited.Result.MessageKey));
            return;
        }

        if (draft.Channel != Here)
        {
            await EditFormMessageAsync(await T("lfg.form.expired"));
            return;
        }

        var input = draft.ToCreateInput();
        var created = await lfg.CreateAsync(Actor, Here, input, CancellationToken.None);
        if (created.Result.MessageKey == "lfg.create.limit" && await OwnerCardWasDeletedAsync())
            created = await lfg.CreateAsync(Actor, Here, input, CancellationToken.None);
        if (!created.Result.Succeeded || created.Listing is not { } listing)
        {
            drafts.Return(draft);
            await ReplyRefusalAsync(draft, created.Result);
            return;
        }

        switch (await PostCardAsync(listing))
        {
            case CardPost.Posted:
                await EditFormMessageAsync(await T("lfg.create.done"));
                break;
            case CardPost.NotPosted:
                drafts.Return(draft); // nothing was opened: the same settings can be saved again
                await ReplyTextAsync("lfg.form.card_failed");
                break;
            default:
                // Maybe posted: the listing is kept (a first click on its card records the card), never a second card.
                await EditFormMessageAsync(await T("lfg.form.card_uncertain"));
                break;
        }
    }

    private enum CardPost
    {
        Posted,
        NotPosted,
        Unknown,
    }

    /// <summary>
    /// Checks the draft exactly like saving would (nothing stored) and shows the settings step — or the reason and the way
    /// back into the form, keeping everything in the draft. Notices with an empty start (now) are refused, never dropped.
    /// </summary>
    private async Task CheckAndShowAsync(string id, LfgFormDraft? draft)
    {
        var L = await TextAsync();
        if (draft is null)
        {
            await ShowAsync(L("lfg.form.expired"), new ComponentBuilder().Build());
            return;
        }

        var check = draft.Kind == LfgFormKind.Create
            ? await lfg.CheckCreateAsync(Actor, Here, draft.ToCreateInput(), CancellationToken.None)
            : await lfg.CheckEditAsync(Actor, draft.ListingId!.Value, draft.ToEditInput(), CancellationToken.None);
        if (!check.Result.Succeeded || check.Preview is null)
        {
            var reason = LfgFormUi.Refusal(check.Result.MessageKey, check.Result.Args, L) + await TraceLineAsync(check.Result);
            draft = drafts.Update(id, Actor, d => LfgFormUi.WithRefusal(d, check.Result.MessageKey)) ?? draft;
            await ShowAsync(reason, LfgFormUi.Retry(draft, L));
            return;
        }

        draft = drafts.Update(id, Actor, d => LfgFormUi.WithCheck(d, check.Preview));
        if (draft is null)
        {
            await ShowAsync(L("lfg.form.expired"), new ComponentBuilder().Build());
            return;
        }

        var (content, components) = LfgFormUi.Settings(draft, Services.Clock.GetUtcNow(), DefaultMinutes, L);
        await ShowAsync(content, components);
    }

    /// <summary>
    /// A save refused on the stored state (the draft was put back): the field and the reason privately, with the way back
    /// into the filled form when a field can fix it; the settings message stays usable.
    /// </summary>
    private async Task ReplyRefusalAsync(LfgFormDraft draft, OperationResult result)
    {
        var L = await TextAsync();
        var text = LfgFormUi.Refusal(result.MessageKey, result.Args, L) + await TraceLineAsync(result);
        draft = drafts.Update(draft.Id, Actor, d => LfgFormUi.WithRefusal(d, result.MessageKey)) ?? draft;
        await SendEphemeralAsync(text, null, LfgFormUi.FieldOf(result.MessageKey) is null ? null : LfgFormUi.Retry(draft, L));
    }

    private ChannelId Here => new(Context.Interaction.ChannelId ?? Context.Channel.Id);

    private int MaxPlayers => Math.Min(options.Value.MaxPlayersPerListing, LfgRules.HardMaxPlayers);

    private int DefaultMinutes => options.Value.DefaultExpirationMinutes;

    private async Task<LfgFormUi.Text> TextAsync()
    {
        var language = await LangAsync();
        return (key, args) => Localizer.Get(language, key, args);
    }

    /// <summary>
    /// The public card, as a follow-up of the save click (the form itself is private); a posted one is recorded so the bot
    /// can edit it later. The listing is removed only when Discord surely did not post the card (it refused the request with
    /// a 4xx). Any other failure may be a lost response of a posted card: the recent channel history is searched for this
    /// listing's card (its Katıl button id) and recorded if found; otherwise the listing is kept (never a dead card, never a
    /// second one) and the first click on the card records it. An empty history proves nothing (no Read Message History
    /// returns an empty list; the message may appear a moment later).
    /// </summary>
    private async Task<CardPost> PostCardAsync(LfgListingView listing)
    {
        var card = renderer.Render(listing, await LangAsync());
        IUserMessage posted;
        try
        {
            posted = await FollowupAsync(embed: DiscordConversions.ToEmbed(card.Embed), components: DiscordConversions.ToComponents(card.Buttons),
                ephemeral: false, allowedMentions: NoPings);
        }
        catch (HttpException ex) when ((int)ex.HttpCode is >= 400 and < 500)
        {
            logger.LogWarning(ex, "LFG listing {Listing}: Discord refused its card; the listing is removed", listing.Id);
            await lfg.DiscardAsync(listing.Id, CancellationToken.None);
            return CardPost.NotPosted;
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            logger.LogWarning(ex, "LFG listing {Listing}: posting its card failed; it may have been posted", listing.Id);
            if (await FindPostedCardAsync(listing.Id) is not { } found)
                return CardPost.Unknown;
            await AttachAsync(listing.Id, found);
            return CardPost.Posted;
        }

        await AttachAsync(listing.Id, new MessageId(posted.Id));
        return CardPost.Posted;
    }

    /// <summary>Records the posted card; if that fails, the first click on the card records it (the card exists either way).</summary>
    private async Task AttachAsync(long listingId, MessageId message)
    {
        try
        {
            await lfg.AttachMessageAsync(listingId, Actor.GuildId, Here, message, CancellationToken.None);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            logger.LogWarning(ex, "LFG listing {Listing}: its posted card could not be recorded yet; the first click records it", listingId);
        }
    }

    /// <summary>The listing's card among the channel's latest messages, or null (not there, or the history cannot be read).</summary>
    private async Task<MessageId?> FindPostedCardAsync(long listingId)
    {
        var join = LfgCardRenderer.JoinPrefix + listingId.ToString(CultureInfo.InvariantCulture);
        try
        {
            var recent = await Context.Channel.GetMessagesAsync(RecentMessages).FlattenAsync();
            var card = recent.FirstOrDefault(m => m.Author.Id == Context.Client.CurrentUser.Id &&
                                                  m.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>()
                                                      .Any(b => b.CustomId == join));
            return card is null ? null : new MessageId(card.Id);
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            logger.LogWarning(ex, "LFG listing {Listing}: could not check the channel for its card", listingId);
            return null;
        }
    }

    /// <summary>Any failure of a Discord or database call, including a request timeout (a TaskCanceledException); no cancellation token is used here.</summary>
    private static bool IsFailure(Exception ex) => ex is not OperationCanceledException || ex is TaskCanceledException;

    /// <summary>The edit is saved; a failed redraw only leaves the card to the worker (it stays marked stale).</summary>
    private async Task RedrawCardAsync(long listingId)
    {
        try
        {
            await cards.SyncAsync(listingId, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LFG listing {Listing}: card redraw after the edit failed; the worker redraws it", listingId);
        }
    }

    /// <summary>
    /// The modal's answer: in place when it was reopened from the private form message, otherwise a new private message.
    /// Never the public card (the edit form is opened from the card, so its source message is not ours to rewrite).
    /// </summary>
    private async Task ShowAsync(string content, MessageComponent components)
    {
        if (Context.Interaction is SocketModal { Message: { } source } modal && source.Flags is { } flags && flags.HasFlag(MessageFlags.Ephemeral))
        {
            await modal.UpdateAsync(m =>
            {
                m.Content = content;
                m.Embed = null;
                m.Components = components;
                m.AllowedMentions = NoPings;
            });
            return;
        }

        await RespondAsync(content, components: components, ephemeral: true, allowedMentions: NoPings);
    }

    private async Task UpdateSettingsAsync(LfgFormDraft? draft)
    {
        var L = await TextAsync();
        var (content, components) = draft?.Preview is not null
            ? LfgFormUi.Settings(draft, Services.Clock.GetUtcNow(), DefaultMinutes, L)
            : (L("lfg.form.expired"), new ComponentBuilder().Build());
        await ((IComponentInteraction)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = content;
            m.Components = components;
            m.AllowedMentions = NoPings;
        });
    }

    /// <summary>Replaces the private form message (after the click was acknowledged) with a final line and no controls.</summary>
    private async Task EditFormMessageAsync(string text)
    {
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = text;
                m.Components = new ComponentBuilder().Build();
                m.AllowedMentions = NoPings;
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LFG: could not update the form message; answering with a new private message");
            await SendEphemeralAsync(text, null, null);
        }
    }

    /// <summary>At the limit: were any of the caller's active cards deleted in Discord? (Orphans them; bounded in time.)</summary>
    private async Task<bool> OwnerCardWasDeletedAsync()
    {
        using var budget = new CancellationTokenSource(LimitRecheckBudget);
        try
        {
            return await cards.VerifyOwnerCardsAsync(Actor.GuildId, Actor.UserId, budget.Token) > 0;
        }
        catch (OperationCanceledException)
        {
            return false; // could not tell in time: the limit answer stands
        }
    }
}
