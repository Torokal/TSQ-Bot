using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Discord.Interactions;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Commands;

/// <summary>
/// Public commands for Filenin Sultanları. Every answer comes from <see cref="VolleyballCache"/> (never a provider call on
/// the interaction path), carries its source and freshness, and degrades honestly: missing/failed data is shown as such,
/// never as "no matches"; "live" is only shown when the provider said so.
/// </summary>
[ToroModule(VolleyballModule.ModuleIdValue)]
[Group("volleyball", "Türkiye women's national volleyball team: next match and schedule")]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class VolleyballCommands(
    InteractionServices services,
    VolleyballCache cache,
    VolleyballNotificationRenderer renderer,
    VbDataMode mode,
    DeploymentPolicy deployment,
    VbSources sources,
    IOptions<VolleyballOptions> options) : ToroInteractionModule(services)
{
    [SlashCommand("next", "The next (or current) match of Filenin Sultanları")]
    public async Task NextAsync()
    {
        await DeferEphemeralAsync();
        if (!await ReadyAsync())
            return;
        var now = Services.Clock.GetUtcNow();
        var lang = await LangAsync();
        var match = VbCommandViews.Next(cache.MatchesOrdered(), now);
        if (match is null)
        {
            await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + Localizer.Get(lang, "vb.next.title"),
                Localizer.Get(lang, "vb.next.none") + await NotesAsync(), null, [], renderer.Footer(lang, sources.AttributionKey), cache.Fixtures.FetchedAt, NeutralColor));
            return;
        }

        var lines = new List<string> { "**" + renderer.Title(match, lang, withScore: match.Started) + "**" };
        if (match.Started && !match.Finished)
        {
            lines.Add(Localizer.Get(lang, "vb.next.live"));
            if (match.CurrentSet is { } set && match.CurrentSetHomePoints is { } h && match.CurrentSetAwayPoints is { } a)
            {
                var (f, o) = match.FollowedSide == FollowedSide.Home ? (h, a) : (a, h);
                lines.Add(Localizer.Get(lang, "vb.next.current_set", set, f, o));
            }
        }
        else if (match.StartTimeUtc is { } start)
        {
            lines.Add("🕒 " + DiscordText.Timestamp(start, 'F') + " (" + DiscordText.Timestamp(start, 'R') + ")");
        }

        lines.Add("🏆 " + DiscordText.Untrusted(match.CompetitionName, 120));
        var place = new[] { match.Venue, match.City }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => DiscordText.Untrusted(s, 80)).ToList();
        if (place.Count > 0)
            lines.Add("📍 " + string.Join(", ", place));
        if (match.Broadcasts.Count > 0)
            lines.Add("📺 " + string.Join(" · ", match.Broadcasts.Select(b => DiscordText.Untrusted(b, 40))));
        if (!sources.Capabilities.HasFlag(VbCapabilities.Broadcast))
            lines.Add(Localizer.Get(lang, "vb.next.no_broadcast_data"));
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + Localizer.Get(lang, "vb.next.title"), string.Join("\n", lines) + await NotesAsync(),
            null, [], renderer.Footer(lang, sources.AttributionKey), cache.Fixtures.FetchedAt, NeutralColor));
    }

    [SlashCommand("schedule", "Upcoming matches and recent results of Filenin Sultanları")]
    public async Task ScheduleAsync()
    {
        await DeferEphemeralAsync();
        if (!await ReadyAsync())
            return;
        var now = Services.Clock.GetUtcNow();
        var lang = await LangAsync();
        var (upcoming, recent) = VbCommandViews.Schedule(cache.MatchesOrdered(), now, 8, 5);
        var sections = new List<string>();
        if (upcoming.Count > 0)
        {
            sections.Add("**" + Localizer.Get(lang, "vb.schedule.upcoming") + "**\n" + string.Join("\n", upcoming.Select(m =>
                "• " + renderer.Title(m, lang, withScore: false) + " — " + (m.StartTimeUtc is { } s ? DiscordText.Timestamp(s, 'f') : Localizer.Get(lang, "vb.schedule.time_tbc")) +
                (m.Started ? " · 🔴 " + Localizer.Get(lang, "vb.state.live") : m.Status == VolleyballMatchStatus.Postponed ? " · ⏸️ " + Localizer.Get(lang, "vb.state.postponed") : ""))));
        }

        if (recent.Count > 0)
        {
            sections.Add("**" + Localizer.Get(lang, "vb.schedule.recent") + "**\n" + string.Join("\n", recent.Select(m =>
                "• " + renderer.Title(m, lang, withScore: true) + (m.StartTimeUtc is { } s ? " — " + DiscordText.Timestamp(s, 'd') : ""))));
        }

        var text = sections.Count == 0 ? Localizer.Get(lang, "vb.schedule.none") : string.Join("\n\n", sections);
        await ReplyEmbedAsync(new MessageEmbed(renderer.Demo(lang) + Localizer.Get(lang, "vb.schedule.title"),
            VolleyballNotificationRenderer.Clip(text + await NotesAsync(), DiscordLimits.EmbedDescriptionMax),
            null, [], renderer.Footer(lang, sources.AttributionKey), cache.Fixtures.FetchedAt, NeutralColor));
    }

    private async Task<string> NotesAsync()
    {
        var feed = cache.Fixtures;
        var notes = "\n\n" + await T("vb.freshness", feed.FetchedAt is { } at ? DiscordText.Timestamp(at, 'R') : "-");
        if (feed.IsStale(Services.Clock.GetUtcNow(), options.Value.FixtureStaleAfter))
            notes += "\n" + await T("vb.stale_warning", feed.LastOutcome?.ToString() ?? "-");
        notes += "\n" + await T("vb.scope_note");
        return notes;
    }

    private async Task<bool> ReadyAsync()
    {
        if (mode.IsDemo && !deployment.MayShowDemoData(Actor.GuildId))
        {
            await ReplyTextAsync("vb.demo_only_test_guild");
            return false;
        }

        if (!sources.Configured)
        {
            await ReplyTextAsync("vb.not_configured");
            return false;
        }

        var feed = cache.Fixtures;
        if (feed.FetchedAt is null)
        {
            await ReplyTextAsync(feed.LastOutcome is null ? "vb.no_data_yet" : "vb.data_unavailable", feed.LastOutcome?.ToString() ?? "-");
            return false;
        }

        return true;
    }
}

/// <summary>Pure selection logic behind the commands (unit-tested). Uses the schedule only for what to SHOW.</summary>
public static class VbCommandViews
{
    /// <summary>A match the provider says is running, otherwise the earliest upcoming one (not cancelled).</summary>
    public static VbMatchView? Next(IReadOnlyList<VbMatchView> matches, DateTimeOffset now) =>
        matches.FirstOrDefault(m => m.Started && !m.Finished && !m.Cancelled && m.StartTimeUtc is { } s && now - s < TimeSpan.FromHours(6))
        ?? matches.Where(m => !m.Started && !m.Cancelled && m.StartTimeUtc is { } s && s > now).OrderBy(m => m.StartTimeUtc).FirstOrDefault();

    public static (IReadOnlyList<VbMatchView> Upcoming, IReadOnlyList<VbMatchView> Recent) Schedule(IReadOnlyList<VbMatchView> matches, DateTimeOffset now, int upcoming, int recent) =>
        (matches.Where(m => !m.Finished && !m.Cancelled && (m.StartTimeUtc is null || m.StartTimeUtc > now || m.Started)).OrderBy(m => m.StartTimeUtc ?? DateTimeOffset.MaxValue).Take(upcoming).ToList(),
         matches.Where(m => m.Finished).OrderByDescending(m => m.StartTimeUtc).Take(recent).ToList());
}
