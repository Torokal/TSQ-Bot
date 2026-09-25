using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ToroSquad.Infrastructure.Persistence;

/// <summary>Migration, online backup and restore helpers used by the CLI verbs (docs/OPERATIONS.md).</summary>
public static class DatabaseMaintenance
{
    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
        DefaultTimeout = 30,
    }.ToString();

    public static async Task MigrateAsync(ToroDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.MigrateAsync(cancellationToken);
        // WAL: readers don't block the single writer; safe for our single-process model.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    /// <summary>
    /// <c>PRAGMA integrity_check</c> on an existing database file (unpooled, so no handle stays open). Returns null when
    /// the file does not exist yet or is healthy, otherwise the first problem reported by SQLite. Never modifies data.
    /// </summary>
    public static string? IntegrityProblem(string databasePath)
    {
        if (!File.Exists(databasePath))
            return null;
        var builder = new SqliteConnectionStringBuilder(ConnectionString(databasePath)) { Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = cmd.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? "no result";
        }
        catch (SqliteException ex)
        {
            // "file is not a database", "database disk image is malformed", …
            return $"SQLite error {ex.SqliteErrorCode}: {ex.Message}";
        }
    }

    /// <summary>Consistent online copy using SQLite's backup API (safe while the bot runs).</summary>
    public static string Backup(string databasePath, string backupDirectory, TimeProvider clock)
    {
        Directory.CreateDirectory(backupDirectory);
        var stamp = clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupDirectory, $"torosquad-{stamp}.db");
        using var source = new SqliteConnection(ConnectionString(databasePath));
        using var destination = new SqliteConnection(ConnectionString(target));
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        return target;
    }

    /// <summary>
    /// Restores a backup file over the live database. The bot MUST be stopped (the instance lock enforces this in
    /// the CLI). The current database is first copied aside so a restore is itself reversible.
    /// </summary>
    public static string Restore(string backupFile, string databasePath, TimeProvider clock)
    {
        if (!File.Exists(backupFile))
            throw new FileNotFoundException("Backup file not found.", backupFile);
        using (var probe = new SqliteConnection(ConnectionString(backupFile)))
        {
            probe.Open();
            using var cmd = probe.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check;";
            var result = cmd.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Backup integrity check failed: {result}");
        }

        SqliteConnection.ClearAllPools();
        var safety = databasePath + "." + clock.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".pre-restore";
        if (File.Exists(databasePath))
            File.Copy(databasePath, safety, overwrite: false);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(databasePath + suffix))
                File.Delete(databasePath + suffix);
        }

        File.Copy(backupFile, databasePath, overwrite: true);
        return safety;
    }
}
