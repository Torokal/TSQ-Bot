using Microsoft.EntityFrameworkCore;
using ToroSquad.Core;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;

namespace ToroSquad.Modules.Esports.Application;

public sealed record PreviewResult(OperationResult Auth, OutgoingMessage? Message, IReadOnlyList<RoleId> WouldPing, bool UsedSample);

/// <summary>
/// Ping-free preview of what this server would receive: uses a real cached match that passes the server filters
/// when available, otherwise a clearly labelled sample. The returned message never pings (Mentions = none); the
/// roles that WOULD be pinged are listed as text instead.
/// </summary>
public sealed class EsportsPreviewService(ToroDbContext db, EsportsCache cache, NotificationRenderer renderer, NotificationPlanner planner, TimeProvider clock)
{
    public async Task<PreviewResult> BuildAsync(ActorContext actor, string language, CancellationToken ct)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return new(OperationResult.Forbidden(auth), null, [], false);

        var now = clock.GetUtcNow();
        var config = await db.Set<EsportsGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == actor.GuildId.Value, ct)
                     ?? new EsportsGuildConfigEntity { GuildId = actor.GuildId.Value };
        var filters = await planner.LoadFiltersAsync(config, ct);
        var candidate = (cache.Matches.Data ?? [])
            .Where(m => m.Status == MatchStatus.Scheduled && m.ScheduledStartUtc > now)
            .OrderBy(m => m.ScheduledStartUtc)
            .FirstOrDefault(m => MatchFilter.Evaluate(m, filters, cache.Resolver).Passes);
        var usedSample = candidate is null;
        var match = candidate ?? Sample(now);

        var mappings = await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == actor.GuildId.Value && m.PingOnReminder).ToListAsync(ct);
        var wouldPing = mappings.Where(m => m.TeamKey.Length == 0 || match.InvolvesTeam(m.TeamKey)).Select(m => new RoleId(m.RoleId)).Distinct().ToList();

        var message = renderer.Reminder(match, language, MentionPolicy.None, now, null).WithoutPings();
        return new(OperationResult.Ok("esports.preview.ready"), message, wouldPing, usedSample);
    }

    private static EsportsMatch Sample(DateTimeOffset now) => new(
        new MatchKey("sample", "preview"),
        new TournamentRef("sample", "Sample", "Sample Tournament", "1", null, null, null),
        now.AddMinutes(15), true, 3, MatchStatus.Scheduled, "sample",
        new MatchOpponent(OpponentKind.Team, new TeamRef("sample", "sample/A", "Team A", "A"), null, OpponentResult.None),
        new MatchOpponent(OpponentKind.Team, new TeamRef("sample", "sample/B", "Team B", "B"), null, OpponentResult.None),
        null, false, false, [], "Sample stage", null, []);
}
