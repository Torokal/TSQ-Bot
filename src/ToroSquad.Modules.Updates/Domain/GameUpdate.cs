using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ToroSquad.Modules.Updates.Domain;

/// <summary>What a classifier decided about one provider post. Only <see cref="Update"/> is ever posted.</summary>
public enum UpdateClassification
{
    /// <summary>Not a game update (event, tournament, marketing, community post).</summary>
    NotUpdate = 0,

    /// <summary>Could be an update, but the evidence is not conclusive. Never posted: a wrong card is worse than a missed one.</summary>
    Ambiguous = 1,

    /// <summary>An official game update / patch notes post.</summary>
    Update = 2,
}

/// <summary><paramref name="Reason"/> is a short stable code for status, doctor and the CLI check (never shown on a card).</summary>
public sealed record UpdateClassificationResult(UpdateClassification Classification, string Reason);

/// <summary>Decides whether a post of one game is an update. Pure: no I/O, no clock, the same input gives the same answer.</summary>
public interface IGameUpdateClassifier
{
    UpdateClassificationResult Classify(GameUpdateCandidate candidate);
}

/// <summary>One titled group of changes of a post ("Bug Fixes" + its first lines). Heading and lines are plain text.</summary>
public sealed record UpdateSection(string? Heading, IReadOnlyList<string> Items);

/// <summary>
/// What a card may show of a post beyond its title: version and build when the post names them, and a small, bounded
/// excerpt of its change list (<see cref="ChangeCount"/> is the size of the whole list). Plain text from the provider —
/// always defused before it is rendered. Built only through <see cref="Create"/>, which enforces the bounds, so what is
/// stored and shown is an excerpt, never the post.
/// </summary>
public sealed record UpdateHighlights
{
    public const int MaxSections = 6;
    public const int MaxItemsPerSection = 5;
    public const int MaxItemLength = 200;
    public const int MaxHeadingLength = 80;
    public const int MaxVersionLength = 32;

    private UpdateHighlights(string? version, string? build, int changeCount, IReadOnlyList<UpdateSection> sections)
    {
        Version = version;
        Build = build;
        ChangeCount = changeCount;
        Sections = sections;
    }

    public string? Version { get; }
    public string? Build { get; }
    public int ChangeCount { get; }
    public IReadOnlyList<UpdateSection> Sections { get; }

    public bool IsEmpty => Version is null && Build is null && ChangeCount == 0 && Sections.Count == 0;

    public static UpdateHighlights Create(string? version, string? build, int changeCount, IEnumerable<UpdateSection> sections)
    {
        var kept = sections
            .Select(s => new UpdateSection(Clean(s.Heading, MaxHeadingLength), s.Items.Select(i => Clean(i, MaxItemLength)).OfType<string>().Take(MaxItemsPerSection).ToList()))
            .Where(s => s.Items.Count > 0)
            .Take(MaxSections)
            .ToList();
        return new UpdateHighlights(Clean(version, MaxVersionLength), Clean(build, MaxVersionLength), Math.Clamp(changeCount, 0, 99_999), kept);
    }

    /// <summary>One line, no control characters, bounded (a cut ends with "…"); null when nothing is left.</summary>
    private static string? Clean(string? value, int max)
    {
        var text = UpdateText.OneLine(value);
        if (text.Length == 0)
            return null;
        if (text.Length <= max)
            return text;
        var cut = text[..(max - 1)];
        if (char.IsHighSurrogate(cut[^1]))
            cut = cut[..^1];
        return cut.TrimEnd() + "…";
    }
}

/// <summary>
/// Labels a provider may put on a post to say where it stands, in words every classifier can rely on (a provider's own tags,
/// like Steam's, are passed through next to them).
/// </summary>
public static class UpdateLabels
{
    /// <summary>The post opens its thread.</summary>
    public const string FirstPost = "post:first";

    /// <summary>The post is a later post of a thread.</summary>
    public const string Reply = "post:reply";

    /// <summary>
    /// The post belongs to a thread that is read post by post: one the game's definition names
    /// (<see cref="GameUpdateDefinition.WatchedThreadIds"/>), or — for a later post — one that is followed because an update
    /// was verified in it.
    /// </summary>
    public const string WatchedThread = "thread:watched";

    /// <summary>Only a short excerpt of the post is known: its text was not loaded.</summary>
    public const string ExcerptOnly = "content:excerpt";
}

/// <summary>
/// One post as a provider normalized it — the only shape the classifier, the planner and the renderer ever see.
/// <see cref="ExternalId"/> is the provider's own id of the post and, with provider and game, the identity (never the title:
/// two updates regularly share one). <see cref="Labels"/> are the provider's own tags and the shared
/// <see cref="UpdateLabels"/>. <see cref="Body"/> is the post text, bounded, used by the classifier in memory only: it is
/// never stored, logged or shown. <see cref="Highlights"/> is the bounded excerpt a card may show (none for providers that
/// do not supply one); it is derived from the text, so it is not part of <see cref="ContentHash"/>.
/// </summary>
public sealed record GameUpdateCandidate(
    string Provider,
    string GameKey,
    string ExternalId,
    string Title,
    string CanonicalUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string> Labels,
    string Body,
    UpdateHighlights? Highlights = null)
{
    public const int TitleMax = 300;
    public const int BodyMax = 16_000;
    public const int ExternalIdMax = 64;

    /// <summary>Everything a classification or a card depends on; a change re-evaluates the stored item.</summary>
    public string ContentHash
    {
        get
        {
            var text = string.Join('\u001F', Title, CanonicalUrl, PublishedAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? "",
                string.Join(',', Labels.Order(StringComparer.Ordinal)), Body);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
        }
    }

    /// <summary>Identity only: the post text can never end up in a log or an exception message through this.</summary>
    public override string ToString() => Provider + ":" + GameKey + ":" + ExternalId;
}

/// <summary>
/// A game whose updates can be followed: which provider serves it, under which id there, and who decides what an update is.
/// Adding a game is one more definition (plus its classifier); nothing else in the module names a game.
/// </summary>
/// <param name="Key">Stable lowercase id (<c>cs2</c>): database key, part of the outbox kind, the value admins pick.</param>
/// <param name="DisplayName">Full name ("Counter-Strike 2").</param>
/// <param name="ShortName">Card title name ("CS2").</param>
/// <param name="Provider">The <see cref="IGameUpdateProvider.Provider"/> that serves this game ("steam").</param>
/// <param name="ProviderGameId">The game's id at that provider (Steam AppID "730"). Public data, not configuration.</param>
public sealed record GameUpdateDefinition(string Key, string DisplayName, string ShortName, string Provider, string ProviderGameId, IGameUpdateClassifier Classifier)
{
    public const int KeyMax = 16;

    /// <summary>
    /// Provider-side ids of threads that are watched post by post, because the source publishes new updates there as further
    /// posts instead of opening a new thread. Empty for sources without threads.
    /// </summary>
    public IReadOnlyList<string> WatchedThreadIds { get; init; } = [];

    public static bool IsValidKey(string? key) =>
        key is { Length: > 0 and <= KeyMax } && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}

public enum UpdateFetchOutcome
{
    /// <summary>A valid answer with at least one usable post.</summary>
    Ok = 0,

    /// <summary>A valid answer without posts: not a failure, but never enough for a baseline.</summary>
    Empty = 1,
    RateLimited = 2,
    Timeout = 3,
    ServerError = 4,

    /// <summary>Any other non-success status (a 4xx, or a redirect: redirects are never followed).</summary>
    HttpError = 5,
    TransportError = 6,

    /// <summary>The body is larger than the configured bound.</summary>
    TooLarge = 7,

    /// <summary>Not parseable (invalid JSON, nesting too deep, not the expected content type).</summary>
    Malformed = 8,

    /// <summary>Parseable but not the documented shape, or it answers for another game.</summary>
    UnexpectedSchema = 9,
}

/// <summary>
/// One provider answer for one game. A failure is never "no updates": only <see cref="Succeeded"/> answers touch the stored
/// posts. <see cref="CacheLifetime"/> is how long the source itself declared the answer fresh (none when it said nothing).
/// </summary>
public sealed record UpdateFetchResult(
    UpdateFetchOutcome Outcome,
    int? HttpStatus,
    IReadOnlyList<GameUpdateCandidate> Items,
    int SkippedItems,
    TimeSpan? CacheLifetime,
    TimeSpan? RetryAfter,
    string? Detail)
{
    public bool Succeeded => Outcome is UpdateFetchOutcome.Ok or UpdateFetchOutcome.Empty;

    /// <summary>
    /// How the <see cref="Detail"/> of a valid answer starts when the round could not do everything it set out to do (a
    /// thread that could not be read, a bound that was reached): status names such a round a partial success.
    /// </summary>
    public const string PartialDetailPrefix = "partial: ";

    /// <summary>
    /// A valid answer that is known not to hold everything the source has published since
    /// <see cref="UpdateFetchContext.Since"/>: something new could not be read in this round and is asked for again. It is
    /// applied like any answer, but the module keeps its catch-up point, so the next round looks back as far again.
    /// </summary>
    public bool Incomplete { get; init; }

    public static UpdateFetchResult Fail(UpdateFetchOutcome outcome, int? status, string detail, TimeSpan? retryAfter = null) =>
        new(outcome, status, [], 0, null, retryAfter, detail);

    /// <summary>
    /// The answer reduced to what a provider may say about <paramref name="game"/>: posts of that provider and game with a
    /// usable id, each id once. Anything else is counted as skipped, and a successful answer that carries nothing for the
    /// game is a failure (<see cref="UpdateFetchOutcome.UnexpectedSchema"/>) — so it is also scheduled and counted as one.
    /// </summary>
    public UpdateFetchResult ForGame(GameUpdateDefinition game)
    {
        if (Outcome != UpdateFetchOutcome.Ok)
            return this;
        var own = Items.Where(i => i.Provider == game.Provider && i.GameKey == game.Key && i.ExternalId.Length is > 0 and <= GameUpdateCandidate.ExternalIdMax)
            .DistinctBy(i => i.ExternalId).ToList();
        if (own.Count == Items.Count)
            return this;
        var skipped = SkippedItems + (Items.Count - own.Count);
        return own.Count == 0
            ? new UpdateFetchResult(UpdateFetchOutcome.UnexpectedSchema, HttpStatus, [], skipped, null, null, Detail ?? "no post of the requested game in the answer")
            : this with { Items = own, SkippedItems = skipped };
    }
}

/// <summary>
/// How long a provider keeps reading a thread after an update was verified in it, and how many such threads at most.
/// The module derives the threads from the posts it already stores; the provider never keeps a list of its own.
/// </summary>
public sealed record ThreadFollowRule(TimeSpan Window, int MaxThreads);

/// <summary>
/// What the module knows that a round may use. <see cref="FollowedThreadIds"/>: threads in which an update of this game
/// was verified recently (newest first, bounded by the provider's <see cref="IGameUpdateProvider.ThreadFollow"/>), so the
/// source's later posts there are still read when the thread is no longer among the newest ones. <see cref="Since"/>: the
/// module's catch-up point — the last round that answered completely, never further back than the module's catch-up
/// window (null: there was none). A source that only shows its newest posts looks back at least this far, within its own
/// bounds, so what was published during an outage is still found.
/// </summary>
public sealed record UpdateFetchContext(IReadOnlyList<string> FollowedThreadIds, DateTimeOffset? Since = null)
{
    public static UpdateFetchContext None { get; } = new([]);
}

/// <summary>
/// A source of game posts ("steam"). It only reads: it never sees guilds, the outbox or Discord. One call serves every guild
/// that follows the game.
/// </summary>
public interface IGameUpdateProvider
{
    /// <summary>Stable lowercase id used in definitions, keys and the database.</summary>
    string Provider { get; }

    /// <summary>Shown as the card's source ("Steam").</summary>
    string DisplayName { get; }

    /// <summary>Localization key of the card's link text for this source.</summary>
    string ReadLinkKey { get; }

    /// <summary>True when <paramref name="url"/> is a link this provider itself would produce (the renderer refuses anything else).</summary>
    bool IsCanonicalUrl(string url);

    /// <summary>
    /// The shortest time this source should be left alone between two rounds, when that is longer than the module's own
    /// interval (a forum is asked less often than a cached API). Null: the module's interval applies.
    /// </summary>
    TimeSpan? MinimumPollInterval => null;

    /// <summary>Set by a source whose updates can continue as further posts of a thread; null: threads are not followed.</summary>
    ThreadFollowRule? ThreadFollow => null;

    /// <summary>The thread a post id of this provider belongs to, or null when the provider has no threads.</summary>
    string? ThreadIdOf(string externalId) => null;

    Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, CancellationToken cancellationToken);

    /// <summary>A round with what the module knows; a provider without threads answers as it always does.</summary>
    Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, UpdateFetchContext context, CancellationToken cancellationToken) =>
        FetchAsync(game, cancellationToken);
}

/// <summary>Plain-text helpers shared by classifiers: deterministic, culture independent, bounded.</summary>
public static class UpdateText
{
    /// <summary>Whitespace and control characters collapsed to single spaces, trimmed (case and letters untouched).</summary>
    public static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>Compatibility-normalized, typographic dashes and quotes folded, one space, invariant lower case.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var text = value.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            var c = ch switch
            {
                '’' or '‘' or 'ʼ' => '\'',
                '‐' or '‑' or '‒' or '–' or '—' => '-',
                _ => ch,
            };
            if (char.IsWhiteSpace(c) || char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>Whole-word test on normalized text (letters and digits are word characters).</summary>
    public static bool ContainsWord(string normalized, string word)
    {
        var from = 0;
        while (from <= normalized.Length - word.Length)
        {
            var at = normalized.IndexOf(word, from, StringComparison.Ordinal);
            if (at < 0)
                return false;
            var before = at == 0 || !char.IsLetterOrDigit(normalized[at - 1]);
            var end = at + word.Length;
            var after = end == normalized.Length || !char.IsLetterOrDigit(normalized[end]);
            if (before && after)
                return true;
            from = at + 1;
        }

        return false;
    }
}
