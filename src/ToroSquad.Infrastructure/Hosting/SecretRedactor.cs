using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace ToroSquad.Infrastructure.Hosting;

/// <summary>
/// Removes secrets from any text that leaves the process (logs, doctor output, error details).
/// Knows the configured secret values and also pattern-matches Discord bot tokens / Apikey headers.
/// </summary>
public sealed partial class SecretRedactor
{
    public const string Mask = "***REDACTED***";
    private readonly string[] _secrets;

    public SecretRedactor(IEnumerable<string?> secretValues)
    {
        _secrets = secretValues
            .Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 8)
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;
        foreach (var secret in _secrets)
            text = text.Replace(secret, Mask, StringComparison.Ordinal);
        text = DiscordTokenPattern().Replace(text, Mask);
        text = ApiKeyHeaderPattern().Replace(text, "Apikey " + Mask);
        text = BotAuthHeaderPattern().Replace(text, "Bot " + Mask);
        return text;
    }

    /// <summary>Discord bot token shape: base64(user id).timestamp.hmac</summary>
    [GeneratedRegex(@"[MNO][A-Za-z\d_-]{22,30}\.[A-Za-z\d_-]{5,8}\.[A-Za-z\d_-]{25,45}", RegexOptions.CultureInvariant)]
    public static partial Regex DiscordTokenPattern();

    [GeneratedRegex(@"Apikey\s+[A-Za-z0-9_\-\.]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyHeaderPattern();

    [GeneratedRegex(@"Bot\s+[A-Za-z0-9_\-\.]{30,}", RegexOptions.CultureInvariant)]
    private static partial Regex BotAuthHeaderPattern();
}

/// <summary>Console log format with secret redaction applied to message and exception text.</summary>
public sealed class RedactingConsoleFormatter(SecretRedactor redactor) : ConsoleFormatter(Name)
{
    public new const string Name = "torosquad";

    public override void Write<TState>(in LogEntry<TState> logEntry, Microsoft.Extensions.Logging.IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
            return;
        var level = logEntry.LogLevel switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "none",
        };
        textWriter.Write(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        textWriter.Write(' ');
        textWriter.Write(level);
        textWriter.Write(' ');
        textWriter.Write(logEntry.Category);
        textWriter.Write(": ");
        textWriter.WriteLine(redactor.Redact(message));
        if (logEntry.Exception is not null)
            textWriter.WriteLine(redactor.Redact(logEntry.Exception.ToString()));
    }
}

public sealed class RedactingConsoleFormatterOptions : ConsoleFormatterOptions;
