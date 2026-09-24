using Discord;
using Discord.Interactions;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;
using DiscordPermission = Discord.GuildPermission;

namespace ToroSquad.Discord.Commands.Core;

/// <summary>Autocomplete for module ids. Answers from the in-memory registry — never slow, never deferred.</summary>
public sealed class ModuleAutocomplete(ModuleRegistry registry) : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        var typed = (autocompleteInteraction.Data.Current.Value as string ?? "").Trim().ToLowerInvariant();
        var results = registry.Optional
            .Where(m => m.Descriptor.Id.Value.Contains(typed, StringComparison.Ordinal))
            .Take(DiscordLimits.AutocompleteChoicesMax)
            .Select(m => new AutocompleteResult(m.Descriptor.Id.Value, m.Descriptor.Id.Value));
        return Task.FromResult(AutocompletionResult.FromSuccess(results));
    }
}

/// <summary>/modules list|enable|disable — Manage Server required (hidden via default_member_permissions AND re-checked).</summary>
[ToroModule("core")]
[Group("modules", "Manage which ToroSquad modules are active in this server")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class ModulesCommands(InteractionServices services, ModuleManagementService modules) : ToroInteractionModule(services)
{
    [SlashCommand("list", "Show modules and whether they are enabled here")]
    public async Task ListAsync()
    {
        var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
        {
            await ReplyResultAsync(OperationResult.Forbidden(auth));
            return;
        }

        await DeferEphemeralAsync();
        var language = await LangAsync();
        var lines = new List<string>();
        foreach (var s in await modules.ListAsync(Actor.GuildId, CancellationToken.None))
        {
            var state = Localizer.Get(language, s.Enabled ? "modules.state_on" : "modules.state_off");
            var core = s.Descriptor.IsCore ? " " + Localizer.Get(language, "modules.core_tag") : "";
            lines.Add($"**{Localizer.Get(language, s.Descriptor.NameKey)}** (`{s.Descriptor.Id}` v{s.Descriptor.Version}){core} — {state}\n{Localizer.Get(language, s.Descriptor.DescriptionKey)}");
        }

        await ReplyEmbedAsync(new MessageEmbed(await T("modules.title"), string.Join("\n\n", lines), null, [], await T("modules.disable_keeps_data"), null, NeutralColor));
    }

    [SlashCommand("enable", "Enable a module in this server")]
    public async Task EnableAsync([Summary("module", "Module id"), Autocomplete(typeof(ModuleAutocomplete))] string module)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await modules.SetEnabledAsync(Actor, module, true, CancellationToken.None));
    }

    [SlashCommand("disable", "Stop a module in this server (its data is kept)")]
    public async Task DisableAsync([Summary("module", "Module id"), Autocomplete(typeof(ModuleAutocomplete))] string module)
    {
        await DeferEphemeralAsync();
        await ReplyResultAsync(await modules.SetEnabledAsync(Actor, module, false, CancellationToken.None));
    }
}

/// <summary>
/// A module's own step inside /setup (e.g. esports: channel, filters, ping-free preview, explicit enable).
/// Keeps the core wizard independent of any specific module.
/// </summary>
public interface IModuleSetupFlow
{
    ModuleId Module { get; }
    Task<(MessageEmbed Embed, MessageComponent Components)> RenderAsync(ActorContext actor, string language, CancellationToken cancellationToken);
}

/// <summary>
/// /setup: language → time zone → module selection → module steps. Every component re-checks that the clicker is the
/// user who opened the wizard AND still has Manage Server.
/// </summary>
[ToroModule("core")]
[DefaultMemberPermissions(DiscordPermission.ManageGuild)]
[CommandContextType(InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.GuildInstall)]
public sealed class SetupCommands(
    InteractionServices services,
    GuildSettingsService settingsService,
    ModuleRegistry registry,
    IEnumerable<IModuleSetupFlow> flows) : ToroInteractionModule(services)
{
    public const string LangId = "tsq:setup:lang:";
    public const string TimeZoneId = "tsq:setup:tz:";
    public const string ModuleStepId = "tsq:setup:module:";
    public const string DoneId = "tsq:setup:done:";

    /// <summary>Offered zones (IANA ids). Europe/Istanbul is the default; users may still pick another.</summary>
    public static readonly IReadOnlyList<string> OfferedTimeZones =
    [
        "Europe/Istanbul", "UTC", "Europe/London", "Europe/Berlin", "Europe/Moscow",
        "Asia/Dubai", "Asia/Tokyo", "America/New_York", "America/Sao_Paulo", "Australia/Sydney",
    ];

    [SlashCommand("setup", "Step-by-step server setup for ToroSquad Bot")]
    public async Task SetupAsync()
    {
        var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
        if (!auth.IsAllowed)
        {
            await ReplyResultAsync(OperationResult.Forbidden(auth));
            return;
        }

        await DeferEphemeralAsync();
        await RenderWizardAsync();
    }

    [ComponentInteraction(LangId + "*", ignoreGroupNames: true)]
    public async Task LanguageAsync(string owner, string[] values)
    {
        if (!await CheckOwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        await ReplyResultAsync(await settingsService.UpdateAsync(Actor, values.FirstOrDefault(), null, CancellationToken.None));
    }

    [ComponentInteraction(TimeZoneId + "*", ignoreGroupNames: true)]
    public async Task TimeZoneAsync(string owner, string[] values)
    {
        if (!await CheckOwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        await ReplyResultAsync(await settingsService.UpdateAsync(Actor, null, values.FirstOrDefault(), CancellationToken.None));
    }

    [ComponentInteraction(ModuleStepId + "*:*", ignoreGroupNames: true)]
    public async Task ModuleStepAsync(string owner, string moduleId)
    {
        if (!await CheckOwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        var flow = flows.FirstOrDefault(f => f.Module.Value == moduleId);
        if (flow is null)
        {
            await ReplyTextAsync("setup.no_module_steps");
            return;
        }

        var (embed, components) = await flow.RenderAsync(Actor, await LangAsync(), CancellationToken.None);
        await ReplyEmbedAsync(embed, components);
    }

    [ComponentInteraction(DoneId + "*", ignoreGroupNames: true)]
    public async Task DoneAsync(string owner)
    {
        if (!await CheckOwnerAsync(owner))
            return;
        await DeferEphemeralAsync();
        await settingsService.MarkSetupCompletedAsync(Actor, CancellationToken.None);
        await ReplyTextAsync("setup.completed");
    }

    private async Task<bool> CheckOwnerAsync(string owner)
    {
        // The wizard belongs to whoever opened it; admins re-verified on every click (permissions may have changed).
        var auth = Authorize.Require(Actor, Actor.GuildId, Authorize.ServerSettings);
        if (owner != Actor.UserId.ToString())
        {
            await ReplyTextAsync("error.not_owner");
            return false;
        }

        if (!auth.IsAllowed)
        {
            await ReplyResultAsync(OperationResult.Forbidden(auth));
            return false;
        }

        return true;
    }

    private async Task RenderWizardAsync()
    {
        var settings = await SettingsAsync();
        var owner = Actor.UserId.ToString();
        var language = settings.Language;

        var languageMenu = new SelectMenuBuilder()
            .WithCustomId(LangId + owner)
            .WithPlaceholder(Localizer.Get(language, "setup.language_placeholder"))
            .AddOption("Türkçe", Languages.Turkish, isDefault: language == Languages.Turkish)
            .AddOption("English", Languages.English, isDefault: language == Languages.English);

        var tzMenu = new SelectMenuBuilder()
            .WithCustomId(TimeZoneId + owner)
            .WithPlaceholder(Localizer.Get(language, "setup.timezone_placeholder"));
        foreach (var tz in OfferedTimeZones)
            tzMenu.AddOption(tz, tz, isDefault: tz == settings.TimeZoneId);

        var components = new ComponentBuilder()
            .WithSelectMenu(languageMenu, row: 0)
            .WithSelectMenu(tzMenu, row: 1);
        foreach (var module in registry.Optional)
            components.WithButton(Localizer.Get(language, "setup.configure_module", Localizer.Get(language, module.Descriptor.NameKey)), ModuleStepId + owner + ":" + module.Descriptor.Id, ButtonStyle.Primary, row: 2);
        components.WithButton(Localizer.Get(language, "setup.finish"), DoneId + owner, ButtonStyle.Success, row: 3);

        var description = Localizer.Get(language, "setup.intro",
            settings.Language, settings.TimeZoneId,
            GuildTime.TryResolve(settings.TimeZoneId, out var zone) ? GuildTime.ToGuildLocal(Services.Clock.GetUtcNow(), zone).ToString("yyyy-MM-dd HH:mm", Languages.Culture(language)) : "?");
        await ReplyEmbedAsync(new MessageEmbed(Localizer.Get(language, "setup.title"), description, null, [], null, null, BrandColor), components.Build());
    }
}
