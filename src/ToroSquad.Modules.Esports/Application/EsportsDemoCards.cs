using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Notifications;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers.Fixtures;

namespace ToroSquad.Modules.Esports.Application;

/// <summary>
/// Controlled TEST/DEMO rendering of every match-card kind for an authorized test guild. Uses the production renderer in
/// demo mode (so cards are labelled TEST/DEMO, link nowhere but the reserved test domain and name no real source) and the durable outbox (so running
/// it twice sends nothing new). A successful demo proves Discord rendering only — never provider behaviour.
/// </summary>
public static class EsportsDemoCards
{
    public const string SourceKey = "demo:match-cards";

    /// <summary>
    /// Controlled "Match Page" target for the forfeit demo card: the IANA-reserved test domain (RFC 2606) — never a real
    /// match page, never HLTV, nothing is fetched. Lets the owner see how the link renders on a TEST/DEMO card.
    /// </summary>
    public const string SafeTestMatchPageUrl = "https://example.com/tsq-bot-demo-match-page";

    public static IReadOnlyList<(string Kind, OutgoingMessage Message)> Build(NotificationRenderer renderer, string language, DateTimeOffset now)
    {
        if (!renderer.IsDemo)
            throw new InvalidOperationException("Demo cards require the demo (fixture) renderer so they are labelled TEST/DEMO.");

        // Every timestamp derives from the current hour, so re-running within the hour renders identical cards (idempotent:
        // the outbox sees no change and neither sends nor edits). Found live: "now" made re-runs edit three messages.
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var tournament = new TournamentRef("demo", "demo:event", "Demo Masters 2026", "1", null, null, null);
        EsportsMatch Match(string id, MatchStatus status, int? a = null, int? b = null, int? winner = null, bool forfeit = false, MatchLinks? links = null) => new(
            new MatchKey("demo", id), tournament, start, true, 3, status, "demo",
            new MatchOpponent(OpponentKind.Team, new TeamRef("demo", "demo:1", "Toro Wolves", "TW"), a, OpponentResult.Scored),
            new MatchOpponent(OpponentKind.Team, new TeamRef("demo", "demo:2", "Nordic Owls", "NO"), b, OpponentResult.Scored),
            winner, false, forfeit, [], null, null, [],
            BeginAtUtc: status is MatchStatus.Live or MatchStatus.Finished ? start : null,
            EndAtUtc: status == MatchStatus.Finished ? start.AddMinutes(106) : null,
            Links: links);

        return
        [
            ("demo-started", renderer.Started(Match("1", MatchStatus.Live), language, MentionPolicy.None, start)),
            ("demo-result", renderer.Result(Match("2", MatchStatus.Finished, 0, 2, 1), language, spoiler: false, MentionPolicy.None, start)),
            ("demo-result-spoiler", renderer.Result(Match("3", MatchStatus.Finished, 2, 1, 0), language, spoiler: true, MentionPolicy.None, start)),
            ("demo-postponed", renderer.Postponed(Match("4", MatchStatus.Postponed), language, start)),
            ("demo-cancelled", renderer.Cancelled(Match("6", MatchStatus.Cancelled), language, start)),
            ("demo-forfeit", renderer.Result(Match("7", MatchStatus.Finished, winner: 0, forfeit: true, links: new MatchLinks(OfficialMatchUrl: SafeTestMatchPageUrl)),
                language, spoiler: false, MentionPolicy.None, start)),
        ];
    }

    public static IEnumerable<NotificationRequest> Requests(GuildId guild, ChannelId channel, IReadOnlyList<(string Kind, OutgoingMessage Message)> cards, DateTimeOffset now) =>
        cards.Select(c => new NotificationRequest(guild, EsportsModule.ModuleIdTyped, SourceKey, channel, c.Kind, c.Message.WithoutPings(), now.AddHours(1), IsDryRun: false));
}
