using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Discord.Transport;
using ToroSquad.Modules.Formula1.Application;
using ToroSquad.Modules.Formula1.Domain;
using ToroSquad.Modules.Formula1.Providers;

namespace ToroSquad.Modules.Formula1.Commands;

/// <summary>
/// Formula 1 commands. Every answer comes from <see cref="Formula1Cache"/> (never a provider call on the interaction path)
/// and is built by <see cref="F1CommandResponses"/>: next, schedule, standings and now answer publicly in the channel,
/// results stays private (spoilers); errors are private. No provider attribution is shown; live status is never guessed.
/// </summary>
[ToroModule(Formula1Module.ModuleIdValue)]
[Group("f1", "Formula 1 schedule, results and championship standings")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class Formula1Commands(
    InteractionServices services,
    Formula1Cache cache,
    Formula1NotificationRenderer renderer,
    F1DataMode mode,
    DeploymentPolicy deployment,
    F1Sources sources,
    IOptions<Formula1Options> options) : ToroInteractionModule(services)
{
    private F1CommandResponses Responses => new(cache, renderer, Localizer, sources, options.Value);

    // Public answers are sent directly (cache-only, well inside Discord's 3-second window): no ephemeral defer.

    [SlashCommand("next", "The next Grand Prix weekend and its sessions")]
    public async Task NextAsync()
    {
        if (await DemoAllowedAsync())
            await SendReplyAsync(Responses.Next(await LangAsync(), Services.Clock.GetUtcNow()));
    }

    [SlashCommand("schedule", "Full season calendar, or one round's weekend schedule")]
    public async Task ScheduleAsync([Summary("round", "Round number for its weekend schedule (default: the whole season)"), MinValue(1), MaxValue(40)] int? round = null)
    {
        if (await DemoAllowedAsync())
            await SendReplyAsync(Responses.Schedule(await LangAsync(), Services.Clock.GetUtcNow(), round));
    }

    [SlashCommand("results", "Latest session classification (from the bot's cache)")]
    public async Task ResultsAsync(
        [Summary("session", "Which session (default: the latest with a result)"),
         Choice("latest", "latest"), Choice("race", "race"), Choice("sprint", "sprint"), Choice("qualifying", "quali"),
         Choice("sprint-qualifying", "sq"), Choice("fp1", "fp1"), Choice("fp2", "fp2"), Choice("fp3", "fp3")] string session = "latest",
        [Summary("spoiler", "Hide the classification behind a spoiler")] bool spoiler = false)
    {
        await DeferEphemeralAsync(); // results stay private: no accidental spoiler for the whole channel
        if (!await DemoAllowedAsync())
            return;
        F1SessionType? type = session != "latest" && F1SessionTypes.TryParseSlug(session, out var t) ? t : null;
        await SendReplyAsync(Responses.Results(await LangAsync(), Services.Clock.GetUtcNow(), type, spoiler));
    }

    [SlashCommand("now", "Is a session running right now? (live provider status, never guessed)")]
    public async Task NowAsync()
    {
        if (await DemoAllowedAsync())
            await SendReplyAsync(Responses.Now(await LangAsync(), Services.Clock.GetUtcNow()));
    }

    [ToroModule(Formula1Module.ModuleIdValue)]
    [Group("standings", "Championship standings")]
    public sealed class StandingsCommands(
        InteractionServices services,
        Formula1Cache cache,
        Formula1NotificationRenderer renderer,
        F1DataMode mode,
        DeploymentPolicy deployment,
        F1Sources sources,
        IOptions<Formula1Options> options) : ToroInteractionModule(services)
    {
        [SlashCommand("drivers", "Drivers' championship standings")]
        public Task DriversAsync() => ShowAsync(F1StandingsKind.Drivers);

        [SlashCommand("constructors", "Constructors' championship standings")]
        public Task ConstructorsAsync() => ShowAsync(F1StandingsKind.Constructors);

        private async Task ShowAsync(F1StandingsKind kind)
        {
            if (mode.IsDemo && !deployment.MayShowDemoData(Actor.GuildId))
            {
                await ReplyTextAsync("f1.demo_only_test_guild");
                return;
            }

            var reply = new F1CommandResponses(cache, renderer, Localizer, sources, options.Value).Standings(await LangAsync(), Services.Clock.GetUtcNow(), kind);
            await SendAsync(reply.Text, reply.Embed is null ? null : DiscordConversions.ToEmbed(reply.Embed), null, reply.Ephemeral);
        }
    }

    private Task SendReplyAsync(F1CommandReply reply) =>
        SendAsync(reply.Text, reply.Embed is null ? null : DiscordConversions.ToEmbed(reply.Embed), null, reply.Ephemeral);

    private async Task<bool> DemoAllowedAsync()
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(Actor.GuildId))
        {
            await ReplyTextAsync("f1.demo_only_test_guild");
            return false;
        }

        return true;
    }
}

public enum F1SeasonMark
{
    Upcoming = 0,
    Current = 1,
    Live = 2,
    Done = 3,
    Cancelled = 4,
}

/// <summary>Pure selection logic behind the commands (unit-tested).</summary>
public static class F1CommandViews
{
    /// <summary>
    /// The current or next weekend: the earliest meeting whose race (or last session) has not been over for more than a
    /// few hours. Uses only the schedule for choosing what to SHOW — never to claim anything happened.
    /// </summary>
    public static IReadOnlyList<F1SessionView>? NextMeeting(IReadOnlyList<F1SessionView> sessions, DateTimeOffset now) =>
        sessions.GroupBy(s => s.Session.MeetingKey)
            .Select(g => g.OrderBy(s => s.Session.ScheduledStartUtc).ThenBy(s => F1SessionTypes.Order(s.Session.Type)).ToList())
            .Where(m => m.Max(s => s.Session.PlannedEndUtc) + TimeSpan.FromHours(6) >= now && m.Any(s => s.State != F1SessionState.Cancelled))
            .OrderBy(m => m[0].Session.ScheduledStartUtc)
            .FirstOrDefault();

    /// <summary>
    /// The season the commands show, never hard-coded: the season of the current/next weekend; else the newest season in
    /// the schedule cache; else the current year.
    /// </summary>
    public static int CurrentSeason(IReadOnlyList<F1SessionView> sessions, DateTimeOffset now) =>
        NextMeeting(sessions, now)?[0].Session.Season ?? (sessions.Count > 0 ? sessions.Max(s => s.Session.Season) : now.Year);

    /// <summary>Every weekend of a season in round order, each with its sessions in time order.</summary>
    public static IReadOnlyList<IReadOnlyList<F1SessionView>> SeasonMeetings(IReadOnlyList<F1SessionView> sessions, int season) =>
        sessions.Where(s => s.Session.Season == season)
            .GroupBy(s => s.Session.Round)
            .OrderBy(g => g.Key)
            .Select(g => (IReadOnlyList<F1SessionView>)g.OrderBy(s => s.Session.ScheduledStartUtc).ThenBy(s => F1SessionTypes.Order(s.Session.Type)).ToList())
            .ToList();

    /// <summary>
    /// Overview marker of one weekend. Live only from the lifecycle provider (a Started/Suspended session); the clock alone
    /// can at most say a weekend is over (after its last planned end) or which one is next — never that one is running.
    /// </summary>
    public static F1SeasonMark SeasonMark(IReadOnlyList<F1SessionView> meeting, string? currentMeetingKey, DateTimeOffset now)
    {
        if (meeting.Any(s => s.State is F1SessionState.Started or F1SessionState.Suspended && now - s.Session.ScheduledStartUtc < F1CommandResponses.LiveStateMaxAge))
            return F1SeasonMark.Live;
        if (meeting.All(s => s.State == F1SessionState.Cancelled))
            return F1SeasonMark.Cancelled;
        if (meeting[0].Session.MeetingKey == currentMeetingKey)
            return F1SeasonMark.Current;
        var race = meeting.FirstOrDefault(s => s.Session.Type == F1SessionType.Race);
        if (race?.State is F1SessionState.FinishedPendingResults or F1SessionState.Finalised || meeting.Max(s => s.Session.PlannedEndUtc) < now)
            return F1SeasonMark.Done;
        return F1SeasonMark.Upcoming;
    }

    public static IReadOnlyList<F1SessionView>? Meeting(IReadOnlyList<F1SessionView> sessions, int season, int round)
    {
        var list = sessions.Where(s => s.Session.Season == season && s.Session.Round == round)
            .OrderBy(s => s.Session.ScheduledStartUtc).ThenBy(s => F1SessionTypes.Order(s.Session.Type)).ToList();
        return list.Count == 0 ? null : list;
    }

    /// <summary>The most recent session (optionally of one type) that has a stored classification.</summary>
    public static (F1SessionView View, F1CachedResult Result)? LatestResult(IReadOnlyList<F1SessionView> sessions, IReadOnlyDictionary<string, F1CachedResult> results, F1SessionType? type)
    {
        var pick = sessions.Where(s => (type is null || s.Session.Type == type) && results.ContainsKey(s.Session.Key))
            .OrderByDescending(s => s.Session.ScheduledStartUtc)
            .FirstOrDefault();
        return pick is null ? null : (pick, results[pick.Session.Key]);
    }
}
