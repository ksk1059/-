using System;
using System.Collections.Generic;
using System.Globalization;

namespace WheelLeg.Config
{
    /// <summary>
    /// Parses the process command line. Unknown arguments are ignored on purpose:
    /// mlagents-learn appends its own flags (--port, -logFile, ...) to the player it
    /// launches, and treating those as errors would make training impossible to start.
    /// </summary>
    public sealed class CommandLine
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly List<string> _overrides = new List<string>();
        readonly string[] _raw;

        /// <summary>Flags that take a value and map onto a config path.</summary>
        static readonly Dictionary<string, string> ValueFlags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "--seed", "run.seed" },
            { "--areas", "run.areas" },
            { "--out", "run.out" },
            { "--mode", "run.mode" },
            { "--controller", "run.controller_type" },
            { "--model", "run.model" },
            { "--time-scale", "run.time_scale" },
            { "--max-steps", "episode.max_steps" },
            { "--log-format", "logging.format" },
            { "--logging", "logging.enabled" },
            { "--determinism", "physics.enhanced_determinism" }
        };

        /// <summary>Flags consumed by the loader itself rather than mapped to a config path.</summary>
        static readonly HashSet<string> LoaderFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "--config-dir", "--config", "--profile", "--commit"
        };

        public CommandLine(string[] args)
        {
            _raw = args ?? new string[0];
            for (int i = 0; i < _raw.Length; i++)
            {
                string a = _raw[i];
                if (a == "--set")
                {
                    if (i + 1 >= _raw.Length)
                        throw new ConfigException("--set requires key.path=value");
                    _overrides.Add(_raw[++i]);
                    continue;
                }

                if (!LoaderFlags.Contains(a) && !ValueFlags.ContainsKey(a)) continue;

                if (i + 1 >= _raw.Length || _raw[i + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ConfigException(a + " requires a value");
                _values[a] = _raw[++i];
            }
        }

        public string[] Raw { get { return _raw; } }

        public string Get(string flag, string fallback)
        {
            string v;
            return _values.TryGetValue(flag, out v) ? v : fallback;
        }

        public bool Has(string flag)
        {
            return _values.ContainsKey(flag);
        }

        /// <summary>
        /// Every override as (dotted config path, value), convenience flags first so an
        /// explicit --set can still win over them.
        /// </summary>
        public List<KeyValuePair<string, string>> Overrides()
        {
            List<KeyValuePair<string, string>> result = new List<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> kv in ValueFlags)
            {
                string v;
                if (_values.TryGetValue(kv.Key, out v))
                    result.Add(new KeyValuePair<string, string>(kv.Value, v));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            foreach (string spec in _overrides)
            {
                int eq = spec.IndexOf('=');
                if (eq <= 0)
                    throw new ConfigException("--set expects key.path=value, got '" + spec + "'");
                result.Add(new KeyValuePair<string, string>(
                    spec.Substring(0, eq).Trim(), spec.Substring(eq + 1).Trim()));
            }
            return result;
        }

        public string Describe()
        {
            return string.Join(" ", _raw);
        }

        public static string Quote(string s)
        {
            return s.IndexOf(' ') >= 0
                ? "\"" + s + "\""
                : s;
        }

        public static string FormatInvariant(float v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
