using System.Text.Json;
using System.Text.Json.Nodes;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>
/// How a post's card excerpt is kept with the post (<c>updates_item.Highlights</c>): a small JSON object, bounded in size.
/// Reading is forgiving — anything that is not this shape is "no highlights", never an error — and goes through
/// <see cref="UpdateHighlights.Create"/> again, so a stored value can never exceed the bounds either.
/// </summary>
public static class UpdateHighlightsJson
{
    public const int MaxLength = 4000;

    public static string? Serialize(UpdateHighlights? highlights)
    {
        if (highlights is null || highlights.IsEmpty)
            return null;
        var sections = highlights.Sections.ToList();
        while (true)
        {
            var json = Write(highlights, sections);
            if (json.Length <= MaxLength || sections.Count == 0)
                return json.Length <= MaxLength ? json : null;
            sections.RemoveAt(sections.Count - 1); // the last section goes first: the card shows the first ones
        }
    }

    public static UpdateHighlights? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxLength)
            return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var sections = new List<UpdateSection>();
            if (root.TryGetProperty("s", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in list.EnumerateArray().Take(UpdateHighlights.MaxSections))
                {
                    if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty("i", out var items) || items.ValueKind != JsonValueKind.Array)
                        continue;
                    sections.Add(new UpdateSection(Text(section, "h"),
                        items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!).Take(UpdateHighlights.MaxItemsPerSection).ToList()));
                }
            }

            var count = root.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var number) ? number : 0;
            var highlights = UpdateHighlights.Create(Text(root, "v"), Text(root, "b"), count, sections);
            return highlights.IsEmpty ? null : highlights;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Write(UpdateHighlights highlights, IReadOnlyList<UpdateSection> sections)
    {
        var root = new JsonObject();
        if (highlights.Version is not null)
            root["v"] = highlights.Version;
        if (highlights.Build is not null)
            root["b"] = highlights.Build;
        root["n"] = highlights.ChangeCount;
        var list = new JsonArray();
        foreach (var section in sections)
        {
            var entry = new JsonObject();
            if (section.Heading is not null)
                entry["h"] = section.Heading;
            entry["i"] = new JsonArray(section.Items.Select(i => (JsonNode)i).ToArray());
            list.Add(entry);
        }

        root["s"] = list;
        return root.ToJsonString();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
