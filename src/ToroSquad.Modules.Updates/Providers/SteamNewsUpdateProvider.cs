using System.Globalization;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Updates.Application;
using ToroSquad.Modules.Updates.Domain;

namespace ToroSquad.Modules.Updates.Providers;

/// <summary>
/// Steam's public news API: GET https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/ (Steamworks documentation: no key;
/// the publisher-only GetNewsForAppAuthed on partner.steam-api.com is a different method and is never called). The address
/// is fixed; only the game's numeric AppID and the bounded count are put into the query. The request itself — no API key, no
/// credentials, no redirects, bounded body, every failure its own kind — is <see cref="ProviderHttp"/>.
/// <para><c>maxlength=0</c> asks for the full post text: any other value returns a generated blurb without the markup the
/// classifier reads. The text is used in memory only.</para>
/// </summary>
public sealed class SteamNewsUpdateProvider(ProviderHttp http, IOptions<UpdatesOptions> options, TimeProvider clock) : IGameUpdateProvider
{
    public const string ProviderId = "steam";
    public const string HttpClientName = "updates-steam-news";
    public const string Endpoint = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/";

    public string Provider => ProviderId;

    public string DisplayName => "Steam";

    public string ReadLinkKey => "updates.card.read_steam";

    public bool IsCanonicalUrl(string url) => SteamNewsUrl.IsCanonical(url);

    /// <summary>The one request this provider makes for a game.</summary>
    public static Uri RequestUri(uint appId, int count) => new(string.Create(CultureInfo.InvariantCulture,
        $"{Endpoint}?appid={appId}&count={count}&maxlength=0&feeds={SteamNewsParser.AnnouncementFeed}&format=json"));

    public async Task<UpdateFetchResult> FetchAsync(GameUpdateDefinition game, CancellationToken cancellationToken)
    {
        if (!uint.TryParse(game.ProviderGameId, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
            return UpdateFetchResult.Fail(UpdateFetchOutcome.UnexpectedSchema, null, "game has no numeric AppID");

        var answer = await http.GetJsonAsync(HttpClientName, RequestUri(appId, options.Value.ItemsPerRequest), cancellationToken);
        if (!answer.Succeeded)
            return answer.AsFailure();

        var parsed = SteamNewsParser.Parse(answer.Body, game, clock.GetUtcNow());
        return parsed.Outcome switch
        {
            SteamParseOutcome.Ok => new(UpdateFetchOutcome.Ok, answer.HttpStatus, parsed.Items, parsed.Skipped, answer.CacheLifetime, null, parsed.Detail),
            SteamParseOutcome.Empty => new(UpdateFetchOutcome.Empty, answer.HttpStatus, [], 0, answer.CacheLifetime, null, parsed.Detail),
            SteamParseOutcome.Malformed => UpdateFetchResult.Fail(UpdateFetchOutcome.Malformed, answer.HttpStatus, parsed.Detail ?? "malformed answer"),
            _ => new(UpdateFetchOutcome.UnexpectedSchema, answer.HttpStatus, [], parsed.Skipped, null, null, parsed.Detail ?? "unexpected answer"),
        };
    }
}
