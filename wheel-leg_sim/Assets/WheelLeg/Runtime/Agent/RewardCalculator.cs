using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Logging;
using WheelLeg.Robot;

namespace WheelLeg.Agent
{
    /// <summary>
    /// Every reward term listed in SPEC.md section 4 is computed on every step. A term is
    /// switched off by giving it a weight of 0 in reward.yaml, never by removing it here,
    /// so the server can enable one without touching code.
    ///
    /// Terms are stored individually as well as summed because the per-term breakdown is
    /// part of the frame log schema; without it a learning curve cannot be attributed to a
    /// particular term.
    /// </summary>
    public sealed class RewardCalculator
    {
        readonly RewardConfig _config;
        readonly float[] _terms = new float[FrameSchema.RewardTermNames.Length];

        // Index into _terms, matching FrameSchema.RewardTermNames order.
        const int LinVelTracking = 0;
        const int AngVelTracking = 1;
        const int PostureUp = 2;
        const int LateralVelocity = 3;
        const int JointTorque = 4;
        const int WheelTorque = 5;
        const int LegActionRate = 6;
        const int WheelActionRate = 7;
        const int WheelSlip = 8;
        const int Alive = 9;
        const int Termination = 10;

        public RewardCalculator(RewardConfig config)
        {
            _config = config;
        }

        /// <summary>Weighted per-term contributions of the last <see cref="Compute"/> call.</summary>
        public float[] Terms { get { return _terms; } }

        public float Total { get; private set; }

        public float Compute(
            RobotState state,
            Vector3 command,
            float[] action,
            float[] previousAction,
            bool terminatedEarly)
        {
            System.Array.Clear(_terms, 0, _terms.Length);

            // Tracking uses the true body-frame velocity, not the noised observation: the
            // reward defines the task, and a noisy sensor should not change what the task is.
            float vxError = command.x - state.LinearVelocityLocal.z;
            float wzError = command.z - state.AngularVelocityLocal.y;
            _terms[LinVelTracking] = _config.LinVelTracking * Gaussian(vxError);
            _terms[AngVelTracking] = _config.AngVelTracking * Gaussian(wzError);

            _terms[PostureUp] = _config.PostureUp * state.UpLocal.y;

            float lateral = state.LinearVelocityLocal.x - command.y;
            _terms[LateralVelocity] = _config.LateralVelocity * lateral * lateral;

            float jointTorqueSq = 0f;
            for (int i = 0; i < RobotLayout.LegJointCount; i++)
                jointTorqueSq += state.LegTorqueNm[i] * state.LegTorqueNm[i];
            _terms[JointTorque] = _config.JointTorque * jointTorqueSq;

            float wheelTorqueSq = 0f;
            for (int w = 0; w < RobotLayout.WheelCount; w++)
                wheelTorqueSq += state.WheelTorqueNm[w] * state.WheelTorqueNm[w];
            _terms[WheelTorque] = _config.WheelTorque * wheelTorqueSq;

            // Leg and wheel actions are penalised separately because their normalised units
            // map onto physically different quantities (an angle versus an angular rate).
            float legRateSq = 0f;
            for (int i = 0; i < RobotLayout.WheelActionOffset; i++)
            {
                float d = action[i] - previousAction[i];
                legRateSq += d * d;
            }
            _terms[LegActionRate] = _config.LegActionRate * legRateSq;

            float wheelRateSq = 0f;
            for (int i = RobotLayout.WheelActionOffset; i < RobotLayout.ActionSize; i++)
            {
                float d = action[i] - previousAction[i];
                wheelRateSq += d * d;
            }
            _terms[WheelActionRate] = _config.WheelActionRate * wheelRateSq;

            // Slip is only meaningful for a wheel that is touching the ground: an airborne
            // wheel spinning at any speed is not slipping.
            float slipSq = 0f;
            float forwardSpeed = state.LinearVelocityLocal.z;
            for (int w = 0; w < RobotLayout.WheelCount; w++)
            {
                if (!state.WheelContact[w]) continue;
                float slip = state.WheelSurfaceSpeed[w] - forwardSpeed;
                slipSq += slip * slip;
            }
            _terms[WheelSlip] = _config.WheelSlip * slipSq;

            _terms[Alive] = _config.Alive;
            _terms[Termination] = terminatedEarly ? _config.Termination : 0f;

            float total = 0f;
            for (int i = 0; i < _terms.Length; i++) total += _terms[i];
            Total = total;
            return total;
        }

        float Gaussian(float error)
        {
            return Mathf.Exp(-(error * error) / _config.TrackingSigma);
        }
    }
}
