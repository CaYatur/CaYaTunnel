using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using CaYaTunnel.Core.Protocol;

namespace CaYaTunnel.Core.Provisioning;

/// <summary>
/// Reads and writes the configuration blob appended to the tail of a client executable.
/// <para>
/// Provisioning deliberately avoids rebuilding: the server ships one prebuilt stub, copies it,
/// and appends a blob. That means the VPS needs no .NET SDK and no compiler. The cost is that
/// appending bytes invalidates any Authenticode signature, so provisioned builds are unsigned —
/// an accepted trade for a self-hosted tool where the operator builds their own clients.
/// </para>
/// <para>
/// The trailer sits at the very end of the file and is located by scanning backwards from EOF,
/// which is why the layout is length-and-magic *last*.
/// </para>
/// </summary>
public static class ClientConfigBlob
{
    /// <summary>Marks a stub that carries a config blob. Must stay exactly 16 bytes.</summary>
    private static ReadOnlySpan<byte> Magic => "CaYaTunnelCfg/1\n"u8;

    private const int MagicLength = 16;
    private const int HashLength = 32;
    private const int LengthFieldLength = 4;
    private const int TrailerLength = HashLength + LengthFieldLength + MagicLength;

    /// <summary>Refuses anything implausible so a corrupt tail can never drive a huge allocation.</summary>
    private const int MaxPayloadLength = 64 * 1024;

    /// <summary>
    /// Copies <paramref name="stubPath"/> to <paramref name="outputPath"/> and appends
    /// <paramref name="config"/>. If the stub already carries a blob it is replaced, so a
    /// provisioned client can be re-provisioned without going back to a pristine stub.
    /// </summary>
    public static async Task WriteAsync(
        string stubPath,
        string outputPath,
        EmbeddedClientConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stubPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(config);

        if (!File.Exists(stubPath))
        {
            throw new FileNotFoundException($"Client stub not found at '{stubPath}'.", stubPath);
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(config, JsonProtocol.Options);
        if (payload.Length > MaxPayloadLength)
        {
            throw new InvalidOperationException($"Client configuration is {payload.Length} bytes, over the {MaxPayloadLength} byte limit.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var baseLength = await GetStubLengthAsync(stubPath, cancellationToken).ConfigureAwait(false);

        await using (var source = File.OpenRead(stubPath))
        await using (var destination = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await CopyExactlyAsync(source, destination, baseLength, cancellationToken).ConfigureAwait(false);

            await destination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(SHA256.HashData(payload), cancellationToken).ConfigureAwait(false);

            var lengthField = new byte[LengthFieldLength];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthField, (uint)payload.Length);
            await destination.WriteAsync(lengthField, cancellationToken).ConfigureAwait(false);

            await destination.WriteAsync(Magic.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads the blob out of the running executable, or null when this build was not
    /// provisioned (a plain stub, or a developer build run from the output directory).
    /// </summary>
    public static EmbeddedClientConfig? ReadFromCurrentProcess()
    {
        // Assembly.Location is empty in a single-file build, so ProcessPath is the only way to
        // find our own bytes on disk.
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? null : Read(path);
    }

    public static EmbeddedClientConfig? Read(string executablePath)
    {
        try
        {
            using var stream = File.Open(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static EmbeddedClientConfig? Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < TrailerLength)
        {
            return null;
        }

        var trailer = new byte[TrailerLength];
        stream.Seek(-TrailerLength, SeekOrigin.End);
        stream.ReadExactly(trailer);

        if (!trailer.AsSpan(HashLength + LengthFieldLength).SequenceEqual(Magic))
        {
            return null; // plain stub — not provisioned
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(HashLength, LengthFieldLength));
        if (payloadLength == 0 || payloadLength > MaxPayloadLength)
        {
            return null;
        }

        var payloadStart = stream.Length - TrailerLength - payloadLength;
        if (payloadStart < 0)
        {
            return null;
        }

        var payload = new byte[payloadLength];
        stream.Seek(payloadStart, SeekOrigin.Begin);
        stream.ReadExactly(payload);

        // Guards against a truncated or partially written download rather than tampering.
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(trailer.AsSpan(0, HashLength)))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EmbeddedClientConfig>(payload, JsonProtocol.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Length of <paramref name="path"/> with any existing blob discounted.</summary>
    private static async Task<long> GetStubLengthAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length < TrailerLength)
        {
            return stream.Length;
        }

        var trailer = new byte[TrailerLength];
        stream.Seek(-TrailerLength, SeekOrigin.End);
        await stream.ReadExactlyAsync(trailer, cancellationToken).ConfigureAwait(false);

        if (!trailer.AsSpan(HashLength + LengthFieldLength).SequenceEqual(Magic))
        {
            return stream.Length;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(trailer.AsSpan(HashLength, LengthFieldLength));
        var stubLength = stream.Length - TrailerLength - payloadLength;
        return stubLength > 0 ? stubLength : stream.Length;
    }

    private static async Task CopyExactlyAsync(Stream source, Stream destination, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var take = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Client stub ended earlier than expected.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }
}
