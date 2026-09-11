using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Control;
using WheelLeg.Core;
using WheelLeg.Robot;

namespace WheelLeg.Agent
{
    /// <summary>
    /// Turns a <see cref="RobotState"/> into the 60-value policy observation, then applies
    /// the sensor model: gaussian noise, a slowly drifting gyro bias, encoder quantisation
    /// and a whole-vector transport delay.
    ///
    /// The model is always compiled in and always runs; its coefficients are config values
    /// and are zero in the local profile. Deferring the implementation until the server run
    /// would mean the sensor model's first execution happens where nobody is watching.
    ///
    /// Only non-privileged quantities are included. Terrain height, contact forces and the
    /// true centre of mass are logged but never handed to the policy (SPEC section 4).
    /// </summary>
    public sealed class ObservationBuilder
    {
        readonly ObservationConfig _config;
        readonly DeterministicRandom _rng;
        readonly float[] _raw = new float[RobotLayout.ObservationSize];
        readonly float[][] _delayBuffer;
        readonly Vector3 _angVelBias;

        Vector3 _angVelBiasState;
        int _delayCursor;
        int _warmupRemaining;

        public ObservationBuilder(ObservationConfig config, DeterministicRandom rng)
        {
            _config = config;
            _rng = rng;
            _angVelBias = Vector3.zero;
            _angVelBiasState = Vector3.zero;

            // One extra slot so a delay of N steps can be served while step N is being written.
            _delayBuffer = new float[config.DelaySteps + 1][];
            for (int i = 0; i < _delayBuffer.Length; i++)
                _delayBuffer[i] = new float[RobotLayout.ObservationSize];
        }

        /// <summary>Clears the delay history and redraws the sensor bias for a new episode.</summary>
        public void OnEpisodeBegin()
        {
            _delayCursor = 0;
            _warmupRemaining = _delayBuffer.Length - 1;
            _angVelBiasState = new Vector3(
                _rng.NextGaussian(_config.AngVelBiasWalkStd),
                _rng.NextGaussian(_config.AngVelBiasWalkStd),
                _rng.NextGaussian(_config.AngVelBiasWalkStd));
            for (int i = 0; i < _delayBuffer.Length; i++)
                System.Array.Clear(_delayBuffer[i], 0, _delayBuffer[i].Length);
        }

        /// <summary>
        /// Writes the observation the policy should act on into <paramref name="output"/>.
        /// During the first <c>delay_steps</c> steps of an episode the delayed slot holds the
        /// oldest available frame rather than a zero vector, so the episode does not start
        /// with a physically impossible reading.
        /// </summary>
        public void Build(
            RobotState state,
            LegJointLimits limits,
            Vector3 command,
            float[] previousAction,
            float[] output)
        {
            BuildRaw(state, limits, command, previousAction, _raw);
            ApplySensorModel(_raw);

            float[] slot = _delayBuffer[_delayCursor];
            System.Array.Copy(_raw, slot, _raw.Length);

            int readIndex = _warmupRemaining > 0
                ? _delayCursor
                : (_delayCursor + 1) % _delayBuffer.Length;
            if (_warmupRemaining > 0) _warmupRemaining--;

            System.Array.Copy(_delayBuffer[readIndex], output, output.Length);
            _delayCursor = (_delayCursor + 1) % _delayBuffer.Length;
        }

        /// <summary>The undelayed, unnoised observation. Logged for analysis, never fed to the policy.</summary>
        public float[] Raw { get { return _raw; } }

        void BuildRaw(
            RobotState state,
            LegJointLimits limits,
            Vector3 command,
            float[] previousAction,
            float[] o)
        {
            o[ObservationLayout.Up + 0] = state.UpLocal.x;
            o[ObservationLayout.Up + 1] = state.UpLocal.y;
            o[ObservationLayout.Up + 2] = state.UpLocal.z;

            o[ObservationLayout.LinearVelocity + 0] = state.LinearVelocityLocal.x;
            o[ObservationLayout.LinearVelocity + 1] = state.LinearVelocityLocal.y;
            o[ObservationLayout.LinearVelocity + 2] = state.LinearVelocityLocal.z;

            o[ObservationLayout.AngularVelocity + 0] = state.AngularVelocityLocal.x;
            o[ObservationLayout.AngularVelocity + 1] = state.AngularVelocityLocal.y;
            o[ObservationLayout.AngularVelocity + 2] = state.AngularVelocityLocal.z;

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                o[ObservationLayout.LegAngle + i] = limits.NormalizeAngle(i, state.LegAngleRad[i]);
                o[ObservationLayout.LegVelocity + i] = state.LegVelocityRad[i];
            }

            for (int w = 0; w < RobotLayout.WheelCount; w++)
            {
                o[ObservationLayout.WheelVelocity + w] = state.WheelVelocityRad[w];
                o[ObservationLayout.WheelContact + w] = state.WheelContact[w] ? 1f : 0f;
            }

            o[ObservationLayout.Command + 0] = command.x;
            o[ObservationLayout.Command + 1] = command.y;
            o[ObservationLayout.Command + 2] = command.z;

            for (int i = 0; i < RobotLayout.ActionSize; i++)
                o[ObservationLayout.PreviousAction + i] = previousAction[i];

            // Wheel angle is deliberately absent: the joint spins without limit, so its
            // absolute angle carries no information about the robot's state.
        }

        void ApplySensorModel(float[] o)
        {
            if (_config.AngVelBiasWalkStd > 0f)
            {
                _angVelBiasState += new Vector3(
                    _rng.NextGaussian(_config.AngVelBiasWalkStd),
                    _rng.NextGaussian(_config.AngVelBiasWalkStd),
                    _rng.NextGaussian(_config.AngVelBiasWalkStd));
                _angVelBiasState = ClampMagnitude(_angVelBiasState, _config.AngVelBiasLimit);
            }

            for (int i = 0; i < 3; i++)
            {
                o[ObservationLayout.Up + i] += _rng.NextGaussian(_config.UpNoiseStd);
                o[ObservationLayout.LinearVelocity + i] += _rng.NextGaussian(_config.LinVelNoiseStd);
                o[ObservationLayout.AngularVelocity + i] +=
                    _rng.NextGaussian(_config.AngVelNoiseStd) + _angVelBiasState[i];
            }

            // The angle channel is already normalised to [-1, 1] over each joint's range, so
            // the encoder quantum is expressed in degrees and converted through that range.
            if (_config.JointAngleQuantumDeg > 0f)
            {
                for (int i = 0; i < RobotLayout.LegJointCount; i++)
                {
                    int index = ObservationLayout.LegAngle + i;
                    o[index] = Quantize(o[index], _config.JointAngleQuantumDeg * Mathf.Deg2Rad);
                }
            }

            if (_config.JointVelQuantum > 0f)
            {
                for (int i = 0; i < RobotLayout.LegJointCount; i++)
                {
                    int index = ObservationLayout.LegVelocity + i;
                    o[index] = Quantize(o[index], _config.JointVelQuantum);
                }
            }

            if (_config.WheelVelQuantum > 0f)
            {
                for (int w = 0; w < RobotLayout.WheelCount; w++)
                {
                    int index = ObservationLayout.WheelVelocity + w;
                    o[index] = Quantize(o[index], _config.WheelVelQuantum);
                }
            }
        }

        static float Quantize(float value, float quantum)
        {
            return quantum <= 0f ? value : Mathf.Round(value / quantum) * quantum;
        }

        static Vector3 ClampMagnitude(Vector3 v, float limit)
        {
            if (limit <= 0f) return Vector3.zero;
            return v.sqrMagnitude > limit * limit ? v.normalized * limit : v;
        }
    }
}
