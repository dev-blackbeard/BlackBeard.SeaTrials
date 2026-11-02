using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BlackBeard.SeaTrials.Core.Provenance;

/// <summary>The JSON could not be put in canonical form: a fraction, an exponent, a duplicate key, a number beyond 64 bits.</summary>
public sealed class CanonicalJsonException : Exception
{
    public CanonicalJsonException(string message) : base(message)
    {
    }
}

/// <summary>
/// The canonical form of a JSON value, the same bytes as tools/stamp.py gives: UTF-8; object keys sorted by Unicode
/// code point; no whitespace; strings escaped only where JSON requires it (the quote, the backslash and the
/// characters below U+0020, as \b \f \n \r \t or \u00xx in lower case), everything else, non-ASCII included, as it is;
/// integers only. The framework's own JSON writer cannot be used: it escapes the quote with a numeric Unicode escape, which is not the canonical form.
/// </summary>
public static class CanonicalJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Serialize(JsonElement value)
    {
        var builder = new StringBuilder();
        try
        {
            Write(builder, value);
        }
        catch (InvalidOperationException error)
        {
            throw new CanonicalJsonException("The JSON holds a value that cannot be read: " + error.Message);
        }

        var text = builder.ToString();
        RequireUtf8(text);
        return text;
    }

    /// <summary>Parses strict JSON (no comments, no trailing commas) and returns its canonical form.</summary>
    public static string SerializeText(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            return Serialize(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new CanonicalJsonException("The text is not valid JSON: " + error.Message);
        }
    }

    /// <summary>The bytes that are hashed: the text as UTF-8.</summary>
    public static byte[] ToUtf8(string text)
    {
        RequireUtf8(text);
        return StrictUtf8.GetBytes(text);
    }

    internal static string Quote(string text)
    {
        var builder = new StringBuilder();
        WriteString(builder, text);
        return builder.ToString();
    }

    private static void RequireUtf8(string text)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(text);
        }
        catch (EncoderFallbackException)
        {
            throw new CanonicalJsonException("A string holds an unpaired surrogate and cannot be written as UTF-8.");
        }
    }

    private static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var members = new List<KeyValuePair<string, JsonElement>>();
                foreach (var property in element.EnumerateObject())
                {
                    members.Add(new KeyValuePair<string, JsonElement>(property.Name, property.Value));
                }

                members.Sort((left, right) => CompareByCodePoint(left.Key, right.Key));
                for (var i = 1; i < members.Count; i++)
                {
                    if (string.Equals(members[i].Key, members[i - 1].Key, StringComparison.Ordinal))
                    {
                        throw new CanonicalJsonException("The JSON has a duplicate key.");
                    }
                }

                builder.Append('{');
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteString(builder, members[i].Key);
                    builder.Append(':');
                    Write(builder, members[i].Value);
                }

                builder.Append('}');
                break;
            }

            case JsonValueKind.Array:
            {
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;
            }

            case JsonValueKind.String:
                WriteString(builder, element.GetString() ?? "");
                break;

            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var number))
                {
                    throw new CanonicalJsonException(
                        "The number " + element.GetRawText() + " is not an integer that fits in 64 bits.");
                }

                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                throw new CanonicalJsonException("The JSON holds a value of a kind that cannot be written.");
        }
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    // Unicode code point order, as Python sorts strings. Ordinal string order compares UTF-16 units, which puts a
    // character outside the Basic Multilingual Plane before U+E000..U+FFFF; this does not.
    private static int CompareByCodePoint(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            _ = Rune.DecodeFromUtf16(left.AsSpan(i), out var leftRune, out var leftLength);
            _ = Rune.DecodeFromUtf16(right.AsSpan(j), out var rightRune, out var rightLength);
            var order = leftRune.Value.CompareTo(rightRune.Value);
            if (order != 0)
            {
                return order;
            }

            i += leftLength;
            j += rightLength;
        }

        return (left.Length - i).CompareTo(right.Length - j);
    }
}
