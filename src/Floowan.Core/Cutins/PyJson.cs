using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Floowan.Core.Cutins;

/// <summary>
/// Python <c>json.dumps</c> output for the values the cut-in tools exchange (strings, integers, booleans,
/// null, arrays, objects). The build key and the manifest must be byte-identical to what
/// scripts/cutin/monster_cutin.py produces, so Floowan and the Python tool reuse each other's builds.
/// </summary>
public static class PyJson
{
    /// <param name="compact">separators (",", ":") instead of Python's default (", ", ": ").</param>
    /// <param name="ensureAscii">escape every character outside ' '..'~' as \uXXXX (Python's default).</param>
    public static string Dumps(JsonNode? node, bool sortKeys = false, bool compact = false, bool ensureAscii = true)
    {
        var sb = new StringBuilder();
        Write(sb, node, sortKeys, compact ? "," : ", ", compact ? ":" : ": ", ensureAscii);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node, bool sortKeys, string itemSep, string keySep, bool ensureAscii)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject obj:
            {
                sb.Append('{');
                var props = sortKeys ? obj.OrderBy(p => p.Key, StringComparer.Ordinal).ToList() : obj.ToList();
                for (var i = 0; i < props.Count; i++)
                {
                    if (i > 0)
                        sb.Append(itemSep);
                    WriteString(sb, props[i].Key, ensureAscii);
                    sb.Append(keySep);
                    Write(sb, props[i].Value, sortKeys, itemSep, keySep, ensureAscii);
                }
                sb.Append('}');
                return;
            }
            case JsonArray arr:
            {
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0)
                        sb.Append(itemSep);
                    Write(sb, arr[i], sortKeys, itemSep, keySep, ensureAscii);
                }
                sb.Append(']');
                return;
            }
            case JsonValue val:
            {
                if (!val.TryGetValue<JsonElement>(out var el))
                {
                    if (val.TryGetValue<string>(out var s)) { WriteString(sb, s, ensureAscii); return; }
                    if (val.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
                    if (val.TryGetValue<int>(out var i32)) { sb.Append(i32.ToString(CultureInfo.InvariantCulture)); return; }
                    if (val.TryGetValue<long>(out var i64)) { sb.Append(i64.ToString(CultureInfo.InvariantCulture)); return; }
                    if (val.TryGetValue<double>(out var d)) { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
                    throw new NotSupportedException("PyJson: unsupported value");
                }
                switch (el.ValueKind)
                {
                    case JsonValueKind.String:
                        WriteString(sb, el.GetString()!, ensureAscii);
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        return;
                    case JsonValueKind.Number:
                        if (el.TryGetInt64(out var l))
                            sb.Append(l.ToString(CultureInfo.InvariantCulture));
                        else
                            sb.Append(el.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                        return;
                }
                throw new NotSupportedException($"PyJson: unsupported value {el.ValueKind}");
            }
        }
    }

    public static void WriteString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20 || (ensureAscii && ch > 0x7E))
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
