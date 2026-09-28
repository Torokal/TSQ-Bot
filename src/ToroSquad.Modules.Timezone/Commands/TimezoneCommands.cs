using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Timezone.Application;

namespace ToroSquad.Modules.Timezone.Commands;

/// <summary>
/// /saat — for everyone, guild only. The card is public; an invalid time gets a short private refusal instead (refusals
/// private, results where they belong — as every other TSQ command). Answered at once (no defer: nothing here waits on
/// I/O), never pings (<see cref="ToroInteractionModule"/> sends every reply without allowed mentions). "Now" comes from the
/// host's <see cref="TimeProvider"/> (a fake clock in tests). Only metadata is logged, at Debug level — never the time.
/// </summary>
[ToroModule(TimezoneModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class TimezoneCommands(InteractionServices services, TimezoneCards cards, ILogger<TimezoneCommands> logger)
    : ToroInteractionModule(services)
{
    [SlashCommand("saat", "Convert a time in Türkiye to other time zones")]
    public async Task ConvertAsync(
        [Summary("time", "Time in Türkiye (today), e.g. 21:00 or 9.30"), MinLength(1), MaxLength(ClockInput.MaxInputLength)] string time)
    {
        logger.LogDebug("Timezone /saat guild={Guild} user={User} input_length={Length}", Actor.GuildId, Actor.UserId, time.Length);
        await AnswerAsync(cards.Convert(await LangAsync(), time, Services.Clock.GetUtcNow(), DisplayName()));
    }

    private Task AnswerAsync(TimezoneReply reply) =>
        reply.Card is { } card
            ? SendAsync(null, DiscordConversions.ToEmbed(card), null, ephemeral: false)
            : SendEphemeralAsync(reply.Refusal, null, null);

    /// <summary>
    /// The invoking member's name as members see it in this server (server nickname / global display name / username), else
    /// global display name, else username — the same order as TSQ Quote and TSQ Randomizer. Defused by <see cref="TimezoneCards"/>.
    /// </summary>
    private string DisplayName() => Context.User is IGuildUser member ? member.DisplayName : Context.User.GlobalName ?? Context.User.Username;
}
