using System.Collections.Concurrent;

namespace CaYaTunnel.Server.Gateway;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString()
        => $"{Timestamp.ToLocalTime():HH:mm:ss} [{Level.ToString().ToUpperInvariant()[..4]}] {Category}: {Message}";
}

/// <summary>
/// In-memory log with a bounded backlog, plus an optional daily file. The admin UI subscribes to
/// <see cref="Entry"/> for a live view and reads <see cref="Recent"/> to fill the pane on open.
/// </summary>
public sealed class GatewayLog
{
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly Lock _fileGate = new();
    private readonly int _capacity;
    private readonly string? _directory;

    public GatewayLog(string? logDirectory = null, int capacity = 2000)
    {
        _directory = logDirectory;
        _capacity = capacity;
    }

    public event Action<LogEntry>? Entry;

    /// <summary>Suppresses Debug entries unless the operator turns them on.</summary>
    public bool VerboseEnabled { get; set; }

    public IReadOnlyList<LogEntry> Recent => [.. _recent];

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public void Info(string category, string message) => Write(LogLevel.Info, category, message);

    public void Warning(string category, string message) => Write(LogLevel.Warning, category, message);

    public void Error(string category, string message) => Write(LogLevel.Error, category, message);

    public void Error(string category, string message, Exception exception)
        => Write(LogLevel.Error, category, $"{message} — {exception.GetType().Name}: {exception.Message}");

    private void Write(LogLevel level, string category, string message)
    {
        if (level == LogLevel.Debug && !VerboseEnabled)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);

        _recent.Enqueue(entry);
        while (_recent.Count > _capacity && _recent.TryDequeue(out _))
        {
            // Trim to the cap.
        }

        Entry?.Invoke(entry);
        AppendToFile(entry);
    }

    private void AppendToFile(LogEntry entry)
    {
        if (string.IsNullOrEmpty(_directory))
        {
            return;
        }

        try
        {
            lock (_fileGate)
            {
                Directory.CreateDirectory(_directory);
                var path = Path.Combine(_directory, $"gateway-{DateTimeOffset.Now:yyyy-MM-dd}.log");
                File.AppendAllText(path, entry + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never be the thing that takes the gateway down.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
