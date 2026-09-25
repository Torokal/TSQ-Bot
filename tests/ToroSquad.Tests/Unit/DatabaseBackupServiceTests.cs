using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ToroSquad.Infrastructure.Hosting;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Tests.Support;

namespace ToroSquad.Tests.Unit;

/// <summary>
/// In-app daily database backups (Railway's own volume backups need the Pro plan): consistent copy, integrity-checked,
/// once per interval, newest N kept, foreign files never touched, and a failure never stops the bot.
/// </summary>
public sealed class DatabaseBackupServiceTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "tsq-backup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(TestHost.T0);

    public DatabaseBackupServiceTests()
    {
        Directory.CreateDirectory(_data);
        // Unpooled: no handle stays open (never ClearAllPools — it would reclaim parallel tests' connections).
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(DatabaseMaintenance.ConnectionString(DatabasePath)) { Pooling = false }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE guilds(id INTEGER); INSERT INTO guilds VALUES (618763184815472651);";
        cmd.ExecuteNonQuery();
    }

    private string DatabasePath => Path.Combine(_data, "torosquad.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DatabaseBackupService Service(Action<DatabaseBackupOptions>? configure = null)
    {
        var options = new BotOptions { DataDirectory = _data };
        configure?.Invoke(options.Backup);
        return new DatabaseBackupService(new DatabaseLocation(DatabasePath), Options.Create(options), _clock, NullLogger<DatabaseBackupService>.Instance);
    }

    [Fact]
    public void The_first_run_writes_a_consistent_integrity_checked_copy_into_data_backups()
    {
        var service = Service();
        var path = service.RunOnce();

        path.Should().NotBeNull();
        Path.GetDirectoryName(path).Should().Be(Path.Combine(_data, "backups"), "next to the database (on Railway: the volume)");
        Path.GetFileName(path).Should().Be("torosquad-20260924T120000Z.db");
        DatabaseMaintenance.IntegrityProblem(path!).Should().BeNull();
        using var copy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        copy.Open();
        using var cmd = copy.CreateCommand();
        cmd.CommandText = "SELECT id FROM guilds";
        cmd.ExecuteScalar().Should().Be(618763184815472651L, "the copy contains the committed data (WAL included)");
    }

    [Fact]
    public void A_backup_is_taken_once_per_interval()
    {
        var service = Service();
        service.RunOnce().Should().NotBeNull();

        _clock.Advance(TimeSpan.FromHours(23));
        service.RunOnce().Should().BeNull("not due yet");
        _clock.Advance(TimeSpan.FromHours(1));
        service.RunOnce().Should().NotBeNull("24 h later");
        service.Backups().Should().HaveCount(2);

        // A restart (new service instance) sees the existing backups and does not take an extra one.
        _clock.Advance(TimeSpan.FromHours(2));
        Service().RunOnce().Should().BeNull();
    }

    [Fact]
    public void Only_the_newest_backups_are_kept_and_foreign_files_are_never_touched()
    {
        var service = Service(o => o.Keep = 3);
        var backups = Path.Combine(_data, "backups");
        Directory.CreateDirectory(backups);
        var handoff = Path.Combine(backups, "torosquad-handoff-manual.db");
        var notes = Path.Combine(backups, "README.txt");
        File.WriteAllText(handoff, "x");
        File.WriteAllText(notes, "x");

        for (var i = 0; i < 5; i++)
        {
            service.RunOnce().Should().NotBeNull();
            _clock.Advance(TimeSpan.FromHours(24));
        }

        service.Backups().Select(b => Path.GetFileName(b.Path)).Should().Equal(
            "torosquad-20260928T120000Z.db", "torosquad-20260927T120000Z.db", "torosquad-20260926T120000Z.db");
        File.Exists(handoff).Should().BeTrue();
        File.Exists(notes).Should().BeTrue();
    }

    [Fact]
    public void Disabled_or_missing_database_does_nothing()
    {
        Service(o => o.Enabled = false).RunOnce().Should().BeNull();
        Directory.Exists(Path.Combine(_data, "backups")).Should().BeFalse();

        var empty = Path.Combine(_data, "empty");
        Directory.CreateDirectory(empty);
        new DatabaseBackupService(new DatabaseLocation(Path.Combine(empty, "torosquad.db")), Options.Create(new BotOptions()), _clock, NullLogger<DatabaseBackupService>.Instance)
            .RunOnce().Should().BeNull("nothing to back up before the first start");
    }

    [Fact]
    public async Task A_failing_backup_is_logged_and_the_service_keeps_running()
    {
        // The backup "directory" is a file: every attempt fails.
        File.WriteAllText(Path.Combine(_data, "not-a-dir"), "x");
        var service = Service(o => o.Directory = "not-a-dir");
        FluentActions.Invoking(service.RunOnce).Should().Throw<IOException>("RunOnce reports the failure");

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        _clock.Advance(TimeSpan.FromMinutes(3)); // first scheduled run → fails inside the loop
        await Task.Delay(100, TestContext.Current.CancellationToken);
        service.ExecuteTask!.IsCompleted.Should().BeFalse("a failed backup never stops the service or the bot");
        await service.StopAsync(CancellationToken.None);
    }
}
