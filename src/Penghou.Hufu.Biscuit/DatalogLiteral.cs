using System.Text;

namespace Penghou.Hufu.Biscuit;

/// <summary>
/// Encodes one bounded value as a quoted literal for the pinned Biscuit Datalog
/// parser. The parser supports escaping quotes, backslashes, and line feeds;
/// carriage returns and tabs are preserved literally inside the quotes.
/// </summary>
internal static class DatalogLiteral
{
    private const int MaximumUtf8Bytes = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Returns a safe quoted Datalog string literal without interpreting the
    /// value as Datalog source. Rejects malformed UTF-16, oversized input, and
    /// control characters the upstream string parser does not support.
    /// </summary>
    internal static string String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int utf8ByteCount;
        try
        {
            utf8ByteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Datalog string values must contain valid Unicode text.",
                nameof(value),
                exception);
        }

        if (utf8ByteCount > MaximumUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Datalog string values must be at most {MaximumUtf8Bytes} UTF-8 bytes.");
        }

        int outputLength = 2;
        foreach (char character in value)
        {
            if (char.IsControl(character) && character is not '\n' and not '\r' and not '\t')
            {
                throw new ArgumentException(
                    "Datalog string values cannot contain unsupported control characters.",
                    nameof(value));
            }

            outputLength = checked(outputLength +
                (character is '"' or '\\' or '\n' ? 2 : 1));
        }

        var result = new StringBuilder(outputLength);
        result.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    result.Append("\\\"");
                    break;
                case '\\':
                    result.Append("\\\\");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                default:
                    // The pinned parser has no \r or \t escape syntax. It
                    // accepts these characters literally within a string.
                    result.Append(character);
                    break;
            }
        }

        result.Append('"');
        return result.ToString();
    }
}
