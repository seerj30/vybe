using System.Text.Json;
using System.Text.RegularExpressions;
using Vybe.Api.Infrastructure;
using Vybe.GameRules;

namespace Vybe.Api.Features;

/// <summary>
/// Parses a submit body on raw JSON tokens (spec §5): shape errors are 400, event content errors
/// are the terminal rejection <c>invalid_event</c>, never a 400.
/// </summary>
public static partial class SubmitParser
{
    private static readonly string[] EventFields = ["aimX", "power", "tMs"];

    public sealed record Shape(string RulesetId, JsonElement Events);

    public static Shape ParseShape(JsonDocument body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rulesetId", out var rulesetId) || rulesetId.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            throw Problem.MalformedRequest("body must be an object with rulesetId (string) and events (array)");
        }

        return new Shape(rulesetId.GetString()!, events);
    }

    /// <summary>Strict integers only: no fraction, no exponent, absolute value below 2^31; exactly tMs, aimX, power.</summary>
    public static bool TryParseEvents(JsonElement events, out List<ShotEvent> parsed)
    {
        parsed = [];
        foreach (var element in events.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var names = element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(EventFields, StringComparer.Ordinal))
            {
                return false;
            }

            if (!TryStrictInt(element.GetProperty("tMs"), out var tMs)
                || !TryStrictInt(element.GetProperty("aimX"), out var aimX)
                || !TryStrictInt(element.GetProperty("power"), out var power))
            {
                return false;
            }

            parsed.Add(new ShotEvent(tMs, aimX, power));
        }

        return true;
    }

    private static bool TryStrictInt(JsonElement value, out int result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number
            && StrictInteger().IsMatch(value.GetRawText())
            && value.TryGetInt32(out result)
            && result != int.MinValue;
    }

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)\z")]
    private static partial Regex StrictInteger();
}
