using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Giveaway.Application;
using ToroSquad.Modules.Giveaway.Domain;
using DiscordPermission = Discord.GuildPermission;

namespace ToroSquad.Modules.Giveaway.Commands;

/// <summary>
/// /giveaway — Manage Server required: hidden by default_member_permissions AND re-authorized in
/// <see cref="GiveawayService"/> for every step (the modal submit and the autocomplete included). create opens the form;
/// its submit posts the card in this channel. end / cancel / reroll take the giveaway number (autocomplete), a message link
/// or a message id. Every answer here is private and never pings; the public card is the bot's own message.
/// </summary>
[ToroModule(GiveawayModule.ModuleIdValue)]
[Group("giveaway", "Start and manage giveaways (admins)")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class GiveawayCommands(InteractionServices services, GiveawayService giveaways) : ToroInteractionModule(services)
{
    private const string TargetDescription = "Giveaway number (#12), message link or message ID";

    [SlashCommand("create", "Start a giveaway in this channel (opens a form)")]
    public async Task CreateAsync()
    {
        // Refusals that do not depend on the form (permission, channel, limit): answered before anyone types.
        if (await giveaways.PrecheckCreateAsync(Actor, Here, CancellationToken.None) is { } refusal)
        {
            await ReplyResultAsync(refusal);
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(GiveawayFormUi.Modal(key => Localizer.Get(language, key)));
    }

    [ModalInteraction(GiveawayFormUi.ModalId, ignoreGroupNames: true)]
    public async Task SubmitAsync(GiveawayModal modal)
    {
        await DeferEphemeralAsync();
        var request = new GiveawayRequest(modal.Prize, modal.Duration, modal.Winners, modal.Description);
        await ReplyResultAsync((await giveaways.CreateAsync(Actor, Here, DisplayName(), request, CancellationToken.None)).Result);
    }

    [SlashCommand("end", "End an active giveaway now and draw the winners")]
    public async Task EndAsync(
        [Summary("giveaway", TargetDescription), MaxLength(GiveawayTarget.MaxInputLength), Autocomplete(typeof(ActiveGiveawayAutocomplete))] string giveaway)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync((await giveaways.EndAsync(Actor, giveaway, CancellationToken.None)).Result);
    }

    [SlashCommand("cancel", "Cancel an active giveaway without drawing")]
    public async Task CancelAsync(
        [Summary("giveaway", TargetDescription), MaxLength(GiveawayTarget.MaxInputLength), Autocomplete(typeof(ActiveGiveawayAutocomplete))] string giveaway)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync((await giveaways.CancelAsync(Actor, giveaway, CancellationToken.None)).Result);
    }

    [SlashCommand("reroll", "Draw new winners for a finished giveaway")]
    public async Task RerollAsync(
        [Summary("giveaway", TargetDescription), MaxLength(GiveawayTarget.MaxInputLength), Autocomplete(typeof(FinishedGiveawayAutocomplete))] string giveaway)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync((await giveaways.RerollAsync(Actor, giveaway, CancellationToken.None)).Result);
    }

    private ChannelId Here => new(Context.Interaction.ChannelId ?? Context.Channel.Id);

    /// <summary>The creator as members see them in this server (nickname / display name / username), as TSQ Randomizer and TSQ Saat.</summary>
    private string DisplayName() => Context.User is IGuildUser member ? member.DisplayName : Context.User.GlobalName ?? Context.User.Username;
}

/// <summary>
/// Suggestions for the giveaway option: this guild's active (end, cancel) or finished (reroll) giveaways, "#12 · prize",
/// newest first; the value is the number. Admins only, nothing while the module is disabled.
/// </summary>
public abstract class GiveawayAutocomplete(bool finished) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        if (ActorFactory.From(context) is not { } actor ||
            !await services.GetRequiredService<IModuleGate>().IsEnabledAsync(actor.GuildId, GiveawayModule.ModuleIdTyped, CancellationToken.None))
            return AutocompletionResult.FromSuccess();

        var suggestions = await services.GetRequiredService<GiveawayService>()
            .SuggestAsync(actor, finished, autocompleteInteraction.Data.Current.Value?.ToString(), CancellationToken.None);
        return AutocompletionResult.FromSuccess(suggestions.Select(s => new AutocompleteResult(s.Label, s.Value)));
    }
}

public sealed class ActiveGiveawayAutocomplete() : GiveawayAutocomplete(finished: false);

public sealed class FinishedGiveawayAutocomplete() : GiveawayAutocomplete(finished: true);
