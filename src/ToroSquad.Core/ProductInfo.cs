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
    /// <summary>The single source of the user-facing product name. Localization strings use the {product} token.</summary>
    public const string ProductName = "TSQ Bot";

    /// <summary>Product token for outgoing HTTP User-Agent headers (no spaces allowed there).</summary>
    public const string UserAgentProduct = "TSQBot";
    public const string LicenseId = "AGPL-3.0-only";

    public bool SourceConfigured => !string.IsNullOrWhiteSpace(SourceUrl);
}

/// <summary>
/// Facts about where this instance runs. Demo/fixture data may only reach a REAL Discord guild if that guild is an
/// explicitly authorized test guild (and it is then labelled TEST/DEMO).
/// </summary>
public sealed record DeploymentPolicy(bool RealDiscordConnection, IReadOnlySet<ulong> TestGuildIds, IReadOnlySet<ulong>? AllowedGuildIds = null, string Hosting = "local")
{
    public bool IsTestGuild(GuildId guild) => TestGuildIds.Contains(guild.Value);

    public bool MayShowDemoData(GuildId guild) => !RealDiscordConnection || IsTestGuild(guild);

    /// <summary>True when a runtime allow-list is configured (Discord:AllowedGuildIds); empty = unrestricted (local dev).</summary>
    public bool GuildRestricted => AllowedGuildIds is { Count: > 0 };

    public bool SingleGuild => AllowedGuildIds is { Count: 1 };

    /// <summary>
    /// Server-side guild guard: with an allow-list, ONLY those guilds may use the bot (commands, buttons, modals,
    /// autocomplete, notifications). A missing guild (DM) is refused whenever the bot is restricted.
    /// </summary>
    public bool IsGuildAllowed(ulong? guildId) =>
        !GuildRestricted || (guildId is { } id && AllowedGuildIds!.Contains(id));

    public bool IsGuildAllowed(GuildId guild) => IsGuildAllowed(guild.Value);
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
