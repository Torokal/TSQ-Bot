using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Sor.Application;
using ToroSquad.Modules.Sor.Domain;

namespace ToroSquad.Modules.Sor.Commands;

/// <summary>
/// /sor and its answer buttons. Guild only. Identity, roles, channel and the asked-about member come from the interaction;
/// a raw mention in the question is resolved with ONE member lookup at most (no member list, no presence, no new intent).
/// The visibility is fixed by the acknowledgement: a private answer is never turned into a channel message. "Devam et" and
/// "Yeni sohbet" open their form at once (a modal must be the first answer — it cannot follow a deferral) and belong to the
/// conversation's owner only. Nothing sent here pings (no allowed mentions, on every send and edit).
/// </summary>
[ToroModule(SorModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class SorCommands(InteractionServices services, SorService sor, SorStore store, ILogger<SorCommands> logger)
    : ToroInteractionModule(services), ISorResponder
{
    public const int MaxQuestionOption = 4000;

    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    private string _language = "tr";
    private bool _private = true;

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

    /// <summary>Only the owner opens the form; nothing is closed or changed until a valid question is submitted.</summary>
    private async Task OpenFormAsync(string conversation, string owner, string visibility, bool isNew)
    {
        if (await OwnerRefusalAsync(conversation, owner, visibility) is { } refusal)
        {
            await ReplyTextAsync(refusal);
            return;
        }

        var language = await LangAsync();
        string L(string key) => Localizer.Get(language, key);
        var args = conversation + ":" + owner + ":" + visibility;
        await RespondWithModalAsync(SorUi.QuestionModal((isNew ? SorUi.NewModalPrefix : SorUi.ContinueModalPrefix) + args,
            L(isNew ? "sor.form.new_title" : "sor.form.continue_title"), L("sor.form.label"), L(isNew ? "sor.form.new_placeholder" : "sor.form.continue_placeholder"),
            MaxQuestionOption));
    }

    private async Task SubmitAsync(string conversation, string owner, string visibility, SorQuestionModal modal, bool isNew)
    {
        if (await OwnerRefusalAsync(conversation, owner, visibility) is { } refusal)
        {
            await ReplyTextAsync(refusal);
            return;
        }

        if (!SorUi.TryParse(conversation + ":" + owner + ":" + visibility, out var id, out _, out var isPrivate))
            return;
        var question = modal.Question ?? "";
        var about = await MentionedMemberAsync(question);
        // Visibility comes from the conversation, not from the form: a private conversation stays private.
        await RunAsync(question, isPrivate, isNew, isNew ? null : id, about);
    }

    /// <summary>The clicker must be the owner named in the button AND the owner stored for the conversation (when it still exists).</summary>
    private async Task<string?> OwnerRefusalAsync(string conversation, string owner, string visibility)
    {
        if (!SorUi.TryParse(conversation + ":" + owner + ":" + visibility, out var id, out var ownerId, out _))
            return "sor.button_invalid";
        if (ownerId != Context.User.Id)
            return "sor.not_your_conversation";
        var stored = await store.OwnerOfAsync(Context.Guild.Id, id, CancellationToken.None);
        return stored is { } s && s != Context.User.Id ? "sor.not_your_conversation" : null;
    }

    private async Task RunAsync(string question, bool isPrivate, bool isNew, long? conversation, SorPerson? about)
    {
        var settings = await SettingsAsync();
        _language = settings.Language;
        var zone = GuildTime.TryResolve(settings.TimeZoneId, out var resolved) ? resolved : TimeZoneInfo.Utc;
        var actor = Actor; // roles from the interaction payload: no REST read
        var channel = Context.Interaction.ChannelId ?? Context.Channel.Id;
        ulong? parent = Context.Channel is SocketThreadChannel thread ? thread.ParentChannel?.Id : null;
        var asker = Context.User is IGuildUser member ? member.DisplayName : Context.User.GlobalName ?? Context.User.Username;
        var ask = new SorAsk(actor, Context.Interaction.Id, channel, parent, question, isPrivate, isNew, conversation, asker, about, settings.Language, zone);
        await sor.AskAsync(ask, this);
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

    private string L(string key, params object?[] args) => Localizer.Get(_language, key, args);

    Task ISorResponder.RefuseAsync(string text) => SendEphemeralAsync(text, null, null);

    Task ISorResponder.DeferAsync(bool isPrivate)
    {
        _private = isPrivate;
        return Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync(ephemeral: isPrivate);
    }

    async Task ISorResponder.FailAsync(string neutral, string detail)
    {
        var isPrivate = _private;
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = isPrivate ? detail : neutral;
                m.Embeds = Array.Empty<Embed>();
                m.Components = new ComponentBuilder().Build();
                m.AllowedMentions = NoPings;
            });
            if (!isPrivate)
                await FollowupAsync(detail, ephemeral: true, allowedMentions: NoPings);
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Sor: the failure message could not be shown (Discord {Status})", ex.HttpCode);
        }
    }

    async Task<SorDelivery> ISorResponder.DeliverAsync(SorReply reply)
    {
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = "";
                m.Embeds = new[] { SorUi.Card(reply, 0, null) };
                m.Components = SorUi.Buttons(reply, key => L(key));
                m.AllowedMentions = NoPings;
            });
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Sor: answer not delivered (Discord {Status})", ex.HttpCode);
            return SorDelivery.Failed;
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException)
        {
            // No confirmation: look once whether the answer is there before calling it delivered.
            return await ConfirmDeliveredAsync() ? SorDelivery.Delivered : SorDelivery.Unknown;
        }

        for (var part = 1; part < reply.Parts.Count; part++)
        {
            try
            {
                await FollowupAsync(embed: SorUi.Card(reply, part, null), ephemeral: reply.IsPrivate, allowedMentions: NoPings);
            }
            catch (Exception ex) when (ex is HttpException or TimeoutException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Sor: continuation {Part} of an answer was not delivered", part);
                break;
            }
        }

        return SorDelivery.Delivered;
    }

    private async Task<bool> ConfirmDeliveredAsync()
    {
        try
        {
            var message = await Context.Interaction.GetOriginalResponseAsync();
            return message?.Embeds.Any(e => e.Title == SorUi.Title) == true;
        }
        catch (Exception ex) when (ex is HttpException or TimeoutException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Sor: delivery could not be confirmed");
            return false;
        }
    }

    async Task ISorResponder.AddFooterAsync(SorReply reply, string line)
    {
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Embeds = new[] { SorUi.Card(reply, 0, line) };
                m.AllowedMentions = NoPings;
            });
        }
        catch (HttpException ex)
        {
            logger.LogDebug(ex, "Sor: the saved-note line could not be added");
        }
    }

    async Task ISorResponder.PrivateNoteAsync(string text, bool memoryButton) =>
        await FollowupAsync(text, components: memoryButton ? SorUi.MemoryButton(key => L(key)) : null, ephemeral: true, allowedMentions: NoPings);
}
