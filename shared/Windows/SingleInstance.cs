using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CaYaTunnel.Ui;

/// <summary>
/// Keeps one copy of an application running, and hands the second launch's job to the first.
/// <para>
/// Two instances sharing the same state is the actual harm: two gateways fight over the same
/// listening ports, and two agents fight over the same device identity — the server evicts one
/// link as the other reconnects, and they flap. The lock is therefore keyed on the state each
/// instance owns rather than on the executable, so two portable clients in separate folders,
/// pointed at different gateways, remain a legitimate and working setup.
/// </para>
/// <para>
/// A second launch signals the first and exits. The first brings its window forward, which is
/// what a user double-clicking the icon again actually wants.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>
    /// Keys already held by this process.
    /// <para>
    /// A Windows mutex is recursive for the thread that owns it, so a second claim from the same
    /// thread would succeed and quietly hand out two "exclusive" locks. The cross-process mutex
    /// cannot see that; this can.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> HeldLocally = [];

    private static readonly Lock LocalGate = new();

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signal;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _listener;
    private readonly string _id;

    private SingleInstance(string id, Mutex mutex, EventWaitHandle signal)
    {
        _id = id;
        _mutex = mutex;
        _signal = signal;

        _listener = new Thread(WaitForSecondInstance)
        {
            IsBackground = true,
            Name = "CaYaTunnel single-instance listener",
        };

        _listener.Start();
    }

    /// <summary>Raised on a background thread when another launch was blocked. Marshal to the UI.</summary>
    public event Action? SecondInstanceAttempted;

    /// <summary>
    /// Claims the lock for <paramref name="key"/>. Returns false when another instance already
    /// holds it, having first signalled that instance to come forward.
    /// </summary>
    public static bool TryClaim(string key, out SingleInstance? instance)
    {
        instance = null;

        // Names are hashed: the key is a file path, and a path contains backslashes, which are
        // namespace separators in kernel object names.
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

        lock (LocalGate)
        {
            if (HeldLocally.Contains(id))
            {
                // Refused, but still announced: a blocked launch always tells the holder to come
                // forward, whichever process it happened in.
                SignalHolder(id);
                return false;
            }
        }

        // Local\ rather than Global\: the lock only needs to hold within a logon session, and
        // Global\ objects need privileges that an ordinary user launch does not have.
        var mutex = new Mutex(initiallyOwned: false, $"Local\\CaYaTunnel-{id}");
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\CaYaTunnel-signal-{id}");

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing — the lock is ours, and the state it
            // owned is free.
            acquired = true;
        }

        if (!acquired)
        {
            signal.Set();
            signal.Dispose();
            mutex.Dispose();
            return false;
        }

        lock (LocalGate)
        {
            HeldLocally.Add(id);
        }

        instance = new SingleInstance(id, mutex, signal);
        return true;
    }

    private static void SignalHolder(string id)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting($"Local\\CaYaTunnel-signal-{id}");
            signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            // The holder is gone or belongs to another user; nothing to bring forward.
        }
    }

    private void WaitForSecondInstance()
    {
        var handles = new WaitHandle[] { _signal, _shutdown.Token.WaitHandle };

        while (!_shutdown.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) == 0)
            {
                SecondInstanceAttempted?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        lock (LocalGate)
        {
            HeldLocally.Remove(_id);
        }

        _shutdown.Cancel();

        try
        {
            _listener.Join(TimeSpan.FromSeconds(1));
        }
        catch (ThreadStateException)
        {
            // Never started; nothing to wait for.
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception ex) when (ex is ApplicationException or ObjectDisposedException)
        {
            // Not held, or already gone.
        }

        _mutex.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }
}
