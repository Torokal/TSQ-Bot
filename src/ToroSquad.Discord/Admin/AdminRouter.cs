using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Localization;
using ToroSquad.Core.Messaging;
using ToroSquad.Core.Security;
using ToroSquad.Discord.Interactions;

namespace ToroSquad.Discord.Admin;

/// <summary>What the flat <c>/tsq-admin</c> command received; <see cref="Provided"/> lists the shared options that were filled.</summary>
public sealed record AdminRequest(ActorContext Actor, string Language, string Module, string Operation, AdminArgs Args, AdminFields Provided);

/// <summary>
/// Routes <c>/tsq-admin modul:&lt;m&gt; islem:&lt;o&gt;</c> and its forms to exactly one registered operation. Nothing is inferred
/// from the text beyond an exact (case-insensitive) id: an unknown module, an operation of another module or an option the
/// operation does not use is refused before anything runs. The caller's permission for the operation is checked here AND
/// again by the module's service; a form re-checks its owner, guild, expiry and that permission on every interaction.
/// </summary>
public sealed class AdminRouter(AdminCatalog catalog, AdminDrafts drafts, ILocalizer localizer, ILogger<AdminRouter> logger)
{
    public async Task RunAsync(AdminRequest request, IAdminResponder respond, IServiceProvider scope)
    {
        string L(string key, params object?[] args) => localizer.Get(request.Language, key, args);

        var module = catalog.Find(Normalize(request.Module));
        if (module is null)
        {
            await respond.SendAsync(L("admin.error.unknown_module", Display(request.Module)));
            return;
        }

        var operation = module.Find(Normalize(request.Operation));
        if (operation is null)
        {
            await respond.SendAsync(L("admin.error.unknown_operation", Display(request.Operation), module.Id));
            return;
        }

        var auth = Authorize.Require(request.Actor, request.Actor.GuildId, operation.Permission);
        if (!auth.IsAllowed)
        {
            await respond.SendAsync(await respond.DescribeAsync(OperationResult.Forbidden(auth)));
            return;
        }

        var unused = request.Provided & ~operation.Accepts;
        if (unused != AdminFields.None)
        {
            await respond.SendAsync(L("admin.error.unused_fields", module.Id + " " + operation.Id, OptionNames(unused)));
            return;
        }

        logger.LogInformation("tsq-admin {Module} {Operation} guild={Guild} user={User}", module.Id, operation.Id, request.Actor.GuildId, request.Actor.UserId);
        var call = new AdminCall(request.Actor, request.Language, localizer, respond, drafts, module, operation, request.Args);
        await operation.Run(scope.GetRequiredService(module.Handler), call);
    }

    /// <summary>A button, select or modal of an admin form: <paramref name="draftId"/> and <paramref name="action"/> come from its custom id.</summary>
    public async Task FormAsync(ActorContext actor, string language, string draftId, string action, AdminInput input, IAdminResponder respond, IServiceProvider scope)
    {
        string L(string key) => localizer.Get(language, key);

        var draft = drafts.Get(draftId);
        if (draft is null)
        {
            await respond.SendAsync(L("admin.form.expired"));
            return;
        }

        if (draft.User != actor.UserId || draft.Guild != actor.GuildId)
        {
            await respond.SendAsync(L("admin.form.not_yours"));
            return;
        }

        var module = catalog.Find(draft.Module);
        var operation = module?.Find(draft.Operation);
        if (module is null || operation is null || scope.GetRequiredService(module.Handler) is not IAdminFormHandler handler)
        {
            drafts.Remove(draft.Id);
            await respond.SendAsync(L("admin.form.expired"));
            return;
        }

        var auth = Authorize.Require(actor, actor.GuildId, operation.Permission);
        if (!auth.IsAllowed)
        {
            await respond.SendAsync(await respond.DescribeAsync(OperationResult.Forbidden(auth)));
            return;
        }

        if (action == CancelAction)
        {
            drafts.Remove(draft.Id);
            await respond.UpdateAsync(L("admin.form.cancelled"), AdminCall.EmptyComponents);
            return;
        }

        await handler.OnFormAsync(new AdminCall(actor, language, localizer, respond, drafts, module, operation, AdminArgs.None, draft, input), action);
    }

    public const string CancelAction = "cancel";

    /// <summary>The ids a user can type: trimmed, lowercase (Turkish-insensitive for the dotted/dotless i).</summary>
    public static string Normalize(string? value) => (value ?? "").Trim().Replace('İ', 'i').Replace('ı', 'i').ToLowerInvariant();

    private static string Display(string? value)
    {
        var text = DiscordText.UntrustedPlain(value ?? "", 40);
        return text.Length == 0 ? "—" : text;
    }

    public static string OptionNames(AdminFields fields) => string.Join(", ", new[]
    {
        (AdminFields.Channel, "kanal"), (AdminFields.User, "uye"), (AdminFields.Role, "rol"), (AdminFields.Date, "tarih"),
    }.Where(f => fields.HasFlag(f.Item1)).Select(f => "`" + f.Item2 + "`"));

    /// <summary>A text or announcement channel of this guild (not a thread, voice or stage channel).</summary>
    public static bool IsUsableChannel(IChannel? channel, GuildId guild) =>
        channel is ITextChannel text and not IThreadChannel and not IVoiceChannel && text.GuildId == guild.Value;
}

/// <summary>Suggests the admin modules the caller can use (by id or localized name). Reads only the in-process registry.</summary>
public sealed class AdminModuleAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (ActorFactory.From(context) is not { } actor)
            return AutocompletionResult.FromSuccess([]);
        var language = await AdminSuggestions.LanguageAsync(services, actor.GuildId);
        var localizer = services.GetRequiredService<ILocalizer>();
        var typed = AdminSuggestions.Fold(autocompleteInteraction.Data.Current.Value?.ToString());
        var results = services.GetRequiredService<AdminCatalog>().Modules
            .Where(m => m.Operations.Any(o => Authorize.Require(actor, actor.GuildId, o.Permission).IsAllowed))
            .Select(m => (Label: AdminSuggestions.Label(localizer.Get(language, m.LabelKey), m.Id), m.Id))
            .Where(m => AdminSuggestions.Matches(typed, m.Label, m.Id))
            .Take(DiscordLimits.AutocompleteChoicesMax)
            .Select(m => new AutocompleteResult(m.Label, m.Id));
        return AutocompletionResult.FromSuccess(results);
    }
}

/// <summary>
/// Suggests the operations of the module currently in <c>modul</c> that the caller may run; nothing before a known module is
/// chosen. Stateless: each request reads its own <c>modul</c> value, so admins never see each other's choice.
/// </summary>
public sealed class AdminOperationAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        if (ActorFactory.From(context) is not { } actor)
            return AutocompletionResult.FromSuccess([]);
        var chosen = autocompleteInteraction.Data.Options?.FirstOrDefault(o => o.Name == "modul")?.Value?.ToString();
        if (services.GetRequiredService<AdminCatalog>().Find(AdminRouter.Normalize(chosen)) is not { } module)
            return AutocompletionResult.FromSuccess([]);
        var language = await AdminSuggestions.LanguageAsync(services, actor.GuildId);
        var localizer = services.GetRequiredService<ILocalizer>();
        var typed = AdminSuggestions.Fold(autocompleteInteraction.Data.Current.Value?.ToString());
        var results = module.Operations
            .Where(o => Authorize.Require(actor, actor.GuildId, o.Permission).IsAllowed)
            .Select(o => (Label: AdminSuggestions.Label(localizer.Get(language, module.OperationLabelKey(o)), o.Id), o.Id))
            .Where(o => AdminSuggestions.Matches(typed, o.Label, o.Id))
            .Take(DiscordLimits.AutocompleteChoicesMax)
            .Select(o => new AutocompleteResult(o.Label, o.Id));
        return AutocompletionResult.FromSuccess(results);
    }
}

public static class AdminSuggestions
{
    /// <summary>"Haberler — news": the readable name first, the id the command uses after it (at most 100 characters).</summary>
    public static string Label(string name, string id)
    {
        var suffix = " — " + id;
        return name.Length + suffix.Length <= 100 ? name + suffix : name[..(100 - suffix.Length - 1)] + "…" + suffix;
    }

    public static string Fold(string? text) => AdminRouter.Normalize(text).Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');

    public static bool Matches(string typed, params string[] candidates) =>
        typed.Length == 0 || candidates.Any(c => Fold(c).Contains(typed, StringComparison.Ordinal));

    public static async Task<string> LanguageAsync(IServiceProvider services, GuildId guild) =>
        (await services.GetRequiredService<IGuildSettingsStore>().GetAsync(guild, CancellationToken.None)).Language;
}
