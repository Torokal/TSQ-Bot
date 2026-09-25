using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ToroSquad.Bot;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Persistence;
using ToroSquad.Modules.Formula1.Persistence;

namespace ToroSquad.Tests.Integration;

/// <summary>The Formula1Module migration: additive only, applies to an empty DB and to the previous production schema with data.</summary>
public sealed class F1PersistenceTests
{
    private const string PreviousMigration = "20260925002558_OutboxDeliveredFingerprint";

    private static ToroDbContext Context(string path) => new(
        new DbContextOptionsBuilder<ToroDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False", o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .Options,
        DesignTimeDbContextFactory.AllContributors());

    private static string TempDb() => Path.Combine(Path.GetTempPath(), "torosquad-tests", Guid.NewGuid().ToString("N") + ".db");

    private static async Task<List<string>> ObjectsAsync(ToroDbContext db, string type)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type ORDER BY name";
        command.Parameters.AddWithValue("$type", type);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public async Task Migration_is_additive_and_only_touches_formula1_tables()
    {
        await using var db = Context(TempDb());
        var script = db.GetService<IMigrator>().GenerateScript(PreviousMigration, "Formula1Module");
        script.Should().Contain("CREATE TABLE \"f1_session_snapshot\"").And.Contain("CREATE TABLE \"f1_guild_config\"")
            .And.Contain("CREATE TABLE \"f1_result_snapshot\"").And.Contain("CREATE TABLE \"f1_standings_snapshot\"").And.Contain("CREATE TABLE \"f1_provider_state\"");
        script.Should().NotContainEquivalentOf("DROP TABLE").And.NotContainEquivalentOf("ALTER TABLE \"esports").And.NotContainEquivalentOf("ALTER TABLE \"outbox");
        foreach (var line in script.Split('\n').Where(l => l.StartsWith("CREATE", StringComparison.Ordinal)))
            line.Should().Contain("f1_", "the Formula 1 migration creates only Formula 1 objects");
    }

    [Fact]
    public async Task Migration_applies_to_an_empty_database_with_the_expected_keys_and_indexes()
    {
        var path = TempDb();
        await using var db = Context(path);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
        (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse("the snapshot matches the runtime model");
        (await ObjectsAsync(db, "table")).Should().Contain(["f1_guild_config", "f1_session_snapshot", "f1_result_snapshot", "f1_standings_snapshot", "f1_provider_state"]);
        (await ObjectsAsync(db, "index")).Should().Contain([
            "IX_f1_session_snapshot_ScheduledStartUtc",
            "IX_f1_session_snapshot_Season_Round",
            "IX_f1_session_snapshot_LifecycleProvider_LifecycleProviderRef",
            "IX_f1_standings_snapshot_Kind_Season_Id",
        ]);

        db.Set<F1SessionSnapshotEntity>().Add(new F1SessionSnapshotEntity { SessionKey = "2030-08-race", MeetingKey = "2030-08", Season = 2030, Round = 8, SessionType = 7 });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        db.Set<F1SessionSnapshotEntity>().Add(new F1SessionSnapshotEntity { SessionKey = "2030-08-race", MeetingKey = "2030-08", Season = 2030, Round = 8, SessionType = 7 });
        var duplicate = () => db.SaveChangesAsync();
        await duplicate.Should().ThrowAsync<DbUpdateException>("the session key is the primary key: one row per real-world session");
    }

    [Fact]
    public async Task Migration_applies_to_the_current_production_schema_and_keeps_esports_data()
    {
        var path = TempDb();
        await using (var before = Context(path))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);
            before.Set<EsportsGuildConfigEntity>().Add(new EsportsGuildConfigEntity { GuildId = 42, ChannelId = 4242, NotifyResults = true, SpoilerMode = true });
            before.Set<MatchSnapshotEntity>().Add(new MatchSnapshotEntity { MatchKey = "pandascore:1", PayloadJson = "{}", ContentHash = "h", Status = 2 });
            await before.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var after = Context(path);
        await after.Database.MigrateAsync(TestContext.Current.CancellationToken);
        (await after.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        var config = await after.Set<EsportsGuildConfigEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        config.Should().BeEquivalentTo(new { GuildId = 42UL, ChannelId = (ulong?)4242, NotifyResults = true, SpoilerMode = true });
        (await after.Set<MatchSnapshotEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ContentHash.Should().Be("h");
        after.Set<Formula1GuildConfigEntity>().Add(new Formula1GuildConfigEntity { GuildId = 42, ChannelId = 99 });
        await after.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await after.Set<Formula1GuildConfigEntity>().AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Should().BeEquivalentTo(new
        {
            NotifyRaceStart = true,
            NotifyRaceResults = true,
            NotifyStandings = true,
            NotifyQualifyingStart = false,
            NotifySprintQualifyingResults = false,
            PingOnResults = false,
        }, "safe defaults: qualifying off, results do not ping");
    }
}
