using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WheelLeg.Config
{
    public enum YamlKind
    {
        Scalar,
        Map,
        Seq
    }

    /// <summary>
    /// Node of a parsed YAML document. Only the subset of YAML used by this
    /// project's config files is represented: block maps, block sequences,
    /// flow sequences of scalars, and plain/quoted scalars.
    /// </summary>
    public sealed class YamlNode
    {
        public readonly YamlKind Kind;
        public readonly string Scalar;
        public readonly Dictionary<string, YamlNode> Map;
        public readonly List<YamlNode> Seq;
        public readonly int Line;

        YamlNode(YamlKind kind, string scalar, Dictionary<string, YamlNode> map, List<YamlNode> seq, int line)
        {
            Kind = kind;
            Scalar = scalar;
            Map = map;
            Seq = seq;
            Line = line;
        }

        public static YamlNode NewScalar(string value, int line)
        {
            return new YamlNode(YamlKind.Scalar, value, null, null, line);
        }

        public static YamlNode NewMap(int line)
        {
            return new YamlNode(YamlKind.Map, null, new Dictionary<string, YamlNode>(StringComparer.Ordinal), null, line);
        }

        public static YamlNode NewSeq(int line)
        {
            return new YamlNode(YamlKind.Seq, null, null, new List<YamlNode>(), line);
        }

        /// <summary>
        /// Deep-merges <paramref name="over"/> onto a copy of <paramref name="under"/>.
        /// Maps merge key by key; scalars and sequences are replaced wholesale.
        /// </summary>
        public static YamlNode Merge(YamlNode under, YamlNode over)
        {
            if (under == null) return over;
            if (over == null) return under;
            if (under.Kind != YamlKind.Map || over.Kind != YamlKind.Map) return over;

            YamlNode merged = NewMap(under.Line);
            foreach (KeyValuePair<string, YamlNode> kv in under.Map)
                merged.Map[kv.Key] = kv.Value;
            foreach (KeyValuePair<string, YamlNode> kv in over.Map)
            {
                YamlNode existing;
                merged.Map[kv.Key] = merged.Map.TryGetValue(kv.Key, out existing)
                    ? Merge(existing, kv.Value)
                    : kv.Value;
            }
            return merged;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case YamlKind.Scalar: return Scalar;
                case YamlKind.Seq: return "[" + string.Join(", ", Seq.ConvertAll(n => n.ToString()).ToArray()) + "]";
                default:
                    StringBuilder sb = new StringBuilder("{");
                    bool first = true;
                    foreach (KeyValuePair<string, YamlNode> kv in Map)
                    {
                        if (!first) sb.Append(", ");
                        sb.Append(kv.Key).Append(": ").Append(kv.Value);
                        first = false;
                    }
                    return sb.Append('}').ToString();
            }
        }
    }

    public sealed class YamlException : Exception
    {
        public YamlException(string source, int line, string message)
            : base(string.Format(CultureInfo.InvariantCulture, "{0}:{1}: {2}", source, line, message))
        {
        }
    }

    /// <summary>
    /// Indentation-based parser for the YAML subset described on <see cref="YamlNode"/>.
    /// Deliberately rejects everything outside that subset rather than guessing, so a
    /// malformed config fails at load instead of silently taking a default.
    /// </summary>
    public static class YamlParser
    {
        struct Line
        {
            public int Indent;
            public string Text;
            public int Number;
        }

        public static YamlNode Parse(string text, string sourceName)
        {
            List<Line> lines = Tokenize(text, sourceName);
            if (lines.Count == 0) return YamlNode.NewMap(1);

            int index = 0;
            YamlNode root = ParseBlock(lines, ref index, lines[0].Indent, sourceName);
            if (index != lines.Count)
                throw new YamlException(sourceName, lines[index].Number, "unexpected indentation");
            return root;
        }

        static List<Line> Tokenize(string text, string sourceName)
        {
            List<Line> lines = new List<Line>();
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < raw.Length; i++)
            {
                string line = raw[i];
                int indent = 0;
                while (indent < line.Length && line[indent] == ' ') indent++;
                if (indent < line.Length && line[indent] == '\t')
                    throw new YamlException(sourceName, i + 1, "tabs are not allowed for indentation");

                string body = StripComment(line.Substring(indent));
                body = body.TrimEnd();
                if (body.Length == 0) continue;

                lines.Add(new Line { Indent = indent, Text = body, Number = i + 1 });
            }
            return lines;
        }

        static string StripComment(string s)
        {
            char quote = '\0';
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '#' && (i == 0 || s[i - 1] == ' '))
                {
                    return s.Substring(0, i);
                }
            }
            return s;
        }

        static YamlNode ParseBlock(List<Line> lines, ref int index, int indent, string source)
        {
            return lines[index].Text.StartsWith("-", StringComparison.Ordinal)
                ? ParseSeq(lines, ref index, indent, source)
                : ParseMap(lines, ref index, indent, source);
        }

        static YamlNode ParseMap(List<Line> lines, ref int index, int indent, string source)
        {
            YamlNode map = YamlNode.NewMap(lines[index].Number);
            while (index < lines.Count && lines[index].Indent == indent)
            {
                Line line = lines[index];
                if (line.Text.StartsWith("-", StringComparison.Ordinal))
                    throw new YamlException(source, line.Number, "sequence item inside a mapping block");

                int colon = FindKeySeparator(line.Text);
                if (colon < 0)
                    throw new YamlException(source, line.Number, "expected 'key: value'");

                string key = Unquote(line.Text.Substring(0, colon).Trim());
                if (key.Length == 0)
                    throw new YamlException(source, line.Number, "empty key");
                if (map.Map.ContainsKey(key))
                    throw new YamlException(source, line.Number, "duplicate key '" + key + "'");

                string rest = line.Text.Substring(colon + 1).Trim();
                index++;

                if (rest.Length > 0)
                {
                    map.Map[key] = ParseInlineValue(rest, line.Number, source);
                    continue;
                }

                if (index < lines.Count && lines[index].Indent > indent)
                    map.Map[key] = ParseBlock(lines, ref index, lines[index].Indent, source);
                else
                    map.Map[key] = YamlNode.NewMap(line.Number);
            }
            return map;
        }

        static YamlNode ParseSeq(List<Line> lines, ref int index, int indent, string source)
        {
            YamlNode seq = YamlNode.NewSeq(lines[index].Number);
            while (index < lines.Count && lines[index].Indent == indent
                   && lines[index].Text.StartsWith("-", StringComparison.Ordinal))
            {
                Line line = lines[index];
                string rest = line.Text.Length > 1 ? line.Text.Substring(1).Trim() : string.Empty;
                index++;

                if (rest.Length > 0)
                {
                    if (FindKeySeparator(rest) >= 0)
                        throw new YamlException(source, line.Number, "sequences of mappings are not supported");
                    seq.Seq.Add(ParseInlineValue(rest, line.Number, source));
                    continue;
                }

                if (index < lines.Count && lines[index].Indent > indent)
                    seq.Seq.Add(ParseBlock(lines, ref index, lines[index].Indent, source));
                else
                    throw new YamlException(source, line.Number, "empty sequence item");
            }
            return seq;
        }

        /// <summary>Index of the ':' that separates a key from its value, or -1.</summary>
        static int FindKeySeparator(string s)
        {
            char quote = '\0';
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'') quote = c;
                else if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (c == ':' && depth == 0 && (i + 1 == s.Length || s[i + 1] == ' '))
                    return i;
            }
            return -1;
        }

        static YamlNode ParseInlineValue(string text, int lineNumber, string source)
        {
            if (!text.StartsWith("[", StringComparison.Ordinal))
                return YamlNode.NewScalar(Unquote(text), lineNumber);

            if (!text.EndsWith("]", StringComparison.Ordinal))
                throw new YamlException(source, lineNumber, "unterminated flow sequence");

            YamlNode seq = YamlNode.NewSeq(lineNumber);
            string inner = text.Substring(1, text.Length - 2).Trim();
            if (inner.Length == 0) return seq;

            foreach (string part in SplitFlow(inner, lineNumber, source))
            {
                string item = part.Trim();
                if (item.Length == 0)
                    throw new YamlException(source, lineNumber, "empty flow sequence item");
                if (item.StartsWith("[", StringComparison.Ordinal))
                    throw new YamlException(source, lineNumber, "nested flow sequences are not supported");
                seq.Seq.Add(YamlNode.NewScalar(Unquote(item), lineNumber));
            }
            return seq;
        }

        static List<string> SplitFlow(string s, int lineNumber, string source)
        {
            List<string> parts = new List<string>();
            char quote = '\0';
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                }
                else if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '[' || c == ']')
                {
                    throw new YamlException(source, lineNumber, "nested flow sequences are not supported");
                }
                else if (c == ',')
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        static string Unquote(string s)
        {
            if (s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[s.Length - 1] == s[0])
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
