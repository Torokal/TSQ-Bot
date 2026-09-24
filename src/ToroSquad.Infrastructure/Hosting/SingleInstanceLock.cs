namespace ToroSquad.Infrastructure.Hosting;

/// <summary>
/// ToroSquad Bot supports exactly one running instance per data directory (SQLite, in-process scheduler).
/// A second instance would send the same notifications in parallel, so it refuses to start instead.
/// Uses an OS-level exclusive file handle (released automatically if the process dies).
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _handle;

    private SingleInstanceLock(FileStream handle, string path)
    {
        _handle = handle;
        Path = path;
    }

    public string Path { get; }

    public static SingleInstanceLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = System.IO.Path.Combine(dataDirectory, "torosquad.instance.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write($"pid={Environment.ProcessId} started={DateTimeOffset.UtcNow:O}");
            }

            stream.Flush();
            return new SingleInstanceLock(stream, path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another ToroSquad Bot instance is already using '{dataDirectory}'. Horizontal scaling is not supported (single SQLite instance).", ex);
        }
    }

    public void Dispose() => _handle.Dispose();
}
