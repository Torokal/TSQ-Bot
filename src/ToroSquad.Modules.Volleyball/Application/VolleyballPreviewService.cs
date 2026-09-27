using ToroSquad.Core;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Modules.Volleyball.Application;

public enum VbPreviewKind
{
    Reminder = 0,
    Started = 1,
    SetWon = 2,
    SetLost = 3,
    FinalWon = 4,
    FinalLost = 5,
    Postponed = 6,
    Cancelled = 7,
}

public sealed record VbPreviewResult(OperationResult Auth, OutgoingMessage? Message, IReadOnlyList<RoleId> WouldPing);

/// <summary>
/// Ping-free admin preview built from SYNTHETIC data only (<see cref="VbDemoData"/>): always rendered by the demo renderer,
/// so it is labelled TEST/DEMO, names no real source and cannot be mistaken for a real match.
/// </summary>
public sealed class VolleyballPreviewService(VolleyballConfigService config, ILocalizer localizer, TimeProvider clock)
{
    public async Task<VbPreviewResult> BuildAsync(ActorContext actor, string language, VbPreviewKind kind, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return new(OperationResult.Forbidden(auth), null, []);

        var settings = await config.GetAsync(actor.GuildId, ct);
        var renderer = new VolleyballNotificationRenderer(localizer, new VbDataMode(VbProviderMode.Fixture), new VbLogoHosts([]));
        var message = VbDemoData.Card(renderer, kind, language, clock.GetUtcNow()).WithoutPings();
        var pingsOnKind = kind switch
        {
            VbPreviewKind.Reminder => settings?.PingOnReminder ?? false,
            VbPreviewKind.FinalWon or VbPreviewKind.FinalLost => settings?.PingOnFinal ?? false,
            _ => false,
        };
        var wouldPing = settings is { PingRoleId: { } role } && role != actor.GuildId.Value && pingsOnKind ? new[] { new RoleId(role) } : [];
        return new(OperationResult.Ok("vb.preview.ready"), message, wouldPing);
    }
}

/// <summary>
/// Deterministic synthetic volleyball data for previews: fictional "TSQ Test Cup" and opponent "Testland" — never real
/// results, never a real source. Times derive from the current hour so re-rendering within the hour is identical.
/// </summary>
public static class VbDemoData
{
    public const string Competition = "TSQ Test Cup";

    public static OutgoingMessage Card(VolleyballNotificationRenderer renderer, VbPreviewKind kind, string language, DateTimeOffset now)
    {
        if (!renderer.IsDemo)
            throw new InvalidOperationException("Demo cards require the demo renderer so they are labelled TEST/DEMO.");
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var start = hour.AddHours(1);
        IReadOnlyList<SetResult> sets = kind switch
        {
            VbPreviewKind.SetWon => [new(1, 25, 21), new(2, 22, 25), new(3, 25, 19)],
            VbPreviewKind.SetLost => [new(1, 25, 21), new(2, 22, 25)],
            VbPreviewKind.FinalWon => [new(1, 25, 21), new(2, 22, 25), new(3, 25, 19), new(4, 25, 23)],
            VbPreviewKind.FinalLost => [new(1, 25, 21), new(2, 22, 25), new(3, 19, 25), new(4, 23, 25)],
            _ => [],
        };
        var home = sets.Count(s => s.HomeWon);
        var view = new VbMatchView("demo:1", "demo", Competition, null, null, start, FollowedSide.Home, "Türkiye", "TUR", "Testland", null, null, null,
            "Test Arena", "Test City", [], VolleyballMatchStatus.Scheduled, home, sets.Count - home, sets, null, null, null,
            kind != VbPreviewKind.Reminder, kind is VbPreviewKind.FinalWon or VbPreviewKind.FinalLost, kind == VbPreviewKind.Postponed, kind == VbPreviewKind.Cancelled, hour);
        const string source = "vb.source.demo";
        return kind switch
        {
            VbPreviewKind.Reminder => renderer.Reminder(view, language, MentionPolicy.None, source),
            VbPreviewKind.Started => renderer.Started(view, start, language, MentionPolicy.None, source),
            VbPreviewKind.SetWon => renderer.SetFinished(view, 3, start.AddMinutes(75), language, MentionPolicy.None, source),
            VbPreviewKind.SetLost => renderer.SetFinished(view, 2, start.AddMinutes(50), language, MentionPolicy.None, source),
            VbPreviewKind.FinalWon or VbPreviewKind.FinalLost => renderer.Final(view, start.AddMinutes(110), language, MentionPolicy.None, source),
            VbPreviewKind.Postponed => renderer.Postponed(view, hour, language, source),
            _ => renderer.Cancelled(view, hour, language, source),
        };
    }
}
