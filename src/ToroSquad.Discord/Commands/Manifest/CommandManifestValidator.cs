using System.Text.RegularExpressions;

namespace ToroSquad.Discord.Commands.Manifest;

/// <summary>
/// Validates the manifest against Discord's documented limits (application-commands docs, verified 2026-09-24)
/// plus TSQ Bot policy (guild-only, Turkish descriptions, admin commands hidden by default permissions).
/// </summary>
public static partial class CommandManifestValidator
{
    public const int MaxCommands = 100;
    public const int MaxOptions = 25;
    public const int MaxChoices = 25;
    public const int MaxCommandCharacters = 8000;
    public const string RequiredLocale = "tr";

    /// <summary>Discord: InteractionContextType.GUILD = 0.</summary>
    public const int GuildContext = 0;

    /// <summary>Discord: ApplicationIntegrationType.GUILD_INSTALL = 0.</summary>
    public const int GuildInstall = 0;

    public static IReadOnlyList<string> Validate(CommandManifest manifest, IReadOnlySet<string> adminCommandNames)
    {
        var errors = new List<string>();
        errors.AddRange(manifest.LoadErrors.Select(e => "load error: " + e));

        if (manifest.Commands.Count == 0)
            errors.Add("manifest is empty");
        if (manifest.Commands.Count > MaxCommands)
            errors.Add($"too many commands ({manifest.Commands.Count} > {MaxCommands})");

        foreach (var dup in manifest.Commands.GroupBy(c => c.Name).Where(g => g.Count() > 1))
            errors.Add($"duplicate command name '{dup.Key}'");

        foreach (var command in manifest.Commands)
        {
            var path = "/" + command.Name;
            CheckName(errors, path, command.Name);
            CheckDescription(errors, path, command.Description, command.DescriptionLocalizations);

            if (!command.Contexts.SequenceEqual([GuildContext]))
                errors.Add($"{path}: must be guild-only (contexts=[0])");
            if (!command.IntegrationTypes.SequenceEqual([GuildInstall]))
                errors.Add($"{path}: must be guild-install only (integration_types=[0])");

            var isAdmin = adminCommandNames.Contains(command.Name);
            if (isAdmin && (command.DefaultMemberPermissions is null || command.DefaultMemberPermissions == ""))
                errors.Add($"{path}: admin command must set default_member_permissions");
            if (!isAdmin && command.DefaultMemberPermissions is not null)
                errors.Add($"{path}: user command must not require member permissions");

            CheckOptions(errors, path, command.Options, depth: 0);

            var chars = CountCharacters(command);
            if (chars > MaxCommandCharacters)
                errors.Add($"{path}: {chars} characters exceed {MaxCommandCharacters}");
        }

        foreach (var name in adminCommandNames.Where(n => manifest.Find(n) is null))
            errors.Add($"expected admin command '/{name}' is missing");

        return errors;
    }

    private static void CheckOptions(List<string> errors, string path, IReadOnlyList<ManifestOption> options, int depth)
    {
        if (options.Count > MaxOptions)
            errors.Add($"{path}: too many options ({options.Count})");
        foreach (var dup in options.GroupBy(o => o.Name).Where(g => g.Count() > 1))
            errors.Add($"{path}: duplicate option '{dup.Key}'");

        var hasSub = options.Any(o => o.Type is OptionType.SubCommand or OptionType.SubCommandGroup);
        if (hasSub && options.Any(o => o.Type is not (OptionType.SubCommand or OptionType.SubCommandGroup)))
            errors.Add($"{path}: cannot mix subcommands with plain options");

        var seenOptional = false;
        foreach (var option in options)
        {
            var optionPath = path + " " + option.Name;
            CheckName(errors, optionPath, option.Name);
            CheckDescription(errors, optionPath, option.Description, option.DescriptionLocalizations);

            switch (option.Type)
            {
                case OptionType.SubCommandGroup:
                    if (depth > 0)
                        errors.Add($"{optionPath}: subcommand groups can only be nested one level");
                    if (option.Options.Count == 0 || option.Options.Any(o => o.Type != OptionType.SubCommand))
                        errors.Add($"{optionPath}: group must contain only subcommands");
                    CheckOptions(errors, optionPath, option.Options, depth + 1);
                    break;
                case OptionType.SubCommand:
                    if (option.Options.Any(o => o.Type is OptionType.SubCommand or OptionType.SubCommandGroup))
                        errors.Add($"{optionPath}: subcommand cannot contain subcommands");
                    CheckOptions(errors, optionPath, option.Options, depth + 1);
                    break;
                default:
                    if (option.Required && seenOptional)
                        errors.Add($"{optionPath}: required options must come before optional ones");
                    seenOptional |= !option.Required;
                    if (option.Choices.Count > MaxChoices)
                        errors.Add($"{optionPath}: too many choices");
                    if (option.Autocomplete && option.Choices.Count > 0)
                        errors.Add($"{optionPath}: autocomplete and choices are mutually exclusive");
                    if (option.Autocomplete && option.Type is not (OptionType.String or OptionType.Integer or OptionType.Number))
                        errors.Add($"{optionPath}: autocomplete only valid for string/integer/number");
                    foreach (var choice in option.Choices)
                    {
                        if (choice.Name.Length is < 1 or > 100)
                            errors.Add($"{optionPath}: choice name length invalid");
                        if (!choice.NameLocalizations.ContainsKey(RequiredLocale))
                            errors.Add($"{optionPath}: choice '{choice.Name}' missing '{RequiredLocale}' localization");
                    }

                    if (option.MinLength is < 0 or > 6000 || option.MaxLength is < 1 or > 6000)
                        errors.Add($"{optionPath}: length bounds invalid");
                    break;
            }
        }
    }

    private static void CheckName(List<string> errors, string path, string name)
    {
        // Discord allows more (unicode letters), TSQ Bot policy keeps command/option names short, stable ASCII.
        if (!NamePattern().IsMatch(name))
            errors.Add($"{path}: invalid name '{name}' (policy: ^[a-z0-9_-]{{1,32}}$)");
    }

    private static void CheckDescription(List<string> errors, string path, string description, IReadOnlyDictionary<string, string> localizations)
    {
        if (description.Length is < 1 or > 100)
            errors.Add($"{path}: description length {description.Length} not in 1..100");
        if (!localizations.TryGetValue(RequiredLocale, out var tr))
            errors.Add($"{path}: missing '{RequiredLocale}' description localization");
        else if (tr.Length is < 1 or > 100)
            errors.Add($"{path}: '{RequiredLocale}' description length {tr.Length} not in 1..100");
        foreach (var (locale, text) in localizations)
        {
            if (text.Length is < 1 or > 100)
                errors.Add($"{path}: '{locale}' description length invalid");
        }
    }

    private static int CountCharacters(ManifestCommand command)
    {
        static int Count(IEnumerable<ManifestOption> options) => options.Sum(o =>
            o.Name.Length + o.Description.Length + o.Choices.Sum(c => c.Name.Length + c.Value.Length) + Count(o.Options));
        return command.Name.Length + command.Description.Length + Count(command.Options);
    }

    [GeneratedRegex("^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
