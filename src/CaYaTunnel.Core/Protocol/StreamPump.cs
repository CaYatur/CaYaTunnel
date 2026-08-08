using System.Buffers;
using System.Net.Sockets;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// Copies bytes both ways between a multiplexed stream and a real socket, and vice versa.
/// Both the gateway (public visitor &lt;-&gt; mux) and the agent (mux &lt;-&gt; local service) use this,
/// so half-close and accounting behave identically at both ends.
/// </summary>
public static class StreamPump
{
    /// <summary>
    /// Runs until both directions finish. Returns bytes copied
    /// (<c>ToTarget</c> = mux -&gt; target, <c>FromTarget</c> = target -&gt; mux).
    /// </summary>
    public static async Task<(long ToTarget, long FromTarget)> RunAsync(
        MuxStream mux,
        Stream target,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var toTarget = CopyAsync(mux, target, linked.Token);
        var fromTarget = CopyAsync(target, mux, linked.Token);

        // Wait for the first direction to end, then give the other a moment to drain rather
        // than cutting a half-closed connection short (HTTP responses arrive after the request
        // body ends; a Minecraft client stops sending long before the server stops talking).
        var first = await Task.WhenAny(toTarget, fromTarget).ConfigureAwait(false);
        _ = first; // surfaced below via the awaits

        try
        {
            await Task.WhenAll(toTarget, fromTarget).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            await linked.CancelAsync().ConfigureAwait(false);
        }

        return (SafeResult(toTarget), SafeResult(fromTarget));
    }

    private static long SafeResult(Task<long> task)
        => task.IsCompletedSuccessfully ? task.Result : 0;

    private static async Task<long> CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.DataChunkSize);
        long total = 0;

        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDisconnect(ex))
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsDisconnect(ex))
                {
                    break;
                }

                total += read;
            }

            await HalfCloseAsync(destination).ConfigureAwait(false);
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Tells the destination that this direction is done without killing the other one, so a
    /// peer waiting on the response half still gets it.
    /// </summary>
    private static async ValueTask HalfCloseAsync(Stream destination)
    {
        try
        {
            switch (destination)
            {
                case MuxStream mux:
                    await mux.CompleteWriteAsync().ConfigureAwait(false);
                    break;

                case NetworkStream { Socket.Connected: true } network:
                    network.Socket.Shutdown(SocketShutdown.Send);
                    break;
            }
        }
        catch (Exception ex) when (IsDisconnect(ex))
        {
            // The peer beat us to it.
        }
    }

    private static bool IsDisconnect(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException;
}
