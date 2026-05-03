using System.Globalization;
using System.Text;

namespace DevBitsLab.Mcp.SourceGraph.Core;

/// <summary>
/// JSON serialisation for <see cref="AttributeArgs"/>. Produces a canonical shape:
/// <code>
/// {
///   "ctor": ["/api/users", 200],
///   "named": { "Name": "users-list", "Order": 0 }
/// }
/// </code>
/// Supported value types: <see cref="string"/>, primitives (<see cref="bool"/>, all numeric
/// types, <see cref="char"/>), enum members (serialised as the underlying numeric value),
/// <see cref="Type"/>-like values (already pre-formatted as a display-string by the indexer),
/// <c>null</c>, and arrays/lists of any of the above. Anything else falls back to
/// <c>ToString()</c>.
/// </summary>
public static class AttributeArgsJson
{
    /// <summary>Serialise to the canonical on-disk JSON shape.</summary>
    public static string Serialize(AttributeArgs args)
    {
        var sb = new StringBuilder();
        sb.Append("{\"ctor\":[");
        for (var i = 0; i < args.Ctor.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteValue(sb, args.Ctor[i]);
        }
        sb.Append("],\"named\":{");
        var first = true;
        foreach (var kv in args.Named)
        {
            if (!first) sb.Append(',');
            first = false;
            WriteString(sb, kv.Key);
            sb.Append(':');
            WriteValue(sb, kv.Value);
        }
        sb.Append("}}");
        return sb.ToString();
    }

    private static void WriteValue(StringBuilder sb, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case char ch:
                WriteString(sb, ch.ToString());
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return;
            case float f:
                if (float.IsFinite(f)) sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
                else WriteString(sb, f.ToString(CultureInfo.InvariantCulture));
                return;
            case double d:
                if (double.IsFinite(d)) sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                else WriteString(sb, d.ToString(CultureInfo.InvariantCulture));
                return;
            case decimal m:
                sb.Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            case Enum e:
                // Serialise enum as the underlying numeric value to avoid leaking culture-
                // specific or version-skewed names. Round-tripping back to the enum name
                // requires the original type, which we don't preserve in args_json.
                sb.Append(Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            case System.Collections.IEnumerable seq when value is not string:
                sb.Append('[');
                var first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            default:
                // Type-display strings reach here as plain strings already (the indexer
                // formats `typeof(X)` via `SymbolDisplayFormat.FullyQualifiedFormat`); any
                // other unknown type gets ToString()'d defensively.
                WriteString(sb, value.ToString() ?? string.Empty);
                return;
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
