using UnityEngine;
using WheelLeg.Config;

namespace WheelLeg.Robot
{
    /// <summary>
    /// Snapshot of the 12 leg joint limits, read once from the imported ArticulationBodies
    /// rather than copied out of the URDF into code, plus the stand pose the action is
    /// measured against.
    ///
    /// The two directions are deliberately NOT the same mapping, so they have distinct names:
    ///
    /// - <see cref="NormalizeAngle"/> is the observation. It spans each joint's full travel,
    ///   so the policy can tell how close a joint is to its mechanical limit.
    /// - <see cref="ActionToTargetRad"/> is the action. It is a residual around the stand
    ///   pose: action 0 means "hold the stand pose".
    ///
    /// Centring the action on the limit midpoint instead looks symmetrical but is wrong on
    /// this robot. Go2-W's rear thigh joints are limited to (-0.5236, 4.5379) rad against the
    /// front's (-1.5708, 3.4907), so the midpoint of the rear thigh is 115 deg while the
    /// stand pose is 50 deg. An untrained policy emits approximately zero, which under a
    /// midpoint mapping commands the rear thighs 65 deg away from where the robot was just
    /// reset. The drive answers with its full force limit, the rear legs are kicked out from
    /// under the body, and the robot is launched off the ground on the first step of every
    /// episode. Measured: 68 Nm demanded against a 35.55 Nm limit, 13 rad/s joint velocity
    /// and 1066 N of wheel contact force against a 192 N body weight.
    /// </summary>
    public sealed class LegJointLimits
    {
        readonly float[] _lower = new float[RobotLayout.LegJointCount];
        readonly float[] _upper = new float[RobotLayout.LegJointCount];
        readonly float[] _mid = new float[RobotLayout.LegJointCount];
        readonly float[] _halfRange = new float[RobotLayout.LegJointCount];
        readonly float[] _standRad = new float[RobotLayout.LegJointCount];
        readonly float _actionScaleRad;

        public LegJointLimits(RobotDriver driver, RobotConfig config)
        {
            float[] standDeg = config.StandPoseDegIndexed();
            _actionScaleRad = config.ActionScaleDeg * Mathf.Deg2Rad;

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                float lower = driver.LegLowerLimitRad(i);
                float upper = driver.LegUpperLimitRad(i);
                if (upper <= lower)
                {
                    // A joint that came in unlimited cannot be normalised meaningfully. Fail
                    // loudly at setup instead of silently producing a constant observation.
                    Debug.LogError(
                        "Leg joint " + i + " has no usable limit range (" + lower + ", " + upper + " rad)");
                    upper = lower + Mathf.Epsilon;
                }
                _lower[i] = lower;
                _upper[i] = upper;
                _mid[i] = 0.5f * (lower + upper);
                _halfRange[i] = 0.5f * (upper - lower);

                float stand = standDeg[i] * Mathf.Deg2Rad;
                if (stand < lower || stand > upper)
                {
                    Debug.LogWarning("Leg joint " + i + ": stand pose " + standDeg[i]
                        + " deg is outside the imported limits (" + lower * Mathf.Rad2Deg + ", "
                        + upper * Mathf.Rad2Deg + " deg); action 0 will not hold the stand pose");
                }
                _standRad[i] = Mathf.Clamp(stand, lower, upper);
            }
        }

        public float LowerRad(int index) { return _lower[index]; }
        public float UpperRad(int index) { return _upper[index]; }

        /// <summary>The stand pose this joint's action is measured against, in radians.</summary>
        public float StandRad(int index) { return _standRad[index]; }

        /// <summary>
        /// Observation channel: physical angle in radians to [-1, 1] across the joint's full
        /// travel. Not the inverse of <see cref="ActionToTargetRad"/>.
        /// </summary>
        public float NormalizeAngle(int index, float angleRad)
        {
            return Mathf.Clamp((angleRad - _mid[index]) / _halfRange[index], -1f, 1f);
        }

        /// <summary>
        /// Action channel: [-1, 1] to a position target in radians, as an offset from the
        /// stand pose, clamped into the joint's imported limits.
        /// </summary>
        public float ActionToTargetRad(int index, float normalized)
        {
            float clamped = normalized < -1f ? -1f : (normalized > 1f ? 1f : normalized);
            return Mathf.Clamp(_standRad[index] + clamped * _actionScaleRad, _lower[index], _upper[index]);
        }
    }
}
