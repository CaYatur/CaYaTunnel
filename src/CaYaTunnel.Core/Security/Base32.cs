namespace CaYaTunnel.Core.Security;

/// <summary>
/// Crockford-style Base32 without the ambiguous letters I, L, O and U. Enrollment keys are
/// normally embedded in a provisioned build, but an operator may have to read one off a screen
/// and type it into a portable client, so the alphabet avoids characters people confuse.
/// </summary>
public static class Base32
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var output = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                output.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bitsLeft)) & 31]);
        }

        return output.ToString();
    }

    public static bool TryDecode(string? text, out byte[] data)
    {
        data = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalised = Normalise(text);
        var bytes = new List<byte>(normalised.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;

        foreach (var c in normalised)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        data = [.. bytes];
        return true;
    }

    /// <summary>
    /// Upper-cases, strips grouping dashes and whitespace, and folds the characters people
    /// typically mistype: I/L to 1, O to 0, U to V.
    /// </summary>
    public static string Normalise(string text)
    {
        var chars = new List<char>(text.Length);
        foreach (var raw in text)
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw);
            chars.Add(c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => c,
            });
        }

        return new string([.. chars]);
    }

    /// <summary>Inserts dashes every <paramref name="groupSize"/> characters for readability.</summary>
    public static string Group(string value, int groupSize = 8)
    {
        if (groupSize <= 0 || value.Length <= groupSize)
        {
            return value;
        }

        var parts = new List<string>();
        for (var i = 0; i < value.Length; i += groupSize)
        {
            parts.Add(value.Substring(i, Math.Min(groupSize, value.Length - i)));
        }

        return string.Join('-', parts);
    }
}
