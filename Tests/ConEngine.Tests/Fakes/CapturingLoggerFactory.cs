using Microsoft.Extensions.Logging;

namespace ConEngine.Tests.Fakes;

/// <summary>An ILoggerFactory whose every logger writes to one shared, thread-safe list.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly CapturingLogger _logger = new();

    public List<(LogLevel Level, string Message)> Entries => _logger.Entries;

    public ILogger CreateLogger(string categoryName) => _logger;
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
