using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToroSquad.Modules.Live.Domain;

namespace ToroSquad.Modules.Live.Providers.Kick;

/// <summary>
/// Section "Live:Kick". <see cref="ClientId"/>/<see cref="ClientSecret"/> belong to the operator's Kick developer app
/// (client credentials / app access token). Secrets: TOROSQUAD_Live__Kick__ClientId / __ClientSecret or user-secrets only.
/// Verified 2026-09-26 (docs.kick.com + api.kick.com/swagger/doc.yaml): token POST https://id.kick.com/oauth/token
/// (grant_type=client_credentials); GET /public/v1/channels with repeated slug (≤50) returns stream_title, category and a
/// stream object (is_live, start_time); GET /public/v1/users with repeated id returns profile_picture. Kick documents no
/// general rate limit numbers.
/// </summary>
public sealed class KickOptions
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string ApiBaseUrl { get; set; } = "https://api.kick.com/public/v1/";
    public string TokenUrl { get; set; } = "https://id.kick.com/oauth/token";
    public int TimeoutSeconds { get; set; } = 15;
    public int MaxRetries { get; set; } = 1;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public string? Problem()
    {
        foreach (var url in new[] { ApiBaseUrl, TokenUrl })
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return "Live:Kick:ApiBaseUrl/TokenUrl must be https URLs";
        }

        if (TimeoutSeconds is < 3 or > 60 || MaxRetries is < 0 or > 3)
            return "Live:Kick:TimeoutSeconds must be 3..60 and MaxRetries 0..3";
        return null;
    }
}

/// <summary>
/// Kick through the official Public API only (kick.com pages are never scraped): one batched <c>GET channels?slug=…</c> per
/// reconciliation. Only an explicit <c>stream.is_live</c> true/false is a live/offline statement; a missing/null stream,
/// a non-boolean is_live or a slug missing from the answer is UNKNOWN (warning, no observation). Kick's push transport is
/// webhooks only (livestream.status.updated / livestream.metadata.updated, delivered to a public HTTPS callback). The
/// current TSQ Bot deployment exposes no HTTP callback endpoint (a generic host worker, no web server), so webhooks are
/// deferred for V1 — not a Railway limitation — and status and title changes come from this reconciliation.
/// </summary>
public sealed class KickStatusProvider : ILiveStatusProvider
{
    public const string HttpClientName = "live-kick";
    public const string AuthHttpClientName = "live-kick-auth";
    private static readonly TimeSpan AvatarRefresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan AvatarRetry = TimeSpan.FromMinutes(30);

    private readonly KickOptions _options;
    private readonly LiveHttp _json;
    private readonly ClientCredentialsTokens _tokens;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, string> _avatars = new(StringComparer.Ordinal);
    private DateTimeOffset _nextAvatarRefresh = DateTimeOffset.MinValue;

    public KickStatusProvider(IHttpClientFactory http, IOptions<KickOptions> options, TimeProvider clock, ILogger<KickStatusProvider> logger)
    {
        _options = options.Value;
        _json = new LiveHttp(http.CreateClient(HttpClientName), clock, logger);
        _tokens = new ClientCredentialsTokens(http.CreateClient(AuthHttpClientName), "Kick", clock, logger);
        _clock = clock;
    }

    public LivePlatform Platform => LivePlatform.Kick;

    public bool IsConfigured => _options.HasCredentials;

    public LiveAuthState Auth => new(_tokens.LastOutcome, _tokens.ValidUntil, null);

    public async Task<LiveProviderResult> GetStatusAsync(IReadOnlyCollection<string> logins, CancellationToken cancellationToken)
    {
        var at = _clock.GetUtcNow();
        if (!IsConfigured)
            return LiveProviderResult.Fail(LiveProviderOutcome.NotConfigured, "Kick client id/secret not set", at);
        if (logins.Count == 0)
            return LiveProviderResult.Ok([], at);

        var response = await GetAsync("channels?" + string.Join("&", logins.Select(l => "slug=" + Uri.EscapeDataString(l))), cancellationToken);
        if (response.Outcome != LiveProviderOutcome.Ok)
            return LiveProviderResult.Fail(response.Outcome, response.Detail ?? response.Outcome.ToString(), at, response.RetryAfter);

        using var doc = response.Document!;
        var userIds = KickParser.UserIds(doc);
        await RefreshAvatarsAsync(userIds, cancellationToken);
        return KickParser.ParseChannels(doc, logins, at, _avatars);
    }

    private async Task<LiveHttpResponse> GetAsync(string query, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokens.GetAsync(_options.TokenUrl, _options.ClientId!, _options.ClientSecret!, _options.TimeoutSeconds, ct);
            if (token.Outcome != LiveProviderOutcome.Ok)
                return new(token.Outcome, null, token.Detail, token.RetryAfter);
            var response = await _json.GetJsonAsync(_options.ApiBaseUrl.TrimEnd('/') + "/" + query, "Kick", token.Token, [], _options.TimeoutSeconds, _options.MaxRetries, ct);
            if (response.Outcome != LiveProviderOutcome.AuthFailed || attempt > 0)
            {
                _tokens.Record(response.Outcome == LiveProviderOutcome.AuthFailed ? LiveProviderOutcome.AuthFailed : LiveProviderOutcome.Ok);
                return response;
            }

            _tokens.Invalidate(); // a 401 means the app token is no longer valid: fetch a new one once
        }
    }

    /// <summary>Profile pictures by broadcaster user id (login → url). Decorative: failures never fail the status answer.</summary>
    private async Task RefreshAvatarsAsync(IReadOnlyDictionary<string, string> userIds, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (userIds.Count == 0 || (now < _nextAvatarRefresh && userIds.Keys.All(_avatars.ContainsKey)))
            return;
        var response = await GetAsync("users?" + string.Join("&", userIds.Values.Select(id => "id=" + Uri.EscapeDataString(id))), ct);
        if (response.Outcome != LiveProviderOutcome.Ok)
        {
            _nextAvatarRefresh = now + AvatarRetry;
            return;
        }

        using var doc = response.Document!;
        var byId = KickParser.ParseAvatars(doc);
        foreach (var (login, id) in userIds)
        {
            if (byId.TryGetValue(id, out var url))
                _avatars[login] = url;
        }

        _nextAvatarRefresh = now + AvatarRefresh;
    }
}

public static class KickParser
{
    /// <summary>
    /// /channels: a requested slug in <c>data</c> is live when <c>stream.is_live</c> is true (title = stream_title, category
    /// name, start = stream.start_time) and offline only when <c>stream.is_live</c> is explicitly false. A slug missing from a
    /// successful answer, a null/missing stream or a non-boolean is_live is not described (warning, no observation) — never
    /// "offline", so a malformed answer can never end a live session.
    /// </summary>
    public static LiveProviderResult ParseChannels(JsonDocument doc, IReadOnlyCollection<string> logins, DateTimeOffset at, IReadOnlyDictionary<string, string> avatars)
    {
        if (!TryData(doc, out var data))
            return LiveProviderResult.Fail(LiveProviderOutcome.SchemaError, "channels response without data array", at);

        var requested = logins.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var observations = new List<LiveObservation>();
        var warnings = new List<string>();
        foreach (var item in data.EnumerateArray())
        {
            var slug = LiveJson.Str(item, "slug")?.ToLowerInvariant();
            if (slug is null || !requested.Contains(slug) || !seen.Add(slug))
                continue;
            var avatar = avatars.GetValueOrDefault(slug);
            var stream = LiveJson.Obj(item, "stream");
            switch (stream is { } s ? LiveJson.Bool(s, "is_live") : null)
            {
                case false:
                    // Only an explicit "is_live": false is offline.
                    observations.Add(new LiveObservation(LivePlatform.Kick, slug, ObservationKind.Status, IsLive: false, at, AvatarUrl: avatar));
                    continue;
                case null:
                    // stream null/missing or is_live missing/not a boolean: ambiguous → unknown (a malformed answer never
                    // ends a live session). Surfaced as a provider warning (doctor + log).
                    warnings.Add(slug + ": stream.is_live not stated");
                    continue;
            }

            var category = LiveJson.Obj(item, "category") is { } c ? LiveJson.Str(c, "name") : null;
            observations.Add(new LiveObservation(LivePlatform.Kick, slug, ObservationKind.Status, IsLive: true, at,
                StartedAt: LiveJson.Instant(stream!.Value, "start_time"), Title: LiveJson.Str(item, "stream_title"),
                Category: string.IsNullOrWhiteSpace(category) ? null : category, AvatarUrl: avatar));
        }

        warnings.AddRange(requested.Where(l => !seen.Contains(l)).Select(l => l + ": channel not in the answer (unknown slug?)"));
        return LiveProviderResult.Ok(observations, at, warnings);
    }

    /// <summary>login → broadcaster_user_id from a channels answer.</summary>
    public static IReadOnlyDictionary<string, string> UserIds(JsonDocument doc)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryData(doc, out var data))
            return result;
        foreach (var item in data.EnumerateArray())
        {
            if (LiveJson.Str(item, "slug")?.ToLowerInvariant() is { } slug && LiveJson.Id(item, "broadcaster_user_id") is { Length: > 0 } id)
                result[slug] = id;
        }

        return result;
    }

    /// <summary>user id → profile_picture from a users answer.</summary>
    public static IReadOnlyDictionary<string, string> ParseAvatars(JsonDocument doc)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryData(doc, out var data))
            return result;
        foreach (var item in data.EnumerateArray())
        {
            if (LiveJson.Id(item, "user_id") is { Length: > 0 } id && LiveJson.Str(item, "profile_picture") is { Length: > 0 } url)
                result[id] = url;
        }

        return result;
    }

    private static bool TryData(JsonDocument doc, out JsonElement data)
    {
        data = default;
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array;
    }
}
