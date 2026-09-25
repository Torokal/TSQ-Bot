namespace ToroSquad.Bot;

/// <summary>
/// Hosting guards for container platforms (Railway, docs/RAILWAY_DEPLOYMENT.md). The container filesystem is thrown away on
/// every deploy, so on Railway the SQLite database MUST live on the attached volume; starting anyway would silently lose
/// guild settings, subscriptions and the notification ledger (duplicate notifications after the next deploy).
/// Values come from the platform's own variables (not from the TOROSQUAD_ configuration); nothing secret is involved.
/// </summary>
public static class HostingChecks
{
    public static bool OnRailway(Func<string, string?> env) =>
        !string.IsNullOrWhiteSpace(env("RAILWAY_ENVIRONMENT_ID")) || !string.IsNullOrWhiteSpace(env("RAILWAY_PROJECT_ID"));

    /// <summary>Storage problems that must stop startup (empty off Railway).</summary>
    public static IReadOnlyList<string> StorageProblems(string databasePath, Func<string, string?> env)
    {
        if (!OnRailway(env))
            return [];
        var mount = env("RAILWAY_VOLUME_MOUNT_PATH");
        if (string.IsNullOrWhiteSpace(mount))
            return ["[storage] Running on Railway WITHOUT a volume: the SQLite database would be lost on every deploy. Attach one volume to this service, mounted at /data."];
        if (!IsInside(databasePath, mount))
            return [$"[storage] The database ({Path.GetFullPath(databasePath)}) is outside the Railway volume ({mount}). Set TOROSQUAD_Bot__DataDirectory={mount}."];
        return [];
    }

    /// <summary>Creates the data directory and proves it is writable (a probe file is written and deleted).</summary>
    public static string? WritableProblem(string dataDirectory, Func<string, string?> env)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var probe = Path.Combine(dataDirectory, ".tsq-write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var hint = OnRailway(env) ? " Railway mounts volumes as root: set RAILWAY_RUN_UID=0 on the service." : "";
            return $"[storage] Data directory {dataDirectory} is not writable ({ex.GetType().Name}).{hint}";
        }
    }

    /// <summary>The commit embedded at build time, else the platform's deployment commit (e.g. RAILWAY_GIT_COMMIT_SHA).</summary>
    public static string? Commit(string? embedded, Func<string, string?> env) =>
        !string.IsNullOrWhiteSpace(embedded) ? embedded
        : env("RAILWAY_GIT_COMMIT_SHA") is { Length: > 0 } sha ? sha
        : null;

    private static bool IsInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith("../", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
