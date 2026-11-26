using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EndToEnd.Tests;

/// <summary>Keeps what a hosted app logs at Warning or above, as "Level: message", for tests to check.</summary>
internal sealed class LogLines : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
            }
        }
    }
}
