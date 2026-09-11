using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Core;
using WheelLeg.Robot;

namespace WheelLeg.Control
{
    /// <summary>
    /// Baseline B (SPEC section 8): linear state feedback on pitch and forward speed. This is
    /// not a gait; the legs carry no swing and only level the base through a static pitch
    /// offset, so the wheels do all the regulating. That is what makes it a different
    /// comparison point from Baseline A rather than a variation of it.
    ///
    /// Like every controller it reads only the 60-value observation. Its leg outputs are a
    /// residual around the stand pose: writing 0 holds the stand pose, and +-1 is
    /// robot.action_scale_deg degrees away from it. The gains below multiply a pitch in radians
    /// straight into that residual, so they are dimensionless and need no conversion.
    ///
    /// The feedback gains are UNFOUNDED INITIAL VALUES from config (SPEC section 11). They
    /// were not solved from a plant model, so "LQR" names the structure, not the derivation.
    /// </summary>
    public sealed class BaselineBalanceLqr : IController
    {
        BaselineConfig _baseline;
        float _commandToWheelAction;
        float _yawToWheelAction;

        public string Name { get { return "baseline_balance_lqr"; } }

        /// <param name="rng">
        /// Unused: the feedback law is deterministic in the observation, and randomness it
        /// does not need would only break reproduction of a seeded run.
        /// </param>
        public void Initialize(SimConfig config, DeterministicRandom rng)
        {
            _baseline = config.Baseline;

            _commandToWheelAction =
                _baseline.WheelSpeedPerCommand / config.Robot.Drive.MaxWheelSpeedRadPerSec;

            // Same convention as Baseline A: Go2-W turns by wheel speed difference, and the
            // geometry that would convert a yaw rate into that difference is not visible to a
            // controller, so the commanded yaw range sets the scale instead.
            float maxYawCommand = Mathf.Max(
                Mathf.Abs(config.Command.Wz.Min),
                Mathf.Abs(config.Command.Wz.Max));
            _yawToWheelAction = maxYawCommand > 0f ? 1f / maxYawCommand : 0f;
        }

        /// <summary>Nothing to reset: the law is memoryless in the observation.</summary>
        public void OnEpisodeBegin()
        {
        }

        public void ComputeAction(float[] observation, float dt, float[] actionOut)
        {
            // World up in body frame gives pitch without any privileged state; positive is
            // nose up, and the matching rate is the negated body angular velocity about x.
            float pitch = Mathf.Atan2(
                observation[ObservationLayout.Up + 2],
                observation[ObservationLayout.Up + 1]);
            float pitchRate = -observation[ObservationLayout.AngularVelocity + 0];

            float commandVx = observation[ObservationLayout.Command + 0];
            float forwardSpeed = observation[ObservationLayout.LinearVelocity + 2];

            // Speed demand in m/s. Leaning nose up means falling backwards, so the pitch term
            // drives the wheels backwards to bring the base back over the contact patch.
            float speedDemand =
                _baseline.LqrVelocity * (commandVx - forwardSpeed)
                - _baseline.LqrPitch * pitch
                - _baseline.LqrPitchRate * pitchRate;

            float forward = speedDemand * _commandToWheelAction;
            float turn = observation[ObservationLayout.Command + 2] * _yawToWheelAction;

            for (int wheel = 0; wheel < RobotLayout.WheelCount; wheel++)
            {
                float leftSign = wheel == 0 || wheel == 2 ? 1f : -1f;
                actionOut[RobotLayout.WheelActionOffset + wheel] =
                    Mathf.Clamp(forward + leftSign * turn, -1f, 1f);
            }

            float thighOffset = _baseline.PostureKp * pitch;

            for (int leg = 0; leg < RobotLayout.LegCount; leg++)
            {
                float frontSign = leg < 2 ? 1f : -1f;

                // Front and rear thighs rotate opposite ways to level the base, and each calf
                // takes the opposite offset so the wheel stays at roughly the same height
                // instead of the robot squatting on one end.
                actionOut[RobotLayout.LegJoint(leg, RobotLayout.HipOffset)] = 0f;
                actionOut[RobotLayout.LegJoint(leg, RobotLayout.ThighOffset)] =
                    Mathf.Clamp(frontSign * thighOffset, -1f, 1f);
                actionOut[RobotLayout.LegJoint(leg, RobotLayout.CalfOffset)] =
                    Mathf.Clamp(-frontSign * thighOffset, -1f, 1f);
            }
        }
    }
}
