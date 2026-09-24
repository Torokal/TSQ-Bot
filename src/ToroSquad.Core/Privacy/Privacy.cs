using System.Text.Json;
using System.Text.Json.Nodes;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Security;

namespace ToroSquad.Core.Privacy;

/// <summary>Per-module view of a single user's stored data, always scoped to one guild.</summary>
public interface IUserDataContributor
{
    ModuleId Module { get; }

    Task<JsonObject> ExportAsync(GuildId guild, UserId user, CancellationToken cancellationToken);

    /// <summary>Human-readable counts ("3 team follows", "1 bot-granted role") — no side effects.</summary>
    Task<IReadOnlyList<DeletionPreviewItem>> PreviewDeletionAsync(GuildId guild, UserId user, CancellationToken cancellationToken);

    /// <summary>Deletes the user's records in this guild and reverts bot-granted roles (pre-existing roles are kept).</summary>
    Task<DeletionReport> DeleteAsync(GuildId guild, UserId user, CancellationToken cancellationToken);

    /// <summary>Bot removed from guild + retention elapsed: purge everything this module stores for that guild.</summary>
    Task<int> PurgeGuildAsync(GuildId guild, CancellationToken cancellationToken);
}

public sealed record DeletionPreviewItem(string LabelKey, int Count);

public sealed record DeletionReport(ModuleId Module, int RecordsDeleted, IReadOnlyList<string> Warnings);

public interface IConfirmationStore
{
    /// <summary>Creates a single-use confirmation bound to guild + user + action; returns an opaque id for a button custom id.</summary>
    Task<string> CreateAsync(GuildId guild, UserId user, string action, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Consumes the confirmation if it exists, is unexpired and matches guild + user + action.</summary>
    Task<bool> TryConsumeAsync(string id, GuildId guild, UserId user, string action, CancellationToken cancellationToken);
}

/// <summary>
/// /privacy export and /privacy delete. Only ever touches the *calling* user's data in the *current* guild —
/// user and guild come from the interaction, never from command options.
/// </summary>
public sealed class PrivacyService(IEnumerable<IUserDataContributor> contributors, IConfirmationStore confirmations, TimeProvider clock)
{
    public const string DeleteAction = "privacy.delete";
    public static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    public async Task<string> ExportAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        var root = new JsonObject
        {
            ["format"] = "tsq-bot-user-export/v1",
            ["product"] = ProductInfo.ProductName,
            ["generatedAtUtc"] = clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["guildId"] = actor.GuildId.ToString(),
            ["userId"] = actor.UserId.ToString(),
        };
        var modules = new JsonObject();
        foreach (var contributor in contributors.OrderBy(c => c.Module.Value, StringComparer.Ordinal))
            modules[contributor.Module.Value] = await contributor.ExportAsync(actor.GuildId, actor.UserId, cancellationToken);
        root["modules"] = modules;
        return root.ToJsonString(ExportJson);
    }

    public async Task<(IReadOnlyList<DeletionPreviewItem> Items, string ConfirmationId)> PreviewDeleteAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        var items = new List<DeletionPreviewItem>();
        foreach (var contributor in contributors)
            items.AddRange(await contributor.PreviewDeletionAsync(actor.GuildId, actor.UserId, cancellationToken));
        var id = await confirmations.CreateAsync(actor.GuildId, actor.UserId, DeleteAction, ConfirmationLifetime, cancellationToken);
        return (items, id);
    }

    public async Task<(OperationResult Result, IReadOnlyList<DeletionReport> Reports)> ConfirmDeleteAsync(ActorContext actor, string confirmationId, CancellationToken cancellationToken)
    {
        if (!await confirmations.TryConsumeAsync(confirmationId, actor.GuildId, actor.UserId, DeleteAction, cancellationToken))
            return (OperationResult.Fail(OperationError.Expired, "privacy.confirmation_invalid"), Array.Empty<DeletionReport>());

        var reports = new List<DeletionReport>();
        foreach (var contributor in contributors)
            reports.Add(await contributor.DeleteAsync(actor.GuildId, actor.UserId, cancellationToken));
        return (OperationResult.Ok("privacy.deleted", reports.Sum(r => r.RecordsDeleted)), reports);
    }
}
