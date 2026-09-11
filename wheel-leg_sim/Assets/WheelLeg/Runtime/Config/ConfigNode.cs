using System;
using System.Collections.Generic;
using System.Globalization;

namespace WheelLeg.Config
{
    public sealed class ConfigException : Exception
    {
        public ConfigException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Typed, path-aware reader over a <see cref="YamlNode"/> tree.
    /// Every read is explicit (no reflection, so IL2CPP stripping cannot break it) and
    /// every key touched is recorded, which lets <see cref="AssertFullyConsumed"/> reject
    /// typos and stale keys instead of letting them silently fall back to a default.
    /// </summary>
    public sealed class ConfigNode
    {
        readonly YamlNode _node;
        readonly string _path;
        readonly HashSet<string> _consumed;

        ConfigNode(YamlNode node, string path, HashSet<string> consumed)
        {
            _node = node;
            _path = path;
            _consumed = consumed;
        }

        public static ConfigNode Root(YamlNode node)
        {
            if (node.Kind != YamlKind.Map)
                throw new ConfigException("config root must be a mapping");
            return new ConfigNode(node, string.Empty, new HashSet<string>(StringComparer.Ordinal));
        }

        public string Path { get { return _path.Length == 0 ? "<root>" : _path; } }

        public IEnumerable<string> Keys
        {
            get
            {
                RequireMap();
                return _node.Map.Keys;
            }
        }

        public bool Has(string key)
        {
            RequireMap();
            return _node.Map.ContainsKey(key);
        }

        public ConfigNode Child(string key)
        {
            ConfigNode child;
            if (!TryChild(key, out child))
                throw new ConfigException(Join(key) + " is required but missing");
            return child;
        }

        public bool TryChild(string key, out ConfigNode child)
        {
            RequireMap();
            YamlNode raw;
            if (!_node.Map.TryGetValue(key, out raw))
            {
                child = null;
                return false;
            }
            _consumed.Add(Join(key));
            child = new ConfigNode(raw, Join(key), _consumed);
            return true;
        }

        public string Str(string key)
        {
            return Child(key).AsString();
        }

        public string Str(string key, string fallback)
        {
            ConfigNode c;
            return TryChild(key, out c) ? c.AsString() : fallback;
        }

        public float Float(string key)
        {
            return Child(key).AsFloat();
        }

        public int Int(string key)
        {
            return Child(key).AsInt();
        }

        public long Long(string key)
        {
            return Child(key).AsLong();
        }

        public bool Bool(string key)
        {
            return Child(key).AsBool();
        }

        /// <summary>Reads a fixed-length numeric sequence, e.g. a [min, max] range.</summary>
        public float[] Floats(string key, int expectedCount)
        {
            ConfigNode c = Child(key);
            float[] values = c.AsFloatArray();
            if (values.Length != expectedCount)
                throw new ConfigException(string.Format(CultureInfo.InvariantCulture,
                    "{0} must have exactly {1} entries but has {2}", c.Path, expectedCount, values.Length));
            return values;
        }

        public float[] FloatArray(string key)
        {
            return Child(key).AsFloatArray();
        }

        public int[] IntArray(string key)
        {
            ConfigNode c = Child(key);
            c.RequireSeq();
            int[] values = new int[c._node.Seq.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = new ConfigNode(c._node.Seq[i], c._path + "[" + i + "]", _consumed).AsInt();
            return values;
        }

        public string[] StringArray(string key)
        {
            ConfigNode c = Child(key);
            c.RequireSeq();
            string[] values = new string[c._node.Seq.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = new ConfigNode(c._node.Seq[i], c._path + "[" + i + "]", _consumed).AsString();
            return values;
        }

        public string AsString()
        {
            if (_node.Kind != YamlKind.Scalar)
                throw new ConfigException(Path + " must be a scalar");
            return _node.Scalar;
        }

        public float AsFloat()
        {
            string s = AsString();
            float v;
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new ConfigException(Path + " is not a number: '" + s + "'");
            return v;
        }

        public int AsInt()
        {
            string s = AsString();
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new ConfigException(Path + " is not an integer: '" + s + "'");
            return v;
        }

        public long AsLong()
        {
            string s = AsString();
            long v;
            if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new ConfigException(Path + " is not an integer: '" + s + "'");
            return v;
        }

        public bool AsBool()
        {
            string s = AsString();
            if (s == "true" || s == "True" || s == "yes" || s == "on" || s == "1") return true;
            if (s == "false" || s == "False" || s == "no" || s == "off" || s == "0") return false;
            throw new ConfigException(Path + " is not a boolean: '" + s + "'");
        }

        public float[] AsFloatArray()
        {
            RequireSeq();
            float[] values = new float[_node.Seq.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = new ConfigNode(_node.Seq[i], _path + "[" + i + "]", _consumed).AsFloat();
            return values;
        }

        /// <summary>
        /// Throws if any key in the document was never read. Catches typos and keys left
        /// behind by an edit, both of which would otherwise look like a working config.
        /// </summary>
        public void AssertFullyConsumed()
        {
            List<string> unused = new List<string>();
            Collect(_node, _path, unused);
            if (unused.Count == 0) return;
            unused.Sort(StringComparer.Ordinal);
            throw new ConfigException("unknown config keys: " + string.Join(", ", unused.ToArray()));
        }

        void Collect(YamlNode node, string path, List<string> unused)
        {
            if (node.Kind != YamlKind.Map) return;
            foreach (KeyValuePair<string, YamlNode> kv in node.Map)
            {
                string childPath = path.Length == 0 ? kv.Key : path + "." + kv.Key;
                if (!_consumed.Contains(childPath)) unused.Add(childPath);
                else Collect(kv.Value, childPath, unused);
            }
        }

        void RequireMap()
        {
            if (_node.Kind != YamlKind.Map)
                throw new ConfigException(Path + " must be a mapping");
        }

        void RequireSeq()
        {
            if (_node.Kind != YamlKind.Seq)
                throw new ConfigException(Path + " must be a sequence");
        }

        string Join(string key)
        {
            return _path.Length == 0 ? key : _path + "." + key;
        }
    }
}
