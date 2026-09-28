using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Modules.Timezone.Application;

namespace ToroSquad.Modules.Timezone.Commands;

/// <summary>
/// Suggestions for /saat's <c>timezone</c> option: the six source zones, labelled in the guild's language, filtered by what
/// was typed (a name or an alias such as <c>pdt</c>). The value sent is the zone's key; any alias typed without picking a
/// suggestion works too. From memory only — never slow, never deferred; nothing while the module is disabled (as the Esports
/// autocomplete).
/// </summary>
public sealed class SourceZoneAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.Guild is null)
            return AutocompletionResult.FromSuccess();
        var guild = new GuildId(context.Guild.Id);
        if (!await services.GetRequiredService<IModuleGate>().IsEnabledAsync(guild, TimezoneModule.ModuleIdTyped, CancellationToken.None))
            return AutocompletionResult.FromSuccess();

        var lang = (await services.GetRequiredService<IGuildSettingsStore>().GetAsync(guild, CancellationToken.None)).Language;
        var localizer = services.GetRequiredService<ILocalizer>();
        return AutocompletionResult.FromSuccess(SourceTimeZones.Suggest(autocompleteInteraction.Data.Current.Value?.ToString(), z => localizer.Get(lang, z.NameKey))
            .Take(DiscordLimits.AutocompleteChoicesMax)
            .Select(z => new AutocompleteResult(localizer.Get(lang, z.NameKey), z.Key)));
    }
}
