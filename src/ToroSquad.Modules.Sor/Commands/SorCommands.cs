using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ToroSquad.Core.Guilds;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Domain;

namespace ToroSquad.Modules.Sor.Commands;

/// <summary>
/// /sor and its answer buttons. Guild only. Identity, roles, channel and the asked-about member come from the interaction;
/// a raw mention in the question is resolved with ONE member lookup at most (no member list, no presence, no new intent).
/// The visibility is fixed by the acknowledgement: a private answer is never turned into a channel message. "Devam et" and
/// "Yeni sohbet" open their form at once (a modal must be the first answer — it cannot follow a deferral) and belong to the
/// conversation's owner only (<see cref="SorConversationGate"/>, checked again on submit). Every Discord call of an answer
/// goes through <see cref="SorInteractionResponder"/>: nothing sent here pings.
/// </summary>
[ToroModule(SorModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class SorCommands(InteractionServices services, SorService sor, SorStore store, ILogger<SorCommands> logger)
    : ToroInteractionModule(services)
{
    public const int MaxQuestionOption = 4000;

    [SlashCommand("sor", "Ask TSQ Sor anything")]
    public async Task AskAsync(
        [Summary("soru", "Your question"), MinLength(1), MaxLength(MaxQuestionOption)] string soru,
        [Summary("gizli", "Only you see the answer (default: no)"), Choice("evet", "evet"), Choice("hayır", "hayir")] string? gizli = null,
        [Summary("yeni", "Start a new conversation; your memory is kept (default: no)"), Choice("evet", "evet"), Choice("hayır", "hayir")] string? yeni = null,
        [Summary("kisi", "The member your question is about (optional)")] IUser? kisi = null)
    {
        var about = kisi is null ? await MentionedMemberAsync(soru) : Person(kisi);
        await RunAsync(soru, gizli == "evet", yeni == "evet", null, about);
    }

    [ComponentInteraction(SorUi.ContinuePrefix + "*:*:*", ignoreGroupNames: true)]
    public Task ContinueAsync(string conversation, string owner, string visibility) => OpenFormAsync(conversation, owner, visibility, isNew: false);

    [ComponentInteraction(SorUi.NewPrefix + "*:*:*", ignoreGroupNames: true)]
    public Task NewAsync(string conversation, string owner, string visibility) => OpenFormAsync(conversation, owner, visibility, isNew: true);

    [ModalInteraction(SorUi.ContinueModalPrefix + "*:*:*", ignoreGroupNames: true)]
    public Task ContinueSubmitAsync(string conversation, string owner, string visibility, SorQuestionModal modal) =>
        SubmitAsync(conversation, owner, visibility, modal, isNew: false);

    [ModalInteraction(SorUi.NewModalPrefix + "*:*:*", ignoreGroupNames: true)]
    public Task NewSubmitAsync(string conversation, string owner, string visibility, SorQuestionModal modal) =>
        SubmitAsync(conversation, owner, visibility, modal, isNew: true);

    /// <summary>
    /// Only the owner opens the form; nothing is closed or changed until a valid question is submitted (dismissing the form
    /// changes nothing). No deferral before the modal: the modal must be this click's first answer.
    /// </summary>
    private async Task OpenFormAsync(string conversation, string owner, string visibility, bool isNew)
    {
        var args = conversation + ":" + owner + ":" + visibility;
        if (await SorConversationGate.RefusalAsync(store, Context.Guild.Id, Context.User.Id, args, CancellationToken.None) is { } refusal)
        {
            await ReplyTextAsync(refusal);
            return;
        }

        var language = await LangAsync();
        await RespondWithModalAsync(SorUi.FormFor(args, isNew, key => Localizer.Get(language, key), MaxQuestionOption));
    }

    private async Task SubmitAsync(string conversation, string owner, string visibility, SorQuestionModal modal, bool isNew)
    {
        var args = conversation + ":" + owner + ":" + visibility;
        var refusal = await SorConversationGate.RefusalAsync(store, Context.Guild.Id, Context.User.Id, args, CancellationToken.None);
        if (refusal is not null || !SorUi.TryParse(args, out var id, out _, out var isPrivate))
        {
            await ReplyTextAsync(refusal ?? "sor.button_invalid");
            return;
        }

        var question = modal.Question ?? "";
        var about = await MentionedMemberAsync(question);
        // Visibility comes from the conversation, not from the form: a private conversation stays private. The module gate
        // ([ToroModule]) ran for this submit, and SorService checks access, quota and versions again.
        await RunAsync(question, isPrivate, isNew, isNew ? null : id, about);
    }

    private async Task RunAsync(string question, bool isPrivate, bool isNew, long? conversation, SorPerson? about)
    {
        var settings = await SettingsAsync();
        var zone = GuildTime.TryResolve(settings.TimeZoneId, out var resolved) ? resolved : TimeZoneInfo.Utc;
        var actor = Actor; // roles from the interaction payload: no REST read
        var channel = Context.Interaction.ChannelId ?? Context.Channel.Id;
        ulong? parent = Context.Channel is SocketThreadChannel thread ? thread.ParentChannel?.Id : null;
        var asker = Context.User is IGuildUser member ? member.DisplayName : Context.User.GlobalName ?? Context.User.Username;
        var ask = new SorAsk(actor, Context.Interaction.Id, channel, parent, question, isPrivate, isNew, conversation, asker, about, settings.Language, zone);
        await sor.AskAsync(ask, new SorInteractionResponder(Context.Interaction, key => Localizer.Get(settings.Language, key), logger));
    }

    private static SorPerson Person(IUser user) =>
        new(user.Id, user is IGuildUser member ? member.DisplayName : user.GlobalName ?? user.Username, user.IsBot);

    /// <summary>A raw &lt;@id&gt; in the question: ONE member lookup (REST), only for that id; nothing when it fails.</summary>
    private async Task<SorPerson?> MentionedMemberAsync(string question)
    {
        if (SorText.FirstUserMention(question) is not { } id || id == Context.User.Id)
            return null;
        try
        {
            var member = await Context.Client.Rest.GetGuildUserAsync(Context.Guild.Id, id);
            return member is null ? null : Person(member);
        }
        catch (HttpException ex)
        {
            logger.LogDebug(ex, "Sor: the mentioned member could not be resolved");
            return null;
        }
    }
}

/// <summary>Who may open or submit a conversation's form: the owner named in the button AND stored for the conversation.</summary>
public static class SorConversationGate
{
    /// <summary>
    /// Null when <paramref name="clicker"/> may continue: the button's owner is the clicker and the stored owner (if the
    /// conversation still exists — it may have expired) is the clicker too. A forged id or owner never opens someone else's
    /// history: the service loads a conversation only for its owner anyway.
    /// </summary>
    public static async Task<string?> RefusalAsync(SorStore store, ulong guild, ulong clicker, string args, CancellationToken ct)
    {
        if (!SorUi.TryParse(args, out var id, out var owner, out _))
            return "sor.button_invalid";
        if (owner != clicker)
            return "sor.not_your_conversation";
        var stored = await store.OwnerOfAsync(guild, id, ct);
        return stored is { } s && s != clicker ? "sor.not_your_conversation" : null;
    }
}
