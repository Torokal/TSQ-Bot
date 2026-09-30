using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ToroSquad.Tests.Support;

/// <summary>
/// Records every formatted log line, every structured value and every exception (with its message) — for privacy tests
/// that assert what must never reach a log.
/// </summary>
public sealed class CapturingLoggers
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger<T> For<T>() => new Logger<T>(this);

    private sealed class Logger<T>(CapturingLoggers owner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            owner.Lines.Enqueue(formatter(state, exception));
            if (exception is not null)
                owner.Lines.Enqueue(exception.ToString());
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (_, value) in values)
                    owner.Lines.Enqueue(value?.ToString() ?? "");
            }
        }
    }
}
