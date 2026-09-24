using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ToroSquad.Core.Localization;

public static class Languages
{
    public const string Turkish = "tr";
    public const string English = "en";
    public const string Default = Turkish;
    public const string Fallback = English;

    public static readonly IReadOnlyList<string> Supported = [Turkish, English];

    public static bool IsSupported(string? language) => language is Turkish or English;

    public static CultureInfo Culture(string language) =>
        CultureInfo.GetCultureInfo(language == Turkish ? "tr-TR" : "en-US");
}

/// <summary>
/// A module's embedded JSON string tables: one flat {"key": "text"} object per language.
/// Resource names: {assembly}.Localization.{lang}.json (see csproj EmbeddedResource).
/// </summary>
public sealed record LocalizationSource(Assembly Assembly, string ResourcePrefix);

public interface ILocalizer
{
    string Get(string language, string key, params object?[] args);
    bool HasKey(string language, string key);
}

/// <summary>Merged, immutable catalog of all modules' strings. Turkish default, English fallback, key as last resort.</summary>
public sealed class LocalizationCatalog : ILocalizer
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _tables;

    public LocalizationCatalog(IEnumerable<LocalizationSource> sources)
    {
        var merged = Languages.Supported.ToDictionary(l => l, _ => new Dictionary<string, string>(StringComparer.Ordinal));
        foreach (var source in sources)
        {
            foreach (var language in Languages.Supported)
            {
                var resourceName = $"{source.ResourcePrefix}.{language}.json";
                using var stream = source.Assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Missing localization resource '{resourceName}' in {source.Assembly.GetName().Name}.");
                var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                    ?? throw new InvalidOperationException($"Empty localization resource '{resourceName}'.");
                foreach (var (key, value) in table)
                {
                    if (!merged[language].TryAdd(key, value))
                        throw new InvalidOperationException($"Duplicate localization key '{key}' ({language}) from {resourceName}.");
                }
            }
        }

        _tables = merged.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, string> Table(string language) => _tables[language];

    public bool HasKey(string language, string key) =>
        _tables.TryGetValue(language, out var table) && table.ContainsKey(key);

    public string Get(string language, string key, params object?[] args)
    {
        if (!Languages.IsSupported(language))
            language = Languages.Default;

        if (!_tables[language].TryGetValue(key, out var template) &&
            !_tables[Languages.Fallback].TryGetValue(key, out template))
        {
            return key;
        }

        if (args.Length == 0)
            return template;

        var culture = Languages.Culture(language);
        var localizedArgs = args.Select(a => a is string s && _tables[language].TryGetValue(s, out var nested) ? nested : a).ToArray();
        return string.Format(culture, template, localizedArgs);
    }
}
