using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Roles;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Guilds;
using ToroSquad.Modules.Volleyball.Application;
using ToroSquad.Modules.Volleyball.Domain;
using ToroSquad.Modules.Volleyball.Providers;

namespace ToroSquad.Tests.Support;

/// <summary>
/// Scriptable volleyball provider: tests set the "real world" state of each match; the provider returns it (or a scripted
/// failure) and counts calls, so shared-fetch behaviour can be asserted.
/// </summary>
public sealed class VbFakeProvider(TimeProvider clock) : IVolleyballDataProvider
{
    public const string ProviderId = "fakevb";

    public Dictionary<string, VolleyballMatch> World { get; } = new(StringComparer.Ordinal);
    public VbProviderOutcome? Failure { get; set; }
    public TimeSpan? RetryAfter { get; set; }
    public VbCapabilities Caps { get; set; } = VbCapabilities.Fixtures | VbCapabilities.Results | VbCapabilities.LiveMatchState | VbCapabilities.SetScores;
    public int FixtureCalls { get; private set; }
    public int LiveCalls { get; private set; }

    public string Id => ProviderId;
    public string AttributionKey => "vb.source.fivb";
    public bool IsConfigured => true;
    public VbCapabilities Capabilities => Caps;

    public void CopyFrom(VbFakeProvider other)
    {
        foreach (var (k, v) in other.World)
            World[k] = v;
    }

    public Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetMatchesAsync(TrackedTeamIdentity team, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken)
    {
        FixtureCalls++;
        if (Failure is { } f)
            return Task.FromResult(VbProviderResult<IReadOnlyList<VolleyballMatch>>.Fail(f, "scripted", clock.GetUtcNow(), RetryAfter));
        IReadOnlyList<VolleyballMatch> list = World.Values.Where(m => m.StartTimeUtc is null || (m.StartTimeUtc >= fromUtc && m.StartTimeUtc <= toUtc)).ToList();
        return Task.FromResult(VbProviderResult<IReadOnlyList<VolleyballMatch>>.Ok(list, clock.GetUtcNow()));
    }

    public Task<VbProviderResult<IReadOnlyList<VolleyballMatch>>> GetLiveStateAsync(IReadOnlyCollection<string> providerMatchIds, CancellationToken cancellationToken)
    {
        LiveCalls++;
        if (Failure is { } f)
            return Task.FromResult(VbProviderResult<IReadOnlyList<VolleyballMatch>>.Fail(f, "scripted", clock.GetUtcNow(), RetryAfter));
        IReadOnlyList<VolleyballMatch> list = World.Values.Where(m => providerMatchIds.Contains(m.ProviderMatchId)).ToList();
        return Task.FromResult(VbProviderResult<IReadOnlyList<VolleyballMatch>>.Ok(list, clock.GetUtcNow()));
    }

    // ------------------------------------------------------------------ world builders

    public static VolleyballTeam Turkey(TeamLevel level = TeamLevel.Senior, TeamGender gender = TeamGender.Women, TeamKind kind = TeamKind.NationalTeam, string name = "Türkiye", string? providerId = "t1") =>
        new("fakevb:team:" + providerId, providerId, name, "TUR", gender, level, kind, level == TeamLevel.AgeGroup ? 19 : null);

    public static VolleyballTeam Opponent(string code = "ITA", string name = "Italy", TeamGender gender = TeamGender.Women) =>
        new("fakevb:team:" + code, code, name, code, gender, TeamLevel.Senior, TeamKind.NationalTeam);

    public static VolleyballMatch Scheduled(string id, DateTimeOffset start, VolleyballTeam? home = null, VolleyballTeam? away = null) =>
        new(VolleyballMatch.Key(ProviderId, id), ProviderId, id, "c1", "Volleyball Nations League", start.Year, null, null, start,
            home ?? Turkey(), away ?? Opponent(), VolleyballMatchStatus.Scheduled, null, null, [], null, null, null, "Test Arena", "Ankara", [], null);

    /// <summary>The match with the given completed sets (Türkiye is home unless the match says otherwise).</summary>
    public static VolleyballMatch WithSets(VolleyballMatch m, VolleyballMatchStatus status, params (int Home, int Away)[] sets)
    {
        var list = sets.Select((s, i) => new VolleyballSet(i + 1, s.Home, s.Away, true)).ToList();
        return m with
        {
            Status = status,
            HomeSets = list.Count(s => s.HomePoints > s.AwayPoints),
            AwaySets = list.Count(s => s.HomePoints < s.AwayPoints),
            Sets = list,
        };
    }

    public void Set(VolleyballMatch m) => World[m.ProviderMatchId] = m;
}

public static class VbTestHostExtensions
{
    public static readonly Dictionary<string, string?> Live = new() { ["Volleyball:Provider:Mode"] = "Live" };

    public static readonly GuildPermission ChannelPermissions =
        GuildPermission.ViewChannel | GuildPermission.SendMessages | GuildPermission.EmbedLinks | GuildPermission.ReadMessageHistory;

    /// <summary>Test host whose volleyball provider is a scriptable fake.</summary>
    public static async Task<(TestHost Host, VbFakeProvider Fake)> CreateVbHostAsync(Dictionary<string, string?>? overrides = null, DateTimeOffset? start = null,
        VbFakeProvider? copyFrom = null, Action<IServiceCollection>? extra = null)
    {
        VbFakeProvider? fake = null;
        var settings = new Dictionary<string, string?>(Live);
        foreach (var (k, v) in overrides ?? [])
            settings[k] = v;
        var host = await TestHost.CreateAsync(settings, start, services =>
        {
            services.AddSingleton(sp => fake ??= new VbFakeProvider(sp.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IVolleyballDataProvider>(sp => sp.GetRequiredService<VbFakeProvider>());
            extra?.Invoke(services);
        });
        var created = host.Services.GetRequiredService<VbFakeProvider>();
        if (copyFrom is not null)
            created.CopyFrom(copyFrom);
        return (host, created);
    }

    /// <summary>Volleyball guild: usable channel, optional ping role, module enabled.</summary>
    public static async Task SetUpVbGuildAsync(this TestHost host, GuildId guild, ChannelId channel, RoleId? pingRole = null)
    {
        var roles = pingRole is { } r ? new[] { new RoleInfo(r, "Sultanlar", 3, GuildPermission.None, false, false, true) } : [];
        host.Guilds.SetSnapshot(FakeGuildGateway.DemoSnapshot(guild, roles));
        host.Guilds.SetChannel(guild, channel, new BotChannelAccess(true, true, ChannelPermissions));
        await host.InScopeAsync(async sp =>
        {
            var admin = TestHost.Admin(guild);
            var config = sp.GetRequiredService<VolleyballConfigService>();
            (await config.SetChannelAsync(admin, channel.Value, CancellationToken.None)).Succeeded.Should().BeTrue();
            if (pingRole is { } role)
                (await config.SetRoleAsync(admin, role.Value, pingOnReminder: true, pingOnFinal: false, CancellationToken.None)).Succeeded.Should().BeTrue();
            (await sp.GetRequiredService<ModuleManagementService>().SetEnabledAsync(admin, "volleyball", true, CancellationToken.None)).Succeeded.Should().BeTrue();
        });
    }
}
