using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WheelLeg.Config
{
    /// <summary>
    /// Builds the process configuration from files plus command line, in this order:
    ///   config/env.yaml -> config/reward.yaml (as the 'reward' subtree)
    ///   -> config/profiles/&lt;profile&gt;.yaml -> --set / convenience flags.
    /// Switching between local and server is a profile swap; no code path differs.
    /// </summary>
    public static class ConfigLoader
    {
        public const string DefaultProfile = "local";

        public static string DefaultConfigDirectory()
        {
            // Editor: <project>/Assets/.. -> <project>. Player: <exe>/<name>_Data/.. -> <exe dir>.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "config"));
        }

        public static SimConfig Load(CommandLine cli)
        {
            string configDir = cli.Get("--config-dir", DefaultConfigDirectory());
            if (!Directory.Exists(configDir))
                throw new ConfigException("config directory not found: " + configDir);

            string profile = cli.Get("--profile", DefaultProfile);
            List<string> provenance = new List<string>();

            YamlNode root = LoadFile(Path.Combine(configDir, "env.yaml"), provenance);

            YamlNode rewardFile = LoadFile(Path.Combine(configDir, "reward.yaml"), provenance);
            YamlNode rewardWrapper = YamlNode.NewMap(rewardFile.Line);
            rewardWrapper.Map["reward"] = rewardFile;
            root = YamlNode.Merge(root, rewardWrapper);

            string profilePath = Path.Combine(Path.Combine(configDir, "profiles"), profile + ".yaml");
            if (!File.Exists(profilePath))
                throw new ConfigException("profile not found: " + profilePath);
            root = YamlNode.Merge(root, LoadFile(profilePath, provenance));

            // An extra --config file, if given, overrides the profile.
            string extra = cli.Get("--config", null);
            if (!string.IsNullOrEmpty(extra))
            {
                if (!File.Exists(extra))
                    throw new ConfigException("--config file not found: " + extra);
                root = YamlNode.Merge(root, LoadFile(extra, provenance));
            }

            foreach (KeyValuePair<string, string> ov in cli.Overrides())
            {
                ApplyOverride(root, ov.Key, ov.Value);
                provenance.Add("cli: " + ov.Key + "=" + ov.Value);
            }

            SimConfig config = SimConfig.Read(ConfigNode.Root(root));
            config.Provenance = provenance;
            return config;
        }

        static YamlNode LoadFile(string path, List<string> provenance)
        {
            if (!File.Exists(path))
                throw new ConfigException("config file not found: " + path);
            YamlNode node = YamlParser.Parse(File.ReadAllText(path), Path.GetFileName(path));
            if (node.Kind != YamlKind.Map)
                throw new ConfigException(path + " must contain a mapping at the top level");
            provenance.Add("file: " + path);
            return node;
        }

        /// <summary>
        /// Sets a dotted path to a literal value. The path must already exist: creating
        /// keys on the fly would let a typo'd --set look like it took effect.
        /// </summary>
        static void ApplyOverride(YamlNode root, string dottedPath, string literal)
        {
            string[] parts = dottedPath.Split('.');
            YamlNode node = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                YamlNode next;
                if (node.Kind != YamlKind.Map || !node.Map.TryGetValue(parts[i], out next))
                    throw new ConfigException("override path '" + dottedPath + "' does not exist in the config");
                node = next;
            }

            string leaf = parts[parts.Length - 1];
            if (node.Kind != YamlKind.Map || !node.Map.ContainsKey(leaf))
                throw new ConfigException("override path '" + dottedPath + "' does not exist in the config");

            node.Map[leaf] = YamlParser.Parse(leaf + ": " + literal, "--set").Map[leaf];
        }
    }
}
