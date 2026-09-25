using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Application;

public enum F1PreviewKind
{
    PracticeStart = 0,
    RaceStart = 1,
    PracticeResult = 2,
    RaceResult = 3,
    RaceResultStandings = 4,
}

public sealed record F1PreviewResult(OperationResult Auth, OutgoingMessage? Message, IReadOnlyList<RoleId> WouldPing);

/// <summary>
/// Ping-free admin preview built from SYNTHETIC data only (<see cref="F1DemoData"/>): always rendered by the demo renderer,
/// so it is labelled TEST/DEMO, names no real source and cannot be mistaken for an actual Grand Prix.
/// </summary>
public sealed class Formula1PreviewService(Formula1ConfigService config, ILocalizer localizer, IOptions<Formula1Options> options, TimeProvider clock)
{
    public async Task<F1PreviewResult> BuildAsync(ActorContext actor, string language, F1PreviewKind kind, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return new(OperationResult.Forbidden(auth), null, []);

        var settings = await config.GetAsync(actor.GuildId, ct);
        var renderer = new Formula1NotificationRenderer(localizer, new F1DataMode(F1ProviderMode.Fixture));
        var now = clock.GetUtcNow();
        var message = F1DemoData.Card(renderer, kind, settings?.SpoilerMode ?? false, language, now, options.Value.CardStandingsRows).WithoutPings();
        var wouldPing = settings is { PingRoleId: { } role } && role != actor.GuildId.Value &&
                        (kind is F1PreviewKind.PracticeStart or F1PreviewKind.RaceStart ? settings.PingOnStarts : settings.PingOnResults)
            ? new[] { new RoleId(role) }
            : [];
        return new(OperationResult.Ok("f1.preview.ready"), message, wouldPing);
    }
}

/// <summary>
/// Deterministic synthetic Formula 1 data for previews and fixture demos. Fictional event, drivers and teams
/// ("TSQ Test Grand Prix", "Test Driver 01", "Test Team A") — never real results, never a real source.
/// Times derive from the current hour so re-rendering within the hour is identical.
/// </summary>
public static class F1DemoData
{
    public const string MeetingName = "TSQ Test Grand Prix";

    public static OutgoingMessage Card(Formula1NotificationRenderer renderer, F1PreviewKind kind, bool spoiler, string language, DateTimeOffset now, int standingsRows)
    {
        if (!renderer.IsDemo)
            throw new InvalidOperationException("Demo cards require the demo renderer so they are labelled TEST/DEMO.");
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var type = kind is F1PreviewKind.PracticeStart or F1PreviewKind.PracticeResult ? F1SessionType.Practice1 : F1SessionType.Race;
        var session = new F1Session(now.Year, 1, type, hour, hour + F1SessionTypes.TypicalDuration(type));
        var started = kind is F1PreviewKind.PracticeStart or F1PreviewKind.RaceStart;
        var view = new F1SessionView(session, MeetingName, "Test Circuit", "Testland", "Test City",
            started ? F1SessionState.Started : F1SessionState.Finalised, hour, started ? null : session.PlannedEndUtc,
            started ? null : session.PlannedEndUtc, null, hour, false);
        if (started)
            return renderer.Started(view, language, MentionPolicy.None, "f1.source.demo");

        var result = new F1CachedResult(Result(session), session.PlannedEndUtc, session.PlannedEndUtc);
        var standings = kind == F1PreviewKind.RaceResultStandings
            ? new F1StandingsAttachment(F1StandingsSection.Attached, DriverStandings(now.Year), ConstructorStandings(now.Year), "f1.source.demo")
            : kind == F1PreviewKind.RaceResult ? new F1StandingsAttachment(F1StandingsSection.Pending, null, null, null)
            : new F1StandingsAttachment(F1StandingsSection.None, null, null, null);
        return renderer.Result(view, result, standings, spoiler, language, MentionPolicy.None, "f1.source.demo", standingsRows);
    }

    public static string Driver(int i) => "Test Driver " + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    public static string Team(int i) => "Test Team " + (char)('A' + ((i - 1) / 2));

    public static F1SessionResult Result(F1Session session)
    {
        var entries = new List<F1DriverResult>();
        var practice = F1SessionTypes.IsPractice(session.Type);
        for (var i = 1; i <= 20; i++)
        {
            if (!practice && i == 18)
            {
                entries.Add(new F1DriverResult(null, 100 + i, Driver(i), "T" + i, Team(i), F1ResultStatus.Dnf, 12, null, null, null, 0));
                continue;
            }

            if (!practice && i == 19)
            {
                entries.Add(new F1DriverResult(null, 100 + i, Driver(i), "T" + i, Team(i), F1ResultStatus.Dsq, null, null, null, null, 0));
                continue;
            }

            if (!practice && i == 20)
            {
                entries.Add(new F1DriverResult(null, 100 + i, Driver(i), "T" + i, Team(i), F1ResultStatus.Dns, null, null, null, null, 0));
                continue;
            }

            var gap = i == 1 ? 0 : practice ? 0.1 * (i - 1) : 2.5 * (i - 1);
            entries.Add(new F1DriverResult(i, 100 + i, Driver(i), "T" + i, Team(i), F1ResultStatus.Classified, practice ? 20 + i : (i == 17 ? 56 : 57),
                practice ? 90.5 + gap : 5400 + gap, i == 1 ? 0 : i == 17 ? null : gap, i == 17 ? 1 : null, null));
        }

        return new F1SessionResult(session.Key, session.Type, "demo", entries);
    }

    public static F1StandingsSnapshot DriverStandings(int season) => new(F1StandingsKind.Drivers, season, 1, "demo",
        Enumerable.Range(1, 20).Select(i => new F1DriverStanding(i, "test_" + i, Driver(i), "T" + i, Team(i), Math.Max(0, 26 - i), i == 1 ? 1 : 0)).ToList(), []);

    public static F1StandingsSnapshot ConstructorStandings(int season) => new(F1StandingsKind.Constructors, season, 1, "demo", [],
        Enumerable.Range(0, 10).Select(i => new F1ConstructorStanding(i + 1, "test_team_" + i, "Test Team " + (char)('A' + i), Math.Max(0, 44 - (4 * i)), i == 0 ? 1 : 0)).ToList());
}
