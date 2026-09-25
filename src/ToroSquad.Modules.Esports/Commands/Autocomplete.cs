using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Esports.Application;
using ToroSquad.Modules.Esports.Domain;
using ToroSquad.Modules.Esports.Persistence;

namespace ToroSquad.Modules.Esports.Commands;

/// <summary>
/// Autocomplete must answer quickly and cannot be deferred: handlers read in-memory caches or a single small SQLite query;
/// team pickers may add ONE time-boxed, cached provider catalog search (<see cref="TeamDirectory"/>). Outside a guild or
/// with the module disabled they return nothing.
/// </summary>
public abstract class EsportsAutocompleteBase : AutocompleteHandler
{
    public sealed override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (context.Guild is null)
            return AutocompletionResult.FromSuccess();
        var guild = new GuildId(context.Guild.Id);
        var gate = services.GetRequiredService<IModuleGate>();
        if (!await gate.IsEnabledAsync(guild, EsportsModule.ModuleIdTyped, CancellationToken.None) && !AllowWhenDisabled)
            return AutocompletionResult.FromSuccess();
        var typed = TeamRankingResolver.Fold(autocompleteInteraction.Data.Current.Value?.ToString() ?? "");
        var results = await SuggestAsync(guild, new UserId(context.User.Id), typed, services);
        return AutocompletionResult.FromSuccess(results
            .Take(DiscordLimits.AutocompleteChoicesMax)
            .Select(r => new AutocompleteResult(DiscordText.UntrustedPlain(r.Name, 100), r.Value)));
    }

    protected virtual bool AllowWhenDisabled => false;

    protected abstract Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services);

    internal static bool Matches(string typed, params string?[] candidates) =>
        typed.Length == 0 || candidates.Any(c => c is not null && TeamRankingResolver.Fold(c).Contains(typed, StringComparison.Ordinal));
}

public sealed class TeamAutocomplete : EsportsAutocompleteBase
{
    protected override Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services) =>
        Suggest(typed, services);

    /// <summary>Known teams, then (3+ characters) the provider's team catalog — so teams without a match in the window can be picked.</summary>
    internal static async Task<IEnumerable<(string Name, object Value)>> Suggest(string typed, IServiceProvider services) =>
        (await services.GetRequiredService<TeamDirectory>().SuggestAsync(typed, CancellationToken.None))
            .Where(s => s.Key.Length <= 100)
            .Select(s => (s.Display, (object)s.Key));
}

/// <summary>Admin variant (works while the module is still disabled, e.g. configuring filters before enabling).</summary>
public sealed class AdminTeamAutocomplete : EsportsAutocompleteBase
{
    protected override bool AllowWhenDisabled => true;

    protected override Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services) =>
        TeamAutocomplete.Suggest(typed, services);
}

/// <summary>Teams to follow, plus "all matches" when the server offers an all-matches self-service role.</summary>
public sealed class FollowAutocomplete : EsportsAutocompleteBase
{
    protected override async Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services)
    {
        var db = services.GetRequiredService<ToroDbContext>();
        var localizer = services.GetRequiredService<ILocalizer>();
        var language = (await services.GetRequiredService<IGuildSettingsStore>().GetAsync(guild, CancellationToken.None)).Language;
        var list = new List<(string, object)>();
        if (await db.Set<RoleMappingEntity>().AnyAsync(m => m.GuildId == guild.Value && m.SelfService && m.TeamKey == ""))
            list.Add((localizer.Get(language, "esports.all_matches"), SubscriptionService.AllMatchesKey));
        list.AddRange(await TeamAutocomplete.Suggest(typed, services));
        return list;
    }
}

public sealed class MyFollowsAutocomplete : EsportsAutocompleteBase
{
    protected override async Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services)
    {
        var db = services.GetRequiredService<ToroDbContext>();
        var rows = await db.Set<TeamFollowEntity>().AsNoTracking()
            .Where(f => f.GuildId == guild.Value && f.UserId == user.Value).OrderBy(f => f.TeamName).ToListAsync();
        return rows.Where(r => Matches(typed, r.TeamName)).Select(r => (r.TeamName, (object)r.TeamKey));
    }
}

public sealed class TournamentAutocomplete : EsportsAutocompleteBase
{
    protected override bool AllowWhenDisabled => true;

    protected override Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services)
    {
        var cache = services.GetRequiredService<EsportsCache>();
        var fromEvents = (cache.Events.Data ?? []).Select(e => e.Tournament);
        var fromMatches = (cache.Matches.Data ?? []).Select(m => m.Tournament);
        return Task.FromResult(fromEvents.Concat(fromMatches)
            .GroupBy(t => t.ParentKey ?? t.Key)
            .Select(g => g.First())
            .Where(t => Matches(typed, t.Name) && (t.ParentKey ?? t.Key).Length <= 100)
            .Select(t => (t.Name, (object)(t.ParentKey ?? t.Key))));
    }
}

public sealed class MappingAutocomplete : EsportsAutocompleteBase
{
    protected override bool AllowWhenDisabled => true;

    protected override async Task<IEnumerable<(string Name, object Value)>> SuggestAsync(GuildId guild, UserId user, string typed, IServiceProvider services)
    {
        var db = services.GetRequiredService<ToroDbContext>();
        var rows = await db.Set<RoleMappingEntity>().AsNoTracking().Where(m => m.GuildId == guild.Value).OrderBy(m => m.Id).ToListAsync();
        var roleNames = new Dictionary<ulong, string>();
        var gateway = services.GetRequiredService<ToroSquad.Core.Roles.IGuildGateway>();
        var snapshot = await gateway.GetRoleSnapshotAsync(guild, CancellationToken.None);
        foreach (var role in snapshot?.Roles ?? [])
            roleNames[role.Id.Value] = role.Name;
        return rows.Select(m =>
        {
            var role = roleNames.GetValueOrDefault(m.RoleId, m.RoleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var scope = m.TeamKey.Length == 0 ? "*" : m.TeamName ?? m.TeamKey;
            return ($"#{m.Id} @{role} → {scope}{(m.SelfService ? " [self-service]" : "")}", (object)m.Id);
        }).Where(r => Matches(typed, r.Item1));
    }
}
