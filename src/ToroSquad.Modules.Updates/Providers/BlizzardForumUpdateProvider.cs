using System.Globalization;
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

    /// <summary>Upper bound of list pages per round (30 threads each).</summary>
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

    /// <summary>
    /// Requests one thread that is read post by post can cost in a round: the thread itself and at most two more for
    /// Blizzard posts beyond its first posts.
    /// </summary>
    public const int MaxRequestsPerThread = 3;

    /// <summary>No combination of settings may let one round ask the forum more often than this.</summary>
    public const int MaxRequestsPerRound = 50;

    /// <summary>The most requests one round can make with these settings (every bound reached at once).</summary>
    public int WorstCaseRequestsPerRound => MaxRequestsPerThread * (MaxWatchedThreads + MaxFollowedThreads) + MaxListPages + MaxThreadRequests;

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
/// one forum category (<see cref="GameUpdateDefinition.ProviderGameId"/>). Three ways in, one list out:
/// <list type="bullet">
/// <item><b>Watched threads</b> (<see cref="GameUpdateDefinition.WatchedThreadIds"/>): every Blizzard post of the thread,
/// read in full on every round — Blizzard adds new builds to its Development Notes thread as further posts, and edits them.</item>
/// <item><b>New threads</b>: the category's newest threads; one whose first post the forum marks as a Blizzard post is a
/// candidate. Its text is only downloaded when the game's classifier does not already rule the title out.</item>
/// <item><b>Followed threads</b> (<see cref="UpdateFetchContext.FollowedThreadIds"/>): threads in which the module verified an
/// update recently. They are read like a watched thread, for a bounded time and number (<see cref="ThreadFollow"/>), so a
/// later Blizzard post or an edit is still seen after the thread has left the newest threads. The first post is produced
/// exactly as the new-thread way produces it (the same post, unchanged); later Blizzard posts are posts of a watched thread.</item>
/// </list>
/// A post is "Blizzard's" only through the forum's own tracked-post markers (<c>first_tracked_post</c>,
/// <c>tracked_posts</c>); player posts and other categories never become candidates. The identity of a post is
/// <c>&lt;topic&gt;:&lt;post number&gt;</c>, the same from every way, so a post found twice is one post.
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
/// any valid combination of settings.
/// </summary>
public sealed class BlizzardForumUpdateProvider(ProviderHttp http, IOptions<BlizzardForumOptions> options, TimeProvider clock, ILogger<BlizzardForumUpdateProvider> logger)
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
        if (!int.TryParse(game.ProviderGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var category) || category <= 0)
            return UpdateFetchResult.Fail(UpdateFetchOutcome.UnexpectedSchema, null, "game has no numeric forum category");
        var o = options.Value;
        var now = clock.GetUtcNow();
        var posts = new Dictionary<string, GameUpdateCandidate>(StringComparer.Ordinal);
        var watched = new HashSet<long>(); // every thread read post by post in this round: configured, then followed
        var skipped = 0;
        var postsNotRead = 0; // Blizzard posts the forum marks in such a thread that the bounded requests did not reach

        foreach (var topicId in game.WatchedThreadIds.Select(TopicId).OfType<long>().Take(BlizzardForumOptions.MaxWatchedThreads))
        {
            if (!watched.Add(topicId))
                continue;
            var read = await TrackedPostsAsync(topicId, category, cancellationToken);
            // A watched thread is one of the game's main sources: its failure is the round's failure.
            if (read.Failure is not null)
                return read.Failure;
            if (read.Thread is null)
            {
                skipped++;
                continue;
            }

            postsNotRead += read.NotRead;
            var title = StableTitle(read.Thread.Title);
            foreach (var post in read.Tracked)
                Add(posts, Full(game, topicId, title, post, watchedThread: true, now));
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
            var read = await TrackedPostsAsync(topicId, category, cancellationToken);
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
            foreach (var post in read.Tracked)
                Add(posts, Full(game, topicId, read.Thread.Title, post, watchedThread: post.Number != 1, now));
        }

        var followed = watched.Count - configured;

        var listed = 0;
        var byBlizzard = new List<ForumThreadRef>();
        // The newest threads are read back over the usual lookback — after an outage back to the module's catch-up point,
        // still within the page bound.
        var lookback = now - TimeSpan.FromHours(o.DiscoveryLookbackHours);
        if (context.Since is { } since && since < lookback)
            lookback = since;
        var reachedBack = false;
        DateTimeOffset? oldestListed = null;
        for (var page = 0; page < o.MaxListPages; page++)
        {
            var answer = await http.GetJsonAsync(HttpClientName, BlizzardForumUrl.ThreadList(category, page), cancellationToken);
            if (!answer.Succeeded)
                return answer.AsFailure();
            var parsed = BlizzardForumParser.ThreadList(answer.Body);
            if (!parsed.Succeeded)
                return UpdateFetchResult.Fail(parsed.Outcome, answer.HttpStatus, parsed.Detail ?? "unexpected thread list");
            var threads = parsed.Value!.Threads;
            listed += threads.Count;
            skipped += parsed.Value.Skipped;
            byBlizzard.AddRange(threads.Where(t => t.CategoryId == category && t.FirstTrackedPost == 1 && !watched.Contains(t.Id)));
            if (threads.Select(t => t.CreatedAt).Min() is { } oldestOnPage && (oldestListed is not { } known || oldestOnPage < known))
                oldestListed = oldestOnPage;
            // Newest first: once a page reaches back past the lookback (or is the last one), older pages add nothing.
            if (threads.Count == 0 || threads.Any(t => t.CreatedAt is null || t.CreatedAt < lookback))
            {
                reachedBack = true;
                break;
            }
        }

        if (listed == 0 && posts.Count == 0)
            return new UpdateFetchResult(UpdateFetchOutcome.Empty, 200, [], skipped, null, null, "the category lists no thread");

        // The page bound ended the list before it reached the catch-up point: threads older than the last page read were not
        // seen in this round, and no later round can see more pages either.
        var beyondBound = !reachedBack && context.Since is { } point && oldestListed is not null && oldestListed >= point;

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

            if (opened >= o.MaxThreadRequests)
            {
                deferred++; // left for the next round rather than judged by its excerpt
                pending |= thread.CreatedAt >= lookback;
                continue;
            }

            opened++;
            var (full, failure) = await ThreadAsync(BlizzardForumUrl.Thread(thread.Id), cancellationToken);
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
            if (full is null || full.Id != thread.Id || full.CategoryId != category || !full.TrackedPosts.Contains(1) || first is not { Visible: true, Cooked.Length: > 0 })
            {
                skipped++;
                continue;
            }

            Add(posts, Full(game, thread.Id, full.Title, first, watchedThread: false, now));
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
        if (beyondBound)
            partial.Add("list page bound reached before the catch-up point");
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"{(partial.Count > 0 ? UpdateFetchResult.PartialDetailPrefix + string.Join(", ", partial) + "; " : "")}{listed} threads listed, {byBlizzard.Count} opened by Blizzard, {opened} read in full, {configured} watched{(followed > 0 ? $", {followed} followed" : "")}{(deferred > 0 ? $", {deferred} left for the next round" : "")}");
        logger.LogDebug("{Game} forum round: {Detail}; {Posts} Blizzard post(s), {Skipped} skipped", game.Key, detail, posts.Count, skipped);
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
    private async Task<(ForumThread? Thread, UpdateFetchResult? Failure)> ThreadAsync(Uri address, CancellationToken cancellationToken)
    {
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
    /// be used: gone, private, moved out of the game's category, or without a single Blizzard marker (the caller counts
    /// the skip, so it shows up in status instead of passing silently). A failure is the caller's to judge.
    /// <para>A long thread only answers with its first posts. The forum's own markers say which Blizzard posts are still
    /// missing, so exactly those are asked for — the newest first, each request aimed to bring as many of them as one
    /// answer can, never the same place twice — in at most <see cref="BlizzardForumOptions.MaxRequestsPerThread"/>
    /// requests for the whole thread. Nothing is searched for by guessing; what is still missing after that is counted.</para>
    /// </summary>
    private async Task<ThreadRead> TrackedPostsAsync(long topicId, int category, CancellationToken cancellationToken)
    {
        var (thread, failure) = await ThreadAsync(BlizzardForumUrl.Thread(topicId), cancellationToken);
        if (failure is not null)
            return new ThreadRead(null, [], 0, failure);
        if (thread is null || thread.Id != topicId || thread.CategoryId != category || thread.TrackedPosts.Count == 0)
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
            var (chunk, chunkFailure) = await ThreadAsync(BlizzardForumUrl.Thread(topicId, near), cancellationToken);
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

    private static long? TopicId(string id) =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var topicId) && topicId > 0 ? topicId : null;

    private static void Add(Dictionary<string, GameUpdateCandidate> posts, GameUpdateCandidate post) => posts.TryAdd(post.ExternalId, post);

    private static GameUpdateCandidate Full(GameUpdateDefinition game, long topicId, string title, ForumPost post, bool watchedThread, DateTimeOffset now)
    {
        var content = ForumPostReader.Read(post.Cooked);
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
    /// The title of a watched thread without the "– Updated &lt;date&gt;" Blizzard appends and rewrites with every new build:
    /// each post of the thread keeps one stable title (its own date is the card's timestamp), so renaming the thread does
    /// not rewrite the cards of earlier builds.
    /// </summary>
    public static string StableTitle(string title)
    {
        foreach (var suffix in UpdatedSuffixes)
        {
            var at = title.LastIndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                return title[..at].TrimEnd();
        }

        return title;
    }
}
