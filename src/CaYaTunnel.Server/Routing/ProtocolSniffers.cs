using System.Buffers.Binary;
using System.Text;

namespace CaYaTunnel.Server.Routing;

/// <summary>
/// Pulls the destination hostname out of the first bytes of a connection.
/// <para>
/// This is what lets many tunnels share one public port. It only works for protocols that
/// announce where they are going — TLS via SNI, HTTP via the Host header, Minecraft Java via its
/// handshake packet. Plain TCP carries no such field, which is exactly why
/// <see cref="Core.Models.TunnelKind.PortForward"/> exists and gets a port of its own.
/// </para>
/// </summary>
public static class ProtocolSniffers
{
    /// <summary>A TLS record starts with handshake(0x16) and a plausible version.</summary>
    public static bool LooksLikeTls(ReadOnlySpan<byte> data)
        => data.Length >= 3 && data[0] == 0x16 && data[1] == 0x03;

    /// <summary>
    /// Extracts the SNI server name from a TLS ClientHello. Returns null if the bytes are not a
    /// ClientHello, are incomplete, or carry no SNI extension (a bare IP connection, say).
    /// </summary>
    public static string? ReadTlsSni(ReadOnlySpan<byte> data)
    {
        try
        {
            if (!LooksLikeTls(data) || data.Length < 5)
            {
                return null;
            }

            var recordLength = BinaryPrimitives.ReadUInt16BigEndian(data[3..5]);
            var body = data[5..];
            if (body.Length < Math.Min(recordLength, body.Length))
            {
                return null;
            }

            if (body.Length < 4 || body[0] != 0x01)
            {
                return null; // not a ClientHello
            }

            // 3-byte handshake length, then: version(2) random(32)
            var cursor = 4 + 2 + 32;
            if (body.Length < cursor + 1)
            {
                return null;
            }

            var sessionIdLength = body[cursor];
            cursor += 1 + sessionIdLength;
            if (body.Length < cursor + 2)
            {
                return null;
            }

            var cipherSuitesLength = BinaryPrimitives.ReadUInt16BigEndian(body[cursor..]);
            cursor += 2 + cipherSuitesLength;
            if (body.Length < cursor + 1)
            {
                return null;
            }

            var compressionLength = body[cursor];
            cursor += 1 + compressionLength;
            if (body.Length < cursor + 2)
            {
                return null;
            }

            var extensionsLength = BinaryPrimitives.ReadUInt16BigEndian(body[cursor..]);
            cursor += 2;
            var extensionsEnd = Math.Min(cursor + extensionsLength, body.Length);

            while (cursor + 4 <= extensionsEnd)
            {
                var extensionType = BinaryPrimitives.ReadUInt16BigEndian(body[cursor..]);
                var extensionLength = BinaryPrimitives.ReadUInt16BigEndian(body[(cursor + 2)..]);
                cursor += 4;

                if (extensionType == 0x0000) // server_name
                {
                    var extensionEnd = Math.Min(cursor + extensionLength, body.Length);
                    if (cursor + 2 > extensionEnd)
                    {
                        return null;
                    }

                    var listCursor = cursor + 2; // skip server_name_list length
                    while (listCursor + 3 <= extensionEnd)
                    {
                        var nameType = body[listCursor];
                        var nameLength = BinaryPrimitives.ReadUInt16BigEndian(body[(listCursor + 1)..]);
                        listCursor += 3;

                        if (listCursor + nameLength > extensionEnd)
                        {
                            return null;
                        }

                        if (nameType == 0x00) // host_name
                        {
                            return Encoding.ASCII.GetString(body.Slice(listCursor, nameLength)).TrimEnd('.');
                        }

                        listCursor += nameLength;
                    }

                    return null;
                }

                cursor += extensionLength;
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Malformed or truncated hello — treat as "no name", never as a crash.
            return null;
        }
    }

    /// <summary>True once the buffer holds a complete HTTP header block.</summary>
    public static bool HasCompleteHttpHead(ReadOnlySpan<byte> data)
        => data.IndexOf("\r\n\r\n"u8) >= 0 || data.IndexOf("\n\n"u8) >= 0;

    /// <summary>
    /// Reads the Host header out of a plaintext HTTP request. The port suffix is stripped, so
    /// "example.com:8080" matches a tunnel registered as "example.com".
    /// </summary>
    public static string? ReadHttpHost(ReadOnlySpan<byte> data)
    {
        var text = Encoding.ASCII.GetString(data);
        var lines = text.Split('\n');

        // Skip the request line; a Host header before it would be malformed anyway.
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
            {
                break; // end of headers
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (!line.AsSpan(0, separator).Trim().Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            return StripPort(value);
        }

        return null;
    }

    /// <summary>
    /// Reads the request target (path and query) from the request line, so a redirect can send
    /// the visitor to the page they actually asked for rather than dumping them on the root.
    /// </summary>
    public static string? ReadHttpTarget(ReadOnlySpan<byte> data)
    {
        var text = Encoding.ASCII.GetString(data);
        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0)
        {
            return null;
        }

        var parts = text[..lineEnd].TrimEnd('\r').Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        var target = parts[1];

        // Only origin-form targets are safe to append; an absolute-form request line would
        // otherwise let a visitor steer the redirect at another host.
        return target.StartsWith('/') ? target : "/";
    }

    /// <summary>
    /// Reads the server address a Minecraft Java client asked for. The client sends the exact
    /// hostname it was given, which is what makes several Minecraft servers able to share one
    /// public 25565.
    /// </summary>
    public static string? ReadMinecraftHostname(ReadOnlySpan<byte> data)
    {
        try
        {
            var cursor = 0;

            // Legacy 1.6 ping starts 0xFE 0x01 and carries no usable address here.
            if (data.Length > 0 && data[0] == 0xFE)
            {
                return null;
            }

            if (!TryReadVarInt(data, ref cursor, out _))
            {
                return null; // packet length
            }

            if (!TryReadVarInt(data, ref cursor, out var packetId) || packetId != 0x00)
            {
                return null;
            }

            if (!TryReadVarInt(data, ref cursor, out _))
            {
                return null; // protocol version
            }

            if (!TryReadVarInt(data, ref cursor, out var addressLength) || addressLength is < 0 or > 255)
            {
                return null;
            }

            if (cursor + addressLength > data.Length)
            {
                return null;
            }

            var address = Encoding.UTF8.GetString(data.Slice(cursor, addressLength));

            // Forge appends "\0FML\0" and proxies append "\0<ip>\0<uuid>"; the real hostname is
            // always the first null-separated field.
            var nul = address.IndexOf('\0');
            if (nul >= 0)
            {
                address = address[..nul];
            }

            return string.IsNullOrWhiteSpace(address) ? null : address.TrimEnd('.').ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>True once enough of a Minecraft handshake has arrived to read its address.</summary>
    public static bool HasCompleteMinecraftHandshake(ReadOnlySpan<byte> data)
        => ReadMinecraftHostname(data) is not null;

    private static bool TryReadVarInt(ReadOnlySpan<byte> data, ref int cursor, out int value)
    {
        value = 0;
        var shift = 0;

        while (shift < 35)
        {
            if (cursor >= data.Length)
            {
                return false;
            }

            var current = data[cursor++];
            value |= (current & 0x7F) << shift;

            if ((current & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    /// <summary>Removes a ":port" suffix while leaving bare IPv6 literals intact.</summary>
    public static string StripPort(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        var value = host.Trim();

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 0 ? value[..(close + 1)] : value;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon && int.TryParse(value[(colon + 1)..], out _))
        {
            value = value[..colon];
        }

        return value.TrimEnd('.').ToLowerInvariant();
    }
}
