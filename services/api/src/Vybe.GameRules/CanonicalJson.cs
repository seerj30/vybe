using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vybe.GameRules;

/// <summary>
/// RFC 8785 canonical JSON and the ruleset checksum (spec v1.3 §4).
/// Supported subset: objects, arrays, integers and ASCII strings that need no
/// escaping. Anything else throws, so the C# and TS implementations cannot
/// silently disagree.
/// </summary>
public static partial class CanonicalJson
{
    // \z, not $: in .NET, $ also matches before a final '\n'.
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)\z")]
    private static partial Regex IntegerToken();

    [GeneratedRegex(@"^[\x20-\x21\x23-\x5b\x5d-\x7e]*\z")]
    private static partial Regex SafeString();

    /// <summary>Same limit as JavaScript <c>Number.isSafeInteger</c>.</summary>
    private const long MaxSafeInteger = 9_007_199_254_740_991;

    public static string Serialize(JsonElement element)
    {
        var builder = new StringBuilder();
        Write(element, builder);
        return builder.ToString();
    }

    /// <summary>Lowercase hex SHA-256 of the canonical JSON.</summary>
    public static string Checksum(JsonElement element) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(element))));

    private static void Write(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(property.Name))
                    {
                        throw new NotSupportedException($"canonical JSON does not support duplicate key '{property.Name}'");
                    }

                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteString(property.Name, builder);
                    builder.Append(':');
                    Write(property.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index++ > 0)
                    {
                        builder.Append(',');
                    }

                    Write(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (!IntegerToken().IsMatch(raw)
                    || !element.TryGetInt64(out var value)
                    || value is < -MaxSafeInteger or > MaxSafeInteger)
                {
                    throw new NotSupportedException($"canonical JSON supports only safe integers, got {raw}");
                }

                builder.Append(value.ToString(CultureInfo.InvariantCulture));
                break;

            case JsonValueKind.String:
                WriteString(element.GetString()!, builder);
                break;

            default:
                throw new NotSupportedException($"canonical JSON does not support {element.ValueKind}");
        }
    }

    private static void WriteString(string value, StringBuilder builder)
    {
        if (!SafeString().IsMatch(value))
        {
            throw new NotSupportedException("canonical JSON supports only ASCII strings without escapes");
        }

        builder.Append('"').Append(value).Append('"');
    }
}
