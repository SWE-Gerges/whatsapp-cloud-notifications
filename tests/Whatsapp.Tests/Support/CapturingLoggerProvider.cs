using Microsoft.Extensions.Logging;

namespace Whatsapp.Tests.Support;

internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>Collects every log entry of every category, so a test can assert on what a component wrote (or did not).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public IEnumerable<LogEntry> AtLeast(LogLevel level) => Entries.Where(e => e.Level >= level);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
    }
}
