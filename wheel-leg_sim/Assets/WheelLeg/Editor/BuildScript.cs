using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using WheelLeg.Config;
using Debug = UnityEngine.Debug;

namespace WheelLeg.EditorTools
{
    /// <summary>
    /// Produces the headless player and the two things that cannot be decided at runtime:
    /// the Enhanced Determinism physics flag, which Unity only exposes as a project setting,
    /// and the commit stamp recorded alongside every run.
    ///
    /// Callable from a shell for CI or a remote build:
    ///   Unity -batchmode -quit -projectPath . -executeMethod WheelLeg.EditorTools.BuildScript.BuildLinuxServer
    /// </summary>
    public static class BuildScript
    {
        const string CommitResourcePath = "Assets/Resources/build_commit.txt";
        const string DynamicsManagerPath = "ProjectSettings/DynamicsManager.asset";

        [MenuItem("WheelLeg/Build/Linux Dedicated Server", false, 40)]
        public static void BuildLinuxServer()
        {
            Build(BuildTarget.StandaloneLinux64, StandaloneBuildSubtarget.Server, "Builds/linux-server/sim.x86_64");
        }

        [MenuItem("WheelLeg/Build/Windows Player", false, 41)]
        public static void BuildWindowsPlayer()
        {
            Build(BuildTarget.StandaloneWindows64, StandaloneBuildSubtarget.Player, "Builds/win64/sim.exe");
        }

        static void Build(BuildTarget target, StandaloneBuildSubtarget subtarget, string relativeOutput)
        {
            string projectRoot = Directory.GetCurrentDirectory();
            string outputPath = Path.Combine(projectRoot, relativeOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            SimConfig config = LoadConfigOrNull();
            if (config != null) BakeEnhancedDeterminism(config.Physics.EnhancedDeterminism);
            WriteCommitStamp(projectRoot);

            if (EditorBuildSettings.scenes.Length == 0)
            {
                Debug.LogError("[WheelLeg] no scene in build settings. Run WheelLeg/Build Simulation Scene first.");
                return;
            }

            BuildPlayerOptions options = new BuildPlayerOptions();
            options.scenes = new[] { EditorBuildSettings.scenes[0].path };
            options.locationPathName = outputPath;
            options.target = target;
            options.subtarget = (int)subtarget;
            options.options = BuildOptions.None;

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError("[WheelLeg] build failed: " + report.summary.result);
                return;
            }

            CopyConfigNextToPlayer(projectRoot, Path.GetDirectoryName(outputPath));
            Debug.Log("[WheelLeg] built " + outputPath + " (" + report.summary.totalSize + " bytes)");
        }

        static SimConfig LoadConfigOrNull()
        {
            try
            {
                return ConfigLoader.Load(new CommandLine(new string[0]));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WheelLeg] could not read config while building, leaving physics "
                    + "settings untouched: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Writes the Enhanced Determinism flag into the physics project settings from the
        /// same config the runtime reads. There is no runtime API for it, so a build is the
        /// only place the two can be kept consistent; without this the flag silently keeps
        /// whatever value it had, which is exactly the kind of hidden difference that makes a
        /// server run non-comparable (SERVER_OPS_1.md section 4-2).
        /// </summary>
        public static void BakeEnhancedDeterminism(bool enabled)
        {
            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath(DynamicsManagerPath);
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[WheelLeg] could not open " + DynamicsManagerPath);
                return;
            }

            SerializedObject settings = new SerializedObject(assets[0]);
            SerializedProperty property = settings.FindProperty("m_EnableEnhancedDeterminism");
            if (property == null)
            {
                Debug.LogWarning("[WheelLeg] m_EnableEnhancedDeterminism not found in " + DynamicsManagerPath);
                return;
            }

            if (property.boolValue == enabled)
            {
                Debug.Log("[WheelLeg] enhanced determinism already " + enabled);
                return;
            }

            property.boolValue = enabled;
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            Debug.Log("[WheelLeg] enhanced determinism baked to " + enabled);
        }

        /// <summary>
        /// Records the commit the build came from. SERVER_OPS_1.md section 2-4: the run
        /// arguments and the commit hash are the only way to answer "what produced this
        /// result" months later.
        /// </summary>
        static void WriteCommitStamp(string projectRoot)
        {
            string commit = TryReadGitCommit(projectRoot);
            Directory.CreateDirectory(Path.Combine(projectRoot, "Assets/Resources"));
            File.WriteAllText(Path.Combine(projectRoot, CommitResourcePath), commit);
            AssetDatabase.ImportAsset(CommitResourcePath);
            Debug.Log("[WheelLeg] build commit stamp: " + commit);
        }

        static string TryReadGitCommit(string projectRoot)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo("git", "rev-parse HEAD");
                info.WorkingDirectory = projectRoot;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                info.UseShellExecute = false;
                info.CreateNoWindow = true;

                using (Process process = Process.Start(info))
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0 && output.Length > 0) return output;
                }
            }
            catch (Exception)
            {
                // git is not required to build; the stamp just records that it was absent.
            }
            return "no-git";
        }

        /// <summary>
        /// Copies config/ next to the player. ConfigLoader resolves its default directory as
        /// &lt;player dir&gt;/config, so the build is self-contained and needs no absolute path.
        /// </summary>
        static void CopyConfigNextToPlayer(string projectRoot, string playerDirectory)
        {
            string source = Path.Combine(projectRoot, "config");
            string destination = Path.Combine(playerDirectory, "config");
            if (!Directory.Exists(source))
            {
                Debug.LogWarning("[WheelLeg] no config directory at " + source);
                return;
            }

            CopyDirectory(source, destination);
            Debug.Log("[WheelLeg] config copied to " + destination);
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
