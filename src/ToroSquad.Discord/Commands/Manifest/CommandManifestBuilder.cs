using System.Globalization;
using Discord;
using Discord.Interactions;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Modules;
using ToroSquad.Discord.Interactions;

namespace ToroSquad.Discord.Commands.Manifest;

/// <summary>Localization keys for command metadata. Base (default) text is English in the attributes; "tr" comes from the catalogs.</summary>
public static class CommandLocalizationKeys
{
    public static string Description(string commandPath) => $"cmd.{commandPath}";
    public static string Option(string commandPath, string option) => $"cmd.{commandPath}.{option}";
    public static string Choice(string commandPath, string option, string value) => $"cmd.{commandPath}.{option}.{value}";
}

/// <summary>
/// Builds the manifest offline from Discord.Net's public module metadata (InteractionService.AddModuleAsync does not
/// need a login). Discord.Net's own converter is internal, so we rebuild the payload here and test it.
/// </summary>
public static class CommandManifestBuilder
{
    public static CommandManifest Build(InteractionService service, ModuleRegistry registry, ILocalizer localizer, IReadOnlyList<string> loadErrors)
    {
        var commands = new List<ManifestCommand>();
        var errors = new List<string>(loadErrors);
        var adminRoot = new List<ModuleInfo>();
        foreach (var module in service.Modules.Where(m => m.Parent is null))
        {
            // Every module contributes its own top-level class to /tsq-admin; they become ONE command below.
            if (module.IsSlashGroup && module.SlashGroupName == TsqAdminRoot.Name)
                adminRoot.Add(module);
            else
                Visit(module, registry, localizer, commands, errors, inheritedPermissions: null);
        }

        if (adminRoot.Count > 0)
            commands.Add(SharedAdminRoot(adminRoot, registry, localizer, errors));
        return new CommandManifest(commands, errors);
    }

    /// <summary>
    /// Merges the /tsq-admin contributors (see <see cref="TsqAdminRoot"/>) into one command: one subcommand group per module,
    /// ordered by name so the payload does not depend on module registration order. Anything that would make the merged
    /// command differ from what Discord.Net dispatches, or hide another module's operations, is a manifest error (sync blocked).
    /// </summary>
    private static ManifestCommand SharedAdminRoot(List<ModuleInfo> contributors, ModuleRegistry registry, ILocalizer localizer, List<string> errors)
    {
        const string path = TsqAdminRoot.Name;
        var first = contributors[0];
        var groups = new List<(ModuleInfo Group, string Owner)>();
        foreach (var contributor in contributors)
        {
            if (contributor.Description != first.Description || contributor.DefaultMemberPermissions != first.DefaultMemberPermissions ||
                contributor.IsNsfw != first.IsNsfw || !Contexts(contributor.ContextTypes).SequenceEqual(Contexts(first.ContextTypes)) ||
                !Integrations(contributor.IntegrationTypes).SequenceEqual(Integrations(first.IntegrationTypes)))
                errors.Add($"/{path}: {contributor.Name} declares the root differently from {first.Name} (derive from {nameof(TsqAdminRoot)})");
            if (contributor.SlashCommands.Count > 0 || contributor.ContextCommands.Count > 0)
                errors.Add($"/{path}: {contributor.Name} adds commands to the root itself; admin operations belong in its module group");

            foreach (var group in contributor.SubModules)
            {
                if (!group.IsSlashGroup)
                {
                    errors.Add($"/{path}: {contributor.Name}.{group.Name} is not a [Group]; each module contributes exactly one group");
                    continue;
                }

                var owner = OwnerOf(group, registry);
                if (owner is "unknown" or "core" || !registry.TryGet(owner, out var module))
                    errors.Add($"/{path} {group.SlashGroupName}: {group.Name} must declare the [ToroModule] of its feature module (found '{owner}')");
                else if (!module.Descriptor.AdminCommands.Contains(TsqAdminRoot.Group(group.SlashGroupName)))
                    errors.Add($"/{path} {group.SlashGroupName}: module '{owner}' does not declare '{TsqAdminRoot.Group(group.SlashGroupName)}' in its AdminCommands");
                groups.Add((group, owner));
            }
        }

        foreach (var dup in groups.GroupBy(g => g.Group.SlashGroupName).Where(g => g.Count() > 1))
            errors.Add($"/{path} {dup.Key}: group declared by {string.Join(", ", dup.Select(g => g.Owner))}; group names must be unique");

        var options = groups.OrderBy(g => g.Group.SlashGroupName, StringComparer.Ordinal).Select(g =>
        {
            var groupPath = path + "." + g.Group.SlashGroupName;
            return new ManifestOption(
                OptionType.SubCommandGroup,
                g.Group.SlashGroupName,
                g.Group.Description,
                Tr(localizer, CommandLocalizationKeys.Description(groupPath)),
                false,
                [],
                FlattenedCommands(g.Group, groupPath, errors).Select(c => SubCommand(groupPath, c, localizer)).ToList(),
                false, null, null, null, null, [], g.Owner);
        }).ToList();

        return new ManifestCommand(
            path,
            first.Description,
            Tr(localizer, CommandLocalizationKeys.Description(path)),
            options,
            Bitfield(first.DefaultMemberPermissions),
            Contexts(first.ContextTypes),
            Integrations(first.IntegrationTypes),
            first.IsNsfw,
            ModuleId.Core.Value);
    }

    /// <summary>
    /// A group's subcommands: its own, then those of nested classes without [Group] (Discord.Net leaves such classes out of the
    /// command path, which is how a former sub-group becomes "&lt;subgroup&gt;-&lt;operation&gt;" with its own dependencies).
    /// </summary>
    private static IEnumerable<SlashCommandInfo> FlattenedCommands(ModuleInfo group, string groupPath, List<string> errors)
    {
        foreach (var command in group.SlashCommands)
            yield return command;
        foreach (var nested in group.SubModules)
        {
            if (nested.IsSlashGroup)
            {
                errors.Add($"/{groupPath.Replace('.', ' ')} {nested.SlashGroupName}: Discord allows no group inside a group; name the operations '<subgroup>-<operation>' instead");
                continue;
            }

            foreach (var command in FlattenedCommands(nested, groupPath, errors))
                yield return command;
        }
    }

    private static void Visit(ModuleInfo module, ModuleRegistry registry, ILocalizer localizer, List<ManifestCommand> commands, List<string> errors, GuildPermission? inheritedPermissions)
    {
        var owner = OwnerOf(module, registry);
        var permissions = Or(inheritedPermissions, module.DefaultMemberPermissions);

        // Context-menu commands are always top-level, whatever group their class sits in. Discord.Net's own converter
        // is internal, so the payload is rebuilt here like the slash commands below.
        foreach (var context in module.ContextCommands)
        {
            commands.Add(new ManifestCommand(
                context.Name,
                "",
                new Dictionary<string, string>(),
                [],
                Bitfield(Or(permissions, context.DefaultMemberPermissions)),
                Contexts(context.ContextTypes),
                Integrations(context.IntegrationTypes),
                context.IsNsfw,
                owner,
                (CommandKind)(int)context.CommandType));
        }

        if (module.IsSlashGroup)
        {
            var path = module.SlashGroupName;
            var options = new List<ManifestOption>();
            foreach (var sub in module.SlashCommands)
                options.Add(SubCommand(path, sub, localizer));
            foreach (var group in module.SubModules)
            {
                if (!group.IsSlashGroup)
                {
                    errors.Add($"/{path}: nested non-group module {group.Name} is not supported");
                    continue;
                }

                var groupPath = path + "." + group.SlashGroupName;
                if (group.SubModules.Count > 0)
                    errors.Add($"/{groupPath}: only one level of subcommand groups is allowed");
                options.Add(new ManifestOption(
                    OptionType.SubCommandGroup,
                    group.SlashGroupName,
                    group.Description,
                    Tr(localizer, CommandLocalizationKeys.Description(groupPath)),
                    false,
                    [],
                    group.SlashCommands.Select(c => SubCommand(groupPath, c, localizer)).ToList(),
                    false, null, null, null, null, []));
            }

            commands.Add(new ManifestCommand(
                path,
                module.Description,
                Tr(localizer, CommandLocalizationKeys.Description(path)),
                options,
                Bitfield(permissions),
                Contexts(module.ContextTypes),
                Integrations(module.IntegrationTypes),
                module.IsNsfw,
                owner));
            return;
        }

        foreach (var command in module.SlashCommands)
        {
            var path = command.Name;
            commands.Add(new ManifestCommand(
                command.Name,
                command.Description,
                Tr(localizer, CommandLocalizationKeys.Description(path)),
                command.Parameters.Select(p => Parameter(path, p, localizer)).ToList(),
                Bitfield(Or(permissions, command.DefaultMemberPermissions)),
                Contexts(command.ContextTypes),
                Integrations(command.IntegrationTypes),
                command.IsNsfw,
                owner));
        }

        foreach (var sub in module.SubModules)
            Visit(sub, registry, localizer, commands, errors, permissions);
    }

    private static ManifestOption SubCommand(string parentPath, SlashCommandInfo command, ILocalizer localizer)
    {
        var path = parentPath + "." + command.Name;
        return new ManifestOption(
            OptionType.SubCommand,
            command.Name,
            command.Description,
            Tr(localizer, CommandLocalizationKeys.Description(path)),
            false,
            [],
            command.Parameters.Select(p => Parameter(path, p, localizer)).ToList(),
            false, null, null, null, null, []);
    }

    private static ManifestOption Parameter(string commandPath, SlashCommandParameterInfo p, ILocalizer localizer)
    {
        var type = (OptionType)(int)(p.DiscordOptionType ?? ApplicationCommandOptionType.String);
        var choices = p.Choices.Select(c =>
        {
            var value = Convert.ToString(c.Value, CultureInfo.InvariantCulture) ?? "";
            return new ManifestChoice(c.Name, value, Tr(localizer, CommandLocalizationKeys.Choice(commandPath, p.Name, value)));
        }).ToList();

        return new ManifestOption(
            type,
            p.Name,
            p.Description,
            Tr(localizer, CommandLocalizationKeys.Option(commandPath, p.Name)),
            p.IsRequired,
            choices,
            [],
            p.IsAutocomplete,
            IsNumeric(type) ? Bound(p.MinValue) : null,
            IsNumeric(type) ? Bound(p.MaxValue) : null,
            type == OptionType.String ? p.MinLength : null,
            type == OptionType.String ? p.MaxLength : null,
            p.ChannelTypes.Select(t => (int)t).ToList());
    }

    private static bool IsNumeric(OptionType type) => type is OptionType.Integer or OptionType.Number;

    // Discord.Net reports +/-(2^53 - 1) when no bound was declared; Discord does not store such values, so emitting
    // them would make every sync see a spurious difference (found in the first live guild sync, 2026-09-25).
    private static double? Bound(double? value) =>
        value is { } v && Math.Abs(v) < CommandManifestValidator.UnsetNumericBound ? v : null;

    private static IReadOnlyDictionary<string, string> Tr(ILocalizer localizer, string key) =>
        localizer.HasKey(Languages.Turkish, key)
            ? new Dictionary<string, string> { [CommandManifestValidator.RequiredLocale] = localizer.Get(Languages.Turkish, key) }
            : new Dictionary<string, string>();

    private static GuildPermission? Or(GuildPermission? a, GuildPermission? b) =>
        a is null && b is null ? null : (a ?? 0) | (b ?? 0);

    private static string? Bitfield(GuildPermission? permissions) =>
        permissions is null ? null : ((ulong)permissions.Value).ToString(CultureInfo.InvariantCulture);

    private static int[] Contexts(IReadOnlyCollection<InteractionContextType> types) =>
        types.Count == 0 ? [] : types.Select(t => (int)t).Order().ToArray();

    private static int[] Integrations(IReadOnlyCollection<ApplicationIntegrationType> types) =>
        types.Count == 0 ? [] : types.Select(t => (int)t).Order().ToArray();

    private static string OwnerOf(ModuleInfo module, ModuleRegistry registry)
    {
        // ToroModuleAttribute is a precondition, which Discord.Net lists under Preconditions (not Attributes).
        var attr = module.Preconditions.OfType<ToroModuleAttribute>().FirstOrDefault()
                   ?? module.Attributes.OfType<ToroModuleAttribute>().FirstOrDefault();
        if (attr is not null)
            return attr.ModuleId;
        return module.Parent is null ? "unknown" : OwnerOf(module.Parent, registry);
    }
}
