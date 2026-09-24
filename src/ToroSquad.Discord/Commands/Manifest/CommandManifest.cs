using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ToroSquad.Discord.Commands.Manifest;

/// <summary>Discord application command option types (wire values).</summary>
#pragma warning disable CA1720 // Names mirror the Discord API option type names.
public enum OptionType
{
    SubCommand = 1,
    SubCommandGroup = 2,
    String = 3,
    Integer = 4,
    Boolean = 5,
    User = 6,
    Channel = 7,
    Role = 8,
    Mentionable = 9,
    Number = 10,
    Attachment = 11,
}
#pragma warning restore CA1720

public sealed record ManifestChoice(string Name, string Value, IReadOnlyDictionary<string, string> NameLocalizations);

public sealed record ManifestOption(
    OptionType Type,
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> DescriptionLocalizations,
    bool Required,
    IReadOnlyList<ManifestChoice> Choices,
    IReadOnlyList<ManifestOption> Options,
    bool Autocomplete,
    double? MinValue,
    double? MaxValue,
    int? MinLength,
    int? MaxLength,
    IReadOnlyList<int> ChannelTypes);

/// <summary>
/// One top-level CHAT_INPUT command as it will be registered with Discord. <see cref="OwnerModule"/> is metadata
/// for docs/tests only and is not part of the Discord payload.
/// </summary>
public sealed record ManifestCommand(
    string Name,
    string Description,
    IReadOnlyDictionary<string, string> DescriptionLocalizations,
    IReadOnlyList<ManifestOption> Options,
    string? DefaultMemberPermissions,
    IReadOnlyList<int> Contexts,
    IReadOnlyList<int> IntegrationTypes,
    bool Nsfw,
    string OwnerModule);

/// <summary>
/// The complete set of slash commands this build exposes, generated offline from the interaction modules.
/// Registration (sync) is a separate, explicit step — see <see cref="CommandSyncPlanner"/>.
/// </summary>
public sealed record CommandManifest(IReadOnlyList<ManifestCommand> Commands, IReadOnlyList<string> LoadErrors)
{
    private static readonly JsonSerializerOptions DocumentOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Hash over the Discord-relevant payload of every command (order-independent).</summary>
    public string Hash => Sha256(string.Join("\n", Commands.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => CanonicalJson(c, includeGlobalOnlyFields: true))));

    public ManifestCommand? Find(string name) => Commands.FirstOrDefault(c => c.Name == name);

    /// <summary>
    /// Deterministic JSON in Discord API shape, used for diffing against what Discord returns.
    /// contexts/integration_types only apply to global commands (Discord ignores them for guild commands), so the
    /// guild-scope comparison leaves them out.
    /// </summary>
    public static string CanonicalJson(ManifestCommand command, bool includeGlobalOnlyFields)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("type", 1);
            w.WriteString("name", command.Name);
            w.WriteString("description", command.Description);
            WriteLocalizations(w, "description_localizations", command.DescriptionLocalizations);
            w.WriteString("default_member_permissions", command.DefaultMemberPermissions);
            w.WriteBoolean("nsfw", command.Nsfw);
            if (includeGlobalOnlyFields)
            {
                WriteInts(w, "contexts", command.Contexts);
                WriteInts(w, "integration_types", command.IntegrationTypes);
            }

            w.WriteStartArray("options");
            foreach (var option in command.Options)
                WriteOption(w, option);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Human-readable manifest file (docs/commands.manifest.json) — includes owner module metadata.</summary>
    public string ToDocumentJson()
    {
        var doc = new
        {
            format = "torosquad-command-manifest/v1",
            hash = Hash,
            commands = Commands.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new
            {
                module = c.OwnerModule,
                payload = JsonDocument.Parse(CanonicalJson(c, includeGlobalOnlyFields: true)).RootElement,
            }),
        };
        return JsonSerializer.Serialize(doc, DocumentOptions);
    }

    private static void WriteOption(Utf8JsonWriter w, ManifestOption o)
    {
        w.WriteStartObject();
        w.WriteNumber("type", (int)o.Type);
        w.WriteString("name", o.Name);
        w.WriteString("description", o.Description);
        WriteLocalizations(w, "description_localizations", o.DescriptionLocalizations);
        if (o.Type is not (OptionType.SubCommand or OptionType.SubCommandGroup))
            w.WriteBoolean("required", o.Required);
        if (o.Autocomplete)
            w.WriteBoolean("autocomplete", true);
        if (o.Choices.Count > 0)
        {
            w.WriteStartArray("choices");
            foreach (var c in o.Choices)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                WriteLocalizations(w, "name_localizations", c.NameLocalizations);
                if (o.Type is OptionType.Integer && long.TryParse(c.Value, out var l))
                    w.WriteNumber("value", l);
                else if (o.Type is OptionType.Number && double.TryParse(c.Value, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    w.WriteNumber("value", d);
                else
                    w.WriteString("value", c.Value);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (o.MinValue is { } min) w.WriteNumber("min_value", min);
        if (o.MaxValue is { } max) w.WriteNumber("max_value", max);
        if (o.MinLength is { } minL) w.WriteNumber("min_length", minL);
        if (o.MaxLength is { } maxL) w.WriteNumber("max_length", maxL);
        if (o.ChannelTypes.Count > 0) WriteInts(w, "channel_types", o.ChannelTypes.Order().ToList());
        if (o.Options.Count > 0)
        {
            w.WriteStartArray("options");
            foreach (var child in o.Options)
                WriteOption(w, child);
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteLocalizations(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
        {
            w.WriteNull(name);
            return;
        }

        w.WriteStartObject(name);
        foreach (var (k, v) in values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            w.WriteString(k, v);
        w.WriteEndObject();
    }

    private static void WriteInts(Utf8JsonWriter w, string name, IReadOnlyList<int> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
            w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
