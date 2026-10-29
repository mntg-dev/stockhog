namespace Stockhog.Presentation.Logging;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Source, string Message);

/// <summary>
/// Recent log entries for the in-app log view. The host's logger appends from any thread;
/// the buffer keeps the newest <see cref="Capacity"/> entries.
/// </summary>
public sealed class LogFeed
{
    public const int Capacity = 2000;

    private readonly Queue<LogEntry> _entries = new(Capacity);
    private readonly Lock _lock = new();

    /// <summary>Raised on the logging thread after each append.</summary>
    public event EventHandler? Appended;

    public void Append(LogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == Capacity)
                _entries.Dequeue();

            _entries.Enqueue(entry);
        }

        Appended?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        Appended?.Invoke(this, EventArgs.Empty);
    }
}
