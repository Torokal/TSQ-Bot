using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Volleyball.Persistence;

namespace ToroSquad.Modules.Volleyball.Providers.Fixtures;

/// <summary>
/// Explicit fixture (TEST/DEMO) mode: answers FIVB VIS requests with a SYNTHETIC timeline in VIS's real JSON shape, so the
/// real client and parser run end to end. Fictional tournament ("TSQ Demo Cup") and opponent ("Testland"); every card is
/// labelled TEST/DEMO and names no real source. Timeline (minutes after the persisted anchor): start +20, sets end +45,
/// +70, +95, +120 (final 3-1). A same-country U19 decoy match is included so the identity filter is visible in demos too.
/// </summary>
public sealed partial class VbFixtureHttpHandler(TimeProvider clock, VbFixtureAnchor anchor) : HttpMessageHandler
{
    /// <summary>Demo match numbers derive from the anchor, so a renewed timeline (after 24 h) is a NEW match, not a finished one.</summary>
    public static int DemoMatchNo(DateTimeOffset anchor) => 900_000 + (int)(anchor.ToUnixTimeSeconds() / 60 % 90_000 * 2);
    private static readonly (int Minute, int Tur, int Opp)[] Sets = [(45, 25, 21), (70, 22, 25), (95, 25, 19), (120, 25, 23)];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = HttpUtility.ParseQueryString(request.RequestUri?.Query ?? "");
        var xml = query["Request"] ?? "";
        if (!xml.Contains("GetVolleyMatchList", StringComparison.Ordinal) || !xml.Contains("Fields=", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        var start = await anchor.GetAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var minute = (int)Math.Floor((now - start).TotalMinutes);
        var version = 1000 + Math.Max(0, minute);
        var requestedVersion = VersionAttr().Match(xml) is { Success: true } v ? long.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var noMatches = NoMatchesAttr().Match(xml) is { Success: true } n
            ? n.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : null;

        var items = new List<JsonObject> { Match(DemoMatchNo(start), start, minute, youth: false), Match(DemoMatchNo(start) + 1, start, minute, youth: true) }
            .Where(m => noMatches is null || noMatches.Contains(((int)m["no"]!).ToString(CultureInfo.InvariantCulture)))
            .Where(m => requestedVersion == 0 || requestedVersion < version)
            .ToList();
        var body = new JsonObject
        {
            ["data"] = new JsonArray(items.Select(i => (JsonNode)i).ToArray()),
            ["nbItems"] = items.Count,
            ["version"] = version,
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    private static JsonObject Match(int no, DateTimeOffset anchor, int minute, bool youth)
    {
        var start = anchor.AddMinutes(20);
        var done = Sets.Count(s => minute >= s.Minute);
        var status = minute < 20 ? 1 : done >= Sets.Length ? 25 : 5 + (3 * done);
        var m = new JsonObject
        {
            ["no"] = no,
            ["noTournament"] = youth ? 90002 : 90001,
            ["dateTimeUtc"] = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["scheduleInfo"] = 4,
            ["city"] = "Test City",
            ["hall"] = "Test Arena",
            ["noTeamA"] = youth ? 800003 : 800001,
            ["noTeamB"] = youth ? 800004 : 800002,
            ["teamACode"] = "TUR",
            ["teamBCode"] = "TST",
            ["teamAName"] = youth ? "Türkiye U19" : "Türkiye",
            ["teamBName"] = youth ? "Testland U19" : "Testland",
            ["matchPointsA"] = minute < 20 ? null : Sets.Take(done).Count(s => s.Tur > s.Opp),
            ["matchPointsB"] = minute < 20 ? null : Sets.Take(done).Count(s => s.Tur < s.Opp),
            ["status"] = status,
            ["resultType"] = 0,
            ["nbSets"] = minute < 20 ? null : Math.Min(done + 1, Sets.Length),
            ["version"] = 1000 + Math.Max(0, minute),
            ["tournament"] = new JsonObject
            {
                ["no"] = youth ? 90002 : 90001,
                ["code"] = youth ? "WTSQU19" : "WTSQTEST",
                ["name"] = youth ? "TSQ Demo U19 Championship" : "TSQ Demo Cup",
                ["season"] = anchor.Year.ToString(CultureInfo.InvariantCulture),
                ["gender"] = 1,
                ["type"] = youth ? 10 : 12,
            },
        };
        for (var i = 1; i <= 5; i++)
        {
            var key = i.ToString(CultureInfo.InvariantCulture);
            JsonNode? a = null, b = null;
            if (i <= done)
            {
                a = Sets[i - 1].Tur;
                b = Sets[i - 1].Opp;
            }
            else if (i == done + 1 && minute >= 20 && done < Sets.Length)
            {
                // Set in progress: a plausible running score.
                var into = minute - (i == 1 ? 20 : Sets[i - 2].Minute);
                a = Math.Min(24, into);
                b = Math.Min(23, Math.Max(0, into - 2));
            }

            m["pointsTeamASet" + key] = a;
            m["pointsTeamBSet" + key] = b;
        }

        return m;
    }

    [GeneratedRegex("Version=\"(\\d+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex VersionAttr();

    [GeneratedRegex("NoMatches=\"([0-9 ]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex NoMatchesAttr();
}

/// <summary>
/// The instant the demo timeline is anchored to, persisted in vb_provider_state and reused for up to <see cref="MaxAge"/>,
/// so a restart continues the same demo match instead of re-announcing a shifted one. Storage problems renew the timeline
/// (never a crash).
/// </summary>
public sealed class VbFixtureAnchor(TimeProvider clock, IServiceScopeFactory scopes, ILogger<VbFixtureAnchor> logger)
{
    public const string Key = "fixture:anchor";
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly Lock _gate = new();
    private Task<DateTimeOffset>? _anchor;

    public Task<DateTimeOffset> GetAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_anchor is null || _anchor.IsFaulted || _anchor.IsCanceled ||
                (_anchor.IsCompletedSuccessfully && clock.GetUtcNow() - _anchor.Result >= MaxAge))
                _anchor = ResolveAsync(cancellationToken);
            return _anchor;
        }
    }

    private async Task<DateTimeOffset> ResolveAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ToroDbContext>();
            var set = db.Set<VbProviderStateEntity>();
            var row = await set.FirstOrDefaultAsync(s => s.Key == Key, ct);
            if (row?.LastSuccessAt is { } stored && stored <= now && now - stored < MaxAge)
                return stored;
            if (row is null)
            {
                row = new VbProviderStateEntity { Key = Key };
                set.Add(row);
            }

            row.LastSuccessAt = now;
            row.LastAttemptAt = now;
            row.LastDetail = "fixture timeline anchor (TEST/DEMO only)";
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not persist the volleyball fixture anchor; the demo timeline starts now");
        }

        return now;
    }
}
