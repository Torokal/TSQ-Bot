using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Agenda.Application;

namespace ToroSquad.Modules.Agenda.Commands;

/// <summary>
/// /ajanda [spor] [donem] and /gundem — two flat commands for everyone (guild only, checked again at run time by
/// <see cref="ToroModuleAttribute"/>, which also answers privately while the module is off). Each answers with ONE public card
/// in the channel it was used in; its buttons (◀ / ▶ / 🔄) edit that same message. Everything goes out with
/// allowed_mentions = none.
/// </summary>
[ToroModule(AgendaModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class AgendaCommands(InteractionServices services, AgendaFlow flow) : ToroInteractionModule(services), IAgendaResponder
{
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [SlashCommand("ajanda", "Upcoming F1, CS2 and volleyball programme")]
    public Task AgendaAsync(
        [Summary("spor", "Which sport (default: all sports)"),
         Choice("Tüm sporlar", AgendaOptions.SportAll), Choice("Formula 1", AgendaOptions.SportF1), Choice("CS2", AgendaOptions.SportCs2),
         Choice("Voleybol", AgendaOptions.SportVolleyball)] string? spor = null,
        [Summary("donem", "Which period (default: the next 7 days)"),
         Choice("Bugün", AgendaOptions.PeriodToday), Choice("Yarın", AgendaOptions.PeriodTomorrow), Choice("Önümüzdeki 7 gün", AgendaOptions.PeriodWeek),
         Choice("Önümüzdeki 14 gün", AgendaOptions.PeriodTwoWeeks)] string? donem = null) =>
        flow.CommandAsync(Request(AgendaView.Agenda, AgendaOptions.Sport(spor), AgendaOptions.Period(donem)), this, CancellationToken.None);

    [SlashCommand("gundem", "Today's rest and tomorrow: sports, LFG and predictions in one card")]
    public Task DigestAsync() =>
        flow.CommandAsync(Request(AgendaView.Digest, AgendaSport.All, AgendaPeriod.Today), this, CancellationToken.None);

    [ComponentInteraction(AgendaCustomId.Prefix + "*", ignoreGroupNames: true)]
    public Task ButtonAsync(string _)
    {
        var component = (IComponentInteraction)Context.Interaction;
        var click = new AgendaClick(new GuildId(Context.Guild.Id), new ChannelId(Context.Interaction.ChannelId ?? 0), new UserId(Context.User.Id),
            new MessageId(component.Message.Id), component.Message.Timestamp, component.Data.CustomId);
        return flow.ButtonAsync(click, this, CancellationToken.None);
    }

    private AgendaRequest Request(AgendaView view, AgendaSport sport, AgendaPeriod period) =>
        new(new GuildId(Context.Guild.Id), new ChannelId(Context.Interaction.ChannelId ?? 0), new UserId(Context.User.Id), view, sport, period);

    Task IAgendaResponder.DeferPublicAsync() => Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync(ephemeral: false);

    Task IAgendaResponder.ReplyPublicAsync(AgendaCard card) =>
        SendAsync(null, DiscordConversions.ToEmbed(card.Embed), DiscordConversions.ToComponents(card.Buttons), ephemeral: false);

    Task IAgendaResponder.ReplyPrivateAsync(string text) => SendEphemeralAsync(text, null, null);

    Task IAgendaResponder.DeferUpdateAsync() => Context.Interaction.HasResponded ? Task.CompletedTask : DeferAsync();

    Task IAgendaResponder.UpdateAsync(AgendaCard card) =>
        Context.Interaction.ModifyOriginalResponseAsync(m =>
        {
            m.Content = "";
            m.Embeds = new[] { DiscordConversions.ToEmbed(card.Embed)! };
            m.Components = DiscordConversions.ToComponents(card.Buttons) ?? new ComponentBuilder().Build();
            m.AllowedMentions = NoPings;
        });
}
