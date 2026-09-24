namespace ToroSquad.Core;

/// <summary>Attribution for third-party code/data this build uses (shown in /bot about and /bot source).</summary>
public sealed record Attribution(string Name, string Url, string License, string Note);

/// <summary>
/// What is running: product identity, exact version/commit and where its Corresponding Source can be obtained.
/// Built once by the composition root.
/// </summary>
public sealed record ProductInfo(
    string Name,
    string Version,
    string? Commit,
    string License,
    string? SourceUrl,
    string? OperatorContact,
    IReadOnlyList<Attribution> Attributions)
{
    public const string ProductName = "ToroSquad Bot";
    public const string LicenseId = "AGPL-3.0-only";

    public bool SourceConfigured => !string.IsNullOrWhiteSpace(SourceUrl);
}

/// <summary>
/// Facts about where this instance runs. Demo/fixture data may only reach a REAL Discord guild if that guild is an
/// explicitly authorized test guild (and it is then labelled TEST/DEMO).
/// </summary>
public sealed record DeploymentPolicy(bool RealDiscordConnection, IReadOnlySet<ulong> TestGuildIds)
{
    public bool IsTestGuild(GuildId guild) => TestGuildIds.Contains(guild.Value);

    public bool MayShowDemoData(GuildId guild) => !RealDiscordConnection || IsTestGuild(guild);
}

/// <summary>Records guild join/leave for the retention policy.</summary>
public interface IGuildPresenceTracker
{
    Task MarkPresentAsync(GuildId guild, CancellationToken cancellationToken);
    Task MarkLeftAsync(GuildId guild, CancellationToken cancellationToken);
}

/// <summary>Remembers which commands the sync tool created, so only those may ever be pruned.</summary>
public interface IManagedCommandStore
{
    Task<IReadOnlyDictionary<string, ulong>> GetAsync(ulong applicationId, string scopeKey, CancellationToken cancellationToken);
    Task UpsertAsync(ulong applicationId, string scopeKey, string name, ulong commandId, string hash, CancellationToken cancellationToken);
    Task RemoveAsync(ulong applicationId, string scopeKey, string name, CancellationToken cancellationToken);
}
