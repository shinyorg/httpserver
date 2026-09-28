using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shiny.Net.HttpServer.JsonPatch;

/// <summary>
/// JSON equality as RFC 6902 §4.6 defines it for <c>test</c>.
/// <para>
/// Not <c>ToJsonString()</c> comparison, which gets two things wrong: numbers are equal when their
/// <em>values</em> are (<c>1</c>, <c>1.0</c> and <c>1e0</c> are one number, written three ways), and
/// objects are equal regardless of member order. Strings compare ordinally, with escapes already
/// decoded — <c>"A"</c> is <c>"A"</c>.
/// </para>
/// </summary>
static class JsonPatchEquality
{
    public static bool DeepEquals(JsonNode? left, JsonNode? right)
    {
        var leftKind = Kind(left);
        var rightKind = Kind(right);

        if (leftKind != rightKind)
            return false;

        switch (leftKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;

            case JsonValueKind.String:
                return string.Equals(left!.GetValue<string>(), right!.GetValue<string>(), StringComparison.Ordinal);

            case JsonValueKind.Number:
                return NumbersEqual(left!.ToJsonString(), right!.ToJsonString());

            case JsonValueKind.Array:
            {
                var a = left!.AsArray();
                var b = right!.AsArray();

                if (a.Count != b.Count)
                    return false;

                for (var i = 0; i < a.Count; i++)
                {
                    if (!DeepEquals(a[i], b[i]))
                        return false;
                }

                return true;
            }

            case JsonValueKind.Object:
            {
                var a = left!.AsObject();
                var b = right!.AsObject();

                if (a.Count != b.Count)
                    return false;

                foreach (var (name, value) in a)
                {
                    if (!b.TryGetPropertyValue(name, out var other) || !DeepEquals(value, other))
                        return false;
                }

                return true;
            }

            default:
                return false;
        }
    }

    static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    /// <summary>
    /// Compares two JSON number literals by value. <see cref="decimal"/> first, because it is exact for
    /// everything an API realistically carries (money, ids, versions), where <see cref="double"/> would
    /// round two different 19-digit ids to the same value and call them equal; <see cref="double"/> only
    /// for literals decimal cannot hold, like <c>1e30</c>.
    /// </summary>
    static bool NumbersEqual(string left, string right)
    {
        if (left == right)
            return true;

        const NumberStyles style = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;

        if (decimal.TryParse(left, style, culture, out var a) && decimal.TryParse(right, style, culture, out var b))
            return a == b;

        return double.TryParse(left, style, culture, out var x)
            && double.TryParse(right, style, culture, out var y)
            && x.Equals(y);
    }
}
