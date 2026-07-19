using System.Collections.Concurrent;

namespace NINA.Headless.Services;

/// <summary>
/// Bounded in-memory capture of recent log lines so the diagnostics bundle can
/// include "what just happened" regardless of where stdout goes (systemd
/// journal in production, a nohup file in dev, nothing at all). Keeps the last
/// <see cref="Capacity"/> formatted lines; oldest drop first.
/// </summary>
public sealed class RingBufferLog : ILoggerProvider
{
    public const int Capacity = 2500;

    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Snapshot()
    {
        return _lines.ToArray();
    }

    public void Append(string line)
    {
        _lines.Enqueue(line);
        while (_lines.Count > Capacity && _lines.TryDequeue(out _)) { }
    }

    public ILogger CreateLogger(string categoryName) => new RingLogger(this, categoryName);

    public void Dispose() { }

    private sealed class RingLogger : ILogger
    {
        private readonly RingBufferLog _owner;
        private readonly string _category;

        public RingLogger(RingBufferLog owner, string category)
        {
            _owner = owner;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{logLevel switch
            {
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => logLevel.ToString()
            }}] {Shorten(_category)}: {formatter(state, exception)}";
            if (exception != null) line += $" | {exception.GetType().Name}: {exception.Message}";
            _owner.Append(line);
        }

        private static string Shorten(string category)
        {
            var idx = category.LastIndexOf('.');
            return idx >= 0 ? category[(idx + 1)..] : category;
        }
    }
}
