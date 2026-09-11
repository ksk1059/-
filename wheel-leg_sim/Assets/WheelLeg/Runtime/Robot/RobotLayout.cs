using UnityEngine;

namespace WheelLeg.Robot
{
    /// <summary>
    /// Fixed index layout shared by observations, actions and logs.
    /// Leg order FL, FR, RL, RR; within a leg hip, thigh, calf; wheels in leg order.
    /// Changing any of this changes the meaning of every recorded run, so it is a
    /// constant rather than a config value.
    /// </summary>
    public static class RobotLayout
    {
        public const int LegCount = 4;
        public const int JointsPerLeg = 3;
        public const int LegJointCount = LegCount * JointsPerLeg; // 12
        public const int WheelCount = LegCount;                   // 4

        public const int HipOffset = 0;
        public const int ThighOffset = 1;
        public const int CalfOffset = 2;

        public static int LegJoint(int leg, int jointInLeg)
        {
            return leg * JointsPerLeg + jointInLeg;
        }

        /// <summary>Observation vector size. Must equal BehaviorParameters Space Size.</summary>
        public const int ObservationSize =
            3                    // body up vector, body frame
            + 3                  // linear velocity, body frame
            + 3                  // angular velocity, body frame
            + LegJointCount      // leg joint angles, normalised to [-1, 1] over their limits
            + LegJointCount      // leg joint velocities
            + WheelCount         // wheel angular velocities
            + WheelCount         // wheel contact flags
            + 3                  // commanded vx, vy, wz
            + 16;                // previous action

        /// <summary>Action vector size: 12 leg position targets, 4 wheel velocity targets.</summary>
        public const int ActionSize = LegJointCount + WheelCount;

        public const int WheelActionOffset = LegJointCount;
    }

    /// <summary>
    /// Full robot state for one physics read. All SI units: metres, radians, seconds,
    /// newton-metres. Allocated once per area and refilled in place; nothing here is
    /// allocated inside the step loop.
    /// </summary>
    public sealed class RobotState
    {
        // World frame.
        public Vector3 BasePosition;
        public Quaternion BaseRotation;
        public Vector3 BaseLinearVelocityWorld;
        public Vector3 BaseAngularVelocityWorld;
        public Vector3 ComPositionWorld;
        public Vector3 ComVelocityWorld;

        // Body frame. Everything the policy sees is expressed here.
        public Vector3 UpLocal;
        public Vector3 LinearVelocityLocal;
        public Vector3 AngularVelocityLocal;

        public float Roll;
        public float Pitch;

        public readonly float[] LegAngleRad = new float[RobotLayout.LegJointCount];
        public readonly float[] LegVelocityRad = new float[RobotLayout.LegJointCount];
        public readonly float[] LegTorqueNm = new float[RobotLayout.LegJointCount];

        public readonly float[] WheelVelocityRad = new float[RobotLayout.WheelCount];
        public readonly float[] WheelTorqueNm = new float[RobotLayout.WheelCount];
        public readonly bool[] WheelContact = new bool[RobotLayout.WheelCount];
        public readonly Vector3[] WheelContactForce = new Vector3[RobotLayout.WheelCount];
        /// <summary>Contact point speed of the wheel rim along the body forward axis, m/s.</summary>
        public readonly float[] WheelSurfaceSpeed = new float[RobotLayout.WheelCount];

        public bool BaseContact;
    }
}
