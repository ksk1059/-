using System;
using System.Collections.Generic;
using WheelLeg.Robot;

namespace WheelLeg.Logging
{
    public enum ColumnType
    {
        Double,
        Int64,
        String
    }

    /// <summary>
    /// The per-frame log schema from SPEC.md section 7, flattened to scalar columns.
    /// Column order is stable and is the contract between the writer and any analysis
    /// script; terrain_seed, seed_band and controller_type are mandatory because
    /// without them a finished run cannot be attributed to a condition afterwards.
    /// </summary>
    public static class FrameSchema
    {
        public static readonly string[] Names;
        public static readonly ColumnType[] Types;

        // Indices of columns written individually. Block columns are addressed by
        // their first index plus an offset.
        public static readonly int T;
        public static readonly int Step;
        public static readonly int EpisodeId;
        public static readonly int AreaId;
        public static readonly int TerrainSeed;
        public static readonly int SeedBand;
        public static readonly int ControllerType;
        public static readonly int BasePos;          // 3
        public static readonly int BaseQuat;         // 4
        public static readonly int BaseLinVel;       // 3
        public static readonly int BaseAngVel;       // 3
        public static readonly int ComPos;           // 3
        public static readonly int ComVel;           // 3
        public static readonly int JointPos;         // 12
        public static readonly int JointVel;         // 12
        public static readonly int JointTorque;      // 12
        public static readonly int WheelVel;         // 4
        public static readonly int WheelTorque;      // 4
        public static readonly int Obs;              // 60
        public static readonly int Action;           // 16
        public static readonly int ContactFlag;      // 4
        public static readonly int ContactForce;     // 12 (4 x xyz)
        public static readonly int Roll;
        public static readonly int Pitch;
        public static readonly int HeightError;
        public static readonly int CmdVel;           // 3
        public static readonly int RewardTotal;
        public static readonly int RewardTerms;      // RewardTermNames.Length
        public static readonly int DoneFlag;
        public static readonly int FailReason;

        /// <summary>Reward terms, in the order they appear as reward_&lt;name&gt; columns.</summary>
        public static readonly string[] RewardTermNames =
        {
            "lin_vel_tracking",
            "ang_vel_tracking",
            "posture_up",
            "lateral_velocity",
            "joint_torque",
            "wheel_torque",
            "leg_action_rate",
            "wheel_action_rate",
            "wheel_slip",
            "alive",
            "termination"
        };

        public static int Count { get { return Names.Length; } }

        static FrameSchema()
        {
            List<string> names = new List<string>();
            List<ColumnType> types = new List<ColumnType>();

            Func<string, ColumnType, int> one = (name, type) =>
            {
                int index = names.Count;
                names.Add(name);
                types.Add(type);
                return index;
            };

            Func<string, int, ColumnType, int> block = (prefix, count, type) =>
            {
                int index = names.Count;
                for (int i = 0; i < count; i++)
                {
                    names.Add(prefix + i);
                    types.Add(type);
                }
                return index;
            };

            Func<string, ColumnType, int> xyz = (prefix, type) =>
            {
                int index = names.Count;
                names.Add(prefix + "_x");
                names.Add(prefix + "_y");
                names.Add(prefix + "_z");
                types.Add(type);
                types.Add(type);
                types.Add(type);
                return index;
            };

            T = one("t", ColumnType.Double);
            Step = one("step", ColumnType.Int64);
            EpisodeId = one("episode_id", ColumnType.Int64);
            AreaId = one("area_id", ColumnType.Int64);
            TerrainSeed = one("terrain_seed", ColumnType.Int64);
            SeedBand = one("seed_band", ColumnType.String);
            ControllerType = one("controller_type", ColumnType.String);

            BasePos = xyz("base_pos", ColumnType.Double);
            BaseQuat = names.Count;
            names.Add("base_quat_x");
            names.Add("base_quat_y");
            names.Add("base_quat_z");
            names.Add("base_quat_w");
            for (int i = 0; i < 4; i++) types.Add(ColumnType.Double);

            BaseLinVel = xyz("base_linvel", ColumnType.Double);
            BaseAngVel = xyz("base_angvel", ColumnType.Double);
            ComPos = xyz("com_pos", ColumnType.Double);
            ComVel = xyz("com_vel", ColumnType.Double);

            JointPos = block("joint_pos_", RobotLayout.LegJointCount, ColumnType.Double);
            JointVel = block("joint_vel_", RobotLayout.LegJointCount, ColumnType.Double);
            JointTorque = block("joint_torque_", RobotLayout.LegJointCount, ColumnType.Double);
            WheelVel = block("wheel_vel_", RobotLayout.WheelCount, ColumnType.Double);
            WheelTorque = block("wheel_torque_", RobotLayout.WheelCount, ColumnType.Double);

            Obs = block("obs_", RobotLayout.ObservationSize, ColumnType.Double);
            Action = block("action_", RobotLayout.ActionSize, ColumnType.Double);

            ContactFlag = block("contact_flag_", RobotLayout.WheelCount, ColumnType.Int64);
            ContactForce = names.Count;
            for (int w = 0; w < RobotLayout.WheelCount; w++)
            {
                names.Add("contact_force_" + w + "_x");
                names.Add("contact_force_" + w + "_y");
                names.Add("contact_force_" + w + "_z");
                types.Add(ColumnType.Double);
                types.Add(ColumnType.Double);
                types.Add(ColumnType.Double);
            }

            Roll = one("roll", ColumnType.Double);
            Pitch = one("pitch", ColumnType.Double);
            HeightError = one("height_error", ColumnType.Double);
            CmdVel = names.Count;
            names.Add("cmd_vel_x");
            names.Add("cmd_vel_y");
            names.Add("cmd_vel_wz");
            for (int i = 0; i < 3; i++) types.Add(ColumnType.Double);

            RewardTotal = one("reward_total", ColumnType.Double);
            RewardTerms = names.Count;
            foreach (string term in RewardTermNames)
            {
                names.Add("reward_" + term);
                types.Add(ColumnType.Double);
            }

            DoneFlag = one("done_flag", ColumnType.Int64);
            FailReason = one("fail_reason", ColumnType.String);

            Names = names.ToArray();
            Types = types.ToArray();
        }
    }

    /// <summary>
    /// Column-major batch of frames. Allocated once at the configured capacity and
    /// refilled after each flush, so steady-state logging allocates nothing and the
    /// memory ceiling is a config value rather than a function of run length.
    /// </summary>
    public sealed class FrameBatch
    {
        public readonly int Capacity;
        public int Count;

        readonly double[][] _doubles;
        readonly long[][] _longs;
        readonly string[][] _strings;

        public FrameBatch(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            Capacity = capacity;
            int columns = FrameSchema.Count;
            _doubles = new double[columns][];
            _longs = new long[columns][];
            _strings = new string[columns][];
            for (int c = 0; c < columns; c++)
            {
                switch (FrameSchema.Types[c])
                {
                    case ColumnType.Double: _doubles[c] = new double[capacity]; break;
                    case ColumnType.Int64: _longs[c] = new long[capacity]; break;
                    default: _strings[c] = new string[capacity]; break;
                }
            }
        }

        public bool IsFull { get { return Count >= Capacity; } }

        public double[] DoubleColumn(int column) { return _doubles[column]; }
        public long[] LongColumn(int column) { return _longs[column]; }
        public string[] StringColumn(int column) { return _strings[column]; }

        public void SetDouble(int column, double value) { _doubles[column][Count] = value; }
        public void SetLong(int column, long value) { _longs[column][Count] = value; }
        public void SetString(int column, string value) { _strings[column][Count] = value ?? string.Empty; }

        public void SetDoubles(int firstColumn, float[] values, int count)
        {
            for (int i = 0; i < count; i++) _doubles[firstColumn + i][Count] = values[i];
        }

        public void CommitRow()
        {
            if (Count >= Capacity) throw new InvalidOperationException("FrameBatch overflow");
            Count++;
        }

        public void Clear()
        {
            Count = 0;
        }
    }

    /// <summary>Destination for completed batches. One instance owns one output file.</summary>
    public interface IFrameSink : IDisposable
    {
        string Extension { get; }
        void Open(string path);
        void Write(FrameBatch batch);
        void Flush();
        /// <summary>Bytes committed to disk so far, used to enforce rotation limits.</summary>
        long BytesWritten { get; }
    }
}
