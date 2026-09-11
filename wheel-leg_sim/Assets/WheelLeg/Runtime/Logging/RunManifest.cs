using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using WheelLeg.Config;

namespace WheelLeg.Logging
{
    /// <summary>
    /// Writes run_manifest.json next to the frame logs. This is the only record of what
    /// produced a result directory (SERVER_OPS_1.md 2-4): the full argv, the config files and
    /// overrides that were merged, the commit, and every resolved value. Without it a run
    /// found on the server months later is unattributable and therefore worthless.
    /// JSON is hand-rolled because a serializer dependency is one more thing that can fail
    /// to link under IL2CPP.
    /// </summary>
    public static class RunManifest
    {
        public const string FileName = "run_manifest.json";

        public static void Write(string outputDirectory, SimConfig config, CommandLine cli, string commitHash)
        {
            if (outputDirectory == null) throw new ArgumentNullException("outputDirectory");
            if (config == null) throw new ArgumentNullException("config");

            Json j = new Json();
            j.ObjectBegin();

            j.Str("written_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            j.Str("commit", commitHash ?? string.Empty);
            j.Str("unity_version", Application.unityVersion);
            j.Str("platform", Application.platform.ToString());

            j.ArrayBegin("argv");
            string[] argv = cli == null ? new string[0] : cli.Raw;
            for (int i = 0; i < argv.Length; i++) j.StrItem(argv[i]);
            j.ArrayEnd();

            j.ArrayBegin("config_provenance");
            List<string> provenance = config.Provenance;
            if (provenance != null)
            {
                for (int i = 0; i < provenance.Count; i++) j.StrItem(provenance[i]);
            }
            j.ArrayEnd();

            WriteScaleSummary(j, config);
            WriteResolvedConfig(j, config);

            j.ObjectEnd();

            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, FileName), j.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// The values that differ between the local check run and the server run, lifted out
        /// of the full dump so the question "what scale was this" is answerable at a glance.
        /// </summary>
        static void WriteScaleSummary(Json j, SimConfig config)
        {
            j.ObjectBegin("scale");
            j.Str("mode", ModeName(config.Run.Mode));
            j.Str("controller", config.Run.ControllerName);
            j.Int("seed", config.Run.Seed);
            j.Int("areas", config.Run.Areas);
            j.Str("out", config.Run.OutDir);
            j.Int("max_steps", config.Episode.MaxSteps);
            j.Range("terrain_difficulty", config.Terrain.Difficulty);
            j.ObjectBegin("seed_bands");
            j.Ints("train", new int[] { config.Seeds.TrainBandLow, config.Seeds.TrainBandHigh });
            j.Ints("eval", new int[] { config.Seeds.EvalBandLow, config.Seeds.EvalBandHigh });
            j.ObjectEnd();
            j.Bool("enhanced_determinism", config.Physics.EnhancedDeterminism);
            j.Str("log_format", FormatName(config.Logging.Format));
            j.ObjectEnd();
        }

        static void WriteResolvedConfig(Json j, SimConfig config)
        {
            j.ObjectBegin("config");

            j.ObjectBegin("run");
            j.Str("mode", ModeName(config.Run.Mode));
            j.Str("controller_type", config.Run.ControllerName);
            j.Int("seed", config.Run.Seed);
            j.Int("areas", config.Run.Areas);
            j.Str("out", config.Run.OutDir);
            j.Str("model", config.Run.ModelPath);
            j.Num("time_scale", config.Run.TimeScale);
            j.ObjectEnd();

            j.ObjectBegin("physics");
            j.Num("fixed_timestep", config.Physics.FixedTimestep);
            j.Int("solver_iterations", config.Physics.SolverIterations);
            j.Int("solver_velocity_iterations", config.Physics.SolverVelocityIterations);
            j.Bool("enhanced_determinism", config.Physics.EnhancedDeterminism);
            j.Bool("self_collision", config.Physics.SelfCollision);
            j.Int("decision_period", config.Physics.DecisionPeriod);
            j.ObjectEnd();

            j.ObjectBegin("episode");
            j.Int("max_steps", config.Episode.MaxSteps);
            j.Floats("area_spacing", new float[] { config.Episode.AreaSpacingX, config.Episode.AreaSpacingZ });
            j.Int("area_grid_columns", config.Episode.AreaGridColumns);
            j.ObjectEnd();

            WriteRobot(j, config.Robot);

            j.ObjectBegin("command");
            j.Range("vx_range", config.Command.Vx);
            j.Range("vy_range", config.Command.Vy);
            j.Range("wz_range", config.Command.Wz);
            j.Int("resample_interval_steps", config.Command.ResampleIntervalSteps);
            j.Num("zero_command_probability", config.Command.ZeroCommandProbability);
            j.ObjectEnd();

            j.ObjectBegin("observation");
            j.ObjectBegin("noise_std");
            j.Num("up", config.Observation.UpNoiseStd);
            j.Num("lin_vel", config.Observation.LinVelNoiseStd);
            j.Num("ang_vel", config.Observation.AngVelNoiseStd);
            j.ObjectEnd();
            j.ObjectBegin("bias");
            j.Num("ang_vel_random_walk_std", config.Observation.AngVelBiasWalkStd);
            j.Num("ang_vel_limit", config.Observation.AngVelBiasLimit);
            j.ObjectEnd();
            j.Int("delay_steps", config.Observation.DelaySteps);
            j.ObjectBegin("quantization");
            j.Num("joint_angle_deg", config.Observation.JointAngleQuantumDeg);
            j.Num("joint_velocity", config.Observation.JointVelQuantum);
            j.Num("wheel_velocity", config.Observation.WheelVelQuantum);
            j.ObjectEnd();
            j.ObjectEnd();

            j.ObjectBegin("terrain");
            j.Floats("size", new float[] { config.Terrain.SizeX, config.Terrain.SizeZ });
            j.Int("resolution", config.Terrain.Resolution);
            j.Range("difficulty", config.Terrain.Difficulty);
            j.Range("slope_deg", config.Terrain.SlopeDeg);
            j.Range("roughness_amplitude", config.Terrain.RoughnessAmplitude);
            j.Range("roughness_wavelength", config.Terrain.RoughnessWavelength);
            j.Range("step_height", config.Terrain.StepHeight);
            j.Num("step_spacing", config.Terrain.StepSpacing);
            j.Range("friction", config.Terrain.Friction);
            j.ObjectEnd();

            j.ObjectBegin("termination");
            j.Num("min_base_height", config.Termination.MinBaseHeight);
            j.Num("min_up_dot", config.Termination.MinUpDot);
            j.Bool("base_contact_terminates", config.Termination.BaseContactTerminates);
            j.ObjectEnd();

            j.ObjectBegin("seeds");
            j.Ints("train_band", new int[] { config.Seeds.TrainBandLow, config.Seeds.TrainBandHigh });
            j.Ints("eval_band", new int[] { config.Seeds.EvalBandLow, config.Seeds.EvalBandHigh });
            j.Ints("eval_list", config.Seeds.EvalList);
            j.Int("eval_episodes_per_seed", config.Seeds.EvalEpisodesPerSeed);
            j.ObjectEnd();

            j.ObjectBegin("logging");
            j.Bool("enabled", config.Logging.Enabled);
            j.Str("format", FormatName(config.Logging.Format));
            j.Str("subdir", config.Logging.SubDir);
            j.Int("flush_interval_frames", config.Logging.FlushIntervalFrames);
            j.Int("max_buffered_frames", config.Logging.MaxBufferedFrames);
            j.Int("max_file_bytes", config.Logging.MaxFileBytes);
            j.Int("max_total_bytes", config.Logging.MaxTotalBytes);
            j.ObjectEnd();

            j.ObjectBegin("reward");
            j.Num("lin_vel_tracking", config.Reward.LinVelTracking);
            j.Num("ang_vel_tracking", config.Reward.AngVelTracking);
            j.Num("tracking_sigma", config.Reward.TrackingSigma);
            j.Num("posture_up", config.Reward.PostureUp);
            j.Num("lateral_velocity", config.Reward.LateralVelocity);
            j.Num("joint_torque", config.Reward.JointTorque);
            j.Num("wheel_torque", config.Reward.WheelTorque);
            j.Num("leg_action_rate", config.Reward.LegActionRate);
            j.Num("wheel_action_rate", config.Reward.WheelActionRate);
            j.Num("wheel_slip", config.Reward.WheelSlip);
            j.Num("alive", config.Reward.Alive);
            j.Num("termination", config.Reward.Termination);
            j.ObjectEnd();

            j.ObjectBegin("baseline");
            j.ObjectBegin("gait_pd");
            j.Num("frequency_hz", config.Baseline.GaitFrequencyHz);
            j.Num("step_amplitude_deg", config.Baseline.GaitStepHeightDeg);
            j.Num("posture_kp", config.Baseline.PostureKp);
            j.Num("posture_kd", config.Baseline.PostureKd);
            j.ObjectEnd();
            j.ObjectBegin("balance_lqr");
            j.Num("k_pitch", config.Baseline.LqrPitch);
            j.Num("k_pitch_rate", config.Baseline.LqrPitchRate);
            j.Num("k_velocity", config.Baseline.LqrVelocity);
            j.Num("wheel_speed_per_command", config.Baseline.WheelSpeedPerCommand);
            j.ObjectEnd();
            j.ObjectEnd();

            j.ObjectEnd();
        }

        static void WriteRobot(Json j, RobotConfig robot)
        {
            j.ObjectBegin("robot");
            j.Str("base_link", robot.BaseLinkName);
            j.Num("spawn_height", robot.SpawnHeight);
            j.Num("action_scale_deg", robot.ActionScaleDeg);
            j.Int("action_delay_steps", robot.ActionDelaySteps);
            j.Strings("legs", robot.Legs);

            j.ObjectBegin("link_name_format");
            j.Str("hip", robot.HipLinkFormat);
            j.Str("thigh", robot.ThighLinkFormat);
            j.Str("calf", robot.CalfLinkFormat);
            j.Str("wheel", robot.WheelLinkFormat);
            j.ObjectEnd();

            j.ObjectBegin("stand_pose_deg");
            for (int i = 0; i < robot.Legs.Length; i++)
                j.Floats(robot.Legs[i], robot.StandPoseDeg[robot.Legs[i]]);
            j.ObjectEnd();

            j.ObjectBegin("drive");
            j.Num("leg_stiffness", robot.Drive.LegStiffness);
            j.Num("leg_damping", robot.Drive.LegDamping);
            j.Floats("leg_force_limit", robot.Drive.LegForceLimits);
            j.Num("wheel_stiffness", robot.Drive.WheelStiffness);
            j.Num("wheel_damping", robot.Drive.WheelDamping);
            j.Num("wheel_force_limit", robot.Drive.WheelForceLimit);
            j.Num("max_wheel_speed", robot.Drive.MaxWheelSpeedRadPerSec);
            // The actuator model changes the dynamics, so a run has to carry whether it was on.
            j.Bool("torque_speed_derating", robot.Drive.TorqueSpeedDerating);
            j.Floats("leg_no_load_speed", robot.Drive.LegNoLoadSpeed);
            j.Num("wheel_no_load_speed", robot.Drive.WheelNoLoadSpeed);
            j.ObjectEnd();

            j.ObjectBegin("reset_randomization");
            j.Range("base_height_offset", robot.Reset.BaseHeightOffset);
            j.Range("base_yaw_deg", robot.Reset.BaseYawDeg);
            j.Range("base_roll_pitch_deg", robot.Reset.BaseRollPitchDeg);
            j.Range("joint_angle_offset_deg", robot.Reset.JointAngleOffsetDeg);
            j.Range("joint_velocity", robot.Reset.JointVelocity);
            j.Range("wheel_velocity", robot.Reset.WheelVelocity);
            j.ObjectEnd();

            j.ObjectEnd();
        }

        static string ModeName(RunMode mode)
        {
            return mode == RunMode.Eval ? "eval" : "train";
        }

        static string FormatName(LogFormat format)
        {
            return format == LogFormat.Csv ? "csv" : "parquet";
        }

        /// <summary>
        /// Minimal indenting JSON emitter. A single "is this the first member" flag is enough
        /// because closing a container always leaves the parent non-empty.
        /// </summary>
        sealed class Json
        {
            readonly StringBuilder _sb = new StringBuilder(8192);
            int _depth;
            bool _first = true;

            public void ObjectBegin()
            {
                Separator();
                _sb.Append('{');
                _depth++;
                _first = true;
            }

            public void ObjectBegin(string name)
            {
                Key(name);
                _sb.Append('{');
                _depth++;
                _first = true;
            }

            public void ObjectEnd()
            {
                _depth--;
                _sb.Append('\n').Append(' ', _depth * 2).Append('}');
                _first = false;
            }

            public void ArrayBegin(string name)
            {
                Key(name);
                _sb.Append('[');
                _depth++;
                _first = true;
            }

            public void ArrayEnd()
            {
                _depth--;
                _sb.Append('\n').Append(' ', _depth * 2).Append(']');
                _first = false;
            }

            public void StrItem(string value)
            {
                Separator();
                AppendString(value);
            }

            public void Str(string name, string value)
            {
                Key(name);
                AppendString(value);
            }

            public void Int(string name, long value)
            {
                Key(name);
                _sb.Append(value.ToString(CultureInfo.InvariantCulture));
            }

            public void Num(string name, float value)
            {
                Key(name);
                AppendNumber(value);
            }

            public void Bool(string name, bool value)
            {
                Key(name);
                _sb.Append(value ? "true" : "false");
            }

            public void Range(string name, FloatRange range)
            {
                Floats(name, new float[] { range.Min, range.Max });
            }

            public void Floats(string name, float[] values)
            {
                Key(name);
                _sb.Append('[');
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    AppendNumber(values[i]);
                }
                _sb.Append(']');
            }

            public void Ints(string name, int[] values)
            {
                Key(name);
                _sb.Append('[');
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    _sb.Append(values[i].ToString(CultureInfo.InvariantCulture));
                }
                _sb.Append(']');
            }

            public void Strings(string name, string[] values)
            {
                Key(name);
                _sb.Append('[');
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) _sb.Append(", ");
                    AppendString(values[i]);
                }
                _sb.Append(']');
            }

            public override string ToString()
            {
                return _sb.ToString() + "\n";
            }

            void Key(string name)
            {
                Separator();
                AppendString(name);
                _sb.Append(": ");
            }

            void Separator()
            {
                if (!_first) _sb.Append(',');
                if (_sb.Length > 0) _sb.Append('\n').Append(' ', _depth * 2);
                _first = false;
            }

            void AppendNumber(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) _sb.Append("null");
                else _sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }

            void AppendString(string value)
            {
                string s = value ?? string.Empty;
                _sb.Append('"');
                for (int i = 0; i < s.Length; i++)
                {
                    char ch = s[i];
                    switch (ch)
                    {
                        case '"': _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        default:
                            if (ch < ' ')
                                _sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                _sb.Append(ch);
                            break;
                    }
                }
                _sb.Append('"');
            }
        }
    }
}
