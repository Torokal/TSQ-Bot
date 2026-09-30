using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Admin;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using DiscordPermission = Discord.GuildPermission;

namespace ToroSquad.Discord.Commands.Core;

/// <summary>
/// <c>/tsq-admin modul:&lt;module&gt; islem:&lt;operation&gt; [kanal] [uye] [rol] [tarih]</c> — ONE flat slash command (no
/// subcommands) for every module's admin operations, plus the buttons, selects and modals of their private forms
/// (<c>tsq:adm:&lt;draft&gt;:&lt;action&gt;</c>). The root is core's and hidden behind Manage Server; each operation is routed by
/// <see cref="AdminRouter"/> to its module, which authorizes it again. This class only adapts Discord to
/// <see cref="IAdminResponder"/>; no module logic lives here.
/// </summary>
[ToroModule("core")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class TsqAdminCommands(InteractionServices services, AdminRouter router, IServiceProvider provider)
    : ToroInteractionModule(services), IAdminResponder
{
    [SlashCommand(AdminCatalog.Name, $"{ProductInfo.ProductName} administration: pick a module and an operation")]
    public async Task RunAsync(
        [Summary("modul", "Module"), Autocomplete(typeof(AdminModuleAutocomplete)), MaxLength(32)] string modul,
        [Summary("islem", "Operation (pick the module first)"), Autocomplete(typeof(AdminOperationAutocomplete)), MaxLength(40)] string islem,
        [Summary("kanal", "Channel, for operations that take one"), ChannelTypes(ChannelType.Text, ChannelType.News)] IChannel? kanal = null,
        [Summary("uye", "Member, for the birthday operations")] IUser? uye = null,
        [Summary("rol", "Role, for operations that take one")] IRole? rol = null,
        [Summary("tarih", "Day and month, e.g. 14.03 (birthday)"), MaxLength(10)] string? tarih = null)
    {
        var actor = Actor;
        if (kanal is not null && !AdminRouter.IsUsableChannel(kanal, actor.GuildId))
        {
            await ReplyTextAsync("admin.error.channel_invalid");
            return;
        }

        var provided = (kanal is null ? AdminFields.None : AdminFields.Channel) | (uye is null ? AdminFields.None : AdminFields.User) |
                       (rol is null ? AdminFields.None : AdminFields.Role) | (string.IsNullOrWhiteSpace(tarih) ? AdminFields.None : AdminFields.Date);
        var args = new AdminArgs(kanal?.Id, uye is null ? null : AdminMember.From(uye, actor.GuildId), rol?.Id, string.IsNullOrWhiteSpace(tarih) ? null : tarih.Trim());
        await router.RunAsync(new AdminRequest(actor, await LangAsync(), modul, islem, args, provided), this, provider);
    }

    [ComponentInteraction(AdminCall.CustomIdPrefix + "*:*", ignoreGroupNames: true)]
    public async Task FormComponentAsync(string draft, string action)
    {
        var input = AdminInput.FromComponent(((IComponentInteraction)Context.Interaction).Data);
        await router.FormAsync(Actor, await LangAsync(), draft, action, input, this, provider);
    }

    [ModalInteraction(AdminCall.CustomIdPrefix + "*:*", ignoreGroupNames: true)]
    public async Task FormModalAsync(string draft, string action, AdminFormModal modal)
    {
        var input = AdminInput.FromModal(((IModalInteraction)Context.Interaction).Data);
        await router.FormAsync(Actor, await LangAsync(), draft, action, input, this, provider);
    }

    Task IAdminResponder.DeferAsync() =>
        Context.Interaction is ISlashCommandInteraction && !Context.Interaction.HasResponded ? DeferAsync(ephemeral: true) : Task.CompletedTask;

    async Task<string> IAdminResponder.DescribeAsync(OperationResult result) =>
        await T(result.MessageKey, result.Args.ToArray()) + await TraceLineAsync(result);

    Task IAdminResponder.SendAsync(string? text, MessageEmbed? embed, MessageComponent? components, bool ephemeral) =>
        SendAsync(text, embed is null ? null : DiscordConversions.ToEmbed(embed), components, ephemeral);

    async Task IAdminResponder.UpdateAsync(string? text, MessageComponent? components, MessageEmbed? embed)
    {
        var none = DiscordConversions.ToAllowedMentions(MentionPolicy.None);
        var discordEmbed = embed is null ? null : DiscordConversions.ToEmbed(embed);
        void Edit(MessageProperties m)
        {
            m.Content = text ?? "";
            m.Components = components ?? AdminCall.EmptyComponents;
            m.Embeds = discordEmbed is null ? Array.Empty<Embed>() : new[] { discordEmbed };
            m.AllowedMentions = none;
        }

        switch (Context.Interaction)
        {
            case IComponentInteraction component when !Context.Interaction.HasResponded:
                await component.UpdateAsync(Edit);
                break;
            case global::Discord.WebSocket.SocketModal { Message: not null } modal when !Context.Interaction.HasResponded:
                await modal.UpdateAsync(Edit);
                break;
            default:
                await SendAsync(text, discordEmbed, components, ephemeral: true);
                break;
        }
    }

    Task IAdminResponder.ModalAsync(Modal modal) => RespondWithModalAsync(modal);
}

/// <summary>Admin forms read their modal fields by custom id (<see cref="AdminInput"/>); nothing is bound here.</summary>
public sealed class AdminFormModal : IModal
{
    public string Title => "";
}
