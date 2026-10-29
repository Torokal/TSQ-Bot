using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ToroSquad.Modules.Letterboxd.Domain;

/// <summary>
/// The kind the feed's guid names: "letterboxd-watch-&lt;id&gt;" (a diary entry without review text) or
/// "letterboxd-review-&lt;id&gt;" (an entry with review text). The number is the same viewing id in both forms (the review
/// page shows it as "viewing-&lt;id&gt;"), so a review added later keeps the identity and only changes this kind.
/// </summary>
public enum EntryGuidKind
{
    Watch = 0,
    Review = 1,
}

/// <summary>What a card says about one entry; also decides whether an entry is worth a card at all.</summary>
public enum EntryCardKind
{
    /// <summary>Review text (rated or not, rewatch or not).</summary>
    Review = 0,

    /// <summary>A rewatch without review text (rating optional).</summary>
    Rewatch = 1,

    /// <summary>A first watch with a rating and no review text.</summary>
    Rating = 2,

    /// <summary>A first watch with neither rating nor review: no card (only used when a posted card loses its content).</summary>
    Watched = 3,
}

/// <summary>
/// The card-visible part of one Letterboxd entry. The same record is built from a parsed feed item and from the stored row,
/// so both hashes are computed from identical inputs. <see cref="RatingHalves"/> is THIS entry's rating (1 = ½ … 10 = ★★★★★),
/// never the member's current rating of the film; null means the entry has none (never shown as 0).
/// </summary>
public sealed record EntryView(
    string Url,
    string FilmTitle,
    int? FilmYear,
    int? RatingHalves,
    bool? Rewatch,
    DateOnly? WatchedDate,
    DateTimeOffset? PublishedAt,
    string? Review,
    bool ReviewTruncated,
    bool? Spoilers,
    string? PosterUrl)
{
    public bool HasReview => !string.IsNullOrEmpty(Review);

    public EntryCardKind CardKind =>
        HasReview ? EntryCardKind.Review
        : Rewatch == true ? EntryCardKind.Rewatch
        : RatingHalves is not null ? EntryCardKind.Rating
        : EntryCardKind.Watched;

    /// <summary>Only reviews, rewatches and rated entries get a card; an unrated first watch without review does not.</summary>
    public bool Postable => CardKind != EntryCardKind.Watched;

    /// <summary>Everything the card shows: a change of any of these edits a posted card. Observation times are not part of it.</summary>
    public string ContentHash => Hash(Url, FilmTitle, Number(FilmYear), Number(RatingHalves), Flag(Rewatch), WatchedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        PublishedAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), Review, ReviewTruncated ? "1" : "0", Flag(Spoilers), PosterUrl);

    /// <summary>
    /// Only what the member controls (rating, rewatch flag, watched date, review). A change here may make an unposted entry
    /// postable; metadata changes (title, year, poster, link) only ever edit an existing card, they never create one.
    /// </summary>
    public string MemberHash => Hash(Number(RatingHalves), Flag(Rewatch), WatchedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Review, Flag(Spoilers));

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Flag(bool? value) => value switch { true => "1", false => "0", null => "?" };

    private static string Hash(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', parts.Select(p => p ?? "\u0000")))))[..32];
}

/// <summary>
/// One feed item as TSQ keeps it. Identity is (account, <see cref="ViewingId"/>) — never the film or its title: the same film
/// watched twice is two entries, and two entries of the same film on the same day stay two entries.
/// <see cref="FilmSlug"/> and the TMDB ids identify the film (shown nowhere, used for diagnostics only).
/// </summary>
public sealed record LetterboxdEntry(
    long ViewingId,
    EntryGuidKind GuidKind,
    string FilmSlug,
    long? TmdbMovieId,
    long? TmdbTvId,
    EntryView View);

/// <summary>Ratings as the feed writes them ("3.5"): half stars from 0.5 to 5.0. Anything else is invalid, never rounded.</summary>
public static class EntryRating
{
    public static bool TryParse(string? value, out int halves)
    {
        halves = 0;
        if (string.IsNullOrWhiteSpace(value) ||
            !decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating))
            return false;
        var doubled = rating * 2;
        if (doubled != decimal.Truncate(doubled) || doubled is < 1 or > 10)
            return false;
        halves = (int)doubled;
        return true;
    }

    /// <summary>"★★★½" (the Letterboxd notation); "½" for half a star.</summary>
    public static string Stars(int halves) => new string('★', halves / 2) + (halves % 2 == 1 ? "½" : "");

    /// <summary>"3,5" in Turkish, "3.5" in English; whole stars without a decimal ("4").</summary>
    public static string Number(int halves, CultureInfo culture) =>
        (halves / 2m).ToString(halves % 2 == 0 ? "0" : "0.0", culture);
}

/// <summary>
/// The only Letterboxd addresses TSQ requests or renders. The feed of the followed member is
/// https://letterboxd.com/&lt;username&gt;/rss/ (built from the validated configured username, never from feed data); entry
/// links must be https://letterboxd.com/&lt;username&gt;/film/&lt;slug&gt;/ (optionally /&lt;n&gt;/ for later entries of the
/// same film) of the SAME member — a list, another member's page or a look-alike host is not an entry.
/// </summary>
public static partial class LetterboxdUrl
{
    public const string Host = "letterboxd.com";
    public const string PosterHost = "a.ltrbxd.com";
    public static readonly IReadOnlyCollection<string> PosterHosts = [PosterHost];

    public static bool IsValidUsername(string? username) => username is not null && UsernamePattern().IsMatch(username);

    public static string Profile(string username) => "https://" + Host + "/" + username.ToLowerInvariant() + "/";

    public static string Feed(string username) => Profile(username) + "rss/";

    /// <summary>The channel link of the feed must be the followed member's profile (case-insensitive, trailing slash optional).</summary>
    public static bool IsProfileOf(string? value, string username)
    {
        if (!TryAbsolute(value, out var uri))
            return false;
        var path = uri.AbsolutePath.TrimEnd('/');
        return string.Equals(path, "/" + username, StringComparison.OrdinalIgnoreCase) && uri.Query.Length == 0;
    }

    public static bool TryParseEntry(string? value, string username, out string slug, out string canonical)
    {
        slug = "";
        canonical = "";
        if (!TryAbsolute(value, out var uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        var match = EntryPath().Match(uri.AbsolutePath);
        if (!match.Success || !string.Equals(match.Groups[1].Value, username, StringComparison.OrdinalIgnoreCase))
            return false;
        slug = match.Groups[2].Value.ToLowerInvariant();
        var index = match.Groups[3].Success ? match.Groups[3].Value + "/" : "";
        canonical = string.Create(CultureInfo.InvariantCulture, $"https://{Host}/{username.ToLowerInvariant()}/film/{slug}/{index}");
        return true;
    }

    /// <summary>
    /// The poster image of the feed item: https://a.ltrbxd.com/resized/… .jpg|.png|.webp only. The "?v=" cache key is dropped
    /// (the path alone serves the same image). Never fetched by TSQ; Discord loads it for the thumbnail.
    /// </summary>
    public static string? Poster(string? value)
    {
        if (!TryAbsolute(value, out var uri, PosterHost) || !uri.AbsolutePath.StartsWith("/resized/", StringComparison.Ordinal))
            return null;
        var path = uri.AbsolutePath;
        if (!path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            return null;
        var url = "https://" + PosterHost + path;
        return url.Length <= 400 ? url : null;
    }

    private static bool TryAbsolute(string? value, out Uri uri, string host = Host)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 500 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttps || !parsed.IsDefaultPort || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.Equals(parsed.IdnHost, host, StringComparison.OrdinalIgnoreCase))
            return false;
        uri = parsed;
        return true;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{2,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    [GeneratedRegex(@"^/([A-Za-z0-9_]{2,30})/film/([A-Za-z0-9-]{1,200})/(?:([0-9]{1,4})/)?$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPath();
}
