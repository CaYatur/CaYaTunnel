using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CaYaTunnel.Ui;

/// <summary>
/// Runs an application's teardown without letting it trap the process.
/// <para>
/// Exiting is the one operation a user cannot work around. Shutdown work therefore runs on a
/// worker thread rather than the UI thread — so a continuation that wants the UI thread can
/// still get it — and it runs under a deadline, so a socket that refuses to close politely ends
/// the process anyway. Losing a graceful goodbye is a far smaller problem than an application
/// that cannot be closed.
/// </para>
/// </summary>
public static class ShutdownGuard
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Returns true when teardown finished in time, false when it was abandoned.</summary>
    public static bool Run(Func<Task> shutdown, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(shutdown);

        var completed = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            try
            {
                shutdown().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Already torn down; nothing left to close.
            }
            catch (Exception)
            {
                // A failure while closing must not stop the process from closing.
            }
            finally
            {
                completed.Set();
            }
        })
        {
            IsBackground = true,
            Name = "CaYaTunnel shutdown",
        };

        worker.Start();

        var finished = completed.Wait(timeout ?? DefaultTimeout);
        completed.Dispose();
        return finished;
    }
}
