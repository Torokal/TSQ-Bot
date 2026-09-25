using Microsoft.Data.Sqlite;
using ToroSquad.Bot;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// Container hosting guards (docs/RAILWAY_DEPLOYMENT.md): on Railway the SQLite database must live on the attached volume,
/// the data directory must be writable, a damaged database stops startup (never deleted/recreated), and the deployment
/// commit is shown when the build had no git metadata.
/// </summary>
public sealed class HostingChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsq-hosting-" + Guid.NewGuid().ToString("N"));

    public HostingChecksTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Off_railway_nothing_is_required()
    {
        HostingChecks.OnRailway(Env()).Should().BeFalse();
        HostingChecks.StorageProblems(Path.Combine(_root, "torosquad.db"), Env()).Should().BeEmpty();
    }

    [Fact]
    public void On_railway_without_a_volume_startup_is_refused()
    {
        var problems = HostingChecks.StorageProblems(Path.Combine(_root, "torosquad.db"), Env(("RAILWAY_ENVIRONMENT_ID", "env")));
        problems.Should().ContainSingle().Which.Should().Contain("WITHOUT a volume").And.Contain("/data");
    }

    [Fact]
    public void On_railway_the_database_must_be_inside_the_volume()
    {
        var volume = Path.Combine(_root, "data");
        var env = Env(("RAILWAY_PROJECT_ID", "p"), ("RAILWAY_VOLUME_MOUNT_PATH", volume));

        HostingChecks.StorageProblems(Path.Combine(volume, "torosquad.db"), env).Should().BeEmpty();
        HostingChecks.StorageProblems(Path.Combine(_root, "app", "data", "torosquad.db"), env)
            .Should().ContainSingle().Which.Should().Contain("outside the Railway volume").And.Contain("TOROSQUAD_Bot__DataDirectory");
        HostingChecks.StorageProblems(Path.Combine(_root, "data-other", "torosquad.db"), env)
            .Should().ContainSingle("a sibling folder whose name merely starts like the volume is still outside it");
    }

    [Fact]
    public void Default_bot_options_resolve_the_database_into_the_configured_data_directory()
    {
        var volume = Path.Combine(_root, "data");
        new BotOptions { DataDirectory = volume }.DatabasePath(Path.Combine(_root, "app")).Should().Be(Path.Combine(volume, "torosquad.db"));
        new BotOptions().DatabasePath(Path.Combine(_root, "app")).Should().Be(Path.Combine(_root, "app", "data", "torosquad.db"), "local default unchanged");
    }

    [Fact]
    public void An_unwritable_data_directory_is_reported_with_the_railway_hint()
    {
        HostingChecks.WritableProblem(Path.Combine(_root, "ok"), Env()).Should().BeNull();
        Directory.GetFiles(Path.Combine(_root, "ok")).Should().BeEmpty("the probe file is removed");

        var file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "x");
        HostingChecks.WritableProblem(file, Env(("RAILWAY_ENVIRONMENT_ID", "env")))
            .Should().Contain("not writable").And.Contain("RAILWAY_RUN_UID=0");
    }

    [Fact]
    public void The_embedded_commit_wins_and_railway_commit_is_the_fallback()
    {
        HostingChecks.Commit("abc123", Env(("RAILWAY_GIT_COMMIT_SHA", "def456"))).Should().Be("abc123");
        HostingChecks.Commit(null, Env(("RAILWAY_GIT_COMMIT_SHA", "def456"))).Should().Be("def456");
        HostingChecks.Commit(null, Env()).Should().BeNull();
    }

    [Fact]
    public void Integrity_check_passes_healthy_and_missing_databases_and_reports_damage_without_changing_it()
    {
        var path = Path.Combine(_root, "torosquad.db");
        DatabaseMaintenance.IntegrityProblem(path).Should().BeNull("a missing database is created by the first start");

        // Unpooled: no handle stays open (never ClearAllPools here — it would reclaim parallel tests' connections).
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder(DatabaseMaintenance.ConnectionString(path)) { Pooling = false }.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE t(x INTEGER); INSERT INTO t VALUES (1);";
            cmd.ExecuteNonQuery();
        }

        DatabaseMaintenance.IntegrityProblem(path).Should().BeNull();

        var damaged = Path.Combine(_root, "damaged.db");
        File.WriteAllBytes(damaged, [.. File.ReadAllBytes(path).Take(100), .. Enumerable.Repeat((byte)0xFF, 4000)]);
        var before = File.ReadAllBytes(damaged);
        DatabaseMaintenance.IntegrityProblem(damaged).Should().NotBeNull("damage stops startup with a clear message");
        File.ReadAllBytes(damaged).Should().Equal(before, "the check never modifies the file");
    }
}
