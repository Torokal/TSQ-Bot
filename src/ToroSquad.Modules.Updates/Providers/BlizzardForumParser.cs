using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>
/// The official World of Warcraft forum (US, English) — the only addresses the Blizzard provider requests and the only link
/// it renders. Every address is built here from numbers; nothing a post or a user supplies becomes part of a request.
/// Post links use the forum's own title-less form <c>/t/&lt;topic&gt;/&lt;post number&gt;</c>, which redirects to the
/// thread (checked 2026-10-05) and does not change when Blizzard renames a thread.
/// </summary>
public static partial class BlizzardForumUrl
{
    public const string Host = "us.forums.blizzard.com";
    public const string Root = "https://" + Host + "/en/wow";

    /// <summary>The category's threads, newest first (the forum's own list, 30 per page).</summary>
    public static Uri ThreadList(int categoryId, int page) => new(string.Create(CultureInfo.InvariantCulture,
        $"{Root}/latest.json?category={categoryId}&order=created{(page > 0 ? "&page=" + page.ToString(CultureInfo.InvariantCulture) : "")}"));

    /// <summary>A thread with its first posts.</summary>
    public static Uri Thread(long topicId) => new(string.Create(CultureInfo.InvariantCulture, $"{Root}/t/{topicId}.json"));

    /// <summary>A thread with the posts around one post number.</summary>
    public static Uri Thread(long topicId, int nearPostNumber) => new(string.Create(CultureInfo.InvariantCulture, $"{Root}/t/{topicId}/{nearPostNumber}.json"));

    public static string Post(long topicId, int postNumber) => string.Create(CultureInfo.InvariantCulture, $"{Root}/t/{topicId}/{postNumber}");

    /// <summary>True for exactly the form <see cref="Post"/> produces.</summary>
    public static bool IsPostLink(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200 || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return false;
        if (!string.Equals(uri.IdnHost, Host, StringComparison.Ordinal))
            return false;
        var match = PostPath().Match(uri.AbsolutePath);
        return match.Success &&
               long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var topic) &&
               int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var post) &&
               string.Equals(value, Post(topic, post), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^/en/wow/t/([1-9][0-9]{0,11})/([1-9][0-9]{0,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex PostPath();
}

/// <summary>One thread of the category list. <see cref="FirstTrackedPost"/> is the forum's own marker of the first Blizzard post in it.</summary>
public sealed record ForumThreadRef(long Id, string Title, int CategoryId, DateTimeOffset? CreatedAt, int? FirstTrackedPost, string Excerpt);

public sealed record ForumThreadList(IReadOnlyList<ForumThreadRef> Threads, int Skipped);

/// <param name="Visible">A regular, visible post (not a moderator action note, not hidden, not deleted).</param>
public sealed record ForumPost(int Number, string Cooked, DateTimeOffset? CreatedAt, bool Visible);

/// <param name="TrackedPosts">Post numbers the forum itself marks as Blizzard posts.</param>
public sealed record ForumThread(long Id, string Title, int CategoryId, IReadOnlySet<int> TrackedPosts, IReadOnlyList<ForumPost> Posts);

/// <summary>A parsed answer, or why it is not one (<see cref="UpdateFetchOutcome.Malformed"/> / <see cref="UpdateFetchOutcome.UnexpectedSchema"/>).</summary>
public sealed record ForumParse<T>(UpdateFetchOutcome Outcome, T? Value, string? Detail)
    where T : class
{
    public bool Succeeded => Outcome == UpdateFetchOutcome.Ok && Value is not null;
}

/// <summary>
/// Reads the two forum answers the provider uses, as observed on 2026-10-05: the category list
/// (<c>topic_list.topics[]</c>: <c>id, title, category_id, created_at, first_tracked_post{post_number}, excerpt, archetype,
/// visible</c>) and a thread (<c>id, title, category_id, tracked_posts[]{post_number}, post_stream.posts[]</c>:
/// <c>post_number, cooked, created_at, post_type, hidden, deleted_at</c>). Unknown fields are ignored; a missing optional
/// field has a safe meaning (no Blizzard marker, no excerpt); an entry without its required fields is skipped and counted;
/// an answer without the expected frame is refused as a whole.
/// </summary>
public static class BlizzardForumParser
{
    public const int MaxDepth = 32;
    public const int MaxThreadsPerPage = 100;
    public const int MaxPostsPerThread = 100;
    public const int MaxCookedLength = 400_000;

    public static ForumParse<ForumThreadList> ThreadList(ReadOnlyMemory<byte> json) => Parse(json, root =>
    {
        if (!root.TryGetProperty("topic_list", out var list) || list.ValueKind != JsonValueKind.Object ||
            !list.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array)
            return new ForumParse<ForumThreadList>(UpdateFetchOutcome.UnexpectedSchema, null, "no topic_list.topics list");
        var threads = new List<ForumThreadRef>();
        var skipped = 0;
        foreach (var topic in topics.EnumerateArray().Take(MaxThreadsPerPage))
        {
            if (Guarded(() => Thread(topic)) is { } thread)
                threads.Add(thread);
            else
                skipped++;
        }

        return new ForumParse<ForumThreadList>(UpdateFetchOutcome.Ok, new ForumThreadList(threads, skipped), null);
    });

    public static ForumParse<ForumThread> Thread(ReadOnlyMemory<byte> json) => Parse(json, root =>
    {
        if (Number(root, "id") is not { } id || id <= 0 || Text(root, "title") is not { Length: > 0 } title || Number(root, "category_id") is not { } category ||
            !root.TryGetProperty("post_stream", out var stream) || stream.ValueKind != JsonValueKind.Object ||
            !stream.TryGetProperty("posts", out var posts) || posts.ValueKind != JsonValueKind.Array)
            return new ForumParse<ForumThread>(UpdateFetchOutcome.UnexpectedSchema, null, "not a thread with id, title, category_id and post_stream.posts");

        var tracked = new HashSet<int>();
        if (root.TryGetProperty("tracked_posts", out var marks) && marks.ValueKind == JsonValueKind.Array)
        {
            foreach (var mark in marks.EnumerateArray().Take(MaxPostsPerThread * 10))
            {
                if (mark.ValueKind == JsonValueKind.Object && Number(mark, "post_number") is > 0 and <= int.MaxValue and var number)
                    tracked.Add((int)number);
            }
        }

        var read = new List<ForumPost>();
        foreach (var post in posts.EnumerateArray().Take(MaxPostsPerThread))
        {
            if (Guarded(() => Post(post)) is { } parsed)
                read.Add(parsed);
        }

        return new ForumParse<ForumThread>(UpdateFetchOutcome.Ok, new ForumThread(id, title, (int)Math.Clamp(category, int.MinValue, int.MaxValue), tracked, read), null);
    });

    private static ForumThreadRef? Thread(JsonElement topic)
    {
        if (topic.ValueKind != JsonValueKind.Object || Number(topic, "id") is not { } id || id <= 0 || Text(topic, "title") is not { Length: > 0 } title ||
            Number(topic, "category_id") is not { } category || category is < int.MinValue or > int.MaxValue)
            return null;
        // Private messages and unlisted threads are not public posts.
        if (Text(topic, "archetype") is { } archetype && archetype != "regular")
            return null;
        if (topic.TryGetProperty("visible", out var visible) && visible.ValueKind == JsonValueKind.False)
            return null;
        int? firstTracked = null;
        if (topic.TryGetProperty("first_tracked_post", out var mark) && mark.ValueKind == JsonValueKind.Object &&
            Number(mark, "post_number") is > 0 and <= int.MaxValue and var number)
            firstTracked = (int)number;
        return new ForumThreadRef(id, title, (int)category, Time(topic, "created_at"), firstTracked, Text(topic, "excerpt") ?? "");
    }

    private static ForumPost? Post(JsonElement post)
    {
        if (post.ValueKind != JsonValueKind.Object || Number(post, "post_number") is not (> 0 and <= int.MaxValue and var number))
            return null;
        var cooked = Text(post, "cooked") ?? "";
        if (cooked.Length > MaxCookedLength)
            cooked = cooked[..MaxCookedLength];
        var regular = Number(post, "post_type") is null or 1;
        var hidden = post.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True;
        var deleted = post.TryGetProperty("deleted_at", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        return new ForumPost((int)number, cooked, Time(post, "created_at"), regular && !hidden && !deleted);
    }

    private static ForumParse<T> Parse<T>(ReadOnlyMemory<byte> json, Func<JsonElement, ForumParse<T>> read)
        where T : class
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return new ForumParse<T>(UpdateFetchOutcome.Malformed, null, "not valid JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new ForumParse<T>(UpdateFetchOutcome.UnexpectedSchema, null, "the answer is not a JSON object");
            try
            {
                return read(document.RootElement);
            }
            catch (InvalidOperationException)
            {
                return new ForumParse<T>(UpdateFetchOutcome.UnexpectedSchema, null, "a required field is not readable text");
            }
        }
    }

    /// <summary>One entry whose strings are not valid text is unusable; the others are not.</summary>
    private static T? Guarded<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
}
