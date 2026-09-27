using System.Net;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>
/// Downloads one avatar image for a quote card through the shared <see cref="IHttpClientFactory"/>. Only Discord CDN urls
/// that Discord itself returned for the author are fetched — never a url typed by a user — and only over https. Bounded in
/// time and size; any failure returns null and the card is drawn with the neutral fallback instead of failing the command.
/// </summary>
public sealed partial class QuoteAvatarClient(IHttpClientFactory http, TimeProvider clock, ILogger<QuoteAvatarClient> logger)
{
    public const string HttpClientName = "quote-avatar";

    /// <summary>Discord avatars are at most 1024 px; a 1024 px PNG is well under this.</summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Hosts Discord serves user, member and default avatars from.</summary>
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.Ordinal) { "cdn.discordapp.com", "media.discordapp.net" };

    public static bool IsAllowed(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && AllowedHosts.Contains(uri.IdnHost.ToLowerInvariant());

    public async Task<byte[]?> DownloadAsync(string? url, CancellationToken cancellationToken)
    {
        if (!IsAllowed(url))
        {
            if (url is not null)
                LogRefused(logger);
            return null;
        }

        using var deadline = new CancellationTokenSource(Timeout, clock);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            using var response = await http.CreateClient(HttpClientName)
                .GetAsync(new Uri(url!), HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                LogFailed(logger, "HTTP " + (int)response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                LogFailed(logger, "response too large");
                return null;
            }

            // Read at most MaxBytes + 1 whatever Content-Length claimed (it may be missing or wrong).
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    LogFailed(logger, "response too large");
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.Length == 0 ? null : buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, "timeout");
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogFailed(logger, ex.HttpRequestError.ToString());
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Quote avatar not fetched: the url is not an https Discord CDN url")]
    private static partial void LogRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Quote avatar download failed ({Reason}); using the fallback")]
    private static partial void LogFailed(ILogger logger, string reason);
}
