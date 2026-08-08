using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaYaTunnel.Core.Protocol;

/// <summary>
/// One set of JSON options for everything that crosses the wire or lands on disk, so the
/// server and client can never disagree about casing or enum representation.
/// </summary>
public static class JsonProtocol
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Enums as strings: adding a value later must not renumber an existing one on disk.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Same shape, but indented — used for human-editable config files.</summary>
    public static readonly JsonSerializerOptions PrettyOptions = new(Options)
    {
        WriteIndented = true,
    };

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Malformed {typeof(T).Name} payload: {ex.Message}", ex);
        }
    }

    /// <summary>Deserialises and fails loudly instead of handing back null.</summary>
    public static T DeserializeRequired<T>(ReadOnlySpan<byte> utf8Json)
        => Deserialize<T>(utf8Json) ?? throw new ProtocolException($"Empty {typeof(T).Name} payload.");
}
