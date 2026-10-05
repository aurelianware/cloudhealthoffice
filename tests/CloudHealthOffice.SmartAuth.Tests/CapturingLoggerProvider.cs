using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.SmartAuth.Tests;

/// <summary>Records every formatted log line with its category.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Category, string Message)> _entries = new();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public IReadOnlyList<string> Lines(string category)
        => _entries.Where(e => e.Category == category).Select(e => e.Message).ToList();

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<(string, string)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => entries.Enqueue((category, formatter(state, exception)));
    }
}
