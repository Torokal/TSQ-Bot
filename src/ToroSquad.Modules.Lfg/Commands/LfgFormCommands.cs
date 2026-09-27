using System.Globalization;
using Discord;
using Discord.Interactions;
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
            listing.VoiceChannel);
        await RespondWithModalAsync(LfgFormUi.Modal(draft, MaxPlayers, await TextAsync()));
    }

    /// <summary>The modal was submitted: checked with the service's rules (nothing stored), then the settings step.</summary>
    [ModalInteraction(LfgForm.ModalPrefix + "*", ignoreGroupNames: true)]
    public async Task SubmitFormAsync(string id, LfgFormModal modal)
    {
        var L = await TextAsync();
        var draft = drafts.Update(id, Actor, d => d with { Values = modal.ToValues(), Preview = null });
        if (draft is null)
        {
            await ShowAsync(L("lfg.form.expired"), new ComponentBuilder().Build());
            return;
        }

        var check = draft.Kind == LfgFormKind.Create
            ? await lfg.CheckCreateAsync(Actor, Here, draft.ToCreateInput() with { NotifyBeforeStart = false, NotifyAtStart = false, VoiceChannel = null },
                CancellationToken.None)
            : await lfg.CheckEditAsync(Actor, draft.ListingId!.Value, draft.ToEditInput() with { NotifyBeforeStart = false, NotifyAtStart = false, VoiceChannel = null },
                CancellationToken.None); // the settings are chosen (and checked on save) in the next step
        if (!check.Result.Succeeded || check.Preview is null)
        {
            var reason = await T(check.Result.MessageKey, check.Result.Args.ToArray()) + await TraceLineAsync(check.Result);
            await ShowAsync(reason, LfgFormUi.Retry(draft, L));
            return;
        }

        // Settings that no longer apply (a start of "now" has no notices) are dropped rather than refused later.
        draft = drafts.Update(id, Actor, d => d with
        {
            Preview = check.Preview,
            NotifyBeforeStart = d.NotifyBeforeStart && (check.Preview.EventAt is not null),
            NotifyAtStart = d.NotifyAtStart && (check.Preview.EventAt is not null),
        }) ?? draft;
        var (content, components) = LfgFormUi.Settings(draft, Services.Clock.GetUtcNow(), L);
        await ShowAsync(content, components);
    }

    [ComponentInteraction(LfgFormUi.NotifyPrefix + "*", ignoreGroupNames: true)]
    public async Task ChooseNoticesAsync(string id, string[] values) =>
        await UpdateSettingsAsync(drafts.Update(id, Actor, d => d with
        {
            NotifyBeforeStart = values.Contains(LfgFormUi.NotifyBefore),
            NotifyAtStart = values.Contains(LfgFormUi.NotifyStart),
        }));

    [ComponentInteraction(LfgFormUi.VoicePrefix + "*", ignoreGroupNames: true)]
    public async Task ChooseVoiceAsync(string id, string[] values)
    {
        ChannelId? voice = values.Length > 0 && ulong.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var channel)
            ? new ChannelId(channel)
            : null;
        await UpdateSettingsAsync(drafts.Update(id, Actor, d => d with { VoiceChannel = voice }));
    }

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
            await EditFormMessageAsync(await T("lfg.form.expired"));
            return;
        }

        if (draft.Kind == LfgFormKind.Edit)
        {
            var edited = await lfg.EditAsync(Actor, draft.ListingId!.Value, draft.ToEditInput(), CancellationToken.None);
            if (!edited.Result.Succeeded)
            {
                drafts.Return(draft);
                await ReplyResultAsync(edited.Result);
                return;
            }

            if (edited.RefreshCard)
                await cards.SyncAsync(draft.ListingId.Value, CancellationToken.None); // edits the same card, never a new message
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
            await ReplyResultAsync(created.Result);
            return;
        }

        if (!await PostCardAsync(listing))
        {
            drafts.Return(draft); // nothing was opened: the same settings can be saved again
            await ReplyTextAsync("lfg.form.card_failed");
            return;
        }

        await EditFormMessageAsync(await T("lfg.create.done"));
    }

    private ChannelId Here => new(Context.Interaction.ChannelId ?? Context.Channel.Id);

    private int MaxPlayers => Math.Min(options.Value.MaxPlayersPerListing, LfgRules.HardMaxPlayers);

    private async Task<LfgFormUi.Text> TextAsync()
    {
        var language = await LangAsync();
        return (key, args) => Localizer.Get(language, key, args);
    }

    /// <summary>
    /// The public card, as a follow-up of the save click (the form itself is private). A card that could not be posted is
    /// removed with its listing (it never existed for anyone); a posted one is recorded so the bot can edit it later.
    /// </summary>
    private async Task<bool> PostCardAsync(LfgListingView listing)
    {
        var card = renderer.Render(listing, await LangAsync());
        IUserMessage posted;
        try
        {
            posted = await FollowupAsync(embed: DiscordConversions.ToEmbed(card.Embed), components: DiscordConversions.ToComponents(card.Buttons),
                ephemeral: false, allowedMentions: NoPings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LFG listing {Listing}: its card could not be posted", listing.Id);
            await lfg.DiscardAsync(listing.Id, CancellationToken.None);
            return false;
        }

        await lfg.AttachMessageAsync(listing.Id, Actor.GuildId, Here, new MessageId(posted.Id), CancellationToken.None);
        return true;
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
            ? LfgFormUi.Settings(draft, Services.Clock.GetUtcNow(), L)
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
