using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace PersonalRepresentativeService.Tests.Fakes;

/// <summary>One captured log entry.</summary>
public sealed record CapturedLog(string Category, LogLevel Level, EventId EventId, string Message);

/// <summary>Captures every log entry written through the host's logging.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();

    public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void Dispose() { }

    private sealed class Logger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedLog> _entries;

        public Logger(string category, ConcurrentQueue<CapturedLog> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(new CapturedLog(_category, logLevel, eventId, formatter(state, exception)));
    }
}
