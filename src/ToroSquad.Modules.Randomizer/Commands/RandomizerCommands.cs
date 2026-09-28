using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Randomizer.Application;

namespace ToroSquad.Modules.Randomizer.Commands;

/// <summary>
/// /zarat, /randomsayi, /sec, /yazitura — for everyone, guild only. The result card is public (everyone in the channel sees
/// it); an invalid input gets a short private refusal instead, so typos do not clutter the channel — the same split as every
/// other TSQ command (refusals private, results where they belong). Answered at once (no defer: nothing here waits on I/O),
/// never pings (<see cref="ToroInteractionModule"/> sends every reply without allowed mentions). Only metadata is logged, at
/// Debug level — never a result and never the text of an option.
/// </summary>
[ToroModule(RandomizerModule.ModuleIdValue)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class RandomizerCommands(InteractionServices services, RandomizerCards cards, ILogger<RandomizerCommands> logger)
    : ToroInteractionModule(services)
{
    [SlashCommand("zarat", "Roll the given dice")]
    public async Task RollAsync(
        [Summary("zar", "Number of dice and sides: 1-20, 2-6 or 2d6"), MinLength(1), MaxLength(DiceNotation.MaxInputLength)] string dice)
    {
        logger.LogDebug("Randomizer /zarat guild={Guild} user={User} input_length={Length}", Actor.GuildId, Actor.UserId, dice.Length);
        await AnswerAsync(cards.Dice(await LangAsync(), dice, DisplayName()));
    }

    [SlashCommand("randomsayi", "Pick a random number from the given range")]
    public async Task NumberAsync(
        [Summary("maksimum", "Largest value (inclusive)"), MinValue(-NumberRanges.Limit), MaxValue(NumberRanges.Limit)] long maximum,
        [Summary("minimum", "Smallest value (inclusive, default: 1)"), MinValue(-NumberRanges.Limit), MaxValue(NumberRanges.Limit)] long minimum = NumberRanges.DefaultMinimum)
    {
        logger.LogDebug("Randomizer /randomsayi guild={Guild} user={User} range={Minimum}..{Maximum}", Actor.GuildId, Actor.UserId, minimum, maximum);
        await AnswerAsync(cards.RandomNumber(await LangAsync(), maximum, minimum, DisplayName()));
    }

    [SlashCommand("sec", "Pick one of the given options at random")]
    public async Task ChooseAsync(
        [Summary("seçenekler", "Options separated by commas or |, e.g. CS2, Valheim | WoW"), MinLength(1), MaxLength(ChoiceList.MaxInputLength)] string options)
    {
        logger.LogDebug("Randomizer /sec guild={Guild} user={User} input_length={Length}", Actor.GuildId, Actor.UserId, options.Length);
        await AnswerAsync(cards.Choose(await LangAsync(), options, DisplayName()));
    }

    [SlashCommand("yazitura", "Flip a coin")]
    public async Task FlipAsync()
    {
        logger.LogDebug("Randomizer /yazitura guild={Guild} user={User}", Actor.GuildId, Actor.UserId);
        await AnswerAsync(cards.CoinFlip(await LangAsync(), DisplayName()));
    }

    private Task AnswerAsync(RandomizerReply reply) =>
        reply.Card is { } card
            ? SendAsync(null, DiscordConversions.ToEmbed(card), null, ephemeral: false)
            : SendEphemeralAsync(reply.Refusal, null, null);

    private string DisplayName() => DisplayNameOf(Context.User);

    /// <summary>
    /// The invoking member's name as members see it in this server, in the same order as TSQ Quote: server nickname / global
    /// display name / username (<see cref="IGuildUser.DisplayName"/>), else global display name, else username. Raw user
    /// content: <see cref="RandomizerCards"/> defuses it before it is shown.
    /// </summary>
    public static string DisplayNameOf(IUser user) => user is IGuildUser member ? member.DisplayName : user.GlobalName ?? user.Username;
}
