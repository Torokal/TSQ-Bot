using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Modules;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.Updates.Domain;
using ToroSquad.Modules.Updates.Persistence;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>
/// Section "Updates:Recovery": a one-off, explicit way to post updates a game's subscribers missed while the game's source
/// was set up wrong (a source move takes everything published before it as history, and the catch-up window is short).
/// Unset by default. <see cref="Apply"/> false (the default) only previews — in the log once after startup, and in
/// <c>islem:status</c> / <c>islem:doctor</c> — and writes nothing. Without <see cref="Items"/> a preview proposes the newest
/// update of the game's watched threads (the newest hotfix day), never the archive.
/// <para><b>Apply posts exactly the named posts, nothing else.</b> It needs <see cref="Items"/> — a fixed list of post ids,
/// at most <see cref="MaxCards"/> of them — and is refused at startup without one: nothing is guessed when cards are really
/// posted. The selection is that list, so it never changes by itself — not with what was delivered, not with a new forum
/// post, a restart, the day or the order of anything; applying again (every round while the setting stays, after any
/// restart) posts nothing twice and never more than the list. Remove the section once done.</para>
/// </summary>
public sealed class UpdatesRecoveryOptions
{
    public const string Section = "Updates:Recovery";
    public const int MaxItems = 10;
    public const int MaxCardsLimit = 3;

    /// <summary>The game whose missed updates are recovered (its key, e.g. "wow-forever").</summary>
    public string? Game { get; set; }

    /// <summary>The provider's post ids to recover ("2403040:5"); not set: a preview proposes the newest update of a watched thread.</summary>
    public string[]? Items { get; set; }

    /// <summary>
    /// The most cards this recovery may post per guild, in all (1 to <see cref="MaxCardsLimit"/>). <see cref="Items"/> may not
    /// name more: a longer list is refused at startup rather than cut or shifted.
    /// </summary>
    public int MaxCards { get; set; } = 1;

    /// <summary>
    /// False: preview only. True: the named posts that are ready are staged — once; the delivery records make a repeat a no-op.
    /// Needs <see cref="Items"/>.
    /// </summary>
    public bool Apply { get; set; }

    public bool IsSet => !string.IsNullOrWhiteSpace(Game);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!IsSet)
        {
            if (Items is { Length: > 0 } || Apply)
                errors.Add(Section + ":Items and " + Section + ":Apply need " + Section + ":Game");
            return errors;
        }

        if (!GameUpdateDefinition.IsValidKey(Game))
            errors.Add(Section + ":Game must be a game key (lowercase letters, digits, '-')");
        var items = Items ?? [];
        if (items.Length > MaxItems || items.Any(i => !IsPostId(i)) || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:Items must be at most {MaxItems} distinct post ids (letters, digits, ':', '-', '_')"));
        if (MaxCards is < 1 or > MaxCardsLimit)
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:MaxCards must be between 1 and {MaxCardsLimit}"));
        else if (items.Length > MaxCards)
            errors.Add(string.Create(CultureInfo.InvariantCulture, $"{Section}:Items names {items.Length} posts but MaxCards is {MaxCards}; name at most MaxCards posts"));
        if (Apply && items.Length == 0)
            errors.Add(Section + ":Apply needs " + Section + ":Items (the exact post ids to post); a proposal is only ever previewed");
        return errors;
    }

    private static bool IsPostId(string? id) =>
        id is { Length: > 0 and <= GameUpdateCandidate.ExternalIdMax } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '-' or '_');
}

/// <summary>Where a post named for recovery stands for one guild.</summary>
public enum RecoveryItemState
{
    /// <summary>Will be posted (on apply).</summary>
    Ready = 0,

    /// <summary>The guild already has this post's card.</summary>
    Delivered = 1,

    /// <summary>The source has not shown this post (yet): it is read on the next rounds.</summary>
    NotSeen = 2,

    /// <summary>Stored, but not classified as an update: never posted.</summary>
    NotAnUpdate = 3,

    /// <summary>Published before the guild's window for this game started (before it turned the game on): never posted.</summary>
    BeforeWindow = 4,

    /// <summary>No title, no publication time or no link of the provider's own form: never posted.</summary>
    Unusable = 5,
}

public sealed record RecoveryItem(string ExternalId, string? Title, DateTimeOffset? PublishedAt, RecoveryItemState State);

/// <param name="WindowStart">Since when this guild follows the game in the running mode: nothing older is ever recovered.</param>
/// <param name="DeliveredCount">Cards of this game the guild already has (in the running mode).</param>
public sealed record RecoveryGuildPlan(ulong GuildId, ulong ChannelId, DateTimeOffset? WindowStart, int DeliveredCount, IReadOnlyList<RecoveryItem> Items)
{
    public int Cards => Items.Count(i => i.State == RecoveryItemState.Ready);
}

/// <param name="Proposed">True: no post was named, so the plan is a proposal (the newest update of a watched thread) — previewed only, never applied.</param>
public sealed record RecoveryPlan(GameUpdateDefinition Game, bool Apply, bool Proposed, bool DryRun, IReadOnlyList<RecoveryGuildPlan> Guilds);

/// <summary>
/// The recovery plan of <see cref="UpdatesRecoveryOptions"/>: per guild that follows the game now (module on, channel,
/// not paused, game on), which named posts would be posted, to which channel — the game's own channel or the guild's
/// Updates channel, exactly as a normal card. Reads only; <see cref="UpdatesPlanner"/> stages what a plan says is ready.
/// <para>What recovery deliberately does NOT apply, and what it still does: it ignores the baseline mark, the first-seen
/// rule and the catch-up window (that is what it is for), but never posts a post the guild already has a card of (the
/// same delivery records as normal cards, so normal rounds and a repeated apply never post it again), never one published
/// before the guild's window for the game started, never one that is not classified as an update, and never more than the
/// named list (at most <see cref="UpdatesRecoveryOptions.MaxCards"/>) per guild in all. The guild, its module, mode, pause,
/// subscription and channel are those of a normal card, and the outbox's delivery policy checks the card again on sending.</para>
/// </summary>
public sealed class UpdatesRecovery(
    ToroDbContext db,
    IModuleGate gate,
    GameUpdateCatalog catalog,
    IOptions<UpdatesRecoveryOptions> recovery,
    IOptions<UpdatesOptions> options,
    IOptions<DeliveryOptions> delivery)
{
    public UpdatesRecoveryOptions Options => recovery.Value;

    /// <summary>The configured recovery's game, when it is a registered one.</summary>
    public GameUpdateDefinition? Game => recovery.Value.IsSet ? catalog.Find(recovery.Value.Game) : null;

    public bool AppliesTo(GameUpdateDefinition game) => recovery.Value.IsSet && string.Equals(recovery.Value.Game, game.Key, StringComparison.Ordinal);

    /// <summary>
    /// Threads of named posts the source has not shown yet: read like followed threads on the next round, so a post outside
    /// the newest threads can still be recovered. Empty when nothing is named or everything is stored.
    /// </summary>
    public async Task<IReadOnlyList<string>> UnseenThreadsAsync(GameUpdateDefinition game, CancellationToken ct)
    {
        if (!AppliesTo(game) || recovery.Value.Items is not { Length: > 0 } named)
            return [];
        var stored = await db.Set<UpdatesItemEntity>().AsNoTracking()
            .Where(a => a.Provider == game.Provider && a.GameKey == game.Key && named.Contains(a.ExternalId))
            .Select(a => a.ExternalId).ToListAsync(ct);
        var provider = catalog.ProviderOf(game);
        return named.Where(id => !stored.Contains(id)).Select(provider.ThreadIdOf).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The plan for every guild (or only <paramref name="onlyGuild"/>); null when no recovery is configured.</summary>
    public async Task<RecoveryPlan?> PlanAsync(ulong? onlyGuild, CancellationToken ct)
    {
        if (Game is not { } game)
            return null;
        var o = recovery.Value;
        var mode = UpdatesModes.Effective(options.Value, delivery.Value);
        var dryRun = mode == UpdatesMode.DryRun;
        var kind = UpdatesPlanner.Kind(game.Key, dryRun);
        var provider = catalog.ProviderOf(game);
        const int update = (int)UpdateClassification.Update;

        // This round's new and changed rows (tracked, not saved yet) win over their stored versions.
        var items = db.Set<UpdatesItemEntity>();
        var tracked = items.Local.Where(a => a.Provider == game.Provider && a.GameKey == game.Key).ToList();
        var named = o.Items is { Length: > 0 } list ? list.Take(UpdatesRecoveryOptions.MaxItems).ToList() : null;
        List<UpdatesItemEntity> stored;
        if (named is not null)
        {
            stored = await items.AsNoTracking().Where(a => a.Provider == game.Provider && a.GameKey == game.Key && named.Contains(a.ExternalId)).ToListAsync(ct);
        }
        else
        {
            // The proposal looks at the newest updates only: the recent ones are where a missed hotfix day is.
            stored = await items.AsNoTracking()
                .Where(a => a.Provider == game.Provider && a.GameKey == game.Key && a.Classification == update && a.Title != null && a.PublishedAt != null)
                .OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.ExternalId).Take(50).ToListAsync(ct);
        }

        var rows = stored.Where(a => tracked.All(t => t.ExternalId != a.ExternalId)).Concat(tracked)
            .GroupBy(a => a.ExternalId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        var guilds = new List<RecoveryGuildPlan>();
        var subscriptions = await db.Set<UpdatesSubscriptionEntity>().AsNoTracking()
            .Where(s => s.GameKey == game.Key && s.Enabled).OrderBy(s => s.GuildId).ToListAsync(ct);
        foreach (var subscription in subscriptions.Where(s => onlyGuild is null || s.GuildId == onlyGuild))
        {
            var config = await db.Set<UpdatesGuildConfigEntity>().AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == subscription.GuildId, ct);
            if (config?.ChannelId is null || config.Paused || !await gate.IsEnabledAsync(new GuildId(config.GuildId), UpdatesModule.ModuleIdTyped, ct))
                continue;
            DateTimeOffset? window = dryRun ? config.DryRunSince : config.LiveSince;
            if (subscription.EnabledAt is { } enabledAt && (window is null || enabledAt > window))
                window = enabledAt;
            var delivered = (await db.Set<UpdatesDeliveryEntity>().AsNoTracking()
                    .Where(d => d.GuildId == config.GuildId && d.Provider == game.Provider && d.GameKey == game.Key && d.Kind == kind)
                    .Select(d => d.ExternalId).ToListAsync(ct))
                .Concat(db.Set<UpdatesDeliveryEntity>().Local
                    .Where(d => d.GuildId == config.GuildId && d.Provider == game.Provider && d.GameKey == game.Key && d.Kind == kind).Select(d => d.ExternalId))
                .ToHashSet(StringComparer.Ordinal);

            var ids = named ?? Proposal(game, provider, rows.Values, window);
            var planned = new List<RecoveryItem>();
            foreach (var id in ids)
            {
                var row = rows.GetValueOrDefault(id);
                var state = row is null ? RecoveryItemState.NotSeen
                    : delivered.Contains(id) ? RecoveryItemState.Delivered
                    : row.Classification != update ? RecoveryItemState.NotAnUpdate
                    : row.Title is null || row.PublishedAt is null || !provider.IsCanonicalUrl(row.Url) ? RecoveryItemState.Unusable
                    : window is null || row.PublishedAt < window ? RecoveryItemState.BeforeWindow
                    : RecoveryItemState.Ready;
                planned.Add(new RecoveryItem(id, row?.Title, row?.PublishedAt, state));
            }

            guilds.Add(new RecoveryGuildPlan(config.GuildId, subscription.ChannelId ?? config.ChannelId.Value, window, delivered.Count, planned));
        }

        return new RecoveryPlan(game, o.Apply, named is null, dryRun, guilds);
    }

    /// <summary>
    /// The newest update of a watched thread, published inside the guild's window — or nothing. Whether the guild already has
    /// it does not matter here (then it is simply delivered): the proposal never walks back to an older day.
    /// </summary>
    private static List<string> Proposal(GameUpdateDefinition game, IGameUpdateProvider provider, IEnumerable<UpdatesItemEntity> rows, DateTimeOffset? window)
    {
        const int update = (int)UpdateClassification.Update;
        var newest = rows
            .Where(a => a.Classification == update && a.Title != null && a.PublishedAt is { } published && window is { } start && published >= start &&
                        provider.ThreadIdOf(a.ExternalId) is { } thread && game.WatchedThreadIds.Contains(thread, StringComparer.Ordinal))
            .OrderByDescending(a => a.PublishedAt).ThenByDescending(a => a.ExternalId, StringComparer.Ordinal)
            .FirstOrDefault();
        return newest is null ? [] : [newest.ExternalId];
    }
}
