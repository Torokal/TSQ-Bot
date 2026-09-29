using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Predictions.Application;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Commands;

/// <summary>
/// /ongoru — one group for members and managers alike, so it carries NO default_member_permissions (hiding it would hide the
/// wallet, daily reward and leaderboards from members); every subcommand authorizes itself in the services instead
/// (channel, creator role, manager, administrator). Predictions channel: yarat, kilitle, sonuclandir, iptal. Commands
/// channel: cuzdan, gunluk, tahminlerim, liderlik, turnuva durum|bitir. Private answers except the leaderboard and the
/// tournament status; nothing here ever pings.
/// </summary>
[ToroModule(PredictionsModule.ModuleIdValue)]
[Group("ongoru", "TSQ Öngörü: fixed-odds community predictions with virtual TSQ Coin")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class PredictionCommands(InteractionServices services, PredictionService predictions, PredictionEconomy economy)
    : PredictionInteractionModule(services)
{
    public const string TargetDescription = "Prediction number (#12), card link or message ID";

    [SlashCommand("yarat", "Create a prediction in this channel (opens a form)")]
    public async Task CreateAsync()
    {
        if (await RefuseBotAsync())
            return;
        var (refusal, draftId, values) = await predictions.OpenFormAsync(Actor, Here, CancellationToken.None);
        if (refusal is not null || draftId is null)
        {
            await ReplyResultAsync(refusal ?? PredictionService.NotFound());
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(PredictionFormUi.CreateModal(draftId, values, key => Localizer.Get(language, key)));
    }

    [SlashCommand("kilitle", "Stop new entries to a prediction now")]
    public async Task LockAsync(
        [Summary("ongoru", TargetDescription), MaxLength(PredictionTarget.MaxInputLength), Autocomplete(typeof(PredictionTargetAutocomplete))] string ongoru)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await predictions.LockAsync(Actor, Here, ongoru, CancellationToken.None));
    }

    [SlashCommand("sonuclandir", "Choose the winning outcome and pay out")]
    public async Task SettleAsync(
        [Summary("ongoru", TargetDescription), MaxLength(PredictionTarget.MaxInputLength), Autocomplete(typeof(PredictionTargetAutocomplete))] string ongoru)
    {
        await DeferEphemeralAsync();
        await ReplyViewAsync(await predictions.StartSettleAsync(Actor, Here, ongoru, CancellationToken.None));
    }

    [SlashCommand("iptal", "Cancel a prediction and refund every stake")]
    public async Task CancelAsync(
        [Summary("ongoru", TargetDescription), MaxLength(PredictionTarget.MaxInputLength), Autocomplete(typeof(PredictionTargetAutocomplete))] string ongoru,
        [Summary("gerekce", "Why it is cancelled (shown on the card)"), MinLength(PredictionRules.CancelReasonMinLength), MaxLength(PredictionRules.CancelReasonMaxLength)] string gerekce)
    {
        await DeferEphemeralAsync();
        await ReplyViewAsync(await predictions.PreviewCancelAsync(Actor, Here, ongoru, gerekce, CancellationToken.None));
    }

    [SlashCommand("cuzdan", "Your TSQ Coin balance, pending coins and record")]
    public async Task WalletAsync()
    {
        await DeferEphemeralAsync();
        await ReplyViewAsync(await economy.WalletAsync(Actor, Here, CancellationToken.None));
    }

    [SlashCommand("gunluk", "Claim your daily TSQ Coin reward")]
    public async Task DailyAsync()
    {
        if (await RefuseBotAsync())
            return;
        await DeferEphemeralAsync();
        await ReplyResultAsync((await economy.ClaimDailyAsync(Actor, Here, CancellationToken.None)).Result);
    }

    [SlashCommand("tahminlerim", "Your entries in the current tournament")]
    public async Task MineAsync()
    {
        await DeferEphemeralAsync();
        await ReplyViewAsync(await economy.MyEntriesAsync(Actor, Here, 0, CancellationToken.None));
    }

    [SlashCommand("liderlik", "Coin and correct-prediction leaderboards")]
    public async Task LeaderboardAsync() =>
        await ReplyViewAsync(await economy.LeaderboardAsync(Actor, Here, CancellationToken.None)); // public: answered directly, never deferred privately

    [ToroModule(PredictionsModule.ModuleIdValue)]
    [Group("turnuva", "The current prediction tournament")]
    public sealed class TournamentCommands(InteractionServices services, PredictionEconomy economy) : PredictionInteractionModule(services)
    {
        [SlashCommand("durum", "Show the current tournament and its numbers")]
        public async Task StatusAsync() =>
            await ReplyViewAsync(await economy.TournamentStatusAsync(Actor, Here, CancellationToken.None));

        [SlashCommand("bitir", "End the tournament and reset balances (administrators)")]
        public async Task EndAsync()
        {
            await DeferEphemeralAsync();
            await ReplyViewAsync(await economy.PreviewTournamentEndAsync(Actor, Here, CancellationToken.None));
        }
    }
}

/// <summary>Shared helpers of the TSQ Öngörü interaction classes: the exact channel, the view replies, the bot refusal.</summary>
public abstract class PredictionInteractionModule(InteractionServices services) : ToroInteractionModule(services)
{
    /// <summary>The channel the interaction was used in — a thread is its own id, never its parent's.</summary>
    protected ChannelId Here => new(Context.Interaction.ChannelId ?? 0);

    /// <summary>The member as others see them in this server (nickname / display name / username), as TSQ Çekiliş does.</summary>
    protected string DisplayName() => Context.User is IGuildUser member ? member.DisplayName : Context.User.GlobalName ?? Context.User.Username;

    /// <summary>Bots never create, enter or claim (Discord does not send their interactions, but nothing trusts that).</summary>
    protected async Task<bool> RefuseBotAsync()
    {
        if (!Context.User.IsBot)
            return false;
        await ReplyTextAsync("predictions.bots");
        return true;
    }

    /// <summary>The reply's view (private unless it is marked public), or its result text; never pings.</summary>
    protected async Task ReplyViewAsync(PredictionReply reply)
    {
        if (reply.View is not { } view)
        {
            await ReplyResultAsync(reply.Result);
            return;
        }

        await SendAsync(view.Content, DiscordConversions.ToEmbed(view.Embed), DiscordConversions.ToComponents(view), ephemeral: !reply.Public);
    }
}

/// <summary>
/// Suggestions for the ongoru option: this guild's open and locked predictions the caller may manage ("#12 · Açık · title"),
/// newest first; the value is the number. Nothing while the module is disabled or for members who manage nothing.
/// </summary>
public sealed class PredictionTargetAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction,
        IParameterInfo parameter, IServiceProvider services)
    {
        if (ActorFactory.From(context) is not { } actor ||
            !await services.GetRequiredService<IModuleGate>().IsEnabledAsync(actor.GuildId, PredictionsModule.ModuleIdTyped, CancellationToken.None))
            return AutocompletionResult.FromSuccess();

        var suggestions = await services.GetRequiredService<PredictionService>()
            .SuggestAsync(actor, autocompleteInteraction.Data.Current.Value?.ToString(), CancellationToken.None);
        return AutocompletionResult.FromSuccess(suggestions.Select(s => new AutocompleteResult(s.Label, s.Value)));
    }
}
