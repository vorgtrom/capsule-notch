using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace Capsule
{
    // JSON in and out, on top of the serializer that ships with .NET Framework.
    // Parse gives Dictionary<string, object> for objects (keys in document order), object[] for arrays,
    // and string, int, long, decimal, double, bool or null for values.
    public static class Json
    {
        public static object Parse(string text)
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            serializer.RecursionLimit = 256;
            // The serializer's own error messages quote the whole input (possibly a token), so they never leave here.
            try { return serializer.DeserializeObject(text); }
            catch (ArgumentException) { throw new FormatException("Malformed JSON."); }
            catch (InvalidOperationException) { throw new FormatException("Malformed JSON."); }
        }

        // Like Parse, but returns null for empty or malformed input instead of throwing.
        public static object TryParse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return Parse(text); }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object>; }
        public static object[] Arr(object value) { return value as object[]; }
        public static string Str(object value) { return value as string; }

        public static double? Num(object value)
        {
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is decimal) return (double)(decimal)value;
            if (value is double) return (double)value;
            return null;
        }

        // Get(root, "a", "b") is root["a"]["b"], or null as soon as a step is missing or not an object.
        public static object Get(object value, params string[] path)
        {
            object current = value;
            foreach (string key in path)
            {
                var obj = current as Dictionary<string, object>;
                if (obj == null || !obj.TryGetValue(key, out current)) return null;
            }
            return current;
        }

        // Indented with two spaces, keys in insertion order, no trailing newline.
        public static string Write(object value)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null) { sb.Append("null"); return; }
            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            if (value is bool) { sb.Append((bool)value ? "true" : "false"); return; }
            if (value is double) { sb.Append(((double)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is float) { sb.Append(((float)value).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (value is int || value is long || value is decimal || value is short || value is byte || value is uint || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            var obj = value as IDictionary<string, object>;
            if (obj != null) { WriteObject(sb, obj, depth); return; }
            var list = value as IEnumerable;
            if (list != null) { WriteArray(sb, list, depth); return; }
            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        static void WriteObject(StringBuilder sb, IDictionary<string, object> obj, int depth)
        {
            if (obj.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            int i = 0;
            foreach (KeyValuePair<string, object> pair in obj)
            {
                Indent(sb, depth + 1);
                WriteString(sb, pair.Key);
                sb.Append(": ");
                WriteValue(sb, pair.Value, depth + 1);
                if (++i < obj.Count) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, depth);
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable list, int depth)
        {
            var items = new List<object>();
            foreach (object item in list) items.Add(item);
            if (items.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            for (int i = 0; i < items.Count; i++)
            {
                Indent(sb, depth + 1);
                WriteValue(sb, items[i], depth + 1);
                if (i < items.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            Indent(sb, depth);
            sb.Append(']');
        }

        static void Indent(StringBuilder sb, int depth) { sb.Append(' ', depth * 2); }

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
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
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
