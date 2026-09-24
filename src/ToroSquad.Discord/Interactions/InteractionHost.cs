using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Commands.Manifest;

namespace ToroSquad.Discord.Interactions;

/// <summary>
/// Owns the single <see cref="InteractionService"/>: loads every registered module's interaction classes and builds
/// the command manifest offline. Used by the running bot and by the CLI (export/sync) alike.
/// </summary>
public sealed class InteractionHost(InteractionService service, ModuleRegistry registry, ILocalizer localizer, ILogger<InteractionHost> logger) : IDisposable
{
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public InteractionService Service => service;

    public CommandManifest? Manifest { get; private set; }

    public IReadOnlySet<string> AdminCommandNames =>
        registry.All.SelectMany(m => m.Descriptor.AdminCommands).ToHashSet(StringComparer.Ordinal);

    public static InteractionServiceConfig CreateConfig() => new()
    {
        // We run each interaction on the thread pool ourselves (inside a DI scope), so Sync is correct here and
        // the gateway thread is never blocked.
        DefaultRunMode = RunMode.Sync,
        AutoServiceScopes = false,
        UseCompiledLambda = false,
        ThrowOnError = false,
        LogLevel = LogSeverity.Info,
        EnableAutocompleteHandlers = true,
    };

    public async Task<CommandManifest> InitializeAsync(IServiceProvider services)
    {
        await _initLock.WaitAsync();
        try
        {
            if (Manifest is not null)
                return Manifest;

            var errors = new List<string>();
            // Module construction may touch scoped services; never resolve them from the root provider.
            await using var scope = services.CreateAsyncScope();
            foreach (var module in registry.All)
            {
                foreach (var type in module.InteractionModuleTypes)
                {
                    try
                    {
                        await service.AddModuleAsync(type, scope.ServiceProvider);
                    }
                    catch (Exception ex)
                    {
                        // A module that fails to load must never cause commands to be deleted on sync.
                        errors.Add($"{module.Descriptor.Id}/{type.Name}: {ex.GetType().Name}: {ex.Message}");
                        logger.LogError(ex, "Failed to load interaction module {Type}", type.FullName);
                    }
                }
            }

            Manifest = CommandManifestBuilder.Build(service, registry, localizer, errors);
            var problems = CommandManifestValidator.Validate(Manifest, AdminCommandNames);
            if (problems.Count > 0)
                logger.LogError("Command manifest has {Count} problem(s): {Problems}", problems.Count, string.Join(" | ", problems));
            else
                logger.LogInformation("Command manifest OK: {Count} top-level commands, hash {Hash}", Manifest.Commands.Count, Manifest.Hash[..12]);
            return Manifest;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _initLock.Dispose();
        service.Dispose();
    }
}
