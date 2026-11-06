using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vybe.GameRules;

namespace Vybe.Api.Gameplay;

/// <summary>
/// Step 4 of the spec §7 check order (<c>invalid_event</c>), on the raw JSON tokens: each event is an
/// object with exactly <c>tMs</c>, <c>aimX</c> and <c>power</c>, each a strict JSON integer
/// (<c>-?(0|[1-9][0-9]*)</c>, absolute value below 2^31).
/// </summary>
internal static partial class RawEvents
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)\z")]
    private static partial Regex StrictInteger();

    /// <summary>Returns the events, or null when any event is invalid.</summary>
    public static IReadOnlyList<ShotEvent>? Parse(JsonElement events)
    {
        var result = new List<ShotEvent>(events.GetArrayLength());
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            int? tMs = null, aimX = null, power = null;
            foreach (var property in item.EnumerateObject())
            {
                if (!TryStrictInteger(property.Value, out var value))
                {
                    return null;
                }

                // A duplicate key counts as an extra field.
                switch (property.Name)
                {
                    case "tMs" when tMs is null:
                        tMs = value;
                        break;
                    case "aimX" when aimX is null:
                        aimX = value;
                        break;
                    case "power" when power is null:
                        power = value;
                        break;
                    default:
                        return null;
                }
            }

            if (tMs is null || aimX is null || power is null)
            {
                return null;
            }

            result.Add(new ShotEvent(tMs.Value, aimX.Value, power.Value));
        }

        return result;
    }

    private static bool TryStrictInteger(JsonElement element, out int value)
    {
        value = 0;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = element.GetRawText();
        // 11 characters hold "-2147483648"; longer tokens are out of range or not integers.
        if (raw.Length > 11 || !StrictInteger().IsMatch(raw)
            || !long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            || parsed is <= -2_147_483_648L or >= 2_147_483_648L)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }
}
