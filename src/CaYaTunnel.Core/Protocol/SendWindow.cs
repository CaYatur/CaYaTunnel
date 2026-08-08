namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// Credit-based send window for one direction of one stream.
/// <para>
/// This is what keeps a single busy stream from starving the others. The link's read loop
/// must never block, so inbound data is always accepted into a buffer; the sender is instead
/// held back by credits that the receiver only returns once the application has actually
/// consumed the bytes. Without this, one large download would grow an unbounded buffer and
/// stall every other tunnel sharing the link.
/// </para>
/// </summary>
internal sealed class SendWindow(int initialCredit)
{
    private readonly Lock _gate = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private int _available = initialCredit;
    private Exception? _fault;

    public int Available
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    /// <summary>
    /// Waits until at least one byte of credit is free and returns how much was granted
    /// (never more than <paramref name="desired"/>). Returning a partial grant rather than
    /// insisting on the full amount is what stops a write larger than the whole window from
    /// deadlocking.
    /// </summary>
    public async ValueTask<int> AcquireAsync(int desired, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desired);

        while (true)
        {
            TaskCompletionSource waiter;
            lock (_gate)
            {
                if (_fault is not null)
                {
                    throw _fault;
                }

                if (_available > 0)
                {
                    var granted = Math.Min(desired, _available);
                    _available -= granted;
                    return granted;
                }

                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
            }

            await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns credit the peer has released via a WindowUpdate frame.</summary>
    public void Add(int credit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(credit);

        List<TaskCompletionSource>? wake = null;
        lock (_gate)
        {
            if (_fault is not null)
            {
                return;
            }

            _available += credit;
            while (_waiters.Count > 0)
            {
                (wake ??= []).Add(_waiters.Dequeue());
            }
        }

        if (wake is null)
        {
            return;
        }

        foreach (var waiter in wake)
        {
            waiter.TrySetResult();
        }
    }

    /// <summary>Aborts every waiter — used when the stream or the whole link dies.</summary>
    public void Fault(Exception exception)
    {
        List<TaskCompletionSource>? wake = null;
        lock (_gate)
        {
            _fault ??= exception;
            while (_waiters.Count > 0)
            {
                (wake ??= []).Add(_waiters.Dequeue());
            }
        }

        if (wake is null)
        {
            return;
        }

        foreach (var waiter in wake)
        {
            waiter.TrySetException(exception);
        }
    }
}
