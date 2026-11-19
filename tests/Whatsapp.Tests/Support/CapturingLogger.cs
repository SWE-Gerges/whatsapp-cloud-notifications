using Microsoft.Extensions.Logging;

namespace Whatsapp.Tests.Support;

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. _messages];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
        lock (_gate)
        {
            _messages.Add(text);
        }
    }
}
