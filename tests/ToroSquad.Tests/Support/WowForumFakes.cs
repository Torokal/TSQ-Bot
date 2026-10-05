using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;

namespace ToroSquad.Tests.Support;

/// <summary>One synthetic forum post (made-up text; the shape is the forum's).</summary>
public sealed record ForumPostSpec(int Number, string Cooked, DateTimeOffset Created, bool Tracked = true, int PostType = 1, bool Hidden = false, bool Deleted = false);

/// <summary>
/// One synthetic forum thread. <see cref="ByBlizzard"/> makes its first post a tracked (Blizzard) post; without explicit
/// <see cref="Posts"/> the thread has one first post built from <see cref="Cooked"/>.
/// </summary>
public sealed record ForumThreadSpec(long Id, string Title, DateTimeOffset Created, bool ByBlizzard = false, int Category = WowForum.Category, string? Cooked = null,
    string Excerpt = "", IReadOnlyList<ForumPostSpec>? Posts = null)
{
    public IReadOnlyList<ForumPostSpec> AllPosts => Posts ?? [new ForumPostSpec(1, Cooked ?? "<p>Synthetic post.</p>", Created, ByBlizzard)];
}

/// <summary>Builds post HTML in the two layouts Blizzard's update posts use (synthetic lines only).</summary>
public static class ForumHtmlSamples
{
    /// <summary>"&lt;p&gt;&lt;strong&gt;Heading&lt;/strong&gt;&lt;/p&gt;&lt;ul&gt;…": bold paragraph, then a list.</summary>
    public static string BoldParagraphSections(string? intro, params (string Heading, string[] Items)[] sections)
    {
        var sb = new StringBuilder();
        if (intro is not null)
            sb.Append("<p>").Append(Escape(intro)).Append("</p>\n");
        sb.Append("<h2><a name=\"p-1-change-log-1\" class=\"anchor\" href=\"#p-1-change-log-1\"></a>Change Log</h2>\n");
        foreach (var (heading, items) in sections)
        {
            sb.Append("<p><strong>").Append(Escape(heading)).Append("</strong></p>\n<ul>\n");
            foreach (var item in items)
                sb.Append("<li>").Append(Escape(item)).Append("</li>\n");
            sb.Append("</ul>\n");
        }

        return sb.ToString();
    }

    /// <summary>"&lt;ul&gt;&lt;li&gt;&lt;strong&gt;Heading&lt;/strong&gt;&lt;ul&gt;…": one list whose items are the sections.</summary>
    public static string NestedListSections(string? intro, params (string Heading, string[] Items)[] sections)
    {
        var sb = new StringBuilder();
        if (intro is not null)
            sb.Append("<p>").Append(Escape(intro)).Append("</p>\n");
        sb.Append("<h2><a name=\"p-1-change-log-1\" class=\"anchor\" href=\"#p-1-change-log-1\"></a>Change Log</h2>\n<ul>\n");
        foreach (var (heading, items) in sections)
        {
            sb.Append("<li><strong>").Append(Escape(heading)).Append("</strong>\n<ul>\n");
            foreach (var item in items)
                sb.Append("<li>").Append(Escape(item)).Append("</li>\n");
            sb.Append("</ul>\n</li>\n");
        }

        sb.Append("</ul>");
        return sb.ToString();
    }

    /// <summary>A short client update: a build line and a plain list.</summary>
    public static string ClientUpdate(string buildLine, params string[] items) =>
        "<p>A few moments ago, we made a new client build available.<br>\n" + Escape(buildLine) + "</p>\n<ul>\n" +
        string.Concat(items.Select(i => "<li>" + Escape(i) + "</li>\n")) + "</ul>\n<p>Thank you for testing.</p>";

    public static string Escape(string text) => SecurityElement.Escape(text);
}

/// <summary>
/// The scripted World of Warcraft forum of a test: threads and posts live here, every request is recorded and answered in
/// the forum's JSON shape (category list newest first, 30 per page; a thread with its first 20 posts, or the posts around a
/// post number). Unknown addresses answer 404 as JSON, like the forum. <see cref="Override"/> replaces an answer (failures).
/// </summary>
public sealed class WowForum : HttpMessageHandler
{
    public const int Category = 349;
    public const long DevNotesThread = 2360696;
    public const int PageSize = 30;
    public const int ChunkSize = 20;
    private const string Root = "/en/wow";

    public List<ForumThreadSpec> Threads { get; } = [];

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>When set and returning non-null, that answer is sent instead.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public IEnumerable<string> RequestPaths => Requests.Select(r => r.RequestUri!.PathAndQuery);

    public int ThreadRequests(long id) => Requests.Count(r => r.RequestUri!.AbsolutePath.StartsWith($"{Root}/t/{id}", StringComparison.Ordinal));

    public ForumThreadSpec Thread(long id) => Threads.Single(t => t.Id == id);

    public void Replace(ForumThreadSpec thread)
    {
        Threads.RemoveAll(t => t.Id == thread.Id);
        Threads.Add(thread);
    }

    /// <summary>The Development Notes thread with these Blizzard posts (post numbers as given).</summary>
    public void SetDevNotes(string title, params ForumPostSpec[] posts) =>
        Replace(new ForumThreadSpec(DevNotesThread, title, posts.Min(p => p.Created), ByBlizzard: true, Posts: posts));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request);
        return Task.FromResult(Override?.Invoke(request) ?? Answer(request.RequestUri!));
    }

    private HttpResponseMessage Answer(Uri uri)
    {
        if (uri.Host != "us.forums.blizzard.com" || !uri.AbsolutePath.StartsWith(Root, StringComparison.Ordinal))
            return NotFound();
        var path = uri.AbsolutePath[Root.Length..];
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (path == "/latest.json")
        {
            var category = int.Parse(query["category"] ?? "0", CultureInfo.InvariantCulture);
            var page = int.Parse(query["page"] ?? "0", CultureInfo.InvariantCulture);
            var topics = Threads.Where(t => t.Category == category).OrderByDescending(t => t.Created).ThenByDescending(t => t.Id).Skip(page * PageSize).Take(PageSize);
            return Json(ListJson(topics));
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is ["t", var idPart, ..] && long.TryParse(idPart.Replace(".json", "", StringComparison.Ordinal), CultureInfo.InvariantCulture, out var id) &&
            Threads.FirstOrDefault(t => t.Id == id) is { } thread)
        {
            var near = parts.Length == 3 ? int.Parse(parts[2].Replace(".json", "", StringComparison.Ordinal), CultureInfo.InvariantCulture) : 1;
            return Json(ThreadJson(thread, near));
        }

        return NotFound();
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NotFound() =>
        Json("{\"errors\":[\"The requested URL or resource could not be found.\"],\"error_type\":\"not_found\"}", HttpStatusCode.NotFound);

    public static string ListJson(IEnumerable<ForumThreadSpec> threads)
    {
        var topics = new JsonArray();
        foreach (var t in threads)
        {
            topics.Add(new JsonObject
            {
                ["id"] = t.Id,
                ["title"] = t.Title,
                ["fancy_title"] = t.Title,
                ["slug"] = "synthetic-" + t.Id.ToString(CultureInfo.InvariantCulture),
                ["posts_count"] = t.AllPosts.Count,
                ["highest_post_number"] = t.AllPosts.Max(p => p.Number),
                ["created_at"] = Iso(t.Created),
                ["bumped_at"] = Iso(t.Created),
                ["archetype"] = "regular",
                ["visible"] = true,
                ["closed"] = false,
                ["pinned"] = false,
                ["excerpt"] = t.Excerpt,
                ["category_id"] = t.Category,
                ["first_tracked_post"] = t.AllPosts.Where(p => p.Tracked).Select(p => (int?)p.Number).Min() is { } first
                    ? new JsonObject { ["group"] = "community-manager", ["post_number"] = first, ["jump_target"] = first }
                    : null,
                ["posters"] = new JsonArray(new JsonObject { ["description"] = "Original Poster", ["user_id"] = 1 }),
            });
        }

        return new JsonObject
        {
            ["users"] = new JsonArray(new JsonObject { ["id"] = 1, ["username"] = "synthetic", ["trust_level"] = 1 }),
            ["primary_groups"] = new JsonArray(),
            ["topic_list"] = new JsonObject { ["can_create_topic"] = false, ["per_page"] = PageSize, ["topics"] = topics },
        }.ToJsonString();
    }

    public static string ThreadJson(ForumThreadSpec thread, int near = 1)
    {
        var all = thread.AllPosts.OrderBy(p => p.Number).ToList();
        // Like the forum: the first chunk, or the chunk around the requested post number.
        var start = near <= 1 ? 0 : Math.Max(0, all.FindIndex(p => p.Number >= near) - 5);
        var posts = new JsonArray();
        foreach (var p in all.Skip(start).Take(ChunkSize))
        {
            posts.Add(new JsonObject
            {
                ["id"] = thread.Id * 100 + p.Number,
                ["username"] = p.Tracked ? "SyntheticBlue" : "player-1",
                ["created_at"] = Iso(p.Created),
                ["updated_at"] = Iso(p.Created),
                ["cooked"] = p.PostType == 1 ? p.Cooked : "",
                ["post_number"] = p.Number,
                ["post_type"] = p.PostType,
                ["staff"] = p.Tracked,
                ["hidden"] = p.Hidden,
                ["deleted_at"] = p.Deleted ? Iso(p.Created) : null,
                ["topic_id"] = thread.Id,
                ["version"] = 1,
                ["user_custom_fields"] = new JsonObject(),
            });
        }

        return new JsonObject
        {
            ["id"] = thread.Id,
            ["title"] = thread.Title,
            ["fancy_title"] = thread.Title,
            ["category_id"] = thread.Category,
            ["posts_count"] = all.Count,
            ["highest_post_number"] = all.Max(p => p.Number),
            ["archetype"] = "regular",
            ["closed"] = true,
            ["tags"] = new JsonArray(),
            ["tracked_posts"] = new JsonArray(all.Where(p => p.Tracked).Select(p => (JsonNode)new JsonObject { ["group"] = "community-manager", ["post_number"] = p.Number }).ToArray()),
            ["post_stream"] = new JsonObject
            {
                ["posts"] = posts,
                ["stream"] = new JsonArray(all.Select(p => (JsonNode)(thread.Id * 100 + p.Number)).ToArray()),
            },
        }.ToJsonString();
    }

    private static string Iso(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
