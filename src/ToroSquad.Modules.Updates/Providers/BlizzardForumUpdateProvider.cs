using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>Section "Updates:BlizzardForum": how gently the forum is read. Project choices — the forum documents no quota.</summary>
public sealed class BlizzardForumOptions
{
    public const string Section = "Updates:BlizzardForum";

    /// <summary>Shortest time between two rounds for one game (the forum sends no cache lifetime of its own).</summary>
    public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>New threads are looked for this far back (further list pages are only read while they are newer).</summary>
    public int DiscoveryLookbackHours { get; set; } = 6;

    /// <summary>Upper bound of list pages per round and category (30 threads each).</summary>
    public int MaxListPages { get; set; } = 3;

    /// <summary>Upper bound of threads opened per round to read a new Blizzard post in full (watched and followed threads not counted).</summary>
    public int MaxThreadRequests { get; set; } = 6;

    /// <summary>
    /// A thread in which an update was verified keeps being read for Blizzard's further posts this many days after its latest
    /// verified update (0: threads are not followed; watched threads are always read).
    /// </summary>
    public int FollowThreadDays { get; set; } = 7;

    /// <summary>Upper bound of followed threads read per round (the most recently updated ones), next to the watched threads.</summary>
    public int MaxFollowedThreads { get; set; } = 5;

    /// <summary>Upper bound of watched threads of one game the provider reads (a game's settings allow no more).</summary>
    public const int MaxWatchedThreads = 5;

    /// <summary>Upper bound of forum categories of one game (each one is listed on every round; a game's settings allow no more).</summary>
    public const int MaxCategories = 3;

    /// <summary>
    /// Requests one thread that is read post by post can cost in a round: the thread itself and at most two more for
    /// Blizzard posts beyond its first posts.
    /// </summary>
    public const int MaxRequestsPerThread = 3;

    /// <summary>
    /// No combination of settings may let one round ask the forum more often than this — checked when the settings are read,
    /// and counted while a round runs (the round stops asking there, whatever the reason).
    /// </summary>
    public const int MaxRequestsPerRound = 50;

    /// <summary>The most requests one round can make with these settings (every bound reached at once, for the most categories).</summary>
    public int WorstCaseRequestsPerRound => MaxRequestsPerThread * (MaxWatchedThreads + MaxFollowedThreads) + MaxListPages * MaxCategories + MaxThreadRequests;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (PollIntervalMinutes is < 5 or > 240)
            errors.Add(Section + ":PollIntervalMinutes must be between 5 and 240");
        if (DiscoveryLookbackHours is < 1 or > 24)
            errors.Add(Section + ":DiscoveryLookbackHours must be between 1 and 24");
        if (MaxListPages is < 1 or > 5)
            errors.Add(Section + ":MaxListPages must be between 1 and 5");
        if (MaxThreadRequests is < 1 or > 20)
            errors.Add(Section + ":MaxThreadRequests must be between 1 and 20");
        if (FollowThreadDays is < 0 or > 30)
            errors.Add(Section + ":FollowThreadDays must be between 0 and 30");
        if (MaxFollowedThreads is < 0 or > 10)
            errors.Add(Section + ":MaxFollowedThreads must be between 0 and 10");
        if (errors.Count == 0 && WorstCaseRequestsPerRound > MaxRequestsPerRound)
            errors.Add(string.Create(CultureInfo.InvariantCulture,
                $"{Section}: these settings allow up to {WorstCaseRequestsPerRound} requests in one round; at most {MaxRequestsPerRound} are allowed (lower MaxFollowedThreads, MaxListPages or MaxThreadRequests)"));
        return errors;
    }
}

/// <summary>
/// Blizzard's own posts on the official World of Warcraft forum (Discourse JSON; no key, no login), for a game that lives in
/// a few forum categories (<see cref="GameUpdateDefinition.ProviderGameId"/>: "347,352", see <see cref="BlizzardForumScope"/>).
/// A thread is in scope when its own category is one of them — a parent category does not stand for its subcategories.
/// Three ways in, one list out:
/// <list type="bullet">
/// <item><b>Watched threads</b> (<see cref="GameUpdateDefinition.WatchedThreadIds"/>): every Blizzard post of the thread,
/// read in full on every round — Blizzard adds new builds to its Development Notes thread as further posts, and edits them.</item>
/// <item><b>New threads</b>: each category's newest threads; one whose first post the forum marks as a Blizzard post is a
/// candidate. Its text is only downloaded when the game's classifier does not already rule the title out. Pinned threads
/// stand at the top whatever their age, so they never end a category's list early.</item>
/// <item><b>Followed threads</b> (<see cref="UpdateFetchContext.FollowedThreadIds"/>): threads in which the module verified an
/// update recently. They are read like a watched thread, for a bounded time and number (<see cref="ThreadFollow"/>), so a
/// later Blizzard post or an edit is still seen after the thread has left the newest threads. The first post is produced
/// exactly as the new-thread way produces it (the same post, unchanged); later Blizzard posts are posts of a watched thread.</item>
/// </list>
/// A post is "Blizzard's" only through the forum's own tracked-post markers (<c>first_tracked_post</c>,
/// <c>tracked_posts</c>); player posts and other categories never become candidates. The identity of a post is
/// <c>&lt;topic&gt;:&lt;post number&gt;</c>, the same from every way, so a post found twice is one post.
/// <para><b>Running hotfix threads.</b> A post that opens with date lines ("November 9, 2026", see
/// <see cref="ForumPostReader.DatedSections"/>) is one hotfix day per section. The opening post of such a thread keeps
/// collecting every day (newest on top), while each day is also posted as a reply: the opening post therefore stands for
/// its own day only — the day it was published with — and every reply for its day: one card per day, never two for the same
/// text. The opening post's own day is its oldest day only while that is provable: no reply carries the same day, and the
/// day is not after the post's own publication date. Otherwise (the first day was deleted, a date no longer reads) the
/// opening post is left out of the round, so its card is neither re-dated nor rewritten with another day's text, and the
/// detail says so. The day is part of the title ("WoW: Forever Hotfixes – November 9, 2026"); the thread's own title, whose
/// date Blizzard rewrites, is not. A day the opening post has but no reply does is named in the round's detail (it is not
/// posted).</para>
/// <para>The forum's group activity feed (/groups/blizzard-tracker) would list the same posts, but lies under a path the
/// forum's robots.txt disallows; it is not requested.</para>
/// What fails a round and what does not:
/// <list type="bullet">
/// <item>The category list and the watched threads are the game's main sources: a failure there fails the round as its own
/// kind, so nothing is ever concluded from half an answer. (A watched thread that is gone, moved to another category or not
/// public is skipped and counted.)</item>
/// <item>A followed thread and a single new thread only add to what a round finds. One that cannot be read — gone, a
/// timeout, a server error, an answer that is not a thread, too large — is left out of this round, counted, named in the
/// round's detail ("partial: …") and asked for again on the next round; the rest of the round goes on.</item>
/// <item>HTTP 429 is the forum asking for a pause, whichever request it answers: the round ends there as rate limited and
/// the forum's Retry-After is honoured.</item>
/// </list>
/// Every bound is hard: at most <see cref="BlizzardForumOptions.MaxRequestsPerThread"/> requests per thread read post by
/// post, the configured numbers of threads and list pages, and <see cref="BlizzardForumOptions.MaxRequestsPerRound"/> for
/// any valid combination of settings — also counted request by request while the round runs.
/// </summary>
public sealed partial class BlizzardForumUpdateProvider(ProviderHttp http, IOptions<BlizzardForumOptions> options, TimeProvider clock, ILogger<BlizzardForumUpdateProvider> logger)
    : IGameUpdateProvider
{
    public const string ProviderId = "blizzard";
    public const string HttpClientName = "updates-blizzard-forum";

    /// <summary>
    /// A request for the posts around post N answers with the posts from N onward (15 of them, and 5 before it — observed
    /// 2026-10-05). A request is aimed at the lowest missing Blizzard post within this reach of the newest missing one, so
    /// one answer brings both; the reach leaves a margin to what was observed.
    /// </summary>
    public const int ChunkReach = 10;

    private static readonly string[] UpdatedSuffixes = [" – Updated", " — Updated", " - Updated"];

    public string Provider => ProviderId;

    public string DisplayName => "Blizzard";

    public string ReadLinkKey => "updates.card.read_blizzard";

    public TimeSpan? MinimumPollInterval => TimeSpan.FromMinutes(options.Value.PollIntervalMinutes);

    public ThreadFollowRule? ThreadFollow => options.Value is { FollowThreadDays: > 0, MaxFollowedThreads: > 0 } o
        ? new ThreadFollowRule(TimeSpan.FromDays(o.FollowThreadDays), o.MaxFollowedThreads)
        : null;

    /// <summary>"2358655:4" → "2358655".</summary>
    public string? ThreadIdOf(string externalId)
    {
        var colon = externalId.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && TopicId(externalId[..colon]) is not null ? externalId[..colon] : null;
    }

    public bool IsCanonicalUrl(string url) => BlizzardForumUrl.IsPostLink(url);

    public Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, CancellationToken cancellationToken) => FetchAsync(game, UpdateFetchContext.None, cancellationToken);

    public async Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, UpdateFetchContext context, CancellationToken cancellationToken)
    {
        if (BlizzardForumScope.Categories(game.ProviderGameId) is not { } categories)
            return UpdateFetchResult.Fail(UpdateFetchOutcome.UnexpectedSchema, null, "game has no valid forum category list");
        var o = options.Value;
        var now = clock.GetUtcNow();
        var budget = new RequestBudget(BlizzardForumOptions.MaxRequestsPerRound);
        var posts = new Dictionary<string, GameUpdateCandidate>(StringComparer.Ordinal);
        var watched = new HashSet<long>(); // every thread read post by post in this round: configured, then followed
        var skipped = 0;
        var postsNotRead = 0; // Blizzard posts the forum marks in such a thread that the bounded requests did not reach
        var watchedUsable = 0;
        var daysOnlyInOpening = new List<string>();
        var openingDayUnproven = 0; // opening posts of hotfix threads whose own day could not be proven: left out

        foreach (var topicId in game.WatchedThreadIds.Select(TopicId).OfType<long>().Take(BlizzardForumOptions.MaxWatchedThreads))
        {
            if (!watched.Add(topicId))
                continue;
            var read = await TrackedPostsAsync(topicId, categories, budget, cancellationToken);
            // A watched thread is one of the game's main sources: its failure is the round's failure.
            if (read.Failure is not null)
                return read.Failure;
            if (read.Thread is null)
            {
                skipped++;
                continue;
            }

            watchedUsable++;
            postsNotRead += read.NotRead;
            openingDayUnproven += AddThread(posts, game, topicId, read, watchedOpening: true, now);
            if (read.NotRead == 0)
                daysOnlyInOpening.AddRange(DaysOnlyInOpening(read.Tracked));
        }

        var configured = watched.Count;
        var followedUnreadable = 0;
        foreach (var topicId in context.FollowedThreadIds.Select(TopicId).OfType<long>())
        {
            if (watched.Contains(topicId))
                continue;
            if (watched.Count - configured >= o.MaxFollowedThreads)
                break;
            watched.Add(topicId);
            var read = await TrackedPostsAsync(topicId, categories, budget, cancellationToken);
            if (read.Failure is { } failure)
            {
                if (EndsRound(failure))
                    return failure;
                // This thread's own trouble: a followed thread only adds to what a round finds, it never decides the round.
                followedUnreadable++;
                skipped++;
                logger.LogDebug("{Game} followed thread {Thread} could not be read in this round: {Outcome} {Detail}", game.Key, topicId, failure.Outcome, failure.Detail);
                continue;
            }

            // Only a thread Blizzard itself opened is followed; a player thread never becomes one through a stored id.
            if (read.Thread is null || !read.Thread.TrackedPosts.Contains(1))
            {
                skipped++;
                continue;
            }

            // The first post exactly as the new-thread way gives it (same title, same labels: the same post); Blizzard's
            // later posts as posts of a thread that is read post by post.
            postsNotRead += read.NotRead;
            openingDayUnproven += AddThread(posts, game, topicId, read, watchedOpening: false, now);
        }

        var followed = watched.Count - configured;

        var listed = 0;
        var outside = 0; // listed threads of a category that is not the game's (a parent lists its subcategories)
        var byBlizzard = new List<ForumThreadRef>();
        // The newest threads are read back over the usual lookback — after an outage back to the module's catch-up point,
        // still within the page bound.
        var lookback = now - TimeSpan.FromHours(o.DiscoveryLookbackHours);
        if (context.Since is { } since && since < lookback)
            lookback = since;
        var beyondBound = new List<int>();
        var reach = new List<string>();
        foreach (var category in categories)
        {
            var reachedBack = false;
            DateTimeOffset? oldestListed = null;
            for (var page = 0; page < o.MaxListPages; page++)
            {
                if (!budget.Take())
                    break;
                var answer = await http.GetJsonAsync(HttpClientName, BlizzardForumUrl.ThreadList(category, page), cancellationToken);
                if (!answer.Succeeded)
                    return answer.AsFailure();
                var parsed = BlizzardForumParser.ThreadList(answer.Body);
                if (!parsed.Succeeded)
                    return UpdateFetchResult.Fail(parsed.Outcome, answer.HttpStatus, parsed.Detail ?? "unexpected thread list");
                var threads = parsed.Value!.Threads;
                listed += threads.Count;
                outside += threads.Count(t => !categories.Contains(t.CategoryId));
                skipped += parsed.Value.Skipped;
                byBlizzard.AddRange(threads.Where(t => categories.Contains(t.CategoryId) && t.FirstTrackedPost == 1 && !watched.Contains(t.Id)));
                // A pinned thread stands on top whatever its age: it says nothing about how far back the list reaches.
                var dated = threads.Where(t => !t.Pinned).ToList();
                if (dated.Select(t => t.CreatedAt).Min() is { } oldestOnPage && (oldestListed is not { } known || oldestOnPage < known))
                    oldestListed = oldestOnPage;
                // Newest first: once a page reaches back past the lookback (or is the last one), older pages add nothing.
                if (threads.Count == 0 || dated.Any(t => t.CreatedAt is null || t.CreatedAt < lookback))
                {
                    reachedBack = true;
                    break;
                }
            }

            // The page bound ended the list before it reached the catch-up point: threads older than the last page read were
            // not seen in this round, and no later round can see more pages either.
            if (!reachedBack && context.Since is { } point && oldestListed is not null && oldestListed >= point)
                beyondBound.Add(category);
            if (categories.Count > 1)
                reach.Add(reachedBack || oldestListed is null
                    ? string.Create(CultureInfo.InvariantCulture, $"{category} {(now - lookback).TotalHours:0.#}h+")
                    : string.Create(CultureInfo.InvariantCulture, $"{category} {Math.Max(0, (now - oldestListed.Value).TotalHours):0.#}h"));
        }

        if (listed == 0 && posts.Count == 0 && !budget.Spent)
            return new UpdateFetchResult(UpdateFetchOutcome.Empty, 200, [], skipped, null, null, "the category lists no thread");

        var opened = 0;
        var deferred = 0;
        var newUnreadable = 0;
        var pending = false; // a new thread inside the lookback that this round could not read: the catch-up point stays
        foreach (var thread in byBlizzard.DistinctBy(t => t.Id).OrderByDescending(t => t.CreatedAt))
        {
            var preliminary = Candidate(game, thread.Id, 1, thread.Title, thread.CreatedAt, [UpdateLabels.FirstPost, UpdateLabels.ExcerptOnly], thread.Excerpt, null, now);
            if (game.Classifier.Classify(preliminary).Classification == UpdateClassification.NotUpdate)
            {
                // The title already rules it out (maintenance, known issues, …): kept as seen, never downloaded.
                Add(posts, preliminary);
                continue;
            }

            // A pinned thread opened before the lookback stands on top of every list page 0: earlier rounds read it when it was
            // new, and it is not downloaded again on every round. (Other old threads on a page read are still opened, so a
            // recent update thread is stored — and followed — also right after a first run or a source move.)
            if (thread is { Pinned: true, CreatedAt: { } created } && created < lookback)
                continue;

            if (opened >= o.MaxThreadRequests)
            {
                deferred++; // left for the next round rather than judged by its excerpt
                pending |= thread.CreatedAt >= lookback;
                continue;
            }

            if (budget.Exhausted)
            {
                deferred++;
                pending |= thread.CreatedAt >= lookback;
                continue;
            }

            opened++;
            var (full, failure) = await ThreadAsync(BlizzardForumUrl.Thread(thread.Id), budget, cancellationToken);
            if (failure is not null)
            {
                if (EndsRound(failure))
                    return failure;
                // One thread that cannot be read does not take the others with it; it is still listed and asked for again.
                newUnreadable++;
                skipped++;
                pending |= thread.CreatedAt >= lookback;
                logger.LogDebug("{Game} new thread {Thread} could not be read in this round: {Outcome} {Detail}", game.Key, thread.Id, failure.Outcome, failure.Detail);
                continue;
            }

            var first = full?.Posts.FirstOrDefault(p => p.Number == 1);
            if (full is null || full.Id != thread.Id || !categories.Contains(full.CategoryId) || !full.TrackedPosts.Contains(1) || first is not { Visible: true, Cooked.Length: > 0 })
            {
                skipped++;
                continue;
            }

            if (Full(game, thread.Id, full.Title, first, watchedThread: false, watchedOpening: false, NoDays, now) is { } candidate)
                Add(posts, candidate);
            else
                openingDayUnproven++;
        }

        skipped += postsNotRead;
        // What the round could not do comes first: status and doctor show the beginning of the detail.
        var partial = new List<string>();
        if (followedUnreadable > 0)
            partial.Add(Count(followedUnreadable, "followed thread") + " unreadable");
        if (newUnreadable > 0)
            partial.Add(Count(newUnreadable, "new thread") + " unreadable");
        if (postsNotRead > 0)
            partial.Add(Count(postsNotRead, "Blizzard post") + " not read");
        if (beyondBound.Count > 0)
            partial.Add("list page bound reached before the catch-up point" + (categories.Count > 1 ? " (" + string.Join(", ", beyondBound) + ")" : ""));
        // Every configured thread gone, moved or without a Blizzard post: an answer, but not the source the game is set up for.
        if (configured > 0 && watchedUsable == 0)
            partial.Add("no watched thread usable");
        if (daysOnlyInOpening.Count > 0)
            partial.Add("hotfix day only in the opening post: " + string.Join(", ", daysOnlyInOpening.Distinct().Take(3)));
        if (openingDayUnproven > 0)
            partial.Add(Count(openingDayUnproven, "hotfix opening post") + " left out (own day not provable)");
        if (budget.Spent)
        {
            partial.Add(string.Create(CultureInfo.InvariantCulture, $"request bound of {BlizzardForumOptions.MaxRequestsPerRound} reached"));
            pending = true;
        }

        var detail = string.Create(CultureInfo.InvariantCulture,
            $"{(partial.Count > 0 ? UpdateFetchResult.PartialDetailPrefix + string.Join(", ", partial) + "; " : "")}{listed} threads listed, {byBlizzard.Count} opened by Blizzard, {opened} read in full, {configured} watched{(followed > 0 ? $", {followed} followed" : "")}{(deferred > 0 ? $", {deferred} left for the next round" : "")}{(outside > 0 ? $", {outside} in other categories ignored" : "")}{(reach.Count > 0 ? "; lists " + string.Join(", ", reach) : "")}");
        logger.LogDebug("{Game} forum round: {Detail}; {Posts} Blizzard post(s), {Skipped} skipped, {Requests} request(s)", game.Key, detail, posts.Count, skipped, budget.Used);
        return new UpdateFetchResult(UpdateFetchOutcome.Ok, 200, posts.Values.ToList(), skipped, null, null, detail) { Incomplete = pending };
    }

    /// <summary>HTTP 429 is the forum asking for a pause — never one thread's own trouble: the round ends with it.</summary>
    private static bool EndsRound(UpdateFetchResult failure) => failure.Outcome == UpdateFetchOutcome.RateLimited;

    private static string Count(int number, string what) => string.Create(CultureInfo.InvariantCulture, $"{number} {what}{(number == 1 ? "" : "s")}");

    /// <summary>What <see cref="TrackedPostsAsync"/> found: the thread and its visible Blizzard posts, or why not.</summary>
    /// <param name="NotRead">Blizzard posts the forum marks in the thread that the bounded requests did not reach.</param>
    private readonly record struct ThreadRead(ForumThread? Thread, IReadOnlyList<ForumPost> Tracked, int NotRead, UpdateFetchResult? Failure);

    /// <summary>
    /// A thread, or the failure that ends the round. A thread that does not exist (any more), is not public or does not have
    /// the expected shape for a missing thread is (null, null): the caller skips it.
    /// </summary>
    private async Task<(ForumThread? Thread, UpdateFetchResult? Failure)> ThreadAsync(Uri address, RequestBudget budget, CancellationToken cancellationToken)
    {
        // The round's bound is spent: the thread is not asked for (the round says so and keeps its catch-up point).
        if (!budget.Take())
            return (null, null);
        var answer = await http.GetJsonAsync(HttpClientName, address, cancellationToken);
        if (answer is { Outcome: UpdateFetchOutcome.HttpError, HttpStatus: 403 or 404 or 410 })
            return (null, null);
        if (!answer.Succeeded)
            return (null, answer.AsFailure());
        var parsed = BlizzardForumParser.Thread(answer.Body);
        return parsed.Succeeded ? (parsed.Value, null) : (null, UpdateFetchResult.Fail(parsed.Outcome, answer.HttpStatus, parsed.Detail ?? "unexpected thread"));
    }

    /// <summary>
    /// The visible Blizzard posts of a thread that is read post by post, oldest first — or no thread when nothing of it may
    /// be used: gone, private, moved out of the game's categories, or without a single Blizzard marker (the caller counts
    /// the skip, so it shows up in status instead of passing silently). A failure is the caller's to judge.
    /// <para>A long thread only answers with its first posts. The forum's own markers say which Blizzard posts are still
    /// missing, so exactly those are asked for — the newest first, each request aimed to bring as many of them as one
    /// answer can, never the same place twice — in at most <see cref="BlizzardForumOptions.MaxRequestsPerThread"/>
    /// requests for the whole thread. Nothing is searched for by guessing; what is still missing after that is counted.</para>
    /// </summary>
    private async Task<ThreadRead> TrackedPostsAsync(long topicId, IReadOnlyList<int> categories, RequestBudget budget, CancellationToken cancellationToken)
    {
        var (thread, failure) = await ThreadAsync(BlizzardForumUrl.Thread(topicId), budget, cancellationToken);
        if (failure is not null)
            return new ThreadRead(null, [], 0, failure);
        if (thread is null || thread.Id != topicId || !categories.Contains(thread.CategoryId) || thread.TrackedPosts.Count == 0)
            return new ThreadRead(null, [], 0, null);

        var loaded = new Dictionary<int, ForumPost>();
        foreach (var post in thread.Posts)
            loaded.TryAdd(post.Number, post);
        var gone = new HashSet<int>(); // asked for by its own number and not in the answer: removed, nothing left to read
        for (var request = 1; request < BlizzardForumOptions.MaxRequestsPerThread; request++)
        {
            var missing = thread.TrackedPosts.Where(number => !loaded.ContainsKey(number) && !gone.Contains(number)).ToList();
            if (missing.Count == 0)
                break;
            var newest = missing.Max();
            var near = missing.Where(number => number >= newest - ChunkReach).Min();
            if (budget.Exhausted)
                break;
            var (chunk, chunkFailure) = await ThreadAsync(BlizzardForumUrl.Thread(topicId, near), budget, cancellationToken);
            if (chunkFailure is not null)
                return new ThreadRead(null, [], 0, chunkFailure);
            if (chunk is not null && chunk.Id == topicId)
            {
                foreach (var post in chunk.Posts)
                    loaded.TryAdd(post.Number, post);
            }

            if (!loaded.ContainsKey(near))
                gone.Add(near);
        }

        var tracked = thread.TrackedPosts.Order()
            .Select(number => loaded.GetValueOrDefault(number))
            .Where(post => post is { Visible: true, Cooked.Length: > 0 })
            .Select(post => post!)
            .ToList();
        return new ThreadRead(thread, tracked, thread.TrackedPosts.Count(number => !loaded.ContainsKey(number) && !gone.Contains(number)), null);
    }

    /// <summary>
    /// Days the opening post of a running hotfix thread carries besides its own (oldest) one, for which no Blizzard reply
    /// carries the same day: they are not posted, and the round's detail names them instead of claiming full coverage.
    /// </summary>
    private static IEnumerable<string> DaysOnlyInOpening(IReadOnlyList<ForumPost> tracked)
    {
        var opening = tracked.FirstOrDefault(p => p.Number == 1);
        if (opening is null || ForumPostReader.DatedSections(opening.Cooked) is not { Count: > 1 } days)
            return [];
        var own = days.Min(d => d.Date);
        var replied = tracked.Where(p => p.Number != 1).SelectMany(p => ForumPostReader.DatedSections(p.Cooked)).Select(d => d.Date).ToHashSet();
        return days.Where(d => d.Date != own && !replied.Contains(d.Date)).OrderBy(d => d.Date).Select(d => d.Label).ToList();
    }

    private static long? TopicId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) && topicId > 0 ? topicId : null;

    private static void Add(Dictionary<string, GameUpdateCandidate> posts, GameUpdateCandidate post) => posts.TryAdd(post.ExternalId, post);

    /// <summary>
    /// The visible Blizzard posts of a thread read post by post. A configured (watched) thread's posts all carry the watched
    /// label and its stable title; a followed thread's opening post is exactly what the new-thread way produces, its later
    /// posts are posts of a watched thread.
    /// </summary>
    /// <returns>How many opening posts were left out because their own hotfix day could not be proven (0 or 1).</returns>
    private static int AddThread(Dictionary<string, GameUpdateCandidate> posts, GameUpdateDefinition game, long topicId, ThreadRead read, bool watchedOpening, DateTimeOffset now)
    {
        var replied = read.Tracked.Where(p => p.Number != 1).SelectMany(p => ForumPostReader.DatedSections(p.Cooked)).Select(d => d.Date).ToHashSet();
        var leftOut = 0;
        foreach (var post in read.Tracked)
        {
            if (Full(game, topicId, read.Thread!.Title, post, watchedThread: watchedOpening || post.Number != 1, watchedOpening, replied, now) is { } candidate)
                Add(posts, candidate);
            else
                leftOut++;
        }

        return leftOut;
    }

    private static readonly IReadOnlySet<DateOnly> NoDays = new HashSet<DateOnly>();

    /// <summary>Blizzard's hotfix days are US days; UTC−8 never puts a post's date later than its Pacific date.</summary>
    private static readonly TimeSpan HotfixDayOffset = TimeSpan.FromHours(-8);

    /// <summary>
    /// The day an opening post was published with: its oldest day, while that is provable — no reply carries the same day
    /// (then it is the reply's), and the day is not after the post's own publication date. Null when it is not provable.
    /// </summary>
    private static ForumDatedSection? OwnDay(IReadOnlyList<ForumDatedSection> days, DateTimeOffset? createdAt, IReadOnlySet<DateOnly> replied)
    {
        var oldest = days.MinBy(d => d.Date)!;
        if (replied.Contains(oldest.Date) || createdAt is not { } at)
            return null;
        return oldest.Date <= DateOnly.FromDateTime(at.ToOffset(HotfixDayOffset).DateTime) ? oldest : null;
    }

    /// <summary>
    /// One post as a candidate. A running hotfix post (see <see cref="ForumPostReader.DatedSections"/>) is its own day: an
    /// opening post the day it was published with (<see cref="OwnDay"/>; the newer ones are Blizzard's replies), a reply the
    /// whole reply under its newest day. Its title is the thread's stable title with that day. Null: an opening post whose own
    /// day cannot be proven — it is left out rather than shown as another day.
    /// </summary>
    private static GameUpdateCandidate? Full(GameUpdateDefinition game, long topicId, string threadTitle, ForumPost post, bool watchedThread, bool watchedOpening,
        IReadOnlySet<DateOnly> repliedDays, DateTimeOffset now)
    {
        var content = ForumPostReader.Read(post.Cooked);
        var title = watchedOpening ? StableTitle(threadTitle) : threadTitle;
        if (ForumPostReader.DatedSections(post.Cooked) is { Count: > 0 } days)
        {
            ForumDatedSection day;
            if (post.Number == 1)
            {
                if (OwnDay(days, post.CreatedAt, repliedDays) is not { } own)
                    return null;
                day = own;
                content = own.Content;
            }
            else
            {
                day = days.MaxBy(d => d.Date)!;
            }

            title = StableTitle(threadTitle) + " – " + day.Label;
        }

        var labels = new List<string> { post.Number == 1 ? UpdateLabels.FirstPost : UpdateLabels.Reply };
        if (watchedThread)
            labels.Add(UpdateLabels.WatchedThread);
        return Candidate(game, topicId, post.Number, title, post.CreatedAt, labels, content.Text, content.Highlights, now);
    }

    private static GameUpdateCandidate Candidate(GameUpdateDefinition game, long topicId, int postNumber, string title, DateTimeOffset? createdAt,
        IReadOnlyList<string> labels, string body, UpdateHighlights? highlights, DateTimeOffset now)
    {
        var text = body.Length > GameUpdateCandidate.BodyMax ? body[..GameUpdateCandidate.BodyMax] : body;
        var name = UpdateText.OneLine(title);
        if (name.Length > GameUpdateCandidate.TitleMax)
            name = name[..GameUpdateCandidate.TitleMax];
        // A time in the future is "unknown", never shown as the publication time.
        var published = createdAt is { } at && at <= now + TimeSpan.FromHours(1) ? at : (DateTimeOffset?)null;
        return new GameUpdateCandidate(ProviderId, game.Key, string.Create(CultureInfo.InvariantCulture, $"{topicId}:{postNumber}"), name,
            BlizzardForumUrl.Post(topicId, postNumber), published, labels, text, highlights);
    }

    /// <summary>
    /// The title of a watched thread without the date Blizzard appends and rewrites with every new build or hotfix day —
    /// "– Updated &lt;date&gt;" or " - November 9, 2026": each post of the thread keeps one stable title (its own date is the
    /// card's timestamp, a hotfix day is added from the post itself), so renaming the thread does not rewrite earlier cards.
    /// </summary>
    public static string StableTitle(string title)
    {
        foreach (var suffix in UpdatedSuffixes)
        {
            var at = title.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                return title[..at].TrimEnd();
        }

        try
        {
            var dated = DateSuffix().Match(title);
            if (dated.Success && dated.Index > 0)
                return title[..dated.Index].TrimEnd();
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return title;
    }

    [GeneratedRegex(@"\s+[-–—]\s+(?:january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{1,2}(?:,?\s+\d{4})?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 250)]
    private static partial Regex DateSuffix();

    /// <summary>The requests one round may still make; every request of the round takes one.</summary>
    private sealed class RequestBudget(int limit)
    {
        public int Used { get; private set; }

        /// <summary>No request is left.</summary>
        public bool Exhausted => Used >= limit;

        /// <summary>A request was refused for lack of budget in this round.</summary>
        public bool Spent { get; private set; }

        public bool Take()
        {
            if (Used >= limit)
            {
                Spent = true;
                return false;
            }

            Used++;
            return true;
        }
    }
}

/// <summary>
/// The forum categories of a game, as its provider game id: category ids, ascending, comma separated ("347,352"). Between 1
/// and <see cref="BlizzardForumOptions.MaxCategories"/> distinct positive ids; anything else is not a scope.
/// </summary>
public static class BlizzardForumScope
{
    public static IReadOnlyList<int>? Categories(string? providerGameId)
    {
        if (string.IsNullOrEmpty(providerGameId) || providerGameId.Length > 64)
            return null;
        var ids = new List<int>();
        foreach (var part in providerGameId.Split(','))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || ids.Contains(id))
                return null;
            ids.Add(id);
        }

        return ids.Count is > 0 and <= BlizzardForumOptions.MaxCategories ? ids.Order().ToList() : null;
    }
}
