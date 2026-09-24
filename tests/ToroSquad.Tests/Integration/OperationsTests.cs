using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Bot;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Providers;
using ToroSquad.Tests.Support;
using ToroSquad.Tests.Unit;

namespace ToroSquad.Tests.Integration;

/// <summary>Criteria 14 and 15 plus operational guarantees (real SQLite, real files).</summary>
public sealed partial class OperationsTests
{
    [Fact]
    public void Shipped_defaults_are_safe_fake_transport_dry_run_and_fixture_data()
    {
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(CommandManifestTests.RepoRoot(), "src", "ToroSquad.Bot", "appsettings.json")));
        var root = json.RootElement;
        root.GetProperty("Discord").GetProperty("Transport").GetString().Should().Be("Fake");
        root.GetProperty("Discord").GetProperty("AllowGlobalCommandSync").GetBoolean().Should().BeFalse();
        root.GetProperty("Delivery").GetProperty("Mode").GetString().Should().Be("DryRun");
        root.GetProperty("Esports").GetProperty("Provider").GetProperty("Mode").GetString().Should().Be("Fixture");
        root.GetProperty("Modules").GetProperty("Example").GetProperty("Enabled").GetBoolean().Should().BeFalse("example module is off in production");
    }

    [Fact]
    public void Going_live_without_token_app_id_or_source_is_refused_at_startup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discord:Transport"] = "Gateway",
            ["Delivery:Mode"] = "Send",
            ["Esports:Provider:Mode"] = "Live",
        }).Build();
        var problems = ToroHost.ValidateConfiguration(config);
        problems.Should().Contain(p => p.Contains("bot token", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("ApplicationId", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("SourceUrl", StringComparison.Ordinal));
        problems.Should().Contain(p => p.Contains("ApiKey is not set", StringComparison.Ordinal));
    }

    [Fact]
    public void Fixture_data_to_real_discord_requires_authorized_test_guilds()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discord:Transport"] = "Gateway",
            ["Discord:Token"] = "x",
            ["Discord:ApplicationId"] = "1",
            ["Bot:SourceUrl"] = "https://example.org/src",
            ["Delivery:Mode"] = "Send",
            ["Esports:Provider:Mode"] = "Fixture",
        }).Build();
        ToroHost.ValidateConfiguration(config).Should().Contain(p => p.Contains("TestGuildIds", StringComparison.Ordinal));
    }

    [Fact]
    public void Product_info_exposes_version_commit_license_and_attributions()
    {
        var info = ToroHost.BuildProductInfo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bot:SourceUrl"] = "https://example.org/torosquad/src/v0.1.0",
        }).Build());
        info.Name.Should().Be("ToroSquad Bot");
        info.License.Should().Be("AGPL-3.0-only");
        info.SourceConfigured.Should().BeTrue();
        info.Attributions.Select(a => a.Name).Should().Contain(n => n.Contains("BOT-Greg-v2_API", StringComparison.Ordinal))
            .And.Contain("Liquipedia").And.Contain("Valve Regional Standings");
        ToroHost.BuildProductInfo(new ConfigurationBuilder().Build()).SourceConfigured.Should().BeFalse();
    }

    [Fact]
    public void Redactor_removes_configured_secrets_and_token_shapes()
    {
        const string fakeToken = "FAKE-TEST-TOKEN-REDACTED-NOT-A-SECRET";
        var redactor = new SecretRedactor(["super-secret-api-key-123"]);
        var text = redactor.Redact($"token={fakeToken} key=super-secret-api-key-123 header=Apikey abcdef123456789 Bot {fakeToken}");
        text.Should().NotContain(fakeToken).And.NotContain("super-secret-api-key-123").And.NotContain("abcdef123456789");
        text.Should().Contain(SecretRedactor.Mask);
    }

    [Fact]
    public void Repository_contains_no_secrets_in_tracked_text_files()
    {
        var root = CommandManifestTests.RepoRoot();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                        !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal) ||
                        f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".ps1", StringComparison.Ordinal) || f.EndsWith(".example", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (file.EndsWith("OperationsTests.cs", StringComparison.Ordinal))
                continue; // contains a deliberately fake token for the redactor test
            SecretRedactor.DiscordTokenPattern().IsMatch(text).Should().BeFalse($"possible Discord token in {file}");
            ApiKeyAssignment().IsMatch(text).Should().BeFalse($"possible API key in {file}");
        }
    }

    [Fact]
    public void Powershell_scripts_are_ascii_so_windows_powershell_5_1_parses_them()
    {
        // PS 5.1 reads BOM-less files as ANSI; a UTF-8 em dash becomes a smart quote and breaks string parsing.
        foreach (var script in Directory.GetFiles(Path.Combine(CommandManifestTests.RepoRoot(), "scripts"), "*.ps1"))
            File.ReadAllBytes(script).Should().OnlyContain(b => b < 0x80, Path.GetFileName(script));
    }

    [Fact]
    public void Localization_catalogs_have_identical_keys_and_placeholders_in_turkish_and_english()
    {
        foreach (var dir in new[] { "src/ToroSquad.Discord/Localization", "src/ToroSquad.Modules.Esports/Localization", "src/ToroSquad.Modules.Example/Localization" })
        {
            var path = Path.Combine(CommandManifestTests.RepoRoot(), dir);
            var tr = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(path, "tr.json")))!;
            var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(path, "en.json")))!;
            tr.Keys.Should().BeEquivalentTo(en.Keys, dir);
            foreach (var key in tr.Keys)
                Placeholders(tr[key]).Should().BeEquivalentTo(Placeholders(en[key]), $"{dir}:{key}");
        }
    }

    [Fact]
    public void Turkish_is_default_and_english_is_the_fallback()
    {
        var catalog = new LocalizationCatalog([new LocalizationSource(typeof(ToroSquad.Discord.CoreBotModule).Assembly, "ToroSquad.Discord.Localization")]);
        catalog.Get("tr", "status.title").Should().Be("ToroSquad Bot durumu");
        catalog.Get("de", "status.title").Should().Be("ToroSquad Bot durumu", "unsupported language → default Turkish");
        catalog.Get("tr", "no.such.key").Should().Be("no.such.key");
    }

    [Fact]
    public void Istanbul_time_zone_resolves_on_windows_and_converts_without_fixed_offsets()
    {
        GuildTime.TryResolve("Europe/Istanbul", out var zone).Should().BeTrue();
        var utc = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        GuildTime.ToGuildLocal(utc, zone).Offset.Should().Be(TimeSpan.FromHours(3));
        GuildTime.TryResolve("Europe/Berlin", out var berlin).Should().BeTrue();
        GuildTime.ToGuildLocal(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), berlin).Offset.Should().Be(TimeSpan.FromHours(1));
        GuildTime.ToGuildLocal(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero), berlin).Offset.Should().Be(TimeSpan.FromHours(2), "DST handled by real rules");
        GuildTime.TryResolve("Mars/Olympus", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Discord_ids_round_trip_losslessly_through_sqlite()
    {
        await using var host = await TestHost.CreateAsync();
        var ids = new[] { 0UL, 1UL, 1_234_567_890_123_456_789UL, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue };
        foreach (var id in ids)
            await host.InScopeAsync(sp => sp.GetRequiredService<IGuildSettingsStore>().SaveAsync(new GuildSettings(new GuildId(id), "en", "UTC", true), new UserId(ulong.MaxValue), CancellationToken.None));
        foreach (var id in ids)
            (await host.InScopeAsync(sp => sp.GetRequiredService<IGuildSettingsStore>().GetAsync(new GuildId(id), CancellationToken.None))).Language.Should().Be("en");
    }

    [Fact]
    public void Second_instance_on_the_same_data_directory_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "torosquad-lock-" + Guid.NewGuid().ToString("N"));
        using var first = SingleInstanceLock.Acquire(dir);
        var second = () => SingleInstanceLock.Acquire(dir);
        second.Should().Throw<InvalidOperationException>().WithMessage("*Another ToroSquad Bot instance*");
    }

    [Fact]
    public async Task Migrations_cover_the_model_and_backup_restore_round_trips()
    {
        await using var host = await TestHost.CreateAsync();
        await host.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<ToroDbContext>();
            db.Database.HasPendingModelChanges().Should().BeFalse("run: dotnet ef migrations add <Name> --project src/ToroSquad.Bot");
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        });
        await host.InScopeAsync(sp => sp.GetRequiredService<GuildSettingsService>().UpdateAsync(TestHost.Admin(new GuildId(5)), "en", null, CancellationToken.None));

        var dbPath = Path.Combine(host.Directory, "torosquad.db");
        var backup = DatabaseMaintenance.Backup(dbPath, Path.Combine(host.Directory, "backups"), host.Clock);
        await host.InScopeAsync(sp => sp.GetRequiredService<GuildSettingsService>().UpdateAsync(TestHost.Admin(new GuildId(5)), "tr", null, CancellationToken.None));

        var restoredPath = Path.Combine(host.Directory, "restored.db");
        File.Copy(dbPath, restoredPath);
        SqliteConnection.ClearAllPools();
        DatabaseMaintenance.Restore(backup, restoredPath, new FakeTimeProvider(TestHost.T0.AddMinutes(1)));
        await using var connection = new SqliteConnection(DatabaseMaintenance.ConnectionString(restoredPath));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Language FROM guild_settings WHERE GuildId = 5";
        (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)).Should().Be("en");
    }

    [Fact]
    public async Task Fixture_mode_runs_the_real_client_parser_and_pagination_end_to_end()
    {
        await using var host = await TestHost.CreateAsync(new() { ["Esports:Liquipedia:PageSize"] = "3" });
        var poller = host.Services.GetRequiredService<EsportsPoller>();
        await poller.RefreshRankingsAsync(CancellationToken.None);
        await poller.RefreshMatchesAsync(CancellationToken.None);
        await poller.RefreshEventsAsync(CancellationToken.None);

        var cache = host.Services.GetRequiredService<EsportsCache>();
        cache.Matches.LastOutcome.Should().Be(ProviderOutcome.Success);
        cache.Matches.Data!.Should().HaveCount(8, "3 pages of 3 via the real pagination loop");
        cache.Matches.Data!.Should().Contain(m => m.Status == MatchStatus.Cancelled);
        cache.Events.Data.Should().HaveCount(3);
        cache.Rankings.Data!.Entries.Should().HaveCount(7);
        cache.Resolver!.Resolve(new TeamRef("liquipedia", "counterstrike/Crimson_Esports", "Crimson Esports", "CRM")).Kind.Should().Be(TeamMatchKind.Normalized);
    }

    [Fact]
    public async Task Provider_failure_keeps_last_good_data_marks_it_stale_and_plans_nothing()
    {
        await using var host = await TestHost.CreateAsync();
        var cache = host.Services.GetRequiredService<EsportsCache>();
        var good = ProviderResult<IReadOnlyList<EsportsMatch>>.Ok([FilterAndRankingTests.Match("X", null, null)], TestHost.T0);
        cache.UpdateMatches(good, TestHost.T0);
        cache.UpdateMatches(ProviderResult<IReadOnlyList<EsportsMatch>>.Fail(ProviderOutcome.AuthFailed, "HTTP 403", TestHost.T0.AddMinutes(10)), TestHost.T0.AddMinutes(10));

        cache.Matches.Data.Should().HaveCount(1, "an API error never replaces data with 'no matches'");
        cache.Matches.LastOutcome.Should().Be(ProviderOutcome.AuthFailed);
        cache.Matches.ConsecutiveFailures.Should().Be(1);
        cache.Matches.IsStale(TestHost.T0.AddMinutes(45), cache.StaleAfter).Should().BeTrue();
    }

    private static string[] Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("\"(ApiKey|Token)\"\\s*:\\s*\"[^\"]{8,}\"", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyAssignment();
}
