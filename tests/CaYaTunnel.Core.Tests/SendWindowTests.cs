using CaYaTunnel.Core.Protocol;
using Xunit;

namespace CaYaTunnel.Tests;

public class SendWindowTests
{
    [Fact]
    public async Task Grants_up_to_the_requested_amount()
    {
        var window = new SendWindow(1000);

        Assert.Equal(400, await window.AcquireAsync(400, default));
        Assert.Equal(600, window.Available);
    }

    [Fact]
    public async Task Grants_partially_rather_than_deadlocking_on_an_oversized_request()
    {
        var window = new SendWindow(100);

        // Asking for more than the whole window must not block — it hands back what it has.
        var granted = await window.AcquireAsync(5000, default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(100, granted);
        Assert.Equal(0, window.Available);
    }

    [Fact]
    public async Task Blocks_at_zero_credit_and_resumes_when_the_peer_returns_some()
    {
        var window = new SendWindow(0);

        var pending = window.AcquireAsync(500, default).AsTask();
        Assert.False(pending.IsCompleted);

        window.Add(120);

        Assert.Equal(120, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Every_waiter_is_released_when_credit_arrives()
    {
        var window = new SendWindow(0);

        var first = window.AcquireAsync(50, default).AsTask();
        var second = window.AcquireAsync(50, default).AsTask();

        window.Add(100);

        var granted = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, granted.Sum());
    }

    [Fact]
    public async Task Faulting_aborts_waiters_and_later_acquisitions()
    {
        var window = new SendWindow(0);
        var pending = window.AcquireAsync(10, default).AsTask();

        window.Fault(new IOException("link died"));

        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<IOException>(async () => await window.AcquireAsync(10, default));
    }

    [Fact]
    public async Task Cancellation_releases_the_caller_without_faulting_the_window()
    {
        var window = new SendWindow(0);
        using var cts = new CancellationTokenSource();

        var pending = window.AcquireAsync(10, cts.Token).AsTask();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        window.Add(64);
        Assert.Equal(64, await window.AcquireAsync(64, default));
    }
}
