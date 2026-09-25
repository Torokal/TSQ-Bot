namespace ToroSquad.Infrastructure.Hosting;

/// <summary>Operator-level settings (section "Bot"). No secrets here.</summary>
public sealed class BotOptions
{
    public const string Section = "Bot";

    /// <summary>SQLite database, instance lock and backups live here. Relative paths resolve from the content root.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// Public URL where users of THIS running version can obtain its Corresponding Source (AGPL-3.0 §13), e.g. a
    /// public git repository tag/commit or a hosted source archive produced by scripts/Export-Source.ps1.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>Operator contact shown in /bot about and used in provider User-Agent strings.</summary>
    public string? OperatorContact { get; set; }

    /// <summary>
    /// Standby (deployment/maintenance): validate configuration and prove the data directory is writable, then idle —
    /// no Discord connection, no background workers and the database file is NOT opened (so it can be replaced safely,
    /// e.g. when moving a local database onto a Railway volume). See docs/RAILWAY_DEPLOYMENT.md.
    /// </summary>
    public bool Standby { get; set; }

    /// <summary>In-app scheduled database backups (see <see cref="DatabaseBackupService"/>).</summary>
    public DatabaseBackupOptions Backup { get; set; } = new();

    /// <summary>How long guild data is kept after the bot is removed from that guild.</summary>
    public int GuildDataRetentionDays { get; set; } = 30;

    public string DatabasePath(string contentRoot) =>
        Path.Combine(Path.GetFullPath(DataDirectory, contentRoot), "torosquad.db");
}
