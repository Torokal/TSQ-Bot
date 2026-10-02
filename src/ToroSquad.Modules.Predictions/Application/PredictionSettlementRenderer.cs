using System.Globalization;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Modules.Predictions.Domain;

namespace ToroSquad.Modules.Predictions.Application;

/// <summary>One winning entry of a settlement: its stake and the payout credited to the wallet (stake × fixed odds).</summary>
public sealed record SettlementWinner(UserId User, long StakeMinor, long PayoutMinor)
{
    /// <summary>The net gain (payout − stake): what the member won on top of getting the stake back.</summary>
    public long NetMinor => PayoutMinor - StakeMinor;
}

/// <summary>What one committed settlement decided (rendered once, inside the settlement transaction).</summary>
public sealed record SettlementResult(long PredictionId, string Title, string OutcomeLabel, int OddsX100, IReadOnlyList<SettlementWinner> Winners, long PayoutTotalMinor);

/// <summary>
/// The public result announcement of a settled prediction for the commands channel, as plain message content (Discord pings
/// only from content): the question, the winning outcome with its odds, the winners with their NET gain, the number of
/// winners and the total PAYOUT credited (stakes included). Each message pings exactly the winners it lists
/// (<see cref="MentionPolicy.ExplicitUsers"/> — never a role, @everyone or @here; untrusted text is defused). Up to
/// <see cref="WinnersPerMessage"/> winners per message and at most <see cref="MaxMessages"/> messages; anyone beyond is
/// counted ("+ 23 kazanan daha"), never silently dropped. Nobody on the winning outcome still gets an announcement.
/// </summary>
public sealed class PredictionSettlementRenderer(ILocalizer localizer)
{
    public const int WinnersPerMessage = 15;
    public const int MaxMessages = 5;

    public IReadOnlyList<OutgoingMessage> Render(SettlementResult result, string language)
    {
        var number = result.PredictionId.ToString(CultureInfo.InvariantCulture);
        var head = string.Join("\n",
            L(language, "predictions.settled.title"),
            "### " + DiscordText.Untrusted(result.Title, 1000),
            "",
            L(language, "predictions.settled.outcome"),
            DiscordText.Untrusted(result.OutcomeLabel, 400) + " · " + Odds.Format(result.OddsX100)) + "\n\n";
        var summary = L(language, "predictions.settled.summary", result.Winners.Count.ToString(CultureInfo.InvariantCulture),
            Coins.Format(result.PayoutTotalMinor, language)) + "\n" + L(language, "predictions.settled.footer", number);
        if (result.Winners.Count == 0)
            return [Message(head + L(language, "predictions.settled.winners_title") + "\n" + L(language, "predictions.settled.none") + "\n\n" + summary, [])];

        var listed = result.Winners.Take(WinnersPerMessage * MaxMessages).ToList();
        var chunks = listed.Chunk(WinnersPerMessage).ToList();
        var more = result.Winners.Count - listed.Count;
        var messages = new List<OutgoingMessage>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var lines = string.Join("\n", chunks[i].Select(w => Mention(w.User) + " — " + L(language, "predictions.settled.line", Coins.Format(w.NetMinor, language))));
            if (i == chunks.Count - 1 && more > 0)
                lines += "\n" + L(language, "predictions.settled.more", more.ToString(CultureInfo.InvariantCulture));
            var text = i == 0
                ? head + L(language, "predictions.settled.winners") + "\n" + lines + "\n\n" + summary
                : L(language, "predictions.settled.continued", (i + 1).ToString(CultureInfo.InvariantCulture), chunks.Count.ToString(CultureInfo.InvariantCulture), number) + "\n" + lines;
            messages.Add(Message(text, chunks[i].Select(w => w.User)));
        }

        return messages;
    }

    /// <summary>The only place this module lets a user id ping: the winners listed in this very message.</summary>
    private static OutgoingMessage Message(string text, IEnumerable<UserId> winners) => new(text, null, MentionPolicy.ExplicitUsers(winners));

    private static string Mention(UserId user) => string.Create(CultureInfo.InvariantCulture, $"<@{user.Value}>");

    private string L(string language, string key, params object?[] args) =>
        args.Length == 0 ? localizer.Get(language, key) : string.Format(CultureInfo.InvariantCulture, localizer.Get(language, key), args);
}
