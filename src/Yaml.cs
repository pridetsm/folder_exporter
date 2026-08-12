// Yaml.cs - a small, strict YAML subset parser with line-accurate errors.
//
// Supported: nested block mappings, block sequences ("- item"), flow sequences
// ([a, b]), quoted and plain scalars, # comments, booleans and numbers.
// Not supported: anchors, aliases, multi-document files, multi-line scalars
// (| and >), and flow mappings spanning lines. That covers everything an
// exporter config needs and keeps the parser small enough to audit.
//
// The one subtlety worth calling out: a Windows path is a plain scalar that
// contains a colon ("path: D:\data\in"). A key/value split must therefore break
// on the first ": " (colon followed by space) or a trailing colon - never on
// just any colon.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderExporter
{
    internal sealed class YamlException : Exception
    {
        public YamlException(string message) : base(message) { }
    }

    internal static class Yaml
    {
        private sealed class Line
        {
            public int Indent;          // indentation of the line itself
            public int ContentIndent;   // where the content starts (after "- ")
            public string Text;
            public bool IsSeqItem;
            public int Number;          // 1-based, for error messages
        }

        public static Dictionary<string, object> ParseFile(string path)
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false));
            return Parse(text);
        }

        public static Dictionary<string, object> Parse(string text)
        {
            List<Line> lines = Tokenize(text);
            if (lines.Count == 0) throw new YamlException("the configuration file is empty");

            int i = 0;
            object root = ParseNode(lines, ref i, lines[0].Indent);
            if (i < lines.Count)
                throw new YamlException(Where(lines[i]) + "unexpected indentation");

            var map = root as Dictionary<string, object>;
            if (map == null)
                throw new YamlException("the configuration file must start with key: value entries");
            return map;
        }

        // ------------------------------------------------------------------ tokenizing

        private static List<Line> Tokenize(string text)
        {
            var result = new List<Line>();
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int n = 0; n < raw.Length; n++)
            {
                string s = StripComment(raw[n]);
                if (s.Trim().Length == 0) continue;
                if (s.TrimEnd() == "---") continue;   // tolerate a document marker

                int indent = 0;
                while (indent < s.Length && s[indent] == ' ') indent++;
                if (indent < s.Length && s[indent] == '\t')
                    throw new YamlException("line " + (n + 1) + ": tabs cannot be used for indentation in YAML, use spaces");

                string content = s.Substring(indent).TrimEnd();
                var line = new Line();
                line.Indent = indent;
                line.ContentIndent = indent;
                line.Number = n + 1;

                if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal))
                {
                    line.IsSeqItem = true;
                    if (content == "-")
                    {
                        line.Text = "";
                        line.ContentIndent = indent + 2;
                    }
                    else
                    {
                        string after = content.Substring(2);
                        string rest = after.TrimStart();
                        line.Text = rest;
                        line.ContentIndent = indent + 2 + (after.Length - rest.Length);
                    }
                }
                else
                {
                    line.Text = content;
                }
                result.Add(line);
            }
            return result;
        }

        /// <summary>Removes a trailing # comment, ignoring '#' inside quotes.</summary>
        private static string StripComment(string s)
        {
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (c == '#' && !inSingle && !inDouble)
                {
                    // A '#' only starts a comment at the start of a line or after a space.
                    if (i == 0 || s[i - 1] == ' ' || s[i - 1] == '\t') return s.Substring(0, i);
                }
            }
            return s;
        }

        // ------------------------------------------------------------------ parsing

        private static object ParseNode(List<Line> lines, ref int i, int indent)
        {
            if (i >= lines.Count) return "";
            if (lines[i].IsSeqItem && lines[i].Indent == indent) return ParseSeq(lines, ref i, indent);
            return ParseMap(lines, ref i, indent);
        }

        private static List<object> ParseSeq(List<Line> lines, ref int i, int indent)
        {
            var list = new List<object>();
            while (i < lines.Count && lines[i].Indent == indent && lines[i].IsSeqItem)
            {
                Line ln = lines[i];
                if (ln.Text.Length == 0)
                {
                    // "-" alone: the item is the indented block that follows.
                    i++;
                    if (i < lines.Count && lines[i].Indent > indent)
                        list.Add(ParseNode(lines, ref i, lines[i].Indent));
                    else
                        list.Add("");
                    continue;
                }

                if (FindKeySeparator(ln.Text) < 0)
                {
                    // A plain item: "- *.tmp" or "- 60".
                    list.Add(ParseScalarOrFlow(ln.Text, ln));
                    i++;
                    continue;
                }

                // A mapping item. Re-read the line as ordinary content starting at
                // ContentIndent, so "- name: a" followed by an aligned "path: b"
                // parses as a single mapping.
                int contentIndent = ln.ContentIndent;
                ln.Indent = contentIndent;
                ln.IsSeqItem = false;
                list.Add(ParseNode(lines, ref i, contentIndent));
            }
            return list;
        }

        private static Dictionary<string, object> ParseMap(List<Line> lines, ref int i, int indent)
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (i < lines.Count && lines[i].Indent == indent && !lines[i].IsSeqItem)
            {
                Line ln = lines[i];
                string key, value;
                SplitKeyValue(ln, out key, out value);

                if (map.ContainsKey(key))
                    throw new YamlException(Where(ln) + "duplicate key '" + key + "'");

                if (value.Length > 0)
                {
                    map[key] = ParseScalarOrFlow(value, ln);
                    i++;
                    continue;
                }

                // Empty value: the value is the block that follows. A nested sequence
                // is allowed to sit at the same indentation as its key.
                i++;
                if (i < lines.Count &&
                    (lines[i].Indent > indent || (lines[i].Indent == indent && lines[i].IsSeqItem)))
                {
                    map[key] = ParseNode(lines, ref i, lines[i].Indent);
                }
                else
                {
                    map[key] = "";
                }
            }
            return map;
        }

        /// <summary>
        /// Index of the ':' that separates a key from its value, or -1 if the text
        /// is a plain scalar. A colon only separates when followed by a space or end
        /// of line - that is what keeps a Windows path like "D:\data" intact.
        /// </summary>
        private static int FindKeySeparator(string s)
        {
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
                if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
                if (c != ':' || inSingle || inDouble) continue;
                if (i + 1 < s.Length && s[i + 1] != ' ') continue;
                return i;
            }
            return -1;
        }

        private static void SplitKeyValue(Line ln, out string key, out string value)
        {
            string s = ln.Text;
            int i = FindKeySeparator(s);
            if (i < 0)
                throw new YamlException(Where(ln) + "expected 'key: value' but found: " + s);

            key = Unquote(s.Substring(0, i).Trim());
            value = i + 1 < s.Length ? s.Substring(i + 1).Trim() : "";
            if (key.Length == 0) throw new YamlException(Where(ln) + "missing key before ':'");
        }

        private static object ParseScalarOrFlow(string v, Line ln)
        {
            if (v.StartsWith("[", StringComparison.Ordinal))
            {
                if (!v.EndsWith("]", StringComparison.Ordinal))
                    throw new YamlException(Where(ln) + "unterminated [ ... ] list");
                var items = new List<object>();
                foreach (string part in SplitFlow(v.Substring(1, v.Length - 2)))
                {
                    string p = part.Trim();
                    if (p.Length > 0) items.Add(Scalar(p));
                }
                return items;
            }
            if (v.StartsWith("{", StringComparison.Ordinal))
            {
                if (!v.EndsWith("}", StringComparison.Ordinal))
                    throw new YamlException(Where(ln) + "unterminated { ... } mapping");
                var m = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in SplitFlow(v.Substring(1, v.Length - 2)))
                {
                    string p = part.Trim();
                    if (p.Length == 0) continue;
                    int c = p.IndexOf(':');
                    if (c < 0) throw new YamlException(Where(ln) + "expected 'key: value' inside { }");
                    m[Unquote(p.Substring(0, c).Trim())] = Scalar(p.Substring(c + 1).Trim());
                }
                return m;
            }
            return Scalar(v);
        }

        private static IEnumerable<string> SplitFlow(string s)
        {
            var parts = new List<string>();
            bool inSingle = false, inDouble = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (c == ',' && !inSingle && !inDouble)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static string Scalar(string v)
        {
            return Unquote(v);
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            {
                var sb = new StringBuilder(v.Length);
                for (int i = 1; i < v.Length - 1; i++)
                {
                    char c = v[i];
                    if (c == '\\' && i + 1 < v.Length - 1)
                    {
                        char n = v[++i];
                        switch (n)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            default: sb.Append('\\').Append(n); break;
                        }
                        continue;
                    }
                    sb.Append(c);
                }
                return sb.ToString();
            }
            if (v.Length >= 2 && v[0] == '\'' && v[v.Length - 1] == '\'')
                return v.Substring(1, v.Length - 2).Replace("''", "'");
            return v;
        }

        private static string Where(Line ln)
        {
            return "line " + ln.Number + ": ";
        }

        // ------------------------------------------------------------------ typed access

        public static Dictionary<string, object> AsMap(object o)
        {
            return o as Dictionary<string, object>;
        }

        public static List<object> AsList(object o)
        {
            if (o == null) return null;
            var l = o as List<object>;
            if (l != null) return l;
            string s = o as string;
            if (s != null && s.Length == 0) return new List<object>();
            return null;
        }

        public static bool TryBool(object o, out bool value)
        {
            value = false;
            string s = o as string;
            if (s == null) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "true": case "yes": case "on": case "1": value = true; return true;
                case "false": case "no": case "off": case "0": value = false; return true;
            }
            return false;
        }

        public static bool TryDouble(object o, out double value)
        {
            value = 0;
            string s = o as string;
            if (s == null) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
