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

/// <summary>
/// One post as a provider normalized it — the only shape the classifier, the planner and the renderer ever see.
/// <see cref="ExternalId"/> is the provider's own id of the post and, with provider and game, the identity (never the title:
/// two updates regularly share one). <see cref="Labels"/> are the provider's own tags. <see cref="Body"/> is the post text,
/// bounded, used by the classifier in memory only: it is never stored, logged or shown.
/// </summary>
public sealed record GameUpdateCandidate(
    string Provider,
    string GameKey,
    string ExternalId,
    string Title,
    string CanonicalUrl,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<string> Labels,
    string Body)
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

    Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, CancellationToken cancellationToken);
}

/// <summary>Plain-text helpers shared by classifiers: deterministic, culture independent, bounded.</summary>
public static class UpdateText
{
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
