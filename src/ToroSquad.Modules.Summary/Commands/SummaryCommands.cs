using Discord;
using Discord.Interactions;
using Discord.Net;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Summary.Application;

namespace ToroSquad.Modules.Summary.Commands;

/// <summary>
/// /ozetle — for everyone, guild only, no options: summarizes the channel or thread it ran in. Refusals (not configured,
/// unsupported channel, no access, cooldown, busy) are private and immediate. Otherwise the command is acknowledged privately
/// (only the member sees "thinking"), and the summary is posted as normal public message(s): the private acknowledgement is
/// settled first so the summary is a separate public message (the first follow-up of a deferred reply would otherwise take
/// the deferred reply's place — and its privacy), then removed. Every error after the acknowledgement replaces it privately:
/// nothing half-done is ever left in the channel. Nothing sent here pings (no allowed mentions).
/// </summary>
[ToroModule(SummaryModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class SummaryCommands(InteractionServices services, SummaryService summaries, ILogger<SummaryCommands> logger)
    : ToroInteractionModule(services), ISummaryResponder
{
    private static AllowedMentions NoPings => DiscordConversions.ToAllowedMentions(MentionPolicy.None);

    [SlashCommand("ozetle", "Summarize the latest messages in this channel")]
    public async Task SummarizeAsync()
    {
        var settings = await SettingsAsync();
        var zone = GuildTime.TryResolve(settings.TimeZoneId, out var resolved) ? resolved : TimeZoneInfo.Utc;
        var request = new SummaryRequest(Actor.GuildId, new ChannelId(Context.Interaction.ChannelId ?? Context.Channel.Id), Actor.UserId,
            settings.Language, zone);
        await summaries.RunAsync(request, new DiscordSummarySource(Context.Client, (IGuildUser)Context.User), this);
    }

    Task ISummaryResponder.ReplyPrivateAsync(string text) => SendEphemeralAsync(text, null, null);

    Task ISummaryResponder.DeferPrivateAsync() => DeferEphemeralAsync();

    async Task<bool> ISummaryResponder.PostPublicAsync(IReadOnlyList<string> parts)
    {
        var language = await LangAsync();
        try
        {
            await Context.Interaction.ModifyOriginalResponseAsync(m =>
            {
                m.Content = Localizer.Get(language, "summary.posting");
                m.AllowedMentions = NoPings;
            });
            foreach (var part in parts)
                await SendAsync(part, null, null, ephemeral: false);
        }
        catch (HttpException ex)
        {
            logger.LogWarning(ex, "Summary not posted in guild {Guild} channel {Channel}: Discord answered {Status}",
                Context.Interaction.GuildId, Context.Interaction.ChannelId, ex.HttpCode);
            try
            {
                await Context.Interaction.ModifyOriginalResponseAsync(m =>
                {
                    m.Content = Localizer.Get(language, "summary.post_failed");
                    m.AllowedMentions = NoPings;
                });
            }
            catch (HttpException inner)
            {
                logger.LogDebug(inner, "Summary: the private acknowledgement could not be updated");
            }

            return false;
        }

        try
        {
            await Context.Interaction.DeleteOriginalResponseAsync(); // the summary is the answer; the private note is no longer needed
        }
        catch (HttpException ex)
        {
            logger.LogDebug(ex, "Summary: the private acknowledgement could not be removed");
        }

        return true;
    }
}
