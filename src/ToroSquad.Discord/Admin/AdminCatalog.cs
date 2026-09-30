using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;

namespace ToroSquad.Discord.Admin;

/// <summary>The shared slash options an operation may take (anything else it needs comes from its own form).</summary>
[Flags]
public enum AdminFields
{
    None = 0,
    Channel = 1,
    User = 2,
    Role = 4,
    Date = 8,
}

/// <summary>One admin operation: <c>/tsq-admin modul:&lt;module&gt; islem:&lt;Id&gt;</c>.</summary>
/// <param name="Permission">
/// What the caller needs (the same bits the service checks with <c>Authorize.Require</c>); used to filter suggestions and to
/// refuse early. The service still authorizes on its own.
/// </param>
public sealed record AdminOperation(string Id, AdminFields Accepts, GuildPermission Permission, Func<object, AdminCall, Task> Run);

/// <summary>
/// A module's admin operations, declared once and used for suggestions, validation, authorization, dispatch, /help and the
/// completeness tests. <see cref="Handler"/> is the module's own class (resolved per interaction from the DI scope) that
/// calls the module's services; it implements <see cref="IAdminFormHandler"/> when an operation continues in a form.
/// </summary>
public sealed class AdminModule
{
    private AdminModule(string id, ModuleId owner, Type handler, IReadOnlyList<AdminOperation> operations)
    {
        Id = id;
        Owner = owner;
        Handler = handler;
        Operations = operations;
    }

    /// <summary>The value of <c>modul</c> — the former <c>/&lt;id&gt;-admin</c> prefix (f1 stays f1).</summary>
    public string Id { get; }

    public ModuleId Owner { get; }

    public Type Handler { get; }

    public IReadOnlyList<AdminOperation> Operations { get; }

    /// <summary>Localized label key, e.g. <c>admin.news</c> → "Haberler".</summary>
    public string LabelKey => "admin." + Id;

    public string OperationLabelKey(AdminOperation operation) => LabelKey + "." + operation.Id;

    public AdminOperation? Find(string operation) => Operations.FirstOrDefault(o => o.Id == operation);

    public static Builder<T> For<T>(string id, ModuleId owner)
        where T : class => new(id, owner);

    public sealed class Builder<T>(string id, ModuleId owner)
        where T : class
    {
        private readonly List<AdminOperation> _operations = [];

        public Builder<T> Op(string operation, Func<T, AdminCall, Task> run, AdminFields accepts = AdminFields.None, GuildPermission permission = Authorize.ServerSettings)
        {
            if (_operations.Any(o => o.Id == operation))
                throw new InvalidOperationException($"Admin operation '{id} {operation}' is declared twice.");
            _operations.Add(new AdminOperation(operation, accepts, permission, (handler, call) => run((T)handler, call)));
            return this;
        }

        public AdminModule Build() => new(id, owner, typeof(T), _operations);
    }
}

/// <summary>Implemented by a module's admin handler when one of its operations continues in a private form.</summary>
public interface IAdminFormHandler
{
    /// <summary>
    /// A click, a selection or a submitted modal of a form this handler opened (<see cref="AdminCall.Draft"/>). The router has
    /// already checked that the draft is the caller's, in this guild, not expired, and that the caller may still run the
    /// operation.
    /// </summary>
    Task OnFormAsync(AdminCall call, string action);
}

/// <summary>Every registered admin module (one per feature module). Module order never matters: lists are sorted by id.</summary>
public sealed class AdminCatalog
{
    public AdminCatalog(IEnumerable<AdminModule> modules)
    {
        var list = modules.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        foreach (var dup in list.GroupBy(m => m.Id).Where(g => g.Count() > 1))
            throw new InvalidOperationException($"Admin module '{dup.Key}' is registered twice.");
        Modules = list;
    }

    public IReadOnlyList<AdminModule> Modules { get; }

    public AdminModule? Find(string module) => Modules.FirstOrDefault(m => m.Id == module);

    /// <summary>
    /// Registration problems that must block a sync: every module's declared "tsq-admin &lt;id&gt;" (descriptor AdminCommands)
    /// needs a registered admin module it owns, and every registered admin module must be declared by its owner.
    /// </summary>
    public IReadOnlyList<string> Problems(ModuleRegistry registry)
    {
        var problems = new List<string>();
        foreach (var module in registry.All)
        {
            foreach (var entry in module.Descriptor.AdminCommands.Where(e => e.StartsWith(Name + " ", StringComparison.Ordinal)))
            {
                var id = entry[(Name.Length + 1)..];
                var registered = Find(id);
                if (registered is null)
                    problems.Add($"/{Name}: module '{module.Descriptor.Id}' declares '{id}' but registered no admin operations for it");
                else if (registered.Owner != module.Descriptor.Id)
                    problems.Add($"/{Name}: '{id}' is declared by '{module.Descriptor.Id}' but owned by '{registered.Owner}'");
            }
        }

        foreach (var admin in Modules)
        {
            if (admin.Operations.Count == 0)
                problems.Add($"/{Name}: '{admin.Id}' has no operations");
            if (!registry.TryGet(admin.Owner, out var owner) || !owner.Descriptor.AdminCommands.Contains(Name + " " + admin.Id))
                problems.Add($"/{Name}: '{admin.Id}' is registered but its module '{admin.Owner}' does not declare '{Name} {admin.Id}'");
        }

        return problems;
    }

    public const string Name = "tsq-admin";

    /// <summary>A module's descriptor AdminCommands entry for its admin operations.</summary>
    public static string Entry(string module) => Name + " " + module;
}

public static class AdminServiceCollectionExtensions
{
    /// <summary>Registers a module's admin operations and its handler (scoped, like the services it calls).</summary>
    public static IServiceCollection AddAdminOperations(this IServiceCollection services, AdminModule module)
    {
        services.AddSingleton(module);
        services.AddScoped(module.Handler);
        return services;
    }
}
