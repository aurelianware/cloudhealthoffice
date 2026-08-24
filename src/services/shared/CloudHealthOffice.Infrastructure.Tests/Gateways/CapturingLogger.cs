using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Tests.Gateways;

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that captures fully-rendered log messages
/// (message + state values) so tests can assert on what would actually be
/// written to a log sink.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // formatter renders the template with all structured values inlined,
        // which is exactly what a text sink would emit.
        Messages.Enqueue(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
