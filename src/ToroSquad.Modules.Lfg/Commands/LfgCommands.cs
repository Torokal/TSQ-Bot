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
/// The card buttons (the /ekip form and ✏️ Düzenle are in <see cref="LfgFormCommands"/>). Katıl/Belki/Ayrıl redraw the card
/// in the same interaction and answer the clicker privately. Every click is re-validated by <see cref="LfgService"/> — the
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

    /// <summary>allowed_mentions = none on EVERY create and edit of a card (players are embed mentions, which never ping anyway).</summary>
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [ComponentInteraction(LfgCardRenderer.JoinPrefix + "*", ignoreGroupNames: true)]
    public Task JoinAsync(string id) => CardActionAsync(id, lfg.JoinAsync);

    [ComponentInteraction(LfgCardRenderer.MaybePrefix + "*", ignoreGroupNames: true)]
    public Task MaybeAsync(string id) => CardActionAsync(id, lfg.MaybeAsync);

    [ComponentInteraction(LfgCardRenderer.LeavePrefix + "*", ignoreGroupNames: true)]
    public Task LeaveAsync(string id) => CardActionAsync(id, lfg.LeaveAsync);

    /// <summary>
    /// "🔊 Ses Odası" on the card and on the event notices (same handler). Moves a Joined player who is already in voice when
    /// the bot may; otherwise answers privately with the channel and a link that opens it — it never claims a connection it
    /// did not make. No card backfill here: a notice is not the card.
    /// </summary>
    [ComponentInteraction(LfgCardRenderer.VoicePrefix + "*", ignoreGroupNames: true)]
    public async Task VoiceAsync(string id)
    {
        await DeferEphemeralAsync();
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var listingId))
        {
            await ReplyTextAsync("lfg.not_found");
            return;
        }

        var result = await lfg.VoiceAsync(Actor, listingId, CancellationToken.None);
        var text = await T(result.Result.MessageKey, result.Result.Args.ToArray());
        var components = result.OpenChannelUrl is { } url
            ? new ComponentBuilder().WithButton(await T("lfg.voice.open_button"), url: url, style: ButtonStyle.Link).Build()
            : null;
        await SendEphemeralAsync(text, null, components);
    }

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
