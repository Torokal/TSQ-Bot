using System.Globalization;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Lfg.Application;
using ToroSquad.Modules.Lfg.Domain;

namespace ToroSquad.Modules.Lfg.Commands;

/// <summary>
/// /ekip and the card buttons. The card is the public response to /ekip (no extra channel message); Katıl/Ayrıl redraw it in
/// the same interaction and answer the clicker privately. Every click is re-validated by <see cref="LfgService"/> — the
/// button's own state (enabled, label) is never trusted. Refusals are ephemeral and nothing here ever pings.
/// </summary>
[ToroModule(LfgModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class LfgCommands(
    InteractionServices services,
    LfgService lfg,
    LfgCardRenderer renderer,
    LfgCardSync cards,
    ILogger<LfgCommands> logger) : ToroInteractionModule(services)
{
    public const string ConfirmClosePrefix = "tsq:lfg:close-yes:";
    public const string KeepOpenPrefix = "tsq:lfg:close-no:";
    private const int MaxRedraws = 3;

    /// <summary>Bound for the at-limit card check: the /ekip answer must still arrive within Discord's 3 seconds.</summary>
    private static readonly TimeSpan LimitRecheckBudget = TimeSpan.FromMilliseconds(1500);

    /// <summary>allowed_mentions = none on EVERY create and edit of a card (players are embed mentions, which never ping anyway).</summary>
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [SlashCommand("ekip", "Find players: open a group listing for any game")]
    public async Task CreateAsync(
        [Summary("oyun", "Game or activity (e.g. Deadlock, CS2, Valheim)"), MinLength(LfgRules.GameNameMinLength), MaxLength(LfgRules.GameNameMaxLength)] string oyun,
        [Summary("kisi", "Total team size including you"), MinValue(LfgRules.MinPlayers), MaxValue(LfgRules.HardMaxPlayers)] int kisi,
        [Summary("detay", "Short details: mode, rank, roles, plan…"), MaxLength(LfgRules.DetailsMaxLength)] string? detay = null,
        [Summary("sure", "How long the listing stays open (empty: default duration)"), Choice("1 hour", 60), Choice("2 hours", 120), Choice("3 hours", 180)] int? sure = null)
    {
        // No defer: validation and the insert take milliseconds, and a refusal must stay private while the card is public.
        var created = await lfg.CreateAsync(Actor, Here, oyun, kisi, detay, sure, CancellationToken.None);
        if (created.Result.MessageKey == "lfg.create.limit" && await OwnerCardWasDeletedAsync())
            created = await lfg.CreateAsync(Actor, Here, oyun, kisi, detay, sure, CancellationToken.None);
        if (!created.Result.Succeeded || created.Listing is not { } listing)
        {
            await ReplyResultAsync(created.Result);
            return;
        }

        var card = renderer.Render(listing, await LangAsync());
        try
        {
            await RespondAsync(embed: DiscordConversions.ToEmbed(card.Embed), components: DiscordConversions.ToComponents(card.Buttons),
                allowedMentions: NoPings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lost response may still have been posted: keep the listing if Discord shows the card, drop it otherwise.
            if (await OriginalResponseAsync() is not { } posted)
            {
                await lfg.DiscardAsync(listing.Id, CancellationToken.None);
                throw;
            }

            await lfg.AttachMessageAsync(listing.Id, Actor.GuildId, Here, posted, CancellationToken.None);
            return;
        }

        if (await OriginalResponseAsync() is { } message)
            await lfg.AttachMessageAsync(listing.Id, Actor.GuildId, Here, message, CancellationToken.None);
        else
            logger.LogWarning("LFG listing {Listing}: card posted but its message id is unknown; the first button click records it", listing.Id);
    }

    [ComponentInteraction(LfgCardRenderer.JoinPrefix + "*", ignoreGroupNames: true)]
    public Task JoinAsync(string id) => CardActionAsync(id, lfg.JoinAsync);

    [ComponentInteraction(LfgCardRenderer.LeavePrefix + "*", ignoreGroupNames: true)]
    public Task LeaveAsync(string id) => CardActionAsync(id, lfg.LeaveAsync);

    /// <summary>Close, step 1: only the owner or a moderator gets the private confirmation.</summary>
    [ComponentInteraction(LfgCardRenderer.ClosePrefix + "*", ignoreGroupNames: true)]
    public async Task CloseAsync(string id)
    {
        await DeferEphemeralAsync();
        if (await ListingIdAsync(id) is not { } listingId)
            return;
        var check = await lfg.CheckCloseAsync(Actor, listingId, CancellationToken.None);
        await ApplyToCardAsync(check);
        if (!check.Result.Succeeded || check.Listing is not { } listing)
        {
            await ReplyResultAsync(check.Result);
            return;
        }

        var components = new ComponentBuilder()
            .WithButton(await T("lfg.close.yes"), ConfirmClosePrefix + Inv($"{listing.Id}"), ButtonStyle.Danger)
            .WithButton(await T("lfg.close.no"), KeepOpenPrefix + Inv($"{listing.Id}"), ButtonStyle.Secondary)
            .Build();
        await SendEphemeralAsync(await T("lfg.close.question", DiscordText.Untrusted(listing.GameName, 120)), null, components);
    }

    /// <summary>Close, step 2 (button in the private confirmation): re-authorized, then the card is redrawn as closed.</summary>
    [ComponentInteraction(ConfirmClosePrefix + "*", ignoreGroupNames: true)]
    public async Task ConfirmCloseAsync(string id)
    {
        await DeferEphemeralAsync();
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var listingId))
        {
            await EditConfirmationAsync(await T("lfg.not_found"));
            return;
        }

        var result = await lfg.CloseAsync(Actor, listingId, CancellationToken.None);
        await EditConfirmationAsync(await T(result.Result.MessageKey, result.Result.Args.ToArray()));
        if (result.Listing is not null)
            await cards.SyncAsync(listingId, CancellationToken.None); // only acts if the card is stale (just closed / expired)
    }

    [ComponentInteraction(KeepOpenPrefix + "*", ignoreGroupNames: true)]
    public async Task KeepOpenAsync(string id)
    {
        await DeferEphemeralAsync();
        await EditConfirmationAsync(await T("lfg.close.kept"));
    }

    private ChannelId Here => new(Context.Interaction.ChannelId ?? Context.Channel.Id);

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

    private async Task CardActionAsync(string id, Func<ActorContext, long, CancellationToken, Task<LfgResult>> action)
    {
        await DeferEphemeralAsync(); // deferred update of the card: acknowledged at once, redrawn below
        if (await ListingIdAsync(id) is not { } listingId)
            return;
        var result = await action(Actor, listingId, CancellationToken.None);
        await ApplyToCardAsync(result);
        await ReplyResultAsync(result.Result);
    }

    /// <summary>Parses the id and records the card's message id if the post confirmation was lost.</summary>
    private async Task<long?> ListingIdAsync(string id)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var listingId))
        {
            await ReplyTextAsync("lfg.not_found");
            return null;
        }

        // Only the bot's own card can carry these buttons; the author check keeps "edit only our own messages" explicit.
        if (Context.Interaction is IComponentInteraction { Message: { } message } && message.Author.Id == Context.Client.CurrentUser.Id)
            await lfg.AttachMessageAsync(listingId, Actor.GuildId, new ChannelId(message.Channel.Id), new MessageId(message.Id), CancellationToken.None);
        return listingId;
    }

    /// <summary>
    /// Redraws the clicked card from the stored state when it changed (or looked out of date). Two simultaneous clicks each
    /// draw their own snapshot and Discord may apply the older edit last, so after editing the state version is checked
    /// again and a newer state is drawn once more. A card whose listing no longer exists loses its buttons. A failed or
    /// unsettled redraw is handed to the worker instead of being lost.
    /// </summary>
    private async Task ApplyToCardAsync(LfgResult result)
    {
        if (result.Listing is not { } listing)
        {
            if (result.Result.MessageKey == "lfg.not_found")
                await TryModifyCardAsync(m => m.Components = new ComponentBuilder().Build());
            return;
        }

        if (!result.RefreshCard)
            return;
        var language = await LangAsync();
        for (var attempt = 0; attempt < MaxRedraws; attempt++)
        {
            var card = renderer.Render(listing, language);
            var drawn = await TryModifyCardAsync(m =>
            {
                m.Embed = DiscordConversions.ToEmbed(card.Embed);
                m.Components = DiscordConversions.ToComponents(card.Buttons);
            });
            if (!drawn)
                break;
            if (await lfg.GetAsync(listing.Id, CancellationToken.None) is not { } latest || latest.Version == listing.Version)
                return;
            listing = latest;
        }

        logger.LogWarning("LFG listing {Listing}: interactive card update failed or did not settle; the worker redraws it", listing.Id);
        await lfg.MarkCardStaleAsync(listing.Id, CancellationToken.None);
    }

    private async Task<bool> TryModifyCardAsync(Action<MessageProperties> change)
    {
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                change(m);
                m.AllowedMentions = NoPings; // every card edit, whatever it changes
            });
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LFG: interactive card update failed");
            return false;
        }
    }

    private async Task EditConfirmationAsync(string text)
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
            logger.LogWarning(ex, "LFG: could not update a close confirmation; answering with a new private message");
            await SendEphemeralAsync(text, null, null);
        }
    }

    private async Task<MessageId?> OriginalResponseAsync()
    {
        try
        {
            var message = await Context.Interaction.GetOriginalResponseAsync();
            return message is null ? null : new MessageId(message.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "LFG: original interaction response not available");
            return null;
        }
    }
}
