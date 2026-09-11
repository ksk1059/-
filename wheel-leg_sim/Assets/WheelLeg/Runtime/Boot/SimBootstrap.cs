using System;
using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;
using WheelLeg.Agent;
using WheelLeg.Config;
using WheelLeg.Control;
using WheelLeg.Core;
using WheelLeg.Env;
using WheelLeg.Logging;
using WheelLeg.Robot;

namespace WheelLeg.Boot
{
    /// <summary>
    /// Process entry point. Reads the command line, loads the config, builds the requested
    /// number of training areas and runs either training or evaluation from the same build
    /// and the same scene. Nothing here branches on "local versus server"; the only inputs
    /// are config values (SPEC.md section 0).
    /// </summary>
    public sealed class SimBootstrap : MonoBehaviour
    {
        public const int ExitOk = 0;
        public const int ExitConfigError = 2;
        public const int ExitSetupError = 3;
        public const int ExitRuntimeError = 4;

        [SerializeField] GameObject _robotPrefab;
        [SerializeField] string _behaviorName = "WheelLegGo2W";

        SimConfig _config;
        readonly List<TrainingArea> _areas = new List<TrainingArea>();
        readonly List<FrameLogger> _loggers = new List<FrameLogger>();
        readonly List<WheelLegAgent> _agents = new List<WheelLegAgent>();
        readonly List<IDisposable> _disposableControllers = new List<IDisposable>();
        ITerrainSeedSource _seedSource;
        string _outputDirectory;
        bool _shuttingDown;
        bool _torqueProbeWritten;

        void Awake()
        {
            try
            {
                Boot();
            }
            catch (ConfigException e)
            {
                Abort(ExitConfigError, "configuration rejected: " + e.Message);
            }
            catch (Exception e)
            {
                Abort(ExitSetupError, "setup failed: " + e);
            }
        }

        void Boot()
        {
            CommandLine cli = new CommandLine(Environment.GetCommandLineArgs());
            _config = ConfigLoader.Load(cli);

            ApplyPhysicsSettings();

            _outputDirectory = ResolveOutputDirectory(_config.Run.OutDir);
            Directory.CreateDirectory(_outputDirectory);
            RunManifest.Write(_outputDirectory, _config, cli, ResolveCommitHash(cli));

            _seedSource = _config.Run.Mode == RunMode.Eval
                ? (ITerrainSeedSource)new EvaluationSeedSource(_config.Seeds)
                : new TrainingSeedSource(_config.Seeds, _config.Run.Seed, _config.Run.Areas);

            if (_robotPrefab == null)
            {
                _robotPrefab = Resources.Load<GameObject>("Go2W");
                if (_robotPrefab == null)
                    throw new InvalidOperationException(
                        "robot prefab is not assigned and Resources/Go2W was not found; "
                        + "run WheelLeg/Import Go2-W URDF then WheelLeg/Build Simulation Scene");
            }

            for (int i = 0; i < _config.Run.Areas; i++) BuildArea(i);

            VerifyNoStrayRobots();
            VerifyActionSourceExists();

            Debug.Log(string.Format(
                "[WheelLeg] mode={0} controller={1} seed={2} areas={3} out={4} log={5}",
                _config.Run.Mode, _config.Run.ControllerName, _config.Run.Seed,
                _config.Run.Areas, _outputDirectory,
                _config.Logging.Enabled ? _config.Logging.Format.ToString() : "off"));
        }

        void ApplyPhysicsSettings()
        {
            PhysicsConfig p = _config.Physics;
            Time.fixedDeltaTime = p.FixedTimestep;
            Physics.defaultSolverIterations = p.SolverIterations;
            Physics.defaultSolverVelocityIterations = p.SolverVelocityIterations;

            if (Mathf.Abs(_config.Run.TimeScale - 1f) > 1e-6f)
                Time.timeScale = _config.Run.TimeScale;

            // Enhanced Determinism is a PhysX scene creation flag that Unity only exposes as a
            // project setting, so it is baked into the player and cannot be toggled here. The
            // editor build tool writes it from this same config value; all this can do is make
            // the intent visible in the log and the manifest.
            Debug.Log("[WheelLeg] physics: dt=" + p.FixedTimestep
                + " solver=" + p.SolverIterations + "/" + p.SolverVelocityIterations
                + " enhanced_determinism(config)=" + p.EnhancedDeterminism
                + " (baked at build time, not settable at runtime)");
        }

        void BuildArea(int areaId)
        {
            EpisodeConfig episode = _config.Episode;
            int column = areaId % episode.AreaGridColumns;
            int row = areaId / episode.AreaGridColumns;
            Vector3 origin = new Vector3(column * episode.AreaSpacingX, 0f, row * episode.AreaSpacingZ);

            GameObject areaObject = new GameObject("Area" + areaId.ToString("D3"));
            areaObject.transform.SetParent(transform, false);
            areaObject.transform.localPosition = origin;
            TrainingArea area = areaObject.AddComponent<TrainingArea>();

            GameObject terrainObject = new GameObject("Terrain");
            terrainObject.transform.SetParent(areaObject.transform, false);
            TerrainSurface terrain = terrainObject.AddComponent<TerrainSurface>();

            GameObject robot = Instantiate(_robotPrefab, areaObject.transform);
            robot.name = "Robot";
            robot.transform.localPosition = Vector3.zero;

            RobotDriver driver = robot.GetComponent<RobotDriver>();
            if (driver == null) driver = robot.AddComponent<RobotDriver>();

            DeterministicRandom areaRng =
                new DeterministicRandom(DeterministicRandom.DeriveSeed(_config.Run.Seed, "area-agent", areaId));

            driver.Initialize(_config.Robot, areaRng, _config.Physics.SelfCollision);
            if (!driver.IsReady)
            {
                throw new InvalidOperationException(
                    "area " + areaId + ": unresolved robot links: " + string.Join(", ", driver.MissingLinks));
            }
            foreach (string warning in driver.ImportWarnings)
                Debug.LogWarning("[WheelLeg] area " + areaId + ": " + warning);

            FrameLogger logger = new FrameLogger(_config, _outputDirectory, areaId);
            _loggers.Add(logger);

            WheelLegAgent agent = robot.GetComponent<WheelLegAgent>();
            if (agent == null) agent = robot.AddComponent<WheelLegAgent>();
            if (robot.GetComponent<DecisionRequester>() == null) robot.AddComponent<DecisionRequester>();

            ConfigureBehavior(robot, areaId);

            IController controller = CreateController();
            area.Configure(_config, areaId, _seedSource, terrain, driver, agent, logger);
            agent.Configure(area, controller, areaRng);

            // Each area gets its own controller instance, so a policy holds one inference worker
            // per area and all of them have to be released explicitly.
            IDisposable disposable = controller as IDisposable;
            if (disposable != null) _disposableControllers.Add(disposable);

            _areas.Add(area);
            _agents.Add(agent);
        }

        void ConfigureBehavior(GameObject robot, int areaId)
        {
            BehaviorParameters behavior = robot.GetComponent<BehaviorParameters>();
            if (behavior == null) behavior = robot.AddComponent<BehaviorParameters>();

            behavior.BehaviorName = _behaviorName;
            behavior.BrainParameters.VectorObservationSize = RobotLayout.ObservationSize;
            behavior.BrainParameters.NumStackedVectorObservations = 1;
            behavior.BrainParameters.ActionSpec = Unity.MLAgents.Actuators.ActionSpec.MakeContinuous(
                RobotLayout.ActionSize);

            // The controller is selected by config alone, never by a second scene or build.
            // HeuristicOnly is what routes Agent.Heuristic, and therefore the IController, to
            // the actuators; Default hands control to a connected trainer instead.
            behavior.BehaviorType = DrivenLocally()
                ? BehaviorType.HeuristicOnly
                : BehaviorType.Default;
        }

        /// <summary>
        /// Refuses to start if the scene contains an articulated body this bootstrap did not
        /// create. Every robot in the run is instantiated here, under this GameObject, so
        /// anything else articulated in the scene is a leftover.
        ///
        /// This happens by running WheelLeg/Import Go2-W URDF while the simulation scene is
        /// open: the importer drops the robot into the active scene and it gets saved with it.
        /// The leftover is never Configure'd, so its drive gains stay zero and it collapses at
        /// the world origin, which is exactly where area 0 spawns. It then acts as terrain the
        /// robot trips over, and because it is a separate articulation the contact counts as
        /// legitimate ground contact and terminates episodes on base_contact. If it also
        /// carries a WheelLegAgent it registers a second agent under the same behaviour name
        /// and feeds the trainer an all-zero observation every step.
        ///
        /// This is the one place in the runtime that searches the whole scene. It runs once,
        /// before any area has stepped, and its entire purpose is to prove that nothing outside
        /// the areas exists; the SERVER_OPS_1.md section 7 ban on global lookups is about
        /// per-area logic reaching outside its own subtree, which this is the opposite of.
        /// </summary>
        void VerifyNoStrayRobots()
        {
            ArticulationBody[] bodies = FindObjectsByType<ArticulationBody>(
                FindObjectsInactive.Include);

            List<string> strays = new List<string>();
            for (int i = 0; i < bodies.Length; i++)
            {
                Transform candidate = bodies[i].transform;
                if (candidate.IsChildOf(transform)) continue;
                string strayName = candidate.root.name;
                if (!strays.Contains(strayName)) strays.Add(strayName);
            }
            if (strays.Count == 0) return;

            throw new InvalidOperationException(
                "the scene contains " + strays.Count + " articulated object(s) this bootstrap "
                + "did not create: " + string.Join(", ", strays.ToArray())
                + ". Every robot in a run is instantiated under " + gameObject.name
                + ", so this is a leftover import sitting in the scene. Delete it from "
                + "Assets/Scenes/WheelLegSim.unity and save the scene, or rebuild the scene "
                + "with WheelLeg/Build Simulation Scene. Left in place it collapses at the "
                + "world origin where area 0 spawns, the robot trips over it, and every "
                + "contact with it terminates an episode on base_contact.");
        }

        /// <summary>
        /// Refuses to start a run that has nothing to produce actions with.
        ///
        /// With controller_type 'policy', BehaviorType.Default asks ML-Agents for a trainer
        /// first, then an assigned model, and falls back to Agent.Heuristic if it finds
        /// neither. This project's Heuristic returns an all-zero action when no baseline is
        /// selected, and zero is a legal action, so the run proceeds: episodes advance,
        /// rewards accumulate and a full frame log is written for a robot that was never
        /// commanded. It is indistinguishable in the logs from a policy that failed to learn.
        /// It has already happened once here, for 569 frames.
        /// </summary>
        void VerifyActionSourceExists()
        {
            if (_config.Run.Controller != ControllerType.Policy) return;

            // A model file is read by PolicyController, which runs through the same
            // IController path as the baselines.
            if (!string.IsNullOrEmpty(_config.Run.ModelPath)) return;
            if (Academy.Instance.IsCommunicatorOn) return;

            throw new InvalidOperationException(
                "controller_type is 'policy' but there is no trainer connected and run.model is "
                + "empty, so ML-Agents falls back to Heuristic and every action is zero: the "
                + "robot is never commanded, yet the run completes and writes a full frame log. "
                + "Launch the run through mlagents-learn, pass --model <file.sentis>, or pick a "
                + "baseline with --controller baseline_gait_pd or --controller "
                + "baseline_balance_lqr.");
        }

        /// <summary>
        /// The controller that will supply actions, or null when the trainer supplies them.
        /// A policy with a model file goes through the same IController path as the baselines,
        /// so all three conditions are provably driven by the identical observation and action
        /// plumbing (SPEC section 8).
        /// </summary>
        IController CreateController()
        {
            switch (_config.Run.Controller)
            {
                case ControllerType.BaselineGaitPd: return new BaselineGaitPd();
                case ControllerType.BaselineBalanceLqr: return new BaselineBalanceLqr();
                case ControllerType.Policy:
                    return string.IsNullOrEmpty(_config.Run.ModelPath) ? null : new PolicyController();
                default: return null;
            }
        }

        /// <summary>
        /// True when actions come from a controller inside this process rather than from a
        /// connected trainer. A loaded policy counts: it is inference, not training.
        /// </summary>
        bool DrivenLocally()
        {
            return _config.Run.Controller != ControllerType.Policy
                || !string.IsNullOrEmpty(_config.Run.ModelPath);
        }

        void Update()
        {
            if (_shuttingDown || _config == null) return;

            WriteTorqueProbeOnce();

            if (_config.Run.Mode != RunMode.Eval) return;

            for (int i = 0; i < _areas.Count; i++)
            {
                if (!_areas[i].Exhausted) return;
            }

            Debug.Log("[WheelLeg] evaluation complete: " + _seedSource.Completed + "/" + _seedSource.Total
                + " episodes");
            Shutdown(ExitOk);
        }

        /// <summary>
        /// Records which ArticulationBody force channel PhysX actually populated, next to the
        /// frame log. The joint_torque and wheel_torque columns and their two reward terms are
        /// only meaningful if a channel reports non-zero, and that varies by Unity version, so
        /// a finished run has to carry the answer with it rather than leave it to be guessed.
        /// </summary>
        void WriteTorqueProbeOnce()
        {
            if (_torqueProbeWritten || _areas.Count == 0) return;
            RobotDriver driver = _areas[0].Driver;
            if (driver == null || !driver.TorqueProbeComplete) return;

            _torqueProbeWritten = true;
            WritePhysicsProbe(driver);
        }

        /// <summary>
        /// Rewrites the probe with everything observed up to now. Called once as soon as the
        /// torque channels have been sampled, so a run that dies early still leaves the file,
        /// and again at shutdown, because the contact fields only mean something after the
        /// robot has had time to touch things.
        /// </summary>
        void WritePhysicsProbe(RobotDriver driver)
        {
            if (driver == null || _outputDirectory == null) return;
            // driveForce is what the torque columns are read from. It reports zero whenever the
            // drive gains failed to apply, which is exactly the failure this file exists to
            // catch: an unactuated robot looks like a policy that has not learned.
            bool usable = driver.MaxPerBodyDriveForce > 0f && driver.DrivesVerified;
            string text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{{\n"
                + "  \"unity_version\": \"{0}\",\n"
                + "  \"joint_torque_columns_usable\": {1},\n"
                + "  \"drive_gains_verified\": {12},\n"
                + "  \"max_per_body_drive_force\": {2:R},\n"
                + "  \"max_per_body_joint_force\": {3:R},\n"
                + "  \"max_hierarchy_joint_force\": {4:R},\n"
                + "  \"max_hierarchy_gravity_force\": {5:R},\n"
                + "  \"leg_drive_readback\": {{ \"stiffness\": {6:R}, \"damping\": {7:R}, \"force_limit\": {8:R} }},\n"
                + "  \"wheel_drive_readback\": {{ \"stiffness\": {9:R}, \"damping\": {10:R}, \"force_limit\": {11:R} }},\n"
                + "  \"self_collision_pairs_ignored\": {18},\n"
                + "  \"robot_collider_friction\": {19:R},\n"
                + "  \"imported_leg_force_limit\": [ {13:R}, {14:R}, {15:R} ],\n"
                + "  \"base_last_external_contact\": {16},\n"
                + "  \"wheel0_last_external_contact\": {17}\n"
                + "}}\n",
                Application.unityVersion,
                usable ? "true" : "false",
                driver.MaxPerBodyDriveForce, driver.MaxPerBodyJointForce,
                driver.MaxHierarchyJointForce, driver.MaxHierarchyGravityForce,
                driver.LegDriveGainsReadback.x, driver.LegDriveGainsReadback.y,
                driver.LegDriveGainsReadback.z,
                driver.WheelDriveGainsReadback.x, driver.WheelDriveGainsReadback.y,
                driver.WheelDriveGainsReadback.z,
                driver.DrivesVerified ? "true" : "false",
                // Leg 0's [hip, thigh, calf] as the URDF declared them, so a finished run
                // proves whether the configured torque ceiling matches the real actuator.
                driver.ImportedLegForceLimit(0), driver.ImportedLegForceLimit(1),
                driver.ImportedLegForceLimit(2),
                JsonString(driver.BaseLastExternalContact),
                JsonString(driver.WheelLastExternalContact(0)),
                driver.SelfCollisionPairsIgnored,
                // Equals the terrain friction this episode drew. If it reads 0 the robot never
                // got a material and every contact is running on PhysX's 0.6 default.
                driver.WheelFriction);

            try
            {
                File.WriteAllText(Path.Combine(_outputDirectory, "physics_probe.json"), text);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WheelLeg] could not write physics_probe.json: " + e.Message);
            }

            if (!usable)
            {
                Debug.LogWarning("[WheelLeg] joint torque read back as zero: the drive gains did "
                    + "not take, so the robot is unactuated and the joint_torque and wheel_torque "
                    + "columns and rewards are meaningless for this run. See physics_probe.json.");
            }
        }

        /// <summary>Quotes a value for the probe, or emits null when nothing was recorded.</summary>
        static string JsonString(string value)
        {
            if (value == null) return "null";
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Ends the process explicitly with a status code. A batch script that cannot tell
        /// success from failure will happily treat an empty log as a finished run
        /// (SERVER_OPS_1.md section 2-2).
        /// </summary>
        public void Shutdown(int exitCode)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            FlushLoggers();

            if (Application.isEditor)
            {
                Debug.Log("[WheelLeg] run finished with exit code " + exitCode + " (editor: not quitting)");
                return;
            }
            Application.Quit(exitCode);
        }

        void Abort(int exitCode, string message)
        {
            Debug.LogError("[WheelLeg] " + message);
            _shuttingDown = true;
            FlushLoggers();
            if (!Application.isEditor) Application.Quit(exitCode);
        }

        void FlushLoggers()
        {
            // The contact fields are only informative once the robot has run, so the probe is
            // rewritten here with the whole run's observations before the process goes away.
            if (_torqueProbeWritten && _areas.Count > 0) WritePhysicsProbe(_areas[0].Driver);

            for (int i = 0; i < _loggers.Count; i++)
            {
                try
                {
                    _loggers[i].Dispose();
                }
                catch (Exception e)
                {
                    Debug.LogError("[WheelLeg] failed to close area log " + i + ": " + e.Message);
                }
            }
            _loggers.Clear();

            // Inference workers hold native memory that a domain reload does not reclaim.
            for (int i = 0; i < _disposableControllers.Count; i++)
            {
                try
                {
                    _disposableControllers[i].Dispose();
                }
                catch (Exception e)
                {
                    Debug.LogError("[WheelLeg] failed to release controller " + i + ": " + e.Message);
                }
            }
            _disposableControllers.Clear();
        }

        void OnApplicationQuit()
        {
            FlushLoggers();
        }

        void OnDestroy()
        {
            FlushLoggers();
        }

        /// <summary>
        /// Resolves --out against the working directory when relative, so nothing is ever
        /// written outside the directory the caller named and no absolute path is baked in.
        /// </summary>
        static string ResolveOutputDirectory(string configured)
        {
            if (Path.IsPathRooted(configured)) return configured;
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
        }

        static string ResolveCommitHash(CommandLine cli)
        {
            string explicitHash = cli.Get("--commit", null);
            if (!string.IsNullOrEmpty(explicitHash)) return explicitHash;

            TextAsset stamp = Resources.Load<TextAsset>("build_commit");
            return stamp != null ? stamp.text.Trim() : "unknown";
        }
    }
}
