using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Giveaway.Domain;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>
/// The short winner announcement under the card, one per draw or reroll: the ONLY giveaway message that pings, and only the
/// winners of that draw (<see cref="MentionPolicy.ExplicitUsers"/>, at most <see cref="GiveawayRules.MaxWinners"/> ids from
/// the database; nothing parsed from text, no role, @everyone or @here). The card edit itself never pings, so without this
/// line a winner would not be told. Delivered through the outbox (at most once, never late).
/// </summary>
public sealed class GiveawayAnnouncementRenderer(ILocalizer localizer)
{
    public OutgoingMessage Render(GiveawayView giveaway, IReadOnlyList<UserId> winners, int round, string language)
    {
        var key = (round == 0, winners.Count == 1) switch
        {
            (true, true) => "giveaway.announce.one",
            (true, false) => "giveaway.announce.many",
            (false, true) => "giveaway.announce.reroll_one",
            _ => "giveaway.announce.reroll_many",
        };
        var text = string.Format(CultureInfo.InvariantCulture, localizer.Get(language, key),
            string.Join(" ", winners.Select(GiveawayCards.Mention)), DiscordText.Untrusted(giveaway.Prize, GiveawayRules.PrizeMaxLength * 2));
        if (giveaway.Message is { } message)
            text += "\n" + string.Create(CultureInfo.InvariantCulture, $"https://discord.com/channels/{giveaway.Guild.Value}/{giveaway.Channel.Value}/{message.Value}");
        return new OutgoingMessage(text, null, MentionPolicy.ExplicitUsers(winners));
    }
}
