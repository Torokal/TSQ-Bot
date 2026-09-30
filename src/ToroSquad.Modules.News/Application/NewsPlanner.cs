using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Core;
using ToroSquad.Core.Guilds;
using ToroSquad.Core.Modules;
using ToroSquad.Core.Notifications;
using ToroSquad.Infrastructure.Delivery;
using ToroSquad.Infrastructure.Persistence;
using ToroSquad.Modules.News.Domain;
using ToroSquad.Modules.News.Persistence;
using ToroSquad.Modules.News.Providers;

namespace ToroSquad.Modules.News.Application;

/// <summary>What one feed round changed (logs, doctor and tests).</summary>
public sealed record NewsRoundSummary(int Items, int NewItems, int Relevant, int Staged, int Edits, bool BaselineEstablished);

/// <summary>
/// Applies one feed result and plans the cards — everything in ONE SaveChanges (one SQLite transaction): the articles, the
/// feed validators and counters, the per-guild delivery records and the outbox rows. If that transaction fails nothing of
/// it is kept, so new validators can never hide items that were not stored.
/// <list type="bullet">
/// <item>Baseline: every item of the first successful, non-empty feed is marked baseline and never sent; a failed or empty
/// first answer establishes nothing.</item>
/// <item>Identity is the HLTV article id; one guild gets an article at most once per mode (dry-run records never count as
/// live), whatever channel it had then — a channel change never re-sends.</item>
/// <item>A guild only gets articles first seen AND published after its channel was configured / it was resumed, and published
/// within <see cref="NewsOptions.CatchUpHours"/> (outage catch-up), at most <see cref="NewsOptions.MaxCardsPerRound"/> new cards per round.</item>
/// <item>A changed headline/link of an already delivered article re-stages the same logical key: the outbox edits the same
/// message silently (identical payload = no edit).</item>
/// <item>A previously unrelated item whose feed content changes is evaluated again (baseline items stay baseline).</item>
/// </list>
/// </summary>
public sealed class NewsPlanner(
    ToroDbContext db,
    INotificationOutbox outbox,
    IModuleGate gate,
    IGuildSettingsStore guildSettings,
    NewsCardRenderer renderer,
    IOptions<NewsOptions> options,
    IOptions<DeliveryOptions> delivery,
    TimeProvider clock,
    ILogger<NewsPlanner> logger)
{
    public const string FeedKey = "hltv-rss";
    public const string ArticleKind = "article";

    public static string SourceKey(long articleId) => NewsArticle.Source + ":" + articleId.ToString(CultureInfo.InvariantCulture);

    public bool DryRun => options.Value.Mode == NewsMode.DryRun || delivery.Value.Mode != DeliveryMode.Send;

    public async Task<NewsFeedStateEntity> StateAsync(CancellationToken ct)
    {
        var set = db.Set<NewsFeedStateEntity>();
        var row = set.Local.FirstOrDefault(s => s.Key == FeedKey) ?? await set.FirstOrDefaultAsync(s => s.Key == FeedKey, ct);
        if (row is null)
        {
            row = new NewsFeedStateEntity { Key = FeedKey };
            set.Add(row);
        }

        return row;
    }

    /// <summary>The roster used for matching: the last successful sync, else the dated seed (both expire by age).</summary>
    public RosterSnapshot? Roster(NewsFeedStateEntity state)
    {
        if (state is { RosterPlayers.Length: > 0, RosterVerifiedAt: { } verified })
            return new RosterSnapshot(state.RosterPlayers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), verified, state.RosterSource ?? "liquipedia");
        var seed = options.Value.Roster;
        return seed.SeedPlayers.Length > 0 && seed.SeedVerifiedAtValue is { } at ? new RosterSnapshot(seed.SeedPlayers, at, "seed") : null;
    }

    public AuroraNewsMatcher Matcher()
    {
        var o = options.Value;
        return new AuroraNewsMatcher(o.Team.Aliases, o.Team.ExcludedNames, o.Roster.AmbiguousNames);
    }

    public async Task<NewsRoundSummary> ApplyAsync(FeedFetchResult result, DateTimeOffset nextPollAt, CancellationToken ct)
    {
        var o = options.Value;
        var now = clock.GetUtcNow();
        var state = await StateAsync(ct);
        state.LastAttemptAt = now;
        state.LastHttpStatus = result.HttpStatus;
        state.NextPollAt = nextPollAt;

        var outcome = result.Outcome;
        if (outcome == FeedOutcome.NotModified && state.BaselineAt is null)
            outcome = FeedOutcome.Malformed; // a 304 without stored content proves nothing

        if (!result.Succeeded || outcome == FeedOutcome.Malformed)
        {
            state.LastOutcome = (int)outcome;
            state.LastDetail = Clip(result.Detail ?? outcome.ToString(), 300);
            state.ConsecutiveFailures++;
            await db.SaveChangesAsync(ct);
            return new NewsRoundSummary(0, 0, 0, 0, 0, false);
        }

        var changed = new List<long>();
        var newItems = 0;
        var baselineNow = false;
        if (outcome == FeedOutcome.Ok)
        {
            var roster = Roster(state);
            var matcher = Matcher();
            var maxAge = TimeSpan.FromDays(o.Roster.MaxAgeDays);
            var ids = result.Items.Select(i => i.ArticleId).ToList();
            var known = await db.Set<NewsArticleEntity>().Where(a => a.Source == NewsArticle.Source && ids.Contains(a.ArticleId)).ToDictionaryAsync(a => a.ArticleId, ct);
            var establishing = state.BaselineAt is null;
            foreach (var item in result.Items)
            {
                var hash = item.ContentHash;
                if (!known.TryGetValue(item.ArticleId, out var row))
                {
                    var match = matcher.Evaluate(item, roster, now, maxAge);
                    row = new NewsArticleEntity
                    {
                        Source = NewsArticle.Source,
                        ArticleId = item.ArticleId,
                        Url = item.CanonicalUrl,
                        Title = item.Title,
                        PublishedAt = item.PublishedAt,
                        FirstSeenAt = now,
                        LastSeenAt = now,
                        ContentHash = hash,
                        Baseline = establishing || item.ArticleId <= state.PrunedBelowArticleId,
                        Relevant = match.Relevant,
                        MatchReason = (int)match.Reason,
                        MatchEvidence = Clip(match.Evidence, 200),
                    };
                    db.Set<NewsArticleEntity>().Add(row);
                    known[item.ArticleId] = row;
                    newItems++;
                    continue;
                }

                row.LastSeenAt = now;
                if (row.ContentHash == hash)
                    continue;
                var again = matcher.Evaluate(item, roster, now, maxAge);
                row.Url = item.CanonicalUrl;
                row.Title = item.Title;
                row.PublishedAt = item.PublishedAt;
                row.ContentHash = hash;
                row.ContentChangedAt = now;
                row.Relevant = again.Relevant;
                row.MatchReason = (int)again.Reason;
                row.MatchEvidence = Clip(again.Evidence, 200);
                changed.Add(item.ArticleId);
            }

            if (establishing && result.Items.Count > 0)
            {
                state.BaselineAt = now;
                baselineNow = true;
                logger.LogInformation("news baseline established with {Count} existing feed items (none of them will be posted)", result.Items.Count);
            }

            state.ETag = Clip(result.ETag, 200);
            state.LastModified = Clip(result.LastModified, 100);
            state.TtlMinutes = result.TtlMinutes;
            state.LastItemCount = result.Items.Count;
            state.LastSkippedCount = result.SkippedItems;
            state.LastRelevantCount = known.Values.Count(a => a.Relevant && ids.Contains(a.ArticleId));
            state.LastNewCount = newItems;
        }
        else if (outcome == FeedOutcome.Empty)
        {
            state.LastItemCount = 0;
            state.LastSkippedCount = 0;
            state.LastRelevantCount = 0;
            state.LastNewCount = 0;
        }

        state.LastOutcome = (int)outcome;
        state.LastDetail = Clip(result.Detail, 300);
        state.LastSuccessAt = now;
        state.ConsecutiveFailures = 0;

        var (staged, edits) = state.BaselineAt is null ? (0, 0) : await PlanDeliveriesAsync(state, changed, now, ct);
        await PruneAsync(state, now, ct);
        await db.SaveChangesAsync(ct);
        return new NewsRoundSummary(outcome == FeedOutcome.Ok ? result.Items.Count : 0, newItems, state.LastRelevantCount, staged, edits, baselineNow);
    }

    /// <summary>True when at least one guild would receive cards now (module on, channel set, not paused).</summary>
    public async Task<bool> HasActiveGuildAsync(CancellationToken ct)
    {
        foreach (var config in await db.Set<NewsGuildConfigEntity>().AsNoTracking().Where(c => c.ChannelId != null && !c.Paused).ToListAsync(ct))
        {
            if (await gate.IsEnabledAsync(new GuildId(config.GuildId), NewsModule.ModuleIdTyped, ct))
                return true;
        }

        return false;
    }

    private async Task<(int Staged, int Edits)> PlanDeliveriesAsync(NewsFeedStateEntity state, IReadOnlyList<long> changed, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        if (o.Mode == NewsMode.Off)
            return (0, 0);
        var dryRun = DryRun;
        var catchUpFrom = now - TimeSpan.FromHours(o.CatchUpHours);
        var staged = 0;
        var edits = 0;
        var articles = db.Set<NewsArticleEntity>();
        var deliveries = db.Set<NewsDeliveryEntity>();
        foreach (var config in await db.Set<NewsGuildConfigEntity>().Where(c => c.ChannelId != null && !c.Paused).ToListAsync(ct))
        {
            var guild = new GuildId(config.GuildId);
            if (!await gate.IsEnabledAsync(guild, NewsModule.ModuleIdTyped, ct))
                continue;
            var channel = new ChannelId(config.ChannelId!.Value);
            var language = (await guildSettings.GetAsync(guild, ct)).Language;
            DateTimeOffset since;
            if (dryRun)
                since = config.DryRunSince ??= now;
            else
                since = config.LiveSince ??= now;

            var delivered = await deliveries.AsNoTracking().Where(d => d.GuildId == config.GuildId && d.Source == NewsArticle.Source && d.DryRun == dryRun)
                .Select(d => d.ArticleId).ToListAsync(ct);
            // Tracked rows (this round's new and changed items) win over their stored versions.
            var tracked = articles.Local.Where(a => a.Source == NewsArticle.Source).ToList();
            var trackedIds = tracked.Select(a => a.ArticleId).ToList();
            var stored = await articles.AsNoTracking()
                .Where(a => a.Source == NewsArticle.Source && a.Relevant && !a.Baseline && a.Title != null && a.FirstSeenAt >= since &&
                            !delivered.Contains(a.ArticleId) && !trackedIds.Contains(a.ArticleId))
                .ToListAsync(ct);
            // Published (or, without a date, first seen) after the guild's start AND within the catch-up window.
            var notBefore = since > catchUpFrom ? since : catchUpFrom;
            var candidates = tracked.Concat(stored)
                .Where(a => a.Relevant && !a.Baseline && a.Title != null && a.FirstSeenAt >= since && !delivered.Contains(a.ArticleId) &&
                            (a.PublishedAt ?? a.FirstSeenAt) >= notBefore)
                .OrderBy(a => a.PublishedAt ?? a.FirstSeenAt).ThenBy(a => a.ArticleId)
                .Take(o.MaxCardsPerRound)
                .ToList();
            foreach (var article in candidates)
            {
                var message = renderer.Render(article.Url, article.Title!, article.PublishedAt, language, null);
                var result = await outbox.StageAsync(new NotificationRequest(guild, NewsModule.ModuleIdTyped, SourceKey(article.ArticleId), channel, ArticleKind,
                    message, now + TimeSpan.FromHours(o.CatchUpHours), dryRun), ct);
                deliveries.Add(new NewsDeliveryEntity
                {
                    GuildId = config.GuildId,
                    Source = NewsArticle.Source,
                    ArticleId = article.ArticleId,
                    DryRun = dryRun,
                    ChannelId = channel.Value,
                    Kind = ArticleKind,
                    StagedAt = now,
                });
                staged++;
                state.LastDeliveryStagedAt = now;
                logger.LogInformation("news card staged guild={Guild} article={Article} dryRun={DryRun} outcome={Outcome}",
                    config.GuildId, article.ArticleId, dryRun, result);
            }

            if (changed.Count == 0)
                continue;
            // Corrections of already delivered articles: the same logical key (its original channel) → a silent edit.
            foreach (var record in await deliveries.AsNoTracking()
                         .Where(d => d.GuildId == config.GuildId && d.Source == NewsArticle.Source && d.DryRun == dryRun && changed.Contains(d.ArticleId)).ToListAsync(ct))
            {
                var article = articles.Local.FirstOrDefault(a => a.Source == NewsArticle.Source && a.ArticleId == record.ArticleId);
                if (article?.Title is null)
                    continue;
                var message = renderer.Render(article.Url, article.Title, article.PublishedAt, language, null);
                var result = await outbox.StageAsync(new NotificationRequest(guild, NewsModule.ModuleIdTyped, SourceKey(article.ArticleId),
                    new ChannelId(record.ChannelId), record.Kind, message, now + TimeSpan.FromHours(o.CatchUpHours), dryRun), ct);
                if (result is StageOutcome.EditScheduled or StageOutcome.UpdatedPending)
                {
                    edits++;
                    logger.LogInformation("news card updated guild={Guild} article={Article} outcome={Outcome} (edit, no mention)", config.GuildId, article.ArticleId, result);
                }
            }
        }

        return (staged, edits);
    }

    /// <summary>
    /// Headlines are dropped after TextRetentionDays; ids and delivery records after DedupRetentionDays. Pruned ids stay covered
    /// by a watermark: an item at or below it is treated as baseline if it ever reappears (never sent again).
    /// </summary>
    private async Task PruneAsync(NewsFeedStateEntity state, DateTimeOffset now, CancellationToken ct)
    {
        var o = options.Value;
        var textCutoff = now - TimeSpan.FromDays(o.TextRetentionDays);
        var idCutoff = now - TimeSpan.FromDays(o.DedupRetentionDays);
        foreach (var old in await db.Set<NewsArticleEntity>().Where(a => a.Title != null && a.LastSeenAt < textCutoff).ToListAsync(ct))
            old.Title = null;

        var expired = await db.Set<NewsArticleEntity>().Where(a => a.LastSeenAt < idCutoff).ToListAsync(ct);
        if (expired.Count > 0)
        {
            state.PrunedBelowArticleId = Math.Max(state.PrunedBelowArticleId, expired.Max(a => a.ArticleId));
            db.Set<NewsArticleEntity>().RemoveRange(expired);
        }

        db.Set<NewsDeliveryEntity>().RemoveRange(await db.Set<NewsDeliveryEntity>().Where(d => d.StagedAt < idCutoff).ToListAsync(ct));
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}
