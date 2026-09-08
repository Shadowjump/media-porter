using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MediaPorter
{
    /// <summary>
    /// Tiny dependency-free JSON reader/writer. Objects become Dictionary&lt;string,object&gt;,
    /// arrays List&lt;object&gt;, numbers double, everything else string/bool/null.
    /// Exists so the app compiles with nothing but csc.exe on any Windows PC.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            int i = 0;
            return ParseValue(text, ref i);
        }

        // ---- convenience accessors (null-safe, never throw) ----

        public static object Get(object node, string path)
        {
            object cur = node;
            foreach (string rawPart in path.Split('.'))
            {
                if (cur == null) return null;
                string part = rawPart;
                int bracket = part.IndexOf('[');
                string key = bracket >= 0 ? part.Substring(0, bracket) : part;

                if (key.Length > 0)
                {
                    var dict = cur as Dictionary<string, object>;
                    if (dict == null || !dict.TryGetValue(key, out cur)) return null;
                }
                while (bracket >= 0)
                {
                    int end = part.IndexOf(']', bracket);
                    if (end < 0) return null;
                    int idx;
                    if (!int.TryParse(part.Substring(bracket + 1, end - bracket - 1), out idx)) return null;
                    var list = cur as List<object>;
                    if (list == null || idx < 0 || idx >= list.Count) return null;
                    cur = list[idx];
                    bracket = part.IndexOf('[', end);
                }
            }
            return cur;
        }

        public static string Str(object node, string path, string fallback = "")
        {
            object v = Get(node, path);
            if (v == null) return fallback;
            if (v is string) return (string)v;
            if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
            if (v is bool) return ((bool)v) ? "true" : "false";
            return fallback;
        }

        public static double Num(object node, string path, double fallback = 0)
        {
            object v = Get(node, path);
            if (v is double) return (double)v;
            double d;
            if (v is string && double.TryParse((string)v, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return fallback;
        }

        public static bool Bool(object node, string path, bool fallback = false)
        {
            object v = Get(node, path);
            if (v is bool) return (bool)v;
            if (v is string) return string.Equals((string)v, "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        public static List<object> Arr(object node, string path)
        {
            var l = Get(node, path) as List<object>;
            return l ?? new List<object>();
        }

        // ---- writer ----

        public static string Write(Dictionary<string, object> obj, bool indent = true)
        {
            var sb = new StringBuilder();
            WriteValue(sb, obj, indent, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object v, bool indent, int depth)
        {
            string pad = indent ? new string(' ', (depth + 1) * 2) : "";
            string padEnd = indent ? new string(' ', depth * 2) : "";
            string nl = indent ? "\r\n" : "";

            if (v == null) { sb.Append("null"); return; }
            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }
            if (v is int || v is long) { sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            var dict = v as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{').Append(nl);
                int n = 0;
                foreach (var kv in dict)
                {
                    sb.Append(pad);
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    if (indent) sb.Append(' ');
                    WriteValue(sb, kv.Value, indent, depth + 1);
                    if (++n < dict.Count) sb.Append(',');
                    sb.Append(nl);
                }
                sb.Append(padEnd).Append('}');
                return;
            }

            var seq = v as System.Collections.IEnumerable;
            if (seq != null)
            {
                var items = new List<object>();
                foreach (object o in seq) items.Add(o);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[').Append(nl);
                for (int i = 0; i < items.Count; i++)
                {
                    sb.Append(pad);
                    WriteValue(sb, items[i], indent, depth + 1);
                    if (i < items.Count - 1) sb.Append(',');
                    sb.Append(nl);
                }
                sb.Append(padEnd).Append(']');
                return;
            }

            WriteString(sb, v.ToString());
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }

        // ---- reader ----

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && i + 4 <= s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 5 <= s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 4 <= s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            return ParseNumber(s, ref i);
        }

        static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') break;
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                d[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return d;
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (i < s.Length)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return l;
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'b') sb.Append('\b');
                else if (e == 'f') sb.Append('\f');
                else if (e == 'u')
                {
                    if (i + 4 <= s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 4;
                    }
                }
                else sb.Append(e);
            }
            return sb.ToString();
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
            double d;
            if (double.TryParse(s.Substring(start, i - start), NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return d;
            return null;
        }
    }
}
