using ToroSquad.Core.Security;

namespace ToroSquad.Core.Modules;

/// <summary>Persistent per-guild module on/off state. Disabling never deletes module data.</summary>
public interface IModuleStateStore
{
    /// <summary>Returns null when the guild never set an explicit state for the module.</summary>
    Task<bool?> GetExplicitStateAsync(GuildId guild, ModuleId module, CancellationToken cancellationToken);

    Task SetStateAsync(GuildId guild, ModuleId module, bool enabled, UserId changedBy, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<ModuleId, bool>> GetExplicitStatesAsync(GuildId guild, CancellationToken cancellationToken);

    /// <summary>All guilds that currently have the module enabled explicitly.</summary>
    Task<IReadOnlyList<GuildId>> GetGuildsWithModuleEnabledAsync(ModuleId module, CancellationToken cancellationToken);
}

/// <summary>
/// Hook a module can implement to react to its own enable/disable transitions (e.g. reset notification
/// watermarks so re-enabling never floods a channel with backlog).
/// </summary>
public interface IModuleLifecycleHandler
{
    ModuleId Module { get; }
    Task OnEnabledAsync(GuildId guild, CancellationToken cancellationToken);
    Task OnDisabledAsync(GuildId guild, CancellationToken cancellationToken);
}

/// <summary>
/// The single place every entry path (slash command, component, modal, background job, delivery) asks
/// "is this module active in this guild?".
/// </summary>
public interface IModuleGate
{
    Task<bool> IsEnabledAsync(GuildId guild, ModuleId module, CancellationToken cancellationToken);
}

public sealed class ModuleGate(ModuleRegistry registry, IModuleStateStore store) : IModuleGate
{
    public async Task<bool> IsEnabledAsync(GuildId guild, ModuleId module, CancellationToken cancellationToken)
    {
        if (!registry.TryGet(module, out var descriptor))
            return false; // unknown/unloaded module is never active
        if (descriptor.Descriptor.IsCore)
            return true;
        var explicitState = await store.GetExplicitStateAsync(guild, module, cancellationToken);
        return explicitState ?? descriptor.Descriptor.EnabledByDefault;
    }
}

/// <summary>Application service behind /modules list|enable|disable.</summary>
public sealed class ModuleManagementService(
    ModuleRegistry registry,
    IModuleStateStore store,
    IModuleGate gate,
    IEnumerable<IModuleLifecycleHandler> lifecycleHandlers)
{
    public sealed record ModuleStatus(ModuleDescriptor Descriptor, bool Enabled);

    public async Task<IReadOnlyList<ModuleStatus>> ListAsync(GuildId guild, CancellationToken cancellationToken)
    {
        var result = new List<ModuleStatus>();
        foreach (var module in registry.All.OrderBy(m => m.Descriptor.IsCore ? 0 : 1).ThenBy(m => m.Descriptor.Id.Value, StringComparer.Ordinal))
            result.Add(new(module.Descriptor, await gate.IsEnabledAsync(guild, module.Descriptor.Id, cancellationToken)));
        return result;
    }

    public async Task<OperationResult> SetEnabledAsync(ActorContext actor, string moduleId, bool enabled, CancellationToken cancellationToken)
    {
        var auth = Authorize.Require(actor, actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
            return OperationResult.Forbidden(auth);

        if (!registry.TryGet(moduleId, out var module))
            return OperationResult.Fail(OperationError.NotFound, "modules.unknown", moduleId);
        if (module.Descriptor.IsCore)
            return OperationResult.Fail(OperationError.InvalidInput, "modules.core_cannot_toggle");

        var current = await gate.IsEnabledAsync(actor.GuildId, module.Descriptor.Id, cancellationToken);
        if (current == enabled)
            return OperationResult.Ok(enabled ? "modules.already_enabled" : "modules.already_disabled", module.Descriptor.NameKey);

        await store.SetStateAsync(actor.GuildId, module.Descriptor.Id, enabled, actor.UserId, cancellationToken);
        foreach (var handler in lifecycleHandlers.Where(h => h.Module == module.Descriptor.Id))
        {
            if (enabled)
                await handler.OnEnabledAsync(actor.GuildId, cancellationToken);
            else
                await handler.OnDisabledAsync(actor.GuildId, cancellationToken);
        }

        return OperationResult.Ok(enabled ? "modules.enabled" : "modules.disabled", module.Descriptor.NameKey);
    }
}
