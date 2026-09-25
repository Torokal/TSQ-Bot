using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Infrastructure.Persistence;

namespace ToroSquad.Infrastructure.Hosting;

/// <summary>Where the live SQLite database is (resolved once from <see cref="BotOptions.DatabasePath"/>).</summary>
public sealed record DatabaseLocation(string Path);

/// <summary>
/// In-app scheduled backups (section "Bot:Backup"): a consistent copy of the live database via SQLite's backup API (safe
/// while the bot runs), integrity-checked, kept to the newest <see cref="DatabaseBackupOptions.Keep"/>. Only files this
/// service names (<c>torosquad-yyyyMMddTHHmmssZ.db</c>) are ever pruned. The copies live next to the database (on Railway:
/// the same volume) — they protect against corruption and mistakes, NOT against losing the volume itself.
/// A failure is logged and never stops the bot.
/// </summary>
public sealed partial class DatabaseBackupService(DatabaseLocation database, IOptions<BotOptions> options, TimeProvider clock, ILogger<DatabaseBackupService> logger)
    : BackgroundService
{
    public string BackupDirectory
    {
        get
        {
            var dataDirectory = System.IO.Path.GetDirectoryName(database.Path)!;
            var configured = options.Value.Backup.Directory;
            return string.IsNullOrWhiteSpace(configured) ? System.IO.Path.Combine(dataDirectory, "backups") : System.IO.Path.GetFullPath(configured, dataDirectory);
        }
    }

    /// <summary>Backups written by this service, newest first.</summary>
    public IReadOnlyList<(string Path, DateTimeOffset TakenAt)> Backups()
    {
        if (!Directory.Exists(BackupDirectory))
            return [];
        return Directory.EnumerateFiles(BackupDirectory, "torosquad-*.db")
            .Select(p => (Path: p, Match: BackupName().Match(System.IO.Path.GetFileName(p))))
            .Where(x => x.Match.Success)
            .Select(x => (x.Path, DateTimeOffset.ParseExact(x.Match.Groups["stamp"].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    /// <summary>Takes a backup when one is due; returns its path, or null when disabled, not due or failed.</summary>
    public string? RunOnce()
    {
        var o = options.Value.Backup;
        if (!o.Enabled || !File.Exists(database.Path))
            return null;
        var now = clock.GetUtcNow();
        if (Backups() is [var latest, ..] && now - latest.TakenAt < TimeSpan.FromHours(Math.Max(1, o.IntervalHours)))
            return null;

        var path = DatabaseMaintenance.Backup(database.Path, BackupDirectory, clock);
        if (DatabaseMaintenance.IntegrityProblem(path) is { } problem)
        {
            File.Delete(path);
            logger.LogError("Database backup failed its integrity check and was discarded: {Problem}", problem);
            return null;
        }

        var removed = 0;
        foreach (var old in Backups().Skip(Math.Max(1, o.Keep)))
        {
            File.Delete(old.Path);
            removed++;
        }

        logger.LogInformation("Database backup written: {Path} ({Bytes} bytes, integrity OK); kept {Kept} newest, removed {Removed}",
            path, new FileInfo(path).Length, Math.Min(Backups().Count, Math.Max(1, o.Keep)), removed);
        return path;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (migrations, first polls) settle, then check hourly; a backup is taken once per interval.
        var delay = TimeSpan.FromMinutes(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                RunOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Database backup run failed; the bot keeps running");
            }

            delay = TimeSpan.FromHours(1);
        }
    }

    [GeneratedRegex(@"^torosquad-(?<stamp>\d{8}T\d{6}Z)\.db$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupName();
}

/// <summary>Section "Bot:Backup".</summary>
public sealed class DatabaseBackupOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalHours { get; set; } = 24;

    /// <summary>How many of this service's backups are kept (newest first).</summary>
    public int Keep { get; set; } = 7;

    /// <summary>Relative to the data directory; default "backups".</summary>
    public string? Directory { get; set; }
}
