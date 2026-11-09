using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace LiveryKit.Mcp
{
    /// <summary>
    /// Just enough JSON. Objects parse to Dictionary&lt;string, object&gt; (key order kept), arrays to object[].
    /// The writer leaves text readable (the framework's serializer turns &amp; into &), which matters when
    /// rewriting someone's AI app settings.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text) =>
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 }.DeserializeObject(text);

        public static string Write(object value, bool indented = false)
        {
            var sb = new StringBuilder();
            Write(sb, value, indented ? 0 : -1);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case IDictionary<string, object> d:
                    sb.Append('{');
                    int n = 0;
                    foreach (var kv in d)
                    {
                        Separate(sb, n++, depth);
                        WriteString(sb, kv.Key);
                        sb.Append(depth < 0 ? ":" : ": ");
                        Write(sb, kv.Value, depth < 0 ? -1 : depth + 1);
                    }
                    Close(sb, n, depth, '}');
                    return;
                case IEnumerable list:
                    sb.Append('[');
                    int m = 0;
                    foreach (var item in list)
                    {
                        Separate(sb, m++, depth);
                        Write(sb, item, depth < 0 ? -1 : depth + 1);
                    }
                    Close(sb, m, depth, ']');
                    return;
                case int _:
                case long _:
                case decimal _:
                case double _:
                case float _:
                    sb.Append(((IFormattable)v).ToString(null, CultureInfo.InvariantCulture));
                    return;
                default:
                    // e.g. a DateTime the framework parser made out of a "\/Date(...)\/" string: refuse rather than write bad JSON.
                    throw new FormatException("Can't write a " + v.GetType().Name + " as JSON.");
            }
        }

        static void Separate(StringBuilder sb, int index, int depth)
        {
            if (index > 0) sb.Append(',');
            if (depth >= 0) sb.Append('\n').Append(' ', (depth + 1) * 2);
        }

        static void Close(StringBuilder sb, int count, int depth, char close)
        {
            if (count > 0 && depth >= 0) sb.Append('\n').Append(' ', depth * 2);
            sb.Append(close);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
