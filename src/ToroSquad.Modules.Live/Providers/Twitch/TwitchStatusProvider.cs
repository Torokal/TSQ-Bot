using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Providers.Twitch;

/// <summary>
/// Section "Live:Twitch". <see cref="ClientId"/>/<see cref="ClientSecret"/> belong to the operator's Twitch developer
/// application (client-credentials / app access token; no user login needed). Secrets: TOROSQUAD_Live__Twitch__ClientId /
/// __ClientSecret or user-secrets only — never repository files. Verified 2026-09-26 (dev.twitch.tv): GET helix/streams
/// with repeated user_login (≤100) returns only broadcasting channels; GET helix/users (≤100) has profile_image_url; app
/// tokens from POST id.twitch.tv/oauth2/token (grant_type=client_credentials) are validated hourly at
/// id.twitch.tv/oauth2/validate; Helix is a points bucket per minute (Ratelimit-* headers, 429 → wait for reset).
/// </summary>
public sealed class TwitchOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.twitch.tv/helix/";
    public string TokenUrl { get; set; } = "https://id.twitch.tv/oauth2/token";
    public string ValidateUrl { get; set; } = "https://id.twitch.tv/oauth2/validate";
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 1;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public string? Problem()
    {
        foreach (var url in new[] { ApiBaseUrl, TokenUrl, ValidateUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return "Live:Twitch:ApiBaseUrl/TokenUrl/ValidateUrl must be https URLs";
        }

        if (TimeoutSeconds is < 3 or > 60 || MaxRetries is < 0 or > 3)
            return "Live:Twitch:TimeoutSeconds must be 3..60 and MaxRetries 0..3";
        return null;
    }
}

/// <summary>
/// Twitch through the official Helix API only (no page scraping, no undocumented endpoints): one batched
/// <c>GET streams</c> per reconciliation describes every tracked channel — present = live (with stream id, title, category,
/// start time), absent from a successful answer = offline. Profile pictures come from a batched <c>GET users</c> every few
/// hours (best effort). EventSub is not used: its webhook transport needs a public HTTPS endpoint (the bot has no inbound
/// networking) and its WebSocket transport needs a stored, rotating user refresh token (see docs/live/TSQ_LIVE.md).
/// </summary>
public sealed class TwitchStatusProvider : ILiveStatusProvider
{
    public const string HttpClientName = "live-twitch";
    public const string AuthHttpClientName = "live-twitch-auth";
    private static readonly TimeSpan ValidateEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan AvatarRefresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan AvatarRetry = TimeSpan.FromMinutes(30);

    private readonly TwitchOptions _options;
    private readonly HttpClient _auth;
    private readonly LiveHttp _json;
    private readonly ClientCredentialsTokens _tokens;
    private readonly TimeProvider _clock;
    private readonly ILogger<TwitchStatusProvider> _logger;
    private readonly Dictionary<string, string> _avatars = new(StringComparer.Ordinal);
    private DateTimeOffset _nextAvatarRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _lastValidatedAt;

    public TwitchStatusProvider(IHttpClientFactory http, IOptions<TwitchOptions> options, TimeProvider clock, ILogger<TwitchStatusProvider> logger)
    {
        _options = options.Value;
        _auth = http.CreateClient(AuthHttpClientName);
        _json = new LiveHttp(http.CreateClient(HttpClientName), clock, logger);
        _tokens = new ClientCredentialsTokens(_auth, "Twitch", clock, logger);
        _clock = clock;
        _logger = logger;
    }

    public LivePlatform Platform => LivePlatform.Twitch;

    public bool IsConfigured => _options.HasCredentials;

    public LiveAuthState Auth => new(_tokens.LastOutcome, _tokens.ValidUntil, _lastValidatedAt);

    public async Task<LiveProviderResult> GetStatusAsync(IReadOnlyCollection<string> logins, CancellationToken cancellationToken)
    {
        var at = _clock.GetUtcNow();
        if (!IsConfigured)
            return LiveProviderResult.Fail(LiveProviderOutcome.NotConfigured, "Twitch client id/secret not set", at);
        if (logins.Count == 0)
            return LiveProviderResult.Ok([], at);

        var query = "streams?first=100&" + string.Join("&", logins.Select(l => "user_login=" + Uri.EscapeDataString(l)));
        var response = await GetAsync(query, cancellationToken);
        if (response.Outcome != LiveProviderOutcome.Ok)
            return LiveProviderResult.Fail(response.Outcome, response.Detail ?? response.Outcome.ToString(), at, response.RetryAfter);

        using var doc = response.Document!;
        await RefreshAvatarsAsync(logins, cancellationToken);
        return TwitchParser.ParseStreams(doc, logins, at, _avatars);
    }

    private async Task<LiveHttpResponse> GetAsync(string query, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await _tokens.GetAsync(_options.TokenUrl, _options.ClientId!, _options.ClientSecret!, _options.TimeoutSeconds, ct);
            if (token.Outcome != LiveProviderOutcome.Ok)
                return new(token.Outcome, null, token.Detail, token.RetryAfter);
            if (!await ValidateAsync(token.Token!, ct))
                continue; // revoked/expired: the next round fetches a fresh token

            var response = await _json.GetJsonAsync(_options.ApiBaseUrl.TrimEnd('/') + "/" + query, "Twitch", token.Token,
                [("Client-Id", _options.ClientId!)], _options.TimeoutSeconds, _options.MaxRetries, ct);
            if (response.Outcome != LiveProviderOutcome.AuthFailed || attempt > 0)
            {
                _tokens.Record(response.Outcome == LiveProviderOutcome.AuthFailed ? LiveProviderOutcome.AuthFailed : LiveProviderOutcome.Ok);
                return response;
            }

            _tokens.Invalidate(); // a 401 means the app token is no longer valid: fetch (and validate) a new one once
            _lastValidatedAt = null;
        }

        _tokens.Record(LiveProviderOutcome.AuthFailed);
        return new(LiveProviderOutcome.AuthFailed, null, "token rejected twice");
    }

    /// <summary>Twitch requires validating tokens at start and hourly. False = token invalid (it is dropped).</summary>
    private async Task<bool> ValidateAsync(string token, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_lastValidatedAt is { } last && now - last < ValidateEvery)
            return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.ValidateUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            using var response = await _auth.SendAsync(request, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Twitch app access token failed validation; requesting a new one");
                _tokens.Invalidate();
                _lastValidatedAt = null;
                return false;
            }

            if (response.IsSuccessStatusCode)
                _lastValidatedAt = now;
            return true; // a validation endpoint outage is not a reason to stop using a token Helix still accepts
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return true;
        }
    }

    private async Task RefreshAvatarsAsync(IReadOnlyCollection<string> logins, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (now < _nextAvatarRefresh && logins.All(_avatars.ContainsKey))
            return;
        var response = await GetAsync("users?" + string.Join("&", logins.Select(l => "login=" + Uri.EscapeDataString(l))), ct);
        if (response.Outcome != LiveProviderOutcome.Ok)
        {
            _nextAvatarRefresh = now + AvatarRetry; // decorative only: never fails the status answer
            return;
        }

        using var doc = response.Document!;
        foreach (var (login, url) in TwitchParser.ParseAvatars(doc))
            _avatars[login] = url;
        _nextAvatarRefresh = now + AvatarRefresh;
    }
}

public static class TwitchParser
{
    /// <summary>
    /// helix/streams: every requested login in <c>data</c> with type "live" is live; a requested login missing from a
    /// successful answer is offline (documented: only broadcasting channels are returned). An entry with another type
    /// (e.g. "" on a Twitch-side error) says nothing: no observation for it.
    /// </summary>
    public static LiveProviderResult ParseStreams(JsonDocument doc, IReadOnlyCollection<string> logins, DateTimeOffset at, IReadOnlyDictionary<string, string> avatars)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return LiveProviderResult.Fail(LiveProviderOutcome.SchemaError, "streams response without data array", at);

        var requested = logins.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observations = new List<LiveObservation>();
        var warnings = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            var login = LiveJson.Str(item, "user_login")?.ToLowerInvariant();
            if (login is null || !requested.Contains(login))
                continue;
            seen.Add(login);
            if (LiveJson.Str(item, "type") != "live")
            {
                warnings.Add(login + ": stream type not live");
                continue;
            }

            observations.Add(new LiveObservation(LivePlatform.Twitch, login, ObservationKind.Status, IsLive: true, at,
                StreamId: LiveJson.Id(item, "id"), StartedAt: LiveJson.Instant(item, "started_at"),
                Title: LiveJson.Str(item, "title"), Category: LiveJson.Str(item, "game_name") is { Length: > 0 } game ? game : null,
                AvatarUrl: avatars.GetValueOrDefault(login)));
        }

        foreach (var login in requested.Where(l => !seen.Contains(l)))
            observations.Add(new LiveObservation(LivePlatform.Twitch, login, ObservationKind.Status, IsLive: false, at, AvatarUrl: avatars.GetValueOrDefault(login)));
        return LiveProviderResult.Ok(observations, at, warnings);
    }

    public static IEnumerable<(string Login, string Url)> ParseAvatars(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in data.EnumerateArray())
        {
            if (LiveJson.Str(item, "login")?.ToLowerInvariant() is { } login && LiveJson.Str(item, "profile_image_url") is { Length: > 0 } url)
                yield return (login, url);
        }
    }
}
