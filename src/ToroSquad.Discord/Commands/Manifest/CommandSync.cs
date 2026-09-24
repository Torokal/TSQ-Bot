using System.Globalization;
using Discord;
using Discord.Rest;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;

namespace ToroSquad.Discord.Commands.Manifest;

/// <summary>Remote command store boundary (Discord REST in production, in-memory in tests).</summary>
public interface ICommandRegistrar
{
    Task<ulong> GetApplicationIdAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteCommand>> GetCommandsAsync(SyncScope scope, CancellationToken cancellationToken);

    /// <summary>Create or update by name (Discord upserts on same name).</summary>
    Task<ulong> UpsertAsync(SyncScope scope, ManifestCommand command, CancellationToken cancellationToken);

    Task DeleteAsync(SyncScope scope, ulong commandId, CancellationToken cancellationToken);
}

public sealed record SyncReport(SyncPlan Plan, bool Applied, IReadOnlyList<string> Performed, IReadOnlyList<string> Failures);

/// <summary>Plans (always) and applies (only when asked) a command sync. See <see cref="CommandSyncPlanner"/>.</summary>
public sealed class CommandSyncService(IManagedCommandStore managedStore, ILogger<CommandSyncService> logger)
{
    public async Task<SyncReport> RunAsync(
        CommandManifest manifest,
        ICommandRegistrar registrar,
        SyncRequest requestTemplate,
        bool apply,
        CancellationToken cancellationToken)
    {
        var actualAppId = await registrar.GetApplicationIdAsync(cancellationToken);
        var request = requestTemplate with { ActualApplicationId = actualAppId };

        // Validate before touching remote state at all.
        var preflight = CommandSyncPlanner.Plan(manifest, [], new HashSet<string>(), request);
        if (preflight.IsBlocked)
            return new SyncReport(preflight, false, [], []);

        var remote = await registrar.GetCommandsAsync(request.Scope, cancellationToken);
        var managed = await managedStore.GetAsync(request.ExpectedApplicationId, request.Scope.Key, cancellationToken);
        var plan = CommandSyncPlanner.Plan(manifest, remote, managed.Keys.ToHashSet(StringComparer.Ordinal), request);
        if (!apply || plan.IsBlocked)
            return new SyncReport(plan, false, [], []);

        var performed = new List<string>();
        var failures = new List<string>();
        foreach (var item in plan.Items)
        {
            try
            {
                switch (item.Action)
                {
                    case SyncAction.Create or SyncAction.Update:
                        var command = manifest.Find(item.Name)!;
                        var id = await registrar.UpsertAsync(request.Scope, command, cancellationToken);
                        await managedStore.UpsertAsync(request.ExpectedApplicationId, request.Scope.Key, item.Name, id,
                            CommandManifest.Sha256(CommandManifest.CanonicalJson(command, true)), cancellationToken);
                        performed.Add($"{item.Action} /{item.Name}");
                        break;
                    case SyncAction.Unchanged:
                        // Adopt existing identical commands as managed (same name, same payload, our application).
                        await managedStore.UpsertAsync(request.ExpectedApplicationId, request.Scope.Key, item.Name, item.RemoteId!.Value,
                            CommandManifest.Sha256(CommandManifest.CanonicalJson(manifest.Find(item.Name)!, true)), cancellationToken);
                        break;
                    case SyncAction.DeleteManaged:
                        await registrar.DeleteAsync(request.Scope, item.RemoteId!.Value, cancellationToken);
                        await managedStore.RemoveAsync(request.ExpectedApplicationId, request.Scope.Key, item.Name, cancellationToken);
                        performed.Add($"Delete /{item.Name}");
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"{item.Action} /{item.Name}: {ex.GetType().Name}: {ex.Message}");
                logger.LogError(ex, "Command sync step failed for /{Name}", item.Name);
            }
        }

        return new SyncReport(plan, true, performed, failures);
    }
}

/// <summary>Discord REST implementation. Only CHAT_INPUT commands are compared; other types are always kept.</summary>
public sealed class DiscordCommandRegistrar(DiscordRestClient client) : ICommandRegistrar
{
    public async Task<ulong> GetApplicationIdAsync(CancellationToken cancellationToken) =>
        (await client.GetApplicationInfoAsync(new RequestOptions { CancelToken = cancellationToken })).Id;

    public async Task<IReadOnlyList<RemoteCommand>> GetCommandsAsync(SyncScope scope, CancellationToken cancellationToken)
    {
        var options = new RequestOptions { CancelToken = cancellationToken };
        IReadOnlyCollection<IApplicationCommand> commands = scope switch
        {
            SyncScope.Guild g => await client.GetGuildApplicationCommands(g.GuildId, withLocalizations: true, options: options),
            _ => await client.GetGlobalApplicationCommands(withLocalizations: true, options: options),
        };
        var includeGlobal = scope is SyncScope.Global;
        return commands
            .Where(c => c.Type == ApplicationCommandType.Slash)
            .Select(c => new RemoteCommand(c.Id, c.Name, CommandManifest.CanonicalJson(FromRemote(c), includeGlobal)))
            .ToList();
    }

    public async Task<ulong> UpsertAsync(SyncScope scope, ManifestCommand command, CancellationToken cancellationToken)
    {
        var properties = ToProperties(command);
        var options = new RequestOptions { CancelToken = cancellationToken };
        return scope switch
        {
            SyncScope.Guild g => (await client.CreateGuildCommand(properties, g.GuildId, options)).Id,
            _ => (await client.CreateGlobalCommand(properties, options)).Id,
        };
    }

    public async Task DeleteAsync(SyncScope scope, ulong commandId, CancellationToken cancellationToken)
    {
        var options = new RequestOptions { CancelToken = cancellationToken };
        IReadOnlyCollection<IApplicationCommand> commands = scope switch
        {
            SyncScope.Guild g => await client.GetGuildApplicationCommands(g.GuildId, options: options),
            _ => await client.GetGlobalApplicationCommands(options: options),
        };
        var target = commands.FirstOrDefault(c => c.Id == commandId);
        if (target is not null)
            await target.DeleteAsync(options);
    }

    public static SlashCommandProperties ToProperties(ManifestCommand command)
    {
        var builder = new SlashCommandBuilder()
            .WithName(command.Name)
            .WithDescription(command.Description)
            .WithNsfw(command.Nsfw)
            .WithDefaultMemberPermissions(command.DefaultMemberPermissions is null
                ? null
                : (GuildPermission)ulong.Parse(command.DefaultMemberPermissions, CultureInfo.InvariantCulture))
            .WithContextTypes(command.Contexts.Select(c => (InteractionContextType)c).ToArray())
            .WithIntegrationTypes(command.IntegrationTypes.Select(i => (ApplicationIntegrationType)i).ToArray());
        if (command.DescriptionLocalizations.Count > 0)
            builder.WithDescriptionLocalizations(command.DescriptionLocalizations.ToDictionary());
        foreach (var option in command.Options)
            builder.AddOption(ToOption(option));
        return builder.Build();
    }

    private static SlashCommandOptionBuilder ToOption(ManifestOption option)
    {
        var b = new SlashCommandOptionBuilder()
            .WithName(option.Name)
            .WithDescription(option.Description)
            .WithType((ApplicationCommandOptionType)(int)option.Type);
        if (option.DescriptionLocalizations.Count > 0)
            b.WithDescriptionLocalizations(option.DescriptionLocalizations.ToDictionary());
        if (option.Type is not (OptionType.SubCommand or OptionType.SubCommandGroup))
            b.WithRequired(option.Required);
        if (option.Autocomplete)
            b.WithAutocomplete(true);
        if (option.MinValue is { } min) b.WithMinValue(min);
        if (option.MaxValue is { } max) b.WithMaxValue(max);
        if (option.MinLength is { } minL) b.WithMinLength(minL);
        if (option.MaxLength is { } maxL) b.WithMaxLength(maxL);
        foreach (var channelType in option.ChannelTypes)
            b.AddChannelType((ChannelType)channelType);
        foreach (var choice in option.Choices)
        {
            var loc = choice.NameLocalizations.Count > 0 ? choice.NameLocalizations.ToDictionary() : null;
            if (option.Type == OptionType.Integer)
                b.AddChoice(choice.Name, long.Parse(choice.Value, CultureInfo.InvariantCulture), loc);
            else if (option.Type == OptionType.Number)
                b.AddChoice(choice.Name, double.Parse(choice.Value, CultureInfo.InvariantCulture), loc);
            else
                b.AddChoice(choice.Name, choice.Value, loc);
        }

        foreach (var child in option.Options)
            b.AddOption(ToOption(child));
        return b;
    }

    /// <summary>Maps what Discord returns into the same model, so the canonical JSON can be diffed.</summary>
    public static ManifestCommand FromRemote(IApplicationCommand c)
    {
        var raw = c.DefaultMemberPermissions.RawValue;
        return new ManifestCommand(
            c.Name,
            c.Description,
            c.DescriptionLocalizations ?? new Dictionary<string, string>(),
            c.Options.Select(FromRemote).ToList(),
            // Discord.Net maps a null default_member_permissions to 0; we never register "0", so 0 means "none".
            raw == 0 ? null : raw.ToString(CultureInfo.InvariantCulture),
            c.ContextTypes?.Select(t => (int)t).Order().ToArray() ?? [],
            c.IntegrationTypes?.Select(t => (int)t).Order().ToArray() ?? [],
            c.IsNsfw,
            "remote");
    }

    private static ManifestOption FromRemote(IApplicationCommandOption o) => new(
        (OptionType)(int)o.Type,
        o.Name,
        o.Description,
        o.DescriptionLocalizations ?? new Dictionary<string, string>(),
        o.IsRequired ?? false,
        (o.Choices ?? []).Select(ch => new ManifestChoice(ch.Name, Convert.ToString(ch.Value, CultureInfo.InvariantCulture) ?? "",
            ch.NameLocalizations ?? new Dictionary<string, string>())).ToList(),
        (o.Options ?? []).Select(FromRemote).ToList(),
        o.IsAutocomplete ?? false,
        o.MinValue,
        o.MaxValue,
        o.MinLength,
        o.MaxLength,
        (o.ChannelTypes ?? []).Select(t => (int)t).ToList());
}
