using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Giveaway.Domain;

namespace ToroSquad.Modules.Giveaway.Application;

/// <summary>A giveaway as stored, with the winners of its latest draw (place order).</summary>
public sealed record GiveawayView(
    long Id,
    GuildId Guild,
    ChannelId Channel,
    MessageId? Message,
    UserId Creator,
    string CreatorName,
    string Prize,
    string? Description,
    int WinnerCount,
    GiveawayStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset EndsAt,
    DateTimeOffset? EndedAt,
    int? EntrantCount,
    int RerollCount,
    IReadOnlyList<UserId> Winners);

/// <summary>
/// The one giveaway card, edited in place through its life, in ONE layout for every state so the eye always finds the same
/// things in the same place: the state as the title; the prize as a heading right under it (the largest text on the card —
/// "what is being given away" is answered first); then compact fields — active: winner count and end side by side (the
/// relative time first, the full date below), how to enter; drawn: the winners (🥇🥈🥉, then numbered), entrants and end;
/// cancelled: its status. The admin's optional description is secondary and always comes last; the footer names who
/// started it (plain text, defused) with the giveaway number used by /giveaway end|cancel|reroll. Winners are user mentions
/// inside the embed and every send and edit goes out with allowed_mentions = none: the card never pings (the separate
/// winner announcement does, see <see cref="GiveawayAnnouncementRenderer"/>). Prize and description are the admin's own
/// text and are defused like every untrusted string (so only the bot's own "## " makes the heading).
/// </summary>
public sealed class GiveawayCards(ILocalizer localizer)
{
    public const uint ActiveColor = 0xE8590C; // the TSQ brand colour
    public const uint FinishedColor = 0x57F287;
    public const uint EndedColor = 0x747F8D;

    public const int CreatorNameMax = 64;

    private static readonly string[] Medals = ["🥇", "🥈", "🥉"];

    public OutgoingMessage Render(GiveawayView giveaway, string language)
    {
        string L(string key, params object?[] args) => Format(language, key, args);

        var fields = new List<EmbedField>();
        string title;
        uint color;
        switch (giveaway.Status)
        {
            case GiveawayStatus.Finished:
                title = L("giveaway.card.title_finished");
                color = FinishedColor;
                fields.Add(giveaway.Winners.Count == 0
                    ? new(L("giveaway.card.winner"), L("giveaway.card.no_entrants"))
                    : new(L(giveaway.Winners.Count == 1 ? "giveaway.card.winner" : "giveaway.card.winners"), WinnerList(giveaway.Winners)));
                fields.Add(new(L("giveaway.card.entrants"), Number(giveaway.EntrantCount ?? 0), true));
                fields.Add(new(L("giveaway.card.ended"), DiscordText.Timestamp(giveaway.EndedAt ?? giveaway.EndsAt, 'f'), true));
                break;

            case GiveawayStatus.Cancelled or GiveawayStatus.Orphaned:
                title = L("giveaway.card.title_cancelled");
                color = EndedColor;
                fields.Add(new(L("giveaway.card.status"), L("giveaway.card.cancelled")));
                break;

            default:
                title = L("giveaway.card.title_active");
                color = ActiveColor;
                fields.Add(new(L("giveaway.card.winner_count"), "**" + Number(giveaway.WinnerCount) + "**", true));
                fields.Add(new(L("giveaway.card.end"), DiscordText.Timestamp(giveaway.EndsAt, 'R') + "\n" + DiscordText.Timestamp(giveaway.EndsAt, 'F'), true));
                fields.Add(new(L("giveaway.card.how_to_enter"), L("giveaway.card.join")));
                break;
        }

        if (!string.IsNullOrEmpty(giveaway.Description))
            fields.Add(new(L("giveaway.card.description"), DiscordText.Untrusted(giveaway.Description, GiveawayRules.DescriptionMaxLength * 2)));

        var creator = DiscordText.UntrustedPlain(giveaway.CreatorName, CreatorNameMax);
        var footer = giveaway.Status == GiveawayStatus.Finished && giveaway.RerollCount > 0
            ? L("giveaway.card.footer_rerolled", creator, Number(giveaway.Id), giveaway.RerollCount)
            : L("giveaway.card.footer", creator, Number(giveaway.Id));
        var embed = new MessageEmbed(title, PrizeBlock(giveaway.Prize, L("giveaway.card.prize")), null, fields, footer, null, color);
        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }

    /// <summary>The label, then the prize as a Discord markdown heading (defused first: the admin's text can never add its own).</summary>
    public static string PrizeBlock(string prize, string label) =>
        label + "\n## " + DiscordText.Untrusted(prize, GiveawayRules.PrizeMaxLength * 2);

    /// <summary>🥇 🥈 🥉 for the first three places, then "4." onwards; one winner per line.</summary>
    public static string WinnerList(IReadOnlyList<UserId> winners) =>
        string.Join("\n", winners.Select((user, i) => (i < Medals.Length ? Medals[i] : Number(i + 1) + ".") + " " + Mention(user)));

    internal static string Mention(UserId user) => string.Create(CultureInfo.InvariantCulture, $"<@{user.Value}>");

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the template here rather than through <see cref="ILocalizer"/>'s arguments: the localizer would replace a
    /// string argument that happens to be a catalog key (a prize typed as "help.title") with that key's text.
    /// </summary>
    private string Format(string language, string key, object?[] args) =>
        args.Length == 0 ? localizer.Get(language, key) : string.Format(CultureInfo.InvariantCulture, localizer.Get(language, key), args);
}
