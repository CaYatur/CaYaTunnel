using System.Security.Cryptography;

namespace CaYaTunnel.Server.Registry;

/// <summary>
/// Generates readable subdomain labels for tunnels created without a name. Short, easy to read
/// aloud, and DNS-safe.
/// </summary>
public static class TunnelNameGenerator
{
    private static readonly string[] Adjectives =
    [
        "amber", "brisk", "calm", "clever", "cosmic", "crimson", "dusty", "eager", "fuzzy",
        "gentle", "hidden", "ivory", "jolly", "keen", "lucky", "mellow", "noble", "olive",
        "polar", "quiet", "rapid", "silver", "swift", "teal", "urban", "vivid", "warm", "zesty",
    ];

    private static readonly string[] Nouns =
    [
        "anchor", "beacon", "canyon", "cedar", "comet", "delta", "ember", "falcon", "forge",
        "harbor", "island", "jungle", "lagoon", "lantern", "meadow", "nebula", "orbit", "pixel",
        "quartz", "ridge", "river", "socket", "summit", "tunnel", "valley", "willow", "zenith",
    ];

    /// <summary>e.g. "swift-falcon-42" — collision-checked by the caller against the registry.</summary>
    public static string NewLabel()
    {
        var adjective = Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)];
        var noun = Nouns[RandomNumberGenerator.GetInt32(Nouns.Length)];
        var number = RandomNumberGenerator.GetInt32(10, 100);
        return $"{adjective}-{noun}-{number}";
    }

    /// <summary>
    /// Forces operator input into a valid DNS label: lower case, alphanumerics and dashes only,
    /// no leading or trailing dash, 63 characters at most.
    /// </summary>
    public static string Sanitise(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var chars = new List<char>(input.Length);
        foreach (var raw in input.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(raw))
            {
                chars.Add(raw);
            }
            else if (raw is '-' or '_' or ' ' or '.' && chars.Count > 0 && chars[^1] != '-')
            {
                chars.Add('-');
            }
        }

        while (chars.Count > 0 && chars[^1] == '-')
        {
            chars.RemoveAt(chars.Count - 1);
        }

        var label = new string([.. chars]);
        return label.Length > 63 ? label[..63].TrimEnd('-') : label;
    }

    public static bool IsValidLabel(string label) =>
        !string.IsNullOrWhiteSpace(label)
        && label.Length <= 63
        && label == Sanitise(label);
}
