using System;
using System.Collections.Generic;
using System.Globalization;
using WheelLeg.Core;

namespace WheelLeg.Config
{
    public struct FloatRange
    {
        public float Min;
        public float Max;

        public FloatRange(float min, float max)
        {
            Min = min;
            Max = max;
        }

        public static FloatRange Read(ConfigNode node, string key)
        {
            float[] v = node.Floats(key, 2);
            if (v[1] < v[0])
                throw new ConfigException(node.Path + "." + key + " has max < min");
            return new FloatRange(v[0], v[1]);
        }

        public float Sample(DeterministicRandom rng)
        {
            return rng.Range(Min, Max);
        }

        public float Lerp(float t)
        {
            return Min + (Max - Min) * (t < 0f ? 0f : (t > 1f ? 1f : t));
        }
    }

    public enum RunMode
    {
        Train,
        Eval
    }

    public enum ControllerType
    {
        Policy,
        BaselineGaitPd,
        BaselineBalanceLqr
    }

    public enum LogFormat
    {
        Parquet,
        Csv
    }

    public sealed class RunConfig
    {
        public RunMode Mode;
        public ControllerType Controller;
        public int Seed;
        public int Areas;
        public string OutDir;
        public string ModelPath;
        public float TimeScale;

        public static RunConfig Read(ConfigNode n)
        {
            return new RunConfig
            {
                Mode = ParseMode(n, "mode"),
                Controller = ParseController(n, "controller_type"),
                Seed = n.Int("seed"),
                Areas = n.Int("areas"),
                OutDir = n.Str("out"),
                ModelPath = n.Str("model", string.Empty),
                TimeScale = n.Float("time_scale")
            };
        }

        static RunMode ParseMode(ConfigNode n, string key)
        {
            string s = n.Str(key);
            switch (s)
            {
                case "train": return RunMode.Train;
                case "eval": return RunMode.Eval;
                default: throw new ConfigException(n.Path + "." + key + " must be 'train' or 'eval', got '" + s + "'");
            }
        }

        public static ControllerType ParseControllerName(string s, string where)
        {
            switch (s)
            {
                case "policy": return ControllerType.Policy;
                case "baseline_gait_pd": return ControllerType.BaselineGaitPd;
                case "baseline_balance_lqr": return ControllerType.BaselineBalanceLqr;
                default:
                    throw new ConfigException(where +
                        " must be one of policy|baseline_gait_pd|baseline_balance_lqr, got '" + s + "'");
            }
        }

        static ControllerType ParseController(ConfigNode n, string key)
        {
            return ParseControllerName(n.Str(key), n.Path + "." + key);
        }

        public string ControllerName
        {
            get
            {
                switch (Controller)
                {
                    case ControllerType.BaselineGaitPd: return "baseline_gait_pd";
                    case ControllerType.BaselineBalanceLqr: return "baseline_balance_lqr";
                    default: return "policy";
                }
            }
        }
    }

    public sealed class PhysicsConfig
    {
        public float FixedTimestep;
        public int SolverIterations;
        public int SolverVelocityIterations;
        public bool EnhancedDeterminism;
        public int DecisionPeriod;
        /// <summary>
        /// Whether colliders belonging to the same robot may collide with each other.
        ///
        /// Must be false on this robot. ArticulationBody only filters a body against its
        /// direct parent, and the URDF Importer gives every link its own body including the
        /// fixed ones, so Go2-W's wheel, wheel motor housing and lower shin end up as three
        /// siblings under the calf with no filtering between them. In the rest pose the wheel
        /// collider (86 mm radius) sits 38 mm from the motor housing collider and engulfs it:
        /// a 108 mm permanent interpenetration on all four legs, plus two more overlapping
        /// pairs per leg, that the solver tries to push apart on every single step.
        /// </summary>
        public bool SelfCollision;

        public static PhysicsConfig Read(ConfigNode n)
        {
            PhysicsConfig c = new PhysicsConfig
            {
                FixedTimestep = n.Float("fixed_timestep"),
                SolverIterations = n.Int("solver_iterations"),
                SolverVelocityIterations = n.Int("solver_velocity_iterations"),
                EnhancedDeterminism = n.Bool("enhanced_determinism"),
                DecisionPeriod = n.Int("decision_period"),
                SelfCollision = n.Bool("self_collision")
            };
            if (c.FixedTimestep <= 0f) throw new ConfigException(n.Path + ".fixed_timestep must be > 0");
            if (c.DecisionPeriod < 1) throw new ConfigException(n.Path + ".decision_period must be >= 1");
            return c;
        }

        /// <summary>Seconds between policy decisions.</summary>
        public float DecisionDt { get { return FixedTimestep * DecisionPeriod; } }
    }

    public sealed class EpisodeConfig
    {
        public int MaxSteps;
        public float AreaSpacingX;
        public float AreaSpacingZ;
        public int AreaGridColumns;

        public static EpisodeConfig Read(ConfigNode n)
        {
            float[] spacing = n.Floats("area_spacing", 2);
            EpisodeConfig c = new EpisodeConfig
            {
                MaxSteps = n.Int("max_steps"),
                AreaSpacingX = spacing[0],
                AreaSpacingZ = spacing[1],
                AreaGridColumns = n.Int("area_grid_columns")
            };
            if (c.MaxSteps < 1) throw new ConfigException(n.Path + ".max_steps must be >= 1");
            if (c.AreaGridColumns < 1) throw new ConfigException(n.Path + ".area_grid_columns must be >= 1");
            return c;
        }
    }

    public sealed class DriveConfig
    {
        public float LegStiffness;
        public float LegDamping;
        /// <summary>
        /// Force limit per joint type, [hip, thigh, calf], because the three are not the same
        /// actuator. Go2-W's URDF gives hip and thigh 23.7 Nm and calf 35.55 Nm; one shared
        /// value set to the largest of them let the hip and thigh pull 50% harder than the
        /// real hardware can.
        /// </summary>
        public float[] LegForceLimits;
        public float WheelStiffness;
        public float WheelDamping;
        public float WheelForceLimit;
        public float MaxWheelSpeedRadPerSec;

        /// <summary>
        /// Whether the torque ceiling falls with joint speed the way a real motor's does.
        /// Always compiled in; false makes it a constant ceiling.
        /// </summary>
        public bool TorqueSpeedDerating;
        /// <summary>No-load speed per joint type, [hip, thigh, calf], rad/s.</summary>
        public float[] LegNoLoadSpeed;
        public float WheelNoLoadSpeed;

        public static DriveConfig Read(ConfigNode n)
        {
            DriveConfig c = new DriveConfig
            {
                LegStiffness = n.Float("leg_stiffness"),
                LegDamping = n.Float("leg_damping"),
                // Three entries: [hip, thigh, calf], the three joint types of one leg.
                LegForceLimits = n.Floats("leg_force_limit", 3),
                WheelStiffness = n.Float("wheel_stiffness"),
                WheelDamping = n.Float("wheel_damping"),
                WheelForceLimit = n.Float("wheel_force_limit"),
                MaxWheelSpeedRadPerSec = n.Float("max_wheel_speed"),
                TorqueSpeedDerating = n.Bool("torque_speed_derating"),
                LegNoLoadSpeed = n.Floats("leg_no_load_speed", 3),
                WheelNoLoadSpeed = n.Float("wheel_no_load_speed")
            };
            if (c.MaxWheelSpeedRadPerSec <= 0f)
                throw new ConfigException(n.Path + ".max_wheel_speed must be > 0");
            if (c.TorqueSpeedDerating)
            {
                // A zero or negative no-load speed would make the derating divide by it and
                // silently zero every torque ceiling, which looks exactly like a robot that
                // cannot move rather than a config that is wrong.
                for (int i = 0; i < c.LegNoLoadSpeed.Length; i++)
                {
                    if (c.LegNoLoadSpeed[i] <= 0f)
                        throw new ConfigException(n.Path + ".leg_no_load_speed[" + i + "] must be > 0");
                }
                if (c.WheelNoLoadSpeed <= 0f)
                    throw new ConfigException(n.Path + ".wheel_no_load_speed must be > 0");
            }
            return c;
        }

        /// <summary>
        /// The torque ceiling this joint can still produce at <paramref name="speedRad"/>.
        ///
        /// First-order motor model: a DC motor's available torque falls linearly from stall
        /// torque at rest to zero at its no-load speed. Without it the drive can hold its full
        /// force limit at any speed, and a policy learns to rely on torque the real actuator
        /// cannot deliver, which is exactly the gap sim-to-real transfer falls into.
        ///
        /// The no-load speeds come from the URDF's per-joint velocity limits. Treating a
        /// maximum joint speed as a no-load speed is an assumption, not a measured motor
        /// curve; it is the strongest statement the available data supports and is a config
        /// value so it can be replaced with real numbers.
        /// </summary>
        public float DeratedLimit(float baseLimit, float noLoadSpeed, float speedRad)
        {
            if (!TorqueSpeedDerating || noLoadSpeed <= 0f) return baseLimit;
            float remaining = 1f - (speedRad < 0f ? -speedRad : speedRad) / noLoadSpeed;
            if (remaining <= 0f) return 0f;
            return remaining >= 1f ? baseLimit : baseLimit * remaining;
        }
    }

    public sealed class ResetRandomizationConfig
    {
        public FloatRange BaseHeightOffset;
        public FloatRange BaseYawDeg;
        public FloatRange BaseRollPitchDeg;
        public FloatRange JointAngleOffsetDeg;
        public FloatRange JointVelocity;
        public FloatRange WheelVelocity;

        public static ResetRandomizationConfig Read(ConfigNode n)
        {
            return new ResetRandomizationConfig
            {
                BaseHeightOffset = FloatRange.Read(n, "base_height_offset"),
                BaseYawDeg = FloatRange.Read(n, "base_yaw_deg"),
                BaseRollPitchDeg = FloatRange.Read(n, "base_roll_pitch_deg"),
                JointAngleOffsetDeg = FloatRange.Read(n, "joint_angle_offset_deg"),
                JointVelocity = FloatRange.Read(n, "joint_velocity"),
                WheelVelocity = FloatRange.Read(n, "wheel_velocity")
            };
        }
    }

    public sealed class RobotConfig
    {
        /// <summary>Leg order. Defines observation and action index layout.</summary>
        public string[] Legs;
        public string BaseLinkName;
        // URDF Importer names each GameObject after the child LINK, and the
        // ArticulationBody on that GameObject is the joint connecting it to its parent.
        // Resolving by link name therefore needs no dependency on the importer package.
        public string HipLinkFormat;
        public string ThighLinkFormat;
        public string CalfLinkFormat;
        public string WheelLinkFormat;
        public float SpawnHeight;
        /// <summary>Per leg, the [hip, thigh, calf] stand pose in degrees.</summary>
        public Dictionary<string, float[]> StandPoseDeg;
        /// <summary>
        /// How far, in degrees, action = +-1 moves a leg joint away from its stand pose. The
        /// action is a residual around the stand pose rather than an absolute position in the
        /// joint's travel, so action 0 holds the robot standing.
        /// </summary>
        public float ActionScaleDeg;
        /// <summary>
        /// Policy steps between choosing an action and it reaching the drives. The actuation
        /// counterpart of observation.delay_steps. 0 disables it.
        /// </summary>
        public int ActionDelaySteps;
        public DriveConfig Drive;
        public ResetRandomizationConfig Reset;

        public static RobotConfig Read(ConfigNode n)
        {
            RobotConfig c = new RobotConfig
            {
                Legs = n.StringArray("legs"),
                BaseLinkName = n.Str("base_link"),
                SpawnHeight = n.Float("spawn_height"),
                ActionScaleDeg = n.Float("action_scale_deg"),
                ActionDelaySteps = n.Int("action_delay_steps"),
                Drive = DriveConfig.Read(n.Child("drive")),
                Reset = ResetRandomizationConfig.Read(n.Child("reset_randomization")),
                StandPoseDeg = new Dictionary<string, float[]>(StringComparer.Ordinal)
            };

            ConfigNode names = n.Child("link_name_format");
            c.HipLinkFormat = names.Str("hip");
            c.ThighLinkFormat = names.Str("thigh");
            c.CalfLinkFormat = names.Str("calf");
            c.WheelLinkFormat = names.Str("wheel");

            if (c.Legs.Length != 4)
                throw new ConfigException(n.Path + ".legs must list exactly 4 legs");
            if (c.ActionScaleDeg <= 0f)
                throw new ConfigException(n.Path + ".action_scale_deg must be > 0");
            if (c.ActionDelaySteps < 0)
                throw new ConfigException(n.Path + ".action_delay_steps must be >= 0");

            ConfigNode pose = n.Child("stand_pose_deg");
            foreach (string leg in c.Legs)
                c.StandPoseDeg[leg] = pose.Floats(leg, 3);

            return c;
        }

        /// <summary>
        /// The 12 stand pose angles in degrees, flattened into observation and action index
        /// order, so the action mapping does not have to know the leg naming scheme.
        /// </summary>
        public float[] StandPoseDegIndexed()
        {
            float[] flat = new float[Legs.Length * 3];
            for (int leg = 0; leg < Legs.Length; leg++)
            {
                float[] pose = StandPoseDeg[Legs[leg]];
                for (int j = 0; j < 3; j++) flat[leg * 3 + j] = pose[j];
            }
            return flat;
        }

        public string HipLink(string leg) { return HipLinkFormat.Replace("{leg}", leg); }
        public string ThighLink(string leg) { return ThighLinkFormat.Replace("{leg}", leg); }
        public string CalfLink(string leg) { return CalfLinkFormat.Replace("{leg}", leg); }
        public string WheelLink(string leg) { return WheelLinkFormat.Replace("{leg}", leg); }

        /// <summary>Link names of the 12 leg joints in observation/action index order.</summary>
        public string[] LegJointLinkNames()
        {
            string[] names = new string[Legs.Length * 3];
            for (int leg = 0; leg < Legs.Length; leg++)
            {
                names[leg * 3 + 0] = HipLink(Legs[leg]);
                names[leg * 3 + 1] = ThighLink(Legs[leg]);
                names[leg * 3 + 2] = CalfLink(Legs[leg]);
            }
            return names;
        }

        /// <summary>Link names of the 4 wheels in leg order.</summary>
        public string[] WheelLinkNames()
        {
            string[] names = new string[Legs.Length];
            for (int leg = 0; leg < Legs.Length; leg++) names[leg] = WheelLink(Legs[leg]);
            return names;
        }
    }

    public sealed class CommandConfig
    {
        public FloatRange Vx;
        public FloatRange Vy;
        public FloatRange Wz;
        public int ResampleIntervalSteps;
        public float ZeroCommandProbability;

        public static CommandConfig Read(ConfigNode n)
        {
            CommandConfig c = new CommandConfig
            {
                Vx = FloatRange.Read(n, "vx_range"),
                Vy = FloatRange.Read(n, "vy_range"),
                Wz = FloatRange.Read(n, "wz_range"),
                ResampleIntervalSteps = n.Int("resample_interval_steps"),
                ZeroCommandProbability = n.Float("zero_command_probability")
            };
            if (c.ResampleIntervalSteps < 1)
                throw new ConfigException(n.Path + ".resample_interval_steps must be >= 1");
            return c;
        }
    }

    public sealed class ObservationConfig
    {
        public float UpNoiseStd;
        public float LinVelNoiseStd;
        public float AngVelNoiseStd;
        public float AngVelBiasWalkStd;
        public float AngVelBiasLimit;
        public int DelaySteps;
        public float JointAngleQuantumDeg;
        public float JointVelQuantum;
        public float WheelVelQuantum;

        public static ObservationConfig Read(ConfigNode n)
        {
            ConfigNode noise = n.Child("noise_std");
            ConfigNode bias = n.Child("bias");
            ConfigNode quant = n.Child("quantization");
            ObservationConfig c = new ObservationConfig
            {
                UpNoiseStd = noise.Float("up"),
                LinVelNoiseStd = noise.Float("lin_vel"),
                AngVelNoiseStd = noise.Float("ang_vel"),
                AngVelBiasWalkStd = bias.Float("ang_vel_random_walk_std"),
                AngVelBiasLimit = bias.Float("ang_vel_limit"),
                DelaySteps = n.Int("delay_steps"),
                JointAngleQuantumDeg = quant.Float("joint_angle_deg"),
                JointVelQuantum = quant.Float("joint_velocity"),
                WheelVelQuantum = quant.Float("wheel_velocity")
            };
            if (c.DelaySteps < 0) throw new ConfigException(n.Path + ".delay_steps must be >= 0");
            return c;
        }
    }

    public sealed class TerrainConfig
    {
        public float SizeX;
        public float SizeZ;
        public int Resolution;
        public FloatRange Difficulty;
        public FloatRange SlopeDeg;
        public FloatRange RoughnessAmplitude;
        public FloatRange RoughnessWavelength;
        public FloatRange StepHeight;
        public float StepSpacing;
        public FloatRange Friction;

        public static TerrainConfig Read(ConfigNode n)
        {
            float[] size = n.Floats("size", 2);
            TerrainConfig c = new TerrainConfig
            {
                SizeX = size[0],
                SizeZ = size[1],
                Resolution = n.Int("resolution"),
                Difficulty = FloatRange.Read(n, "difficulty"),
                SlopeDeg = FloatRange.Read(n, "slope_deg"),
                RoughnessAmplitude = FloatRange.Read(n, "roughness_amplitude"),
                RoughnessWavelength = FloatRange.Read(n, "roughness_wavelength"),
                StepHeight = FloatRange.Read(n, "step_height"),
                StepSpacing = n.Float("step_spacing"),
                Friction = FloatRange.Read(n, "friction")
            };
            if (c.Resolution < 2) throw new ConfigException(n.Path + ".resolution must be >= 2");
            if (c.SizeX <= 0f || c.SizeZ <= 0f) throw new ConfigException(n.Path + ".size must be > 0");
            if (c.RoughnessWavelength.Min <= 0f)
                throw new ConfigException(n.Path + ".roughness_wavelength must be > 0");
            if (c.StepSpacing <= 0f) throw new ConfigException(n.Path + ".step_spacing must be > 0");
            return c;
        }
    }

    public sealed class TerminationConfig
    {
        public float MinBaseHeight;
        public float MinUpDot;
        public bool BaseContactTerminates;

        public static TerminationConfig Read(ConfigNode n)
        {
            return new TerminationConfig
            {
                MinBaseHeight = n.Float("min_base_height"),
                MinUpDot = n.Float("min_up_dot"),
                BaseContactTerminates = n.Bool("base_contact_terminates")
            };
        }
    }

    public sealed class SeedConfig
    {
        public int TrainBandLow;
        public int TrainBandHigh;
        public int EvalBandLow;
        public int EvalBandHigh;
        public int[] EvalList;
        public int EvalEpisodesPerSeed;

        public static SeedConfig Read(ConfigNode n)
        {
            int[] train = n.IntArray("train_band");
            int[] eval = n.IntArray("eval_band");
            if (train.Length != 2 || eval.Length != 2)
                throw new ConfigException(n.Path + ".train_band / eval_band must each be [low, high]");

            SeedConfig c = new SeedConfig
            {
                TrainBandLow = train[0],
                TrainBandHigh = train[1],
                EvalBandLow = eval[0],
                EvalBandHigh = eval[1],
                EvalList = n.IntArray("eval_list"),
                EvalEpisodesPerSeed = n.Int("eval_episodes_per_seed")
            };

            if (c.TrainBandLow > c.TrainBandHigh || c.EvalBandLow > c.EvalBandHigh)
                throw new ConfigException(n.Path + " seed bands must be ordered [low, high]");
            if (c.TrainBandLow <= c.EvalBandHigh && c.EvalBandLow <= c.TrainBandHigh)
                throw new ConfigException(n.Path + " train_band and eval_band overlap; "
                    + "evaluation terrain would be exposed during training");
            foreach (int s in c.EvalList)
            {
                if (s < c.EvalBandLow || s > c.EvalBandHigh)
                    throw new ConfigException(string.Format(CultureInfo.InvariantCulture,
                        "{0}.eval_list contains {1}, outside eval_band [{2}, {3}]",
                        n.Path, s, c.EvalBandLow, c.EvalBandHigh));
            }
            if (c.EvalEpisodesPerSeed < 1)
                throw new ConfigException(n.Path + ".eval_episodes_per_seed must be >= 1");
            return c;
        }

        public bool IsEvalSeed(int seed)
        {
            return seed >= EvalBandLow && seed <= EvalBandHigh;
        }

        public string BandName(int seed)
        {
            if (IsEvalSeed(seed)) return "eval";
            if (seed >= TrainBandLow && seed <= TrainBandHigh) return "train";
            return "unbanded";
        }
    }

    public sealed class LoggingConfig
    {
        public bool Enabled;
        public LogFormat Format;
        public string SubDir;
        public int FlushIntervalFrames;
        public long MaxFileBytes;
        public long MaxTotalBytes;
        public int MaxBufferedFrames;

        public static LoggingConfig Read(ConfigNode n)
        {
            LoggingConfig c = new LoggingConfig
            {
                Enabled = n.Bool("enabled"),
                Format = ParseFormat(n, "format"),
                SubDir = n.Str("subdir"),
                FlushIntervalFrames = n.Int("flush_interval_frames"),
                MaxFileBytes = n.Long("max_file_bytes"),
                MaxTotalBytes = n.Long("max_total_bytes"),
                MaxBufferedFrames = n.Int("max_buffered_frames")
            };
            if (c.FlushIntervalFrames < 1) throw new ConfigException(n.Path + ".flush_interval_frames must be >= 1");
            if (c.MaxBufferedFrames < c.FlushIntervalFrames)
                throw new ConfigException(n.Path + ".max_buffered_frames must be >= flush_interval_frames");
            if (c.MaxFileBytes < 1024) throw new ConfigException(n.Path + ".max_file_bytes must be >= 1024");
            return c;
        }

        static LogFormat ParseFormat(ConfigNode n, string key)
        {
            string s = n.Str(key);
            switch (s)
            {
                case "parquet": return LogFormat.Parquet;
                case "csv": return LogFormat.Csv;
                default: throw new ConfigException(n.Path + "." + key + " must be 'parquet' or 'csv', got '" + s + "'");
            }
        }
    }

    /// <summary>
    /// Reward weights. Every term listed in the spec exists here; a term is disabled by
    /// setting its weight to 0, never by removing it from the code.
    /// </summary>
    public sealed class RewardConfig
    {
        public float LinVelTracking;
        public float AngVelTracking;
        public float TrackingSigma;
        public float PostureUp;
        public float LateralVelocity;
        public float JointTorque;
        public float WheelTorque;
        public float LegActionRate;
        public float WheelActionRate;
        public float WheelSlip;
        public float Alive;
        public float Termination;

        public static RewardConfig Read(ConfigNode n)
        {
            RewardConfig c = new RewardConfig
            {
                LinVelTracking = n.Float("lin_vel_tracking"),
                AngVelTracking = n.Float("ang_vel_tracking"),
                TrackingSigma = n.Float("tracking_sigma"),
                PostureUp = n.Float("posture_up"),
                LateralVelocity = n.Float("lateral_velocity"),
                JointTorque = n.Float("joint_torque"),
                WheelTorque = n.Float("wheel_torque"),
                LegActionRate = n.Float("leg_action_rate"),
                WheelActionRate = n.Float("wheel_action_rate"),
                WheelSlip = n.Float("wheel_slip"),
                Alive = n.Float("alive"),
                Termination = n.Float("termination")
            };
            if (c.TrackingSigma <= 0f) throw new ConfigException(n.Path + ".tracking_sigma must be > 0");
            return c;
        }
    }

    public sealed class BaselineConfig
    {
        public float GaitFrequencyHz;
        public float GaitStepHeightDeg;
        public float PostureKp;
        public float PostureKd;
        public float LqrPitch;
        public float LqrPitchRate;
        public float LqrVelocity;
        public float WheelSpeedPerCommand;

        public static BaselineConfig Read(ConfigNode n)
        {
            ConfigNode gait = n.Child("gait_pd");
            ConfigNode lqr = n.Child("balance_lqr");
            return new BaselineConfig
            {
                GaitFrequencyHz = gait.Float("frequency_hz"),
                GaitStepHeightDeg = gait.Float("step_amplitude_deg"),
                PostureKp = gait.Float("posture_kp"),
                PostureKd = gait.Float("posture_kd"),
                LqrPitch = lqr.Float("k_pitch"),
                LqrPitchRate = lqr.Float("k_pitch_rate"),
                LqrVelocity = lqr.Float("k_velocity"),
                WheelSpeedPerCommand = lqr.Float("wheel_speed_per_command")
            };
        }
    }

    /// <summary>
    /// The complete, validated configuration for one process. Nothing that scales with
    /// local-vs-server lives outside this object.
    /// </summary>
    public sealed class SimConfig
    {
        public RunConfig Run;
        public PhysicsConfig Physics;
        public EpisodeConfig Episode;
        public RobotConfig Robot;
        public CommandConfig Command;
        public ObservationConfig Observation;
        public TerrainConfig Terrain;
        public TerminationConfig Termination;
        public SeedConfig Seeds;
        public LoggingConfig Logging;
        public RewardConfig Reward;
        public BaselineConfig Baseline;

        /// <summary>Every source file and CLI override that produced this config, in order.</summary>
        public List<string> Provenance = new List<string>();

        public static SimConfig Read(ConfigNode root)
        {
            SimConfig c = new SimConfig
            {
                Run = RunConfig.Read(root.Child("run")),
                Physics = PhysicsConfig.Read(root.Child("physics")),
                Episode = EpisodeConfig.Read(root.Child("episode")),
                Robot = RobotConfig.Read(root.Child("robot")),
                Command = CommandConfig.Read(root.Child("command")),
                Observation = ObservationConfig.Read(root.Child("observation")),
                Terrain = TerrainConfig.Read(root.Child("terrain")),
                Termination = TerminationConfig.Read(root.Child("termination")),
                Seeds = SeedConfig.Read(root.Child("seeds")),
                Logging = LoggingConfig.Read(root.Child("logging")),
                Reward = RewardConfig.Read(root.Child("reward")),
                Baseline = BaselineConfig.Read(root.Child("baseline"))
            };

            if (c.Run.Areas < 1) throw new ConfigException("run.areas must be >= 1");
            root.AssertFullyConsumed();
            return c;
        }
    }
}
