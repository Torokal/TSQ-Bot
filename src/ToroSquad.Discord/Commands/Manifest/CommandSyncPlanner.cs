namespace ToroSquad.Discord.Commands.Manifest;

/// <summary>Where commands would be registered. Global registration is a separate approval gate.</summary>
public abstract record SyncScope
{
    public sealed record Guild(ulong GuildId) : SyncScope
    {
        public override string Key => "guild:" + GuildId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public sealed record Global : SyncScope
    {
        public override string Key => "global";
    }

    public abstract string Key { get; }
}

public sealed record RemoteCommand(ulong Id, string Name, string CanonicalJson);

public enum SyncAction
{
    Create = 0,
    Update = 1,
    Unchanged = 2,
    KeepUnmanaged = 3,
    KeepManagedNotInManifest = 4,
    DeleteManaged = 5,
}

/// <param name="Detail">For updates: where the local definition first differs from what Discord returned.</param>
public sealed record SyncPlanItem(SyncAction Action, string Name, ulong? RemoteId, string? Detail = null);

public sealed record SyncRequest(
    SyncScope Scope,
    ulong ExpectedApplicationId,
    ulong ActualApplicationId,
    IReadOnlySet<ulong> AllowedGuildIds,
    bool AllowGlobal,
    bool Prune,
    IReadOnlySet<string> AdminCommandNames);

public sealed record SyncPlan(IReadOnlyList<SyncPlanItem> Items, IReadOnlyList<string> BlockingErrors)
{
    public bool IsBlocked => BlockingErrors.Count > 0;
    public bool HasChanges => Items.Any(i => i.Action is SyncAction.Create or SyncAction.Update or SyncAction.DeleteManaged);
}

/// <summary>
/// Pure diff between the local manifest and what Discord currently has. Never a blind bulk overwrite:
/// <list type="bullet">
/// <item>An invalid, empty or partially loaded manifest blocks the sync (nothing is deleted).</item>
/// <item>The token's application must match the configured one; guild must be explicitly allow-listed.</item>
/// <item>Remote commands we did not create are kept. Commands we created but no longer define are only deleted
/// with explicit <c>Prune</c>.</item>
/// </list>
/// </summary>
public static class CommandSyncPlanner
{
    public static SyncPlan Plan(
        CommandManifest manifest,
        IReadOnlyList<RemoteCommand> remote,
        IReadOnlyDictionary<string, ulong> managed,
        SyncRequest request)
    {
        var blocking = new List<string>();
        blocking.AddRange(CommandManifestValidator.Validate(manifest, request.AdminCommandNames));

        if (request.ExpectedApplicationId == 0)
            blocking.Add("Discord:ApplicationId is not configured");
        else if (request.ActualApplicationId != request.ExpectedApplicationId)
            blocking.Add($"token belongs to application {request.ActualApplicationId}, expected {request.ExpectedApplicationId}");

        switch (request.Scope)
        {
            case SyncScope.Guild g when !request.AllowedGuildIds.Contains(g.GuildId):
                blocking.Add($"guild {g.GuildId} is not in Discord:CommandSyncGuildIds (explicit allow-list)");
                break;
            case SyncScope.Global when !request.AllowGlobal:
                blocking.Add("global registration requires Discord:AllowGlobalCommandSync=true (separate approval gate)");
                break;
        }

        if (blocking.Count > 0)
            return new SyncPlan([], blocking);

        var includeGlobalFields = request.Scope is SyncScope.Global;
        var items = new List<SyncPlanItem>();
        var remoteByName = remote.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());

        foreach (var command in manifest.Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (!remoteByName.TryGetValue(command.Name, out var existing))
            {
                items.Add(new(SyncAction.Create, command.Name, null));
                continue;
            }

            var local = CommandManifest.CanonicalJson(command, includeGlobalFields);
            items.Add(local == existing.CanonicalJson
                ? new(SyncAction.Unchanged, command.Name, existing.Id)
                : new(SyncAction.Update, command.Name, existing.Id, DescribeDifference(local, existing.CanonicalJson)));
        }

        foreach (var extra in remote.Where(r => manifest.Find(r.Name) is null).OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            // Managed = created by this tool AND still the same command id (a same-named command re-created by
            // someone else is not ours).
            if (!managed.TryGetValue(extra.Name, out var managedId) || managedId != extra.Id)
                items.Add(new(SyncAction.KeepUnmanaged, extra.Name, extra.Id));
            else
                items.Add(new(request.Prune ? SyncAction.DeleteManaged : SyncAction.KeepManagedNotInManifest, extra.Name, extra.Id));
        }

        return new SyncPlan(items, []);
    }

    /// <summary>Shows a short window around the first differing character of the two canonical JSON documents.</summary>
    public static string DescribeDifference(string local, string remote)
    {
        var i = 0;
        while (i < local.Length && i < remote.Length && local[i] == remote[i])
            i++;
        const int Before = 60, After = 60;
        var start = Math.Max(0, i - Before);
        string Window(string s) => s.Length <= start ? "<end>" : s.Substring(start, Math.Min(Before + After, s.Length - start));
        return $"at char {i}: local …{Window(local)}… | discord …{Window(remote)}…";
    }
}
