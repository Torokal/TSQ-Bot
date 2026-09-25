namespace ToroSquad.Discord;

public enum DiscordTransportMode
{
    /// <summary>Safe local default: no Discord connection; messages are captured in-process and logged.</summary>
    Fake = 0,

    /// <summary>Real bot connection (gateway + REST) using the bot token.</summary>
    Gateway = 1,
}

/// <summary>Section "Discord". The token comes from environment variables or user-secrets — never appsettings files.</summary>
public sealed class DiscordOptions
{
    public const string Section = "Discord";

    public DiscordTransportMode Transport { get; set; } = DiscordTransportMode.Fake;

    /// <summary>Bot token (secret). Set via TOROSQUAD_Discord__Token or `dotnet user-secrets`.</summary>
    public string? Token { get; set; }

    /// <summary>Application (client) id of YOUR bot application. Sync refuses to run if the token belongs to another app.</summary>
    public ulong ApplicationId { get; set; }

    /// <summary>
    /// Explicitly authorized test guilds. Fixture/demo data may only be shown in Discord in these guilds (and is
    /// labelled TEST/DEMO).
    /// </summary>
    public ulong[] TestGuildIds { get; set; } = [];

    /// <summary>
    /// Runtime guild allow-list (server-side guard). When set, interactions from any other guild are refused and no
    /// notification is planned or delivered elsewhere. Empty = unrestricted (local development only).
    /// </summary>
    public ulong[] AllowedGuildIds { get; set; } = [];

    /// <summary>Guilds the Sync-Commands tool is allowed to register commands in (explicit allow-list).</summary>
    public ulong[] CommandSyncGuildIds { get; set; } = [];

    /// <summary>Separate approval gate for global command registration. Default false.</summary>
    public bool AllowGlobalCommandSync { get; set; }
}
