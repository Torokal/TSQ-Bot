using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Pfp.Application;

namespace ToroSquad.Modules.Pfp.Commands;

/// <summary>
/// /pfp and /banner — for everyone, guild only (registered for guilds only and checked again at run time by
/// <see cref="ToroModuleAttribute"/>). <c>uye</c> is Discord's native USER option (the member picker; the chosen id is used,
/// never a typed name), <c>tur</c> one of three fixed choices. The flow (<see cref="PfpCommandFlow"/>) decides: private
/// refusals before the acknowledgement, otherwise one public acknowledgement completed once with the card or a short notice.
/// Every reply goes through <see cref="ToroInteractionModule"/> (allowed mentions: none — nothing here pings).
/// </summary>
[ToroModule(PfpModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class PfpCommands(InteractionServices services, PfpCommandFlow flow, ILogger<PfpCommands> logger)
    : ToroInteractionModule(services), IPfpResponder
{
    [SlashCommand("pfp", "Show a member's profile picture large")]
    public Task AvatarAsync(
        [Summary("uye", "Member (default: you)")] IUser? uye = null,
        [Summary("tur", "Which profile picture (default: automatic)"),
         Choice("Otomatik", PfpScopes.Auto), Choice("Genel", PfpScopes.Global), Choice("Sunucu", PfpScopes.Server)] string? tur = null) =>
        RunAsync(PfpKind.Avatar, uye, tur);

    [SlashCommand("banner", "Show a member's profile banner large")]
    public Task BannerAsync(
        [Summary("uye", "Member (default: you)")] IUser? uye = null,
        [Summary("tur", "Which profile banner (default: automatic)"),
         Choice("Otomatik", PfpScopes.Auto), Choice("Genel", PfpScopes.Global), Choice("Sunucu", PfpScopes.Server)] string? tur = null) =>
        RunAsync(PfpKind.Banner, uye, tur);

    private async Task RunAsync(PfpKind kind, IUser? target, string? scope)
    {
        var request = new PfpRequest(Context.Guild.Id, Context.User.Id, TargetId(target, Context.User.Id), kind, scope, await LangAsync());
        await flow.RunAsync(request, new DiscordPfpProfileSource(Context.Client.Rest, logger), this);
    }

    /// <summary>The chosen member's id, or the member who ran the command when <c>uye</c> is left empty.</summary>
    public static ulong TargetId(IUser? chosen, ulong invoker) => chosen?.Id ?? invoker;

    Task IPfpResponder.ReplyPrivateAsync(string text) => SendEphemeralAsync(text, null, null);

    Task IPfpResponder.DeferPublicAsync() => Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync(ephemeral: false);

    Task IPfpResponder.ReplyPublicAsync(PfpReply reply) =>
        reply.Card is { } card
            ? SendAsync(null, ToEmbed(card), ToComponents(card), ephemeral: false)
            : SendAsync(reply.Text, null, null, ephemeral: false);

    /// <summary>The picture in the embed's image field (large), never only as a thumbnail.</summary>
    public static Embed ToEmbed(PfpCard card)
    {
        var builder = new EmbedBuilder().WithTitle(card.Title).WithImageUrl(card.ImageUrl).WithColor(new Color(card.Color));
        if (card.Description is { } description)
            builder.WithDescription(description);
        return builder.Build();
    }

    /// <summary>One link button ("Tam boy aç") to exactly the picture the embed shows.</summary>
    public static MessageComponent ToComponents(PfpCard card) =>
        new ComponentBuilder().WithButton(card.ButtonLabel, url: card.ImageUrl, style: ButtonStyle.Link).Build();
}
