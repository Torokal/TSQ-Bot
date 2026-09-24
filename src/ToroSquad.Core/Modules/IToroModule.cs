using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Security;

namespace ToroSquad.Core.Modules;

/// <summary>Stable module identifier, e.g. "core", "esports". Lowercase ASCII, used in DB rows and custom IDs.</summary>
public readonly record struct ModuleId
{
    public ModuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 || !value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            throw new ArgumentException($"Invalid module id '{value}'. Use 1-32 chars of [a-z0-9-].", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;

    public static readonly ModuleId Core = new("core");
}

/// <summary>
/// Static facts about a module. Localized names come from the module's localization catalog.
/// <see cref="AdminCommands"/> lists the module's top-level slash commands that must be hidden from regular members
/// (default_member_permissions) — validated against the generated manifest.
/// </summary>
public sealed record ModuleDescriptor(
    ModuleId Id,
    Version Version,
    string NameKey,
    string DescriptionKey,
    bool IsCore,
    bool EnabledByDefault,
    GuildPermission RequiredBotChannelPermissions,
    GuildPermission OptionalBotPermissions,
    IReadOnlyList<string> AdminCommands);

/// <summary>
/// Contract every ToroSquad feature module implements. Modules are compiled in and registered explicitly in the
/// composition root (ToroSquad.Bot) — there is deliberately no runtime DLL plugin loading.
/// </summary>
public interface IToroModule
{
    ModuleDescriptor Descriptor { get; }

    /// <summary>Discord interaction module classes owned by this module (Core does not reference the Discord SDK).</summary>
    IReadOnlyList<Type> InteractionModuleTypes { get; }

    /// <summary>Register services, hosted jobs, EF model contributors, localization catalogs.</summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>Return human-readable configuration problems (empty = valid). Must not include secret values.</summary>
    IReadOnlyList<string> ValidateConfiguration(IConfiguration configuration);
}

public enum HealthState
{
    Healthy = 0,
    Degraded = 1,
    Unavailable = 2,
    NotConfigured = 3,
}

public sealed record HealthEntry(string Component, HealthState State, string DetailKey, IReadOnlyList<object>? Args = null);

public sealed record ModuleHealthReport(ModuleId Module, IReadOnlyList<HealthEntry> Entries)
{
    public HealthState Overall => Entries.Count == 0 ? HealthState.Healthy : Entries.Max(e => e.State);
}

/// <summary>Optional per-module health probe; must be cheap (no provider calls) — report cached provider state.</summary>
public interface IModuleHealthCheck
{
    ModuleId Module { get; }
    Task<ModuleHealthReport> CheckAsync(CancellationToken cancellationToken);
}
