using CaYaTunnel.Server.Gateway;

namespace CaYaTunnel.Server.App;

/// <summary>
/// Sample activity for screenshots. Lives here rather than in the shared preview data because
/// <see cref="LogEntry"/> belongs to the gateway, which the client never references.
/// </summary>
public static class ServerPreview
{
    public static IEnumerable<LogEntry> SampleLog()
    {
        var now = DateTimeOffset.Now;

        yield return new LogEntry(now.AddSeconds(-3), LogLevel.Info, "route", "88.230.14.7 -> 'Valheim' flow started.");
        yield return new LogEntry(now.AddSeconds(-21), LogLevel.Info, "tunnel", "'CAGAN-PC' created TCP + UDP tunnel :2456 -> 127.0.0.1:2456.");
        yield return new LogEntry(now.AddMinutes(-4), LogLevel.Info, "dns", "Created a record for dev.tunnel.example.com.");
        yield return new LogEntry(now.AddMinutes(-4), LogLevel.Info, "tunnel", "'TUF-A16' created https tunnel dev.tunnel.example.com -> 127.0.0.1:5173.");
        yield return new LogEntry(now.AddMinutes(-42), LogLevel.Info, "session", "'TUF-A16' connected from 88.230.14.7.");
        yield return new LogEntry(now.AddHours(-6), LogLevel.Info, "session", "'CAGAN-PC' connected from 88.230.14.7.");
        yield return new LogEntry(now.AddHours(-6), LogLevel.Info, "gateway", "Listening for clients on 0.0.0.0:48771.");
    }
}
