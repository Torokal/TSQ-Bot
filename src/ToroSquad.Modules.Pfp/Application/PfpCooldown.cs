namespace ToroSquad.Modules.Pfp.Application;

/// <summary>
/// One short in-memory cooldown shared by /pfp and /banner, per guild + invoking member: after an admitted command the same
/// member waits <see cref="Duration"/> in that guild. Nothing is persisted (a restart forgets it, which is harmless); expired
/// entries are dropped on every call, so the map only holds members who used the commands in the last few seconds.
/// Thread-safe.
/// </summary>
public sealed class PfpCooldown(TimeProvider clock)
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<(ulong Guild, ulong User), DateTimeOffset> _until = [];

    /// <summary>True (and the cooldown starts) when the member may run a command now; otherwise how long to wait.</summary>
    public bool TryEnter(ulong guildId, ulong userId, out TimeSpan wait)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var key in _until.Where(p => p.Value <= now).Select(p => p.Key).ToList())
                _until.Remove(key);
            if (_until.TryGetValue((guildId, userId), out var until))
            {
                wait = until - now;
                return false;
            }

            _until[(guildId, userId)] = now + Duration;
            wait = TimeSpan.Zero;
            return true;
        }
    }
}
