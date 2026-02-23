namespace MsDos.Core.Platform;

/// <summary>
/// Log severity levels for emulator messages.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// Represents a log entry produced by the emulator.
/// </summary>
public readonly struct LogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Source { get; init; }
    public string Message { get; init; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] [{Level}] {Source}: {Message}";
}

/// <summary>
/// Centralized logging for the DOS emulator. Fires events so that
/// frontends (WinForms, Blazor) can display messages in their UI.
/// Can also record a circular buffer of recent entries for diagnostics.
/// </summary>
public sealed class EmulatorLog
{
    private readonly List<LogEntry> _entries = new();
    private readonly int _maxEntries;

    /// <summary>Fires whenever a new log entry is added.</summary>
    public event Action<LogEntry>? EntryAdded;

    /// <summary>Current minimum level. Messages below this are discarded.</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    /// <summary>When true, CPU instruction traces are logged at Trace level.</summary>
    public bool TraceInstructions { get; set; }

    public EmulatorLog(int maxEntries = 500)
    {
        _maxEntries = maxEntries;
    }

    public void Log(LogLevel level, string source, string message)
    {
        if (level < MinLevel) return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Source = source,
            Message = message
        };

        lock (_entries)
        {
            _entries.Add(entry);
            if (_entries.Count > _maxEntries)
                _entries.RemoveAt(0);
        }

        EntryAdded?.Invoke(entry);
    }

    public void Trace(string source, string message) => Log(LogLevel.Trace, source, message);
    public void Debug(string source, string message) => Log(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Log(LogLevel.Info, source, message);
    public void Warn(string source, string message) => Log(LogLevel.Warning, source, message);
    public void Error(string source, string message) => Log(LogLevel.Error, source, message);

    /// <summary>Get a snapshot of recent log entries.</summary>
    public IReadOnlyList<LogEntry> GetEntries()
    {
        lock (_entries)
            return _entries.ToList();
    }

    /// <summary>Clear all stored entries.</summary>
    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
    }
}
