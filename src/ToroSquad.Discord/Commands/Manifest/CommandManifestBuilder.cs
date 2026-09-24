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
        foreach (var module in service.Modules.Where(m => m.Parent is null))
            Visit(module, registry, localizer, commands, errors, inheritedPermissions: null);
        return new CommandManifest(commands, errors);
    }

    private static void Visit(ModuleInfo module, ModuleRegistry registry, ILocalizer localizer, List<ManifestCommand> commands, List<string> errors, GuildPermission? inheritedPermissions)
    {
        var owner = OwnerOf(module, registry);
        var permissions = Or(inheritedPermissions, module.DefaultMemberPermissions);

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
            p.MinValue,
            p.MaxValue,
            p.MinLength,
            p.MaxLength,
            p.ChannelTypes.Select(t => (int)t).ToList());
    }

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
