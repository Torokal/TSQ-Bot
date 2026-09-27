using Microsoft.Extensions.Logging;
using ToroSquad.Core;

namespace ToroSquad.Modules.Quote.Application;

/// <summary>The PNG to post, or the trace code of a failure the user can quote to an admin.</summary>
public sealed record QuoteCardResult(QuoteCard? Card, string? TraceCode)
{
    public static QuoteCardResult Ok(QuoteCard card) => new(card, null);

    public static QuoteCardResult Failed(string traceCode) => new(null, traceCode);
}

/// <summary>
/// Resolved message → PNG, within the request: download the avatar, draw, hand back the bytes. Nothing is kept — the text,
/// names and avatar bytes live only in this call. A drawing failure is logged by ids, exception type and trace code only:
/// an exception message could repeat the quoted text, so it never reaches the log or the user.
/// </summary>
public sealed partial class QuoteCardBuilder(QuoteAvatarClient avatars, IQuoteRenderer renderer, ILogger<QuoteCardBuilder> logger)
{
    public async Task<QuoteCardResult> BuildAsync(QuoteSourceMessage message, string text, CancellationToken cancellationToken)
    {
        var avatar = await avatars.DownloadAsync(message.Author.AvatarUrl, cancellationToken);
        try
        {
            return QuoteCardResult.Ok(renderer.Render(new QuoteRenderModel(text, message.Author.DisplayName, message.Author.Username, avatar)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var trace = TraceCodes.New();
            LogRenderFailed(logger, trace, ex.GetType().FullName ?? "?", ex.TargetSite?.Name ?? "?", message.Channel.Value, message.Id.Value);
            return QuoteCardResult.Failed(trace);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Quote card rendering failed [{TraceCode}] {ExceptionType} in {Method} channel={Channel} message={Message}")]
    private static partial void LogRenderFailed(ILogger logger, string traceCode, string exceptionType, string method, ulong channel, ulong message);
}
