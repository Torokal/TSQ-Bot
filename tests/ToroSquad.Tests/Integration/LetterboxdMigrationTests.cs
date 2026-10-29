using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ToroSquad.Bot;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Letterboxd.Persistence;

namespace ToroSquad.Tests.Integration;

/// <summary>
/// The <c>LetterboxdModule</c> migration on a copy of the base schema (main 3a8ebc9, last migration <see cref="BaseMigration"/>):
/// existing tables and indexes are byte-for-byte unchanged, existing rows stay, the four <c>letterboxd_*</c> tables appear,
/// the model has no pending change, and the migration can be rolled back to the base schema.
/// </summary>
public sealed class LetterboxdMigrationTests
{
    public const string BaseMigration = "20261026160046_SorModule";
    public const string LetterboxdMigration = "20261029104431_LetterboxdModule";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_migration_is_additive_on_the_base_schema_and_reversible()
    {
        var directory = Path.Combine(Path.GetTempPath(), "torosquad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "upgrade.db");
        var options = new DbContextOptionsBuilder<ToroDbContext>()
            .UseSqlite(DatabaseMaintenance.ConnectionString(path), o => o.MigrationsAssembly(typeof(DesignTimeDbContextFactory).Assembly.GetName().Name))
            .ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>()
            .Options;
        try
        {
            await using var db = new ToroDbContext(options, DesignTimeDbContextFactory.AllContributors());
            var migrator = db.GetService<IMigrator>();
            DesignTimeDbContextFactory.AllContributors().Should().ContainSingle(c => c is LetterboxdModelContributor, "design time sees the Letterboxd tables");

            await migrator.MigrateAsync(BaseMigration, Ct);
            var before = await SchemaAsync(db);
            before.Should().NotContain(o => o.Name.StartsWith("letterboxd_", StringComparison.Ordinal));
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO guild_settings (GuildId, Language, TimeZoneId, SetupCompleted, UpdatedAt, UpdatedBy) VALUES (4242, 'tr', 'Europe/Istanbul', 1, 0, 1);", Ct);

            await migrator.MigrateAsync(LetterboxdMigration, Ct);
            var after = await SchemaAsync(db);
            after.Where(o => !o.Name.StartsWith("letterboxd_", StringComparison.Ordinal) && !o.Name.StartsWith("IX_letterboxd_", StringComparison.Ordinal))
                .Should().Equal(before, "no existing table or index changes");
            after.Where(o => o.Type == "table" && o.Name.StartsWith("letterboxd_", StringComparison.Ordinal)).Select(o => o.Name).Should().BeEquivalentTo(
                "letterboxd_entry", "letterboxd_delivery", "letterboxd_source_state", "letterboxd_guild_config");
            after.Should().Contain(o => o.Name == "IX_letterboxd_entry_LastSeenAt");
            (await db.Database.SqlQueryRaw<long>("SELECT GuildId AS Value FROM guild_settings").ToListAsync(Ct)).Should().Equal(4242L);

            await db.Database.MigrateAsync(Ct);
            (await db.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();
            db.Database.HasPendingModelChanges().Should().BeFalse();

            await migrator.MigrateAsync(BaseMigration, Ct);
            (await SchemaAsync(db)).Should().Equal(before, "rolling back removes exactly the Letterboxd tables (and every later migration)");
        }
        finally
        {
            using (var connection = new SqliteConnection(DatabaseMaintenance.ConnectionString(path)))
                SqliteConnection.ClearPool(connection);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // best effort on Windows
            }
        }
    }

    private sealed record SchemaObject(string Type, string Name, string? Sql);

    private static async Task<List<SchemaObject>> SchemaAsync(ToroDbContext db)
    {
        var rows = await db.Database.SqlQueryRaw<SchemaRow>(
            "SELECT type AS Type, name AS Name, sql AS Sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' AND name <> '__EFMigrationsLock' ORDER BY type, name")
            .ToListAsync(Ct);
        return rows.Select(r => new SchemaObject(r.Type, r.Name, r.Sql)).ToList();
    }

    private sealed class SchemaRow
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Sql { get; set; }
    }
}
