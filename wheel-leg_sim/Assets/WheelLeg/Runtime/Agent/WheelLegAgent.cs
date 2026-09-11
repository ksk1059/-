using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Control;
using WheelLeg.Core;
using WheelLeg.Env;
using WheelLeg.Logging;
using WheelLeg.Robot;

namespace WheelLeg.Agent
{
    /// <summary>
    /// The learning agent for one training area.
    ///
    /// Observation is <see cref="RobotLayout.ObservationSize"/> = 60 values and must match
    /// BehaviorParameters Space Size exactly, or training never starts (SPEC 10-A item 3).
    /// Action is 16 values: 12 leg position targets and 4 wheel velocity targets, normalised
    /// separately because they map onto physically different quantities.
    ///
    /// A baseline controller runs through this same class rather than a separate scene: only
    /// the source of the action differs, so the terrain, the logger and the observation
    /// pipeline are provably identical between conditions (SPEC section 8).
    /// </summary>
    public sealed class WheelLegAgent : Unity.MLAgents.Agent
    {
        SimConfig _config;
        TrainingArea _area;
        RobotDriver _driver;
        LegJointLimits _limits;
        ObservationBuilder _observationBuilder;
        RewardCalculator _rewardCalculator;
        IController _baseline;
        DeterministicRandom _rng;
        FrameLogger _logger;

        readonly RobotState _state = new RobotState();
        readonly float[] _observation = new float[RobotLayout.ObservationSize];
        readonly float[] _action = new float[RobotLayout.ActionSize];
        readonly float[] _previousAction = new float[RobotLayout.ActionSize];
        /// <summary>What actually reaches the drives, which lags _action by the actuation delay.</summary>
        readonly float[] _appliedAction = new float[RobotLayout.ActionSize];
        ActionDelayLine _actionDelay;
        readonly float[] _legTargetsRad = new float[RobotLayout.LegJointCount];
        readonly float[] _wheelTargetsRad = new float[RobotLayout.WheelCount];
        readonly float[] _resetLegAngles = new float[RobotLayout.LegJointCount];
        readonly float[] _resetLegVelocities = new float[RobotLayout.LegJointCount];
        readonly float[] _resetWheelVelocities = new float[RobotLayout.WheelCount];

        Vector3 _command;
        int _stepInEpisode;
        long _episodeId;
        long _globalStep;
        string _failReason = string.Empty;
        bool _configured;
        bool _episodeActive;
        /// <summary>True once this episode's state has been read from physics at least once.</summary>
        bool _stateValid;

        public long EpisodeCount { get { return _episodeId; } }
        public bool AreaExhausted { get { return _area != null && _area.Exhausted; } }

        public void Configure(TrainingArea area, IController baseline, DeterministicRandom rng)
        {
            _area = area;
            _config = area.Config;
            _driver = area.Driver;
            _logger = area.Logger;
            _baseline = baseline;
            _rng = rng;

            _limits = new LegJointLimits(_driver, _config.Robot);
            _observationBuilder = new ObservationBuilder(_config.Observation, rng);
            _rewardCalculator = new RewardCalculator(_config.Reward);
            _actionDelay = new ActionDelayLine(_config.Robot.ActionDelaySteps);

            DecisionRequester requester = GetComponent<DecisionRequester>();
            if (requester != null)
            {
                // Decision period is a config value, not an inspector value, because the
                // server has no inspector (SERVER_OPS_1.md section 2-3).
                requester.DecisionPeriod = _config.Physics.DecisionPeriod;
                // False on purpose. The drives are position and velocity targets that PhysX
                // holds between decisions, so there is nothing to re-apply on the three
                // intermediate physics steps. With this true, OnActionReceived fires once per
                // physics step against an observation that is only refreshed on decision
                // steps, which would add the same reward four times, write four identical log
                // rows, and make episode.max_steps count physics steps instead of decisions.
                requester.TakeActionsBetweenDecisions = false;
            }

            if (_baseline != null) _baseline.Initialize(_config, rng);
            _configured = true;
        }

        public override void OnEpisodeBegin()
        {
            if (!_configured) return;

            Vector3 spawnPosition;
            Quaternion spawnRotation;
            if (!_area.BeginEpisode(out spawnPosition, out spawnRotation))
            {
                // Evaluation sweep finished. Stop stepping; the bootstrap notices and exits.
                _episodeActive = false;
                enabled = false;
                return;
            }

            BuildResetPose();
            _driver.ResetTo(spawnPosition, spawnRotation, _resetLegAngles, _resetLegVelocities, _resetWheelVelocities);

            System.Array.Clear(_action, 0, _action.Length);
            System.Array.Clear(_previousAction, 0, _previousAction.Length);
            System.Array.Clear(_appliedAction, 0, _appliedAction.Length);
            _actionDelay.OnEpisodeBegin();
            _observationBuilder.OnEpisodeBegin();
            if (_baseline != null) _baseline.OnEpisodeBegin();

            _stepInEpisode = 0;
            _episodeId++;
            _failReason = string.Empty;
            _episodeActive = true;
            // The RobotState still holds the previous episode's readings until the next
            // CollectObservations. Judging termination against it would end the new episode
            // before the robot has been looked at even once.
            _stateValid = false;
            ResampleCommand();
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (!_configured)
            {
                for (int i = 0; i < RobotLayout.ObservationSize; i++) sensor.AddObservation(0f);
                return;
            }

            _driver.ReadState(_state, Time.fixedDeltaTime);
            _stateValid = true;
            _observationBuilder.Build(_state, _limits, _command, _previousAction, _observation);
            for (int i = 0; i < _observation.Length; i++) sensor.AddObservation(_observation[i]);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!_configured || !_episodeActive || !_stateValid) return;

            ActionSegment<float> continuous = actions.ContinuousActions;
            for (int i = 0; i < RobotLayout.ActionSize; i++)
                _action[i] = Mathf.Clamp(continuous[i], -1f, 1f);

            bool terminated = CheckTermination();
            float reward = _rewardCalculator.Compute(_state, _command, _action, _previousAction, terminated);
            AddReward(reward);

            ApplyAction();
            LogFrame(reward, terminated);

            System.Array.Copy(_action, _previousAction, _action.Length);
            _stepInEpisode++;
            _globalStep++;

            if (_stepInEpisode % _config.Command.ResampleIntervalSteps == 0) ResampleCommand();

            if (terminated)
            {
                _episodeActive = false;
                EndEpisode();
            }
            else if (_stepInEpisode >= _config.Episode.MaxSteps)
            {
                // A time limit is not a failure of the policy, so bootstrap the value
                // function rather than treating the cut-off as a terminal state.
                _failReason = "timeout";
                _episodeActive = false;
                EpisodeInterrupted();
            }
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuous = actionsOut.ContinuousActions;
            if (!_configured || _baseline == null)
            {
                for (int i = 0; i < continuous.Length; i++) continuous[i] = 0f;
                return;
            }

            // The baseline sees exactly the observation the policy sees: same noise, same
            // delay, same quantisation. Anything else would make the comparison meaningless.
            _baseline.ComputeAction(_observation, _config.Physics.DecisionDt, _action);
            for (int i = 0; i < continuous.Length && i < _action.Length; i++)
                continuous[i] = _action[i];
        }

        void ApplyAction()
        {
            // The drives get the delayed command, not the one just chosen. The observation's
            // previous-action channel and the action-rate reward still use the chosen action:
            // that is what the policy did, and it should be judged on it.
            _actionDelay.Push(_action, _appliedAction);

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
                _legTargetsRad[i] = _limits.ActionToTargetRad(i, _appliedAction[i]);
            _driver.SetLegTargetsRad(_legTargetsRad);

            float maxWheelSpeed = _config.Robot.Drive.MaxWheelSpeedRadPerSec;
            for (int w = 0; w < RobotLayout.WheelCount; w++)
                _wheelTargetsRad[w] = _appliedAction[RobotLayout.WheelActionOffset + w] * maxWheelSpeed;
            _driver.SetWheelTargetVelocitiesRad(_wheelTargetsRad);
        }

        bool CheckTermination()
        {
            TerminationConfig t = _config.Termination;

            float ground = _area.GroundHeightUnder(_state.BasePosition);
            if (_state.BasePosition.y - ground < t.MinBaseHeight)
            {
                _failReason = "low_base";
                return true;
            }

            // UpLocal.y is the dot product of the body up axis with world up.
            if (_state.UpLocal.y < t.MinUpDot)
            {
                _failReason = "tilted";
                return true;
            }

            if (t.BaseContactTerminates && _state.BaseContact)
            {
                _failReason = "base_contact";
                return true;
            }

            return false;
        }

        void BuildResetPose()
        {
            RobotConfig robot = _config.Robot;
            ResetRandomizationConfig reset = robot.Reset;

            for (int leg = 0; leg < RobotLayout.LegCount; leg++)
            {
                float[] pose = robot.StandPoseDeg[robot.Legs[leg]];
                for (int j = 0; j < RobotLayout.JointsPerLeg; j++)
                {
                    int index = RobotLayout.LegJoint(leg, j);
                    float angleDeg = pose[j] + reset.JointAngleOffsetDeg.Sample(_rng);
                    float angleRad = angleDeg * Mathf.Deg2Rad;
                    // Clamp into the joint's own runtime limits: a stand pose outside them
                    // would be silently truncated by PhysX and desynchronise the drive target.
                    _resetLegAngles[index] = Mathf.Clamp(angleRad, _limits.LowerRad(index), _limits.UpperRad(index));
                    _resetLegVelocities[index] = reset.JointVelocity.Sample(_rng);
                }
            }

            for (int w = 0; w < RobotLayout.WheelCount; w++)
                _resetWheelVelocities[w] = reset.WheelVelocity.Sample(_rng);
        }

        void ResampleCommand()
        {
            CommandConfig c = _config.Command;
            if (_rng.Chance(c.ZeroCommandProbability))
            {
                _command = Vector3.zero;
                return;
            }
            _command = new Vector3(c.Vx.Sample(_rng), c.Vy.Sample(_rng), c.Wz.Sample(_rng));
        }

        void LogFrame(float reward, bool terminated)
        {
            if (_logger == null || !_logger.IsWriting) return;

            FrameLogger batch = _logger;

            batch.SetDouble(FrameSchema.T, _globalStep * _config.Physics.DecisionDt);
            batch.SetLong(FrameSchema.Step, _stepInEpisode);
            batch.SetLong(FrameSchema.EpisodeId, _episodeId);
            batch.SetLong(FrameSchema.AreaId, _area.AreaId);
            batch.SetLong(FrameSchema.TerrainSeed, _area.TerrainSeed);
            batch.SetString(FrameSchema.SeedBand, _config.Seeds.BandName(_area.TerrainSeed));
            batch.SetString(FrameSchema.ControllerType, _config.Run.ControllerName);

            SetVector(batch, FrameSchema.BasePos, _state.BasePosition);
            batch.SetDouble(FrameSchema.BaseQuat + 0, _state.BaseRotation.x);
            batch.SetDouble(FrameSchema.BaseQuat + 1, _state.BaseRotation.y);
            batch.SetDouble(FrameSchema.BaseQuat + 2, _state.BaseRotation.z);
            batch.SetDouble(FrameSchema.BaseQuat + 3, _state.BaseRotation.w);
            SetVector(batch, FrameSchema.BaseLinVel, _state.BaseLinearVelocityWorld);
            SetVector(batch, FrameSchema.BaseAngVel, _state.BaseAngularVelocityWorld);
            SetVector(batch, FrameSchema.ComPos, _state.ComPositionWorld);
            SetVector(batch, FrameSchema.ComVel, _state.ComVelocityWorld);

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                batch.SetDouble(FrameSchema.JointPos + i, _state.LegAngleRad[i]);
                batch.SetDouble(FrameSchema.JointVel + i, _state.LegVelocityRad[i]);
                batch.SetDouble(FrameSchema.JointTorque + i, _state.LegTorqueNm[i]);
            }

            for (int w = 0; w < RobotLayout.WheelCount; w++)
            {
                batch.SetDouble(FrameSchema.WheelVel + w, _state.WheelVelocityRad[w]);
                batch.SetDouble(FrameSchema.WheelTorque + w, _state.WheelTorqueNm[w]);
                batch.SetLong(FrameSchema.ContactFlag + w, _state.WheelContact[w] ? 1 : 0);
                Vector3 force = _state.WheelContactForce[w];
                batch.SetDouble(FrameSchema.ContactForce + w * 3 + 0, force.x);
                batch.SetDouble(FrameSchema.ContactForce + w * 3 + 1, force.y);
                batch.SetDouble(FrameSchema.ContactForce + w * 3 + 2, force.z);
            }

            for (int i = 0; i < RobotLayout.ObservationSize; i++)
                batch.SetDouble(FrameSchema.Obs + i, _observation[i]);
            for (int i = 0; i < RobotLayout.ActionSize; i++)
                batch.SetDouble(FrameSchema.Action + i, _action[i]);

            batch.SetDouble(FrameSchema.Roll, _state.Roll);
            batch.SetDouble(FrameSchema.Pitch, _state.Pitch);
            float ground = _area.GroundHeightUnder(_state.BasePosition);
            batch.SetDouble(FrameSchema.HeightError,
                (_state.BasePosition.y - ground) - _config.Robot.SpawnHeight);
            batch.SetDouble(FrameSchema.CmdVel + 0, _command.x);
            batch.SetDouble(FrameSchema.CmdVel + 1, _command.y);
            batch.SetDouble(FrameSchema.CmdVel + 2, _command.z);

            batch.SetDouble(FrameSchema.RewardTotal, reward);
            float[] terms = _rewardCalculator.Terms;
            for (int i = 0; i < terms.Length; i++)
                batch.SetDouble(FrameSchema.RewardTerms + i, terms[i]);

            bool done = terminated || _stepInEpisode + 1 >= _config.Episode.MaxSteps;
            batch.SetLong(FrameSchema.DoneFlag, done ? 1 : 0);
            batch.SetString(FrameSchema.FailReason,
                terminated ? _failReason : (done ? "timeout" : string.Empty));

            _logger.CommitRow();
        }

        static void SetVector(FrameLogger batch, int firstColumn, Vector3 v)
        {
            batch.SetDouble(firstColumn + 0, v.x);
            batch.SetDouble(firstColumn + 1, v.y);
            batch.SetDouble(firstColumn + 2, v.z);
        }
    }
}
