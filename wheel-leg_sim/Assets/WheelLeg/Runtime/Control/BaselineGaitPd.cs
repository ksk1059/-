using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Core;
using WheelLeg.Robot;

namespace WheelLeg.Control
{
    /// <summary>
    /// Baseline A (SPEC section 8): a fixed-phase trot plus a PD posture term.
    ///
    /// The controller sees only the 60-value observation, exactly as the policy does, so the
    /// two can be compared on the same terrain through the same logger.
    ///
    /// action[0..11] is a residual around the stand pose: 0 holds the stand pose and +-1 is
    /// robot.action_scale_deg degrees away from it. That is NOT the same space as
    /// observation[LegAngle + i], which spans the joint's whole travel; the two are
    /// deliberately different mappings and a controller may not read the joint limits that
    /// define the observation one. Every angle this controller wants in degrees is therefore
    /// divided by action_scale_deg, which is the only quantity that converts between them.
    ///
    /// Every gain, the step amplitude and the gait frequency are UNFOUNDED INITIAL VALUES
    /// carried in config (SPEC section 11). Nothing here has been tuned against a trajectory.
    /// </summary>
    public sealed class BaselineGaitPd : IController
    {
        BaselineConfig _baseline;
        float _stepAmplitude;
        float _commandToWheelAction;
        float _yawToWheelAction;
        float _phase;

        public string Name { get { return "baseline_gait_pd"; } }

        /// <param name="rng">
        /// Unused: the trot is a fixed phase ramp, so introducing randomness would only make
        /// two runs of the same seed diverge for no gain.
        /// </param>
        public void Initialize(SimConfig config, DeterministicRandom rng)
        {
            _baseline = config.Baseline;

            // Degrees of swing to action units. The action is a residual around the stand pose
            // scaled by robot.action_scale_deg, so dividing by it is what makes
            // baseline.gait_pd.step_amplitude_deg mean the number of degrees it says.
            _stepAmplitude = _baseline.GaitStepHeightDeg / config.Robot.ActionScaleDeg;

            _commandToWheelAction =
                _baseline.WheelSpeedPerCommand / config.Robot.Drive.MaxWheelSpeedRadPerSec;

            // Go2-W has no steering joint, so yaw is a wheel speed difference. Converting a
            // yaw rate into that difference needs the track width over the wheel radius,
            // which is robot geometry the controller may not read and which SimConfig does
            // not carry. Scaling by the commanded yaw range instead keeps the term
            // dimensionless and configurable: a command at the edge of its range asks for a
            // full-scale differential.
            float maxYawCommand = Mathf.Max(
                Mathf.Abs(config.Command.Wz.Min),
                Mathf.Abs(config.Command.Wz.Max));
            _yawToWheelAction = maxYawCommand > 0f ? 1f / maxYawCommand : 0f;
        }

        public void OnEpisodeBegin()
        {
            _phase = 0f;
        }

        public void ComputeAction(float[] observation, float dt, float[] actionOut)
        {
            _phase += _baseline.GaitFrequencyHz * dt;
            _phase -= Mathf.Floor(_phase);

            // World up in body frame: x leans with roll, z leans with pitch, and the matching
            // body rates are angular velocity z and -x. The PD term drives both to zero.
            float rollCorrection =
                _baseline.PostureKp * observation[ObservationLayout.Up + 0]
                + _baseline.PostureKd * observation[ObservationLayout.AngularVelocity + 2];
            float pitchCorrection =
                _baseline.PostureKp * observation[ObservationLayout.Up + 2]
                - _baseline.PostureKd * observation[ObservationLayout.AngularVelocity + 0];

            for (int leg = 0; leg < RobotLayout.LegCount; leg++)
            {
                // Trot: FL (0) and RR (3) in phase, FR (1) and RL (2) half a period behind.
                float legPhase = _phase + (leg == 1 || leg == 2 ? 0.5f : 0f);
                float swing = Mathf.Sin(2f * Mathf.PI * legPhase) * _stepAmplitude;

                float leftSign = leg == 0 || leg == 2 ? 1f : -1f;
                float frontSign = leg < 2 ? 1f : -1f;

                // Hips abduct against roll, thighs pitch against pitch, and the calf takes the
                // opposite step offset from the thigh so the wheel lifts instead of the whole
                // leg swinging out from under the body.
                actionOut[RobotLayout.LegJoint(leg, RobotLayout.HipOffset)] =
                    Mathf.Clamp(leftSign * rollCorrection, -1f, 1f);
                actionOut[RobotLayout.LegJoint(leg, RobotLayout.ThighOffset)] =
                    Mathf.Clamp(frontSign * pitchCorrection + swing, -1f, 1f);
                actionOut[RobotLayout.LegJoint(leg, RobotLayout.CalfOffset)] =
                    Mathf.Clamp(-swing, -1f, 1f);
            }

            float forward =
                observation[ObservationLayout.Command + 0] * _commandToWheelAction;
            float turn =
                observation[ObservationLayout.Command + 2] * _yawToWheelAction;

            for (int wheel = 0; wheel < RobotLayout.WheelCount; wheel++)
            {
                // A positive yaw command is a turn to the right about the body up axis, so the
                // left wheels (FL, RL) speed up and the right wheels slow down.
                float leftSign = wheel == 0 || wheel == 2 ? 1f : -1f;
                actionOut[RobotLayout.WheelActionOffset + wheel] =
                    Mathf.Clamp(forward + leftSign * turn, -1f, 1f);
            }
        }
    }
}
