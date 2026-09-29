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
/// The one giveaway card, edited in place through its life: active (prize, winner count, end as Discord timestamps, how to
/// enter), drawn (winners 🥇🥈🥉 then numbered, entrants, end) or cancelled. The TSQ card style: emoji title, compact fields,
/// who started it in the footer (plain text, defused) with the giveaway number used by /giveaway end|cancel|reroll. Winners
/// are user mentions inside the embed and every send and edit goes out with allowed_mentions = none: the card never pings
/// (the separate winner announcement does, see <see cref="GiveawayAnnouncementRenderer"/>). Prize and description are the
/// admin's own text and are defused like every untrusted string.
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

        var prize = DiscordText.Untrusted(giveaway.Prize, GiveawayRules.PrizeMaxLength * 2);
        var description = string.IsNullOrEmpty(giveaway.Description) ? null : DiscordText.Untrusted(giveaway.Description, GiveawayRules.DescriptionMaxLength * 2);
        var footer = L("giveaway.card.footer", DiscordText.UntrustedPlain(giveaway.CreatorName, CreatorNameMax), Number(giveaway.Id));
        var fields = new List<EmbedField> { new(L("giveaway.card.prize"), prize) };

        MessageEmbed embed;
        switch (giveaway.Status)
        {
            case GiveawayStatus.Finished:
                {
                    var end = giveaway.EndedAt ?? giveaway.EndsAt;
                    var lines = new List<string>();
                    if (description is not null)
                        lines.Add(description);
                    if (giveaway.Winners.Count == 0)
                    {
                        lines.Add(L("giveaway.card.no_entrants"));
                    }
                    else
                    {
                        fields.Add(new(L(giveaway.Winners.Count == 1 ? "giveaway.card.winner" : "giveaway.card.winners"), WinnerList(giveaway.Winners)));
                    }

                    if (giveaway.RerollCount > 0)
                        lines.Add(L("giveaway.card.rerolled", giveaway.RerollCount));
                    fields.Add(new(L("giveaway.card.entrants"), Number(giveaway.EntrantCount ?? 0), true));
                    fields.Add(new(L("giveaway.card.end"), DiscordText.Timestamp(end, 'f'), true));
                    embed = new MessageEmbed(L("giveaway.card.title_finished"), lines.Count == 0 ? null : string.Join("\n\n", lines), null, fields, footer, null, FinishedColor);
                    break;
                }

            case GiveawayStatus.Cancelled or GiveawayStatus.Orphaned:
                embed = new MessageEmbed(L("giveaway.card.title_cancelled"), L("giveaway.card.cancelled"), null, fields, footer, null, EndedColor);
                break;

            default:
                {
                    fields.Add(new(L("giveaway.card.winner_count"), Number(giveaway.WinnerCount), true));
                    fields.Add(new(L("giveaway.card.end"), DiscordText.Timestamp(giveaway.EndsAt, 'F') + "\n" + DiscordText.Timestamp(giveaway.EndsAt, 'R'), true));
                    var join = L("giveaway.card.join");
                    embed = new MessageEmbed(L("giveaway.card.title_active"), description is null ? join : description + "\n\n" + join, null, fields, footer, null, ActiveColor);
                    break;
                }
        }

        return new OutgoingMessage(null, embed, MentionPolicy.None);
    }

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
