namespace ToroSquad.Core.Modules;

/// <summary>The single, explicit list of modules compiled into this build.</summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<ModuleId, IToroModule> _modules;

    public ModuleRegistry(IEnumerable<IToroModule> modules)
    {
        _modules = [];
        foreach (var module in modules)
        {
            if (!_modules.TryAdd(module.Descriptor.Id, module))
                throw new InvalidOperationException($"Duplicate module id '{module.Descriptor.Id}'.");
        }

        var interactionTypeOwners = new Dictionary<Type, ModuleId>();
        foreach (var module in _modules.Values)
        {
            foreach (var type in module.InteractionModuleTypes)
            {
                if (!interactionTypeOwners.TryAdd(type, module.Descriptor.Id))
                    throw new InvalidOperationException($"Interaction type {type.FullName} is claimed by two modules.");
            }
        }
    }

    public IReadOnlyCollection<IToroModule> All => _modules.Values;

    public IEnumerable<IToroModule> Optional => _modules.Values.Where(m => !m.Descriptor.IsCore);

    public bool TryGet(ModuleId id, out IToroModule module) => _modules.TryGetValue(id, out module!);

    public bool TryGet(string id, out IToroModule module)
    {
        module = null!;
        try
        {
            return _modules.TryGetValue(new ModuleId(id), out module!);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Which module owns a given interaction module class — also a class nested in a registered one (a module's
    /// <c>/tsq-admin</c> group is nested in the class it registers).
    /// </summary>
    public ModuleId? OwnerOf(Type interactionModuleType)
    {
        for (var type = interactionModuleType; type is not null; type = type.DeclaringType)
        {
            foreach (var module in _modules.Values)
            {
                foreach (var t in module.InteractionModuleTypes)
                {
                    if (t == type || type.IsSubclassOf(t))
                        return module.Descriptor.Id;
                }
            }
        }

        return null;
    }
}
