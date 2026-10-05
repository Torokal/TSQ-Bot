using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Application;

/// <summary>
/// Every registered game and the provider that serves it. The single list behind polling, admin choices, status and the CLI:
/// a game added to the registration appears everywhere, and nothing else names a game. A definition with an invalid key, a
/// duplicate key or an unregistered provider is a startup error (never a silently ignored game).
/// </summary>
public sealed class GameUpdateCatalog
{
    private readonly Dictionary<string, IGameUpdateProvider> _providers;

    public GameUpdateCatalog(IEnumerable<GameUpdateDefinition> games, IEnumerable<IGameUpdateProvider> providers)
    {
        _providers = new Dictionary<string, IGameUpdateProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!_providers.TryAdd(provider.Provider, provider))
                throw new InvalidOperationException($"Update provider '{provider.Provider}' is registered twice.");
        }

        var list = games.OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var game in list)
        {
            if (!GameUpdateDefinition.IsValidKey(game.Key))
                throw new InvalidOperationException($"Game key '{game.Key}' must be 1-{GameUpdateDefinition.KeyMax} characters of a-z, 0-9 or '-'.");
            if (!_providers.ContainsKey(game.Provider))
                throw new InvalidOperationException($"Game '{game.Key}' names the unregistered update provider '{game.Provider}'.");
        }

        foreach (var duplicate in list.GroupBy(g => g.Key, StringComparer.Ordinal).Where(g => g.Count() > 1))
            throw new InvalidOperationException($"Game '{duplicate.Key}' is registered twice.");
        Games = list;
    }

    /// <summary>Sorted by key.</summary>
    public IReadOnlyList<GameUpdateDefinition> Games { get; }

    public GameUpdateDefinition? Find(string? key) => key is null ? null : Games.FirstOrDefault(g => g.Key == key);

    public IGameUpdateProvider ProviderOf(GameUpdateDefinition game) => _providers[game.Provider];

    public IGameUpdateProvider? Provider(string provider) => _providers.GetValueOrDefault(provider);
}
