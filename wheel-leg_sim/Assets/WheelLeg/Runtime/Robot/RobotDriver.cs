using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using WheelLeg.Config;
using WheelLeg.Core;

namespace WheelLeg.Robot
{
    /// <summary>
    /// Drives one imported Go2-W and reads its state back. Sits on the robot root and
    /// resolves everything inside its own subtree only: 32 identical robots run in one
    /// process, so a global search would bind a neighbouring area's links.
    ///
    /// Unit trap that this whole file is shaped around: ArticulationBody reduced-space
    /// values (jointPosition, jointVelocity, driveForce) are radians, rad/s and Nm, while
    /// the xDrive fields (target, targetVelocity, lowerLimit, upperLimit) are degrees and
    /// deg/s. Every Rad2Deg / Deg2Rad below is that boundary, not a tuning factor.
    /// </summary>
    public sealed class RobotDriver : MonoBehaviour
    {
        readonly ArticulationBody[] _legJoints = new ArticulationBody[RobotLayout.LegJointCount];
        readonly ArticulationBody[] _wheels = new ArticulationBody[RobotLayout.WheelCount];
        readonly WheelContactSensor[] _wheelSensors = new WheelContactSensor[RobotLayout.WheelCount];
        readonly float[] _legLowerRad = new float[RobotLayout.LegJointCount];
        readonly float[] _legUpperRad = new float[RobotLayout.LegJointCount];
        // The force limit the importer wrote from the URDF <limit effort="...">, captured
        // before the configured gains overwrite it. Recorded so a run carries proof that the
        // configured torque ceiling matches the actuator the robot actually has.
        readonly float[] _importedLegForceLimit = new float[RobotLayout.LegJointCount];

        ArticulationBody _rootBody;
        ArticulationBody _baseBody;
        WheelContactSensor _baseSensor;
        ArticulationBody[] _allBodies = new ArticulationBody[0];
        float[] _bodyMasses = new float[0];
        float _inverseTotalMass;
        // Joint torque has to be read back for the whole articulation at once: the per-body
        // driveForce and jointForce properties both return exactly zero in this Unity version
        // even while the drives are holding the robot up. Allocated once; GetJointForces and
        // GetDofStartIndices refill the same lists every step.
        readonly List<float> _jointForceBuffer = new List<float>();
        readonly List<int> _dofStartIndices = new List<int>();
        readonly int[] _legDofIndex = new int[RobotLayout.LegJointCount];
        readonly int[] _wheelDofIndex = new int[RobotLayout.WheelCount];

        // Which force channel PhysX actually fills varies by Unity version, and a channel that
        // returns zero would leave the joint_torque and wheel_torque rewards permanently dead
        // and sixteen log columns empty without anything failing. Sampled over the opening
        // steps of a run and recorded next to the frame log so a finished run can be checked.
        const int TorqueProbeSamples = 10;
        readonly List<float> _probeBuffer = new List<float>();
        int _torqueProbeStepsLeft = TorqueProbeSamples;
        float _maxPerBodyDriveForce;
        float _maxPerBodyJointForce;
        float _maxHierarchyJointForce;
        float _maxHierarchyGravityForce;
        float _maxWheelSpeedRad;
        int _selfCollisionPairsIgnored;
        PhysicsMaterial _frictionMaterial;
        bool _ready;

        // Drive gains written during Awake are silently dropped: the ArticulationBody
        // hierarchy is not built yet at that point, so the xDrive setter has nothing to write
        // through to and the joints stay at stiffness 0, damping 0, force limit 0 for the
        // whole run. That is invisible from the outside because the robot still simulates,
        // it just simulates as a ragdoll and looks like a policy that never learned. The
        // gains are therefore re-applied from FixedUpdate and read back until they stick.
        DriveConfig _driveConfig;
        readonly List<string> _driveWarnings = new List<string>();
        bool _drivesVerified;

        /// <summary>Rolling radius in metres, measured from the imported wheel collider.</summary>
        public float WheelRadius;

        /// <summary>Link names the config asked for that this subtree does not provide.</summary>
        public string[] MissingLinks = new string[0];

        /// <summary>Import defects this driver found, and corrected where it could.</summary>
        public string[] ImportWarnings = new string[0];

        /// <summary>False if any of the 12 leg joints, 4 wheels or the base is unusable.</summary>
        public bool IsReady { get { return _ready; } }

        /// <param name="rng">
        /// The driver draws no random numbers: reset poses and commands are sampled by the
        /// caller so one area keeps one stream, which is what makes a run reproducible.
        /// </param>
        /// <param name="selfCollision">
        /// physics.self_collision. False disables collision between every pair of colliders in
        /// this robot; see <see cref="DisableSelfCollision"/> for why that is not optional here.
        /// </param>
        public void Initialize(RobotConfig config, DeterministicRandom rng, bool selfCollision)
        {
            List<string> missing = new List<string>();
            List<string> warnings = new List<string>();

            _allBodies = transform.GetComponentsInChildren<ArticulationBody>(true);
            _bodyMasses = new float[_allBodies.Length];
            float totalMass = 0f;
            for (int i = 0; i < _allBodies.Length; i++)
            {
                _bodyMasses[i] = _allBodies[i].mass;
                totalMass += _bodyMasses[i];
            }
            _inverseTotalMass = totalMass > 0f ? 1f / totalMass : 0f;

            Dictionary<string, ArticulationBody> byName =
                new Dictionary<string, ArticulationBody>(StringComparer.Ordinal);
            for (int i = 0; i < _allBodies.Length; i++)
            {
                ArticulationBody body = _allBodies[i];
                if (byName.ContainsKey(body.name))
                {
                    warnings.Add("duplicate link name '" + body.name + "'; the first one is bound");
                    continue;
                }
                byName.Add(body.name, body);
            }

            _rootBody = FindArticulationRoot(_allBodies);
            if (_rootBody == null) missing.Add("<articulation root>");

            _baseBody = Resolve(byName, config.BaseLinkName, missing);
            if (_baseBody != null && _rootBody != null && _baseBody != _rootBody)
            {
                warnings.Add("base link '" + config.BaseLinkName + "' is not the articulation root '"
                    + _rootBody.name + "'; ResetTo places the root, not the base");
            }
            if (_baseBody != null) _baseSensor = EnsureSensor(_baseBody.gameObject);

            string[] legNames = config.LegJointLinkNames();
            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                _legJoints[i] = Resolve(byName, legNames[i], missing);
                if (_legJoints[i] == null) continue;
                if (!IsDrivenRevolute(_legJoints[i], missing))
                {
                    _legJoints[i] = null;
                    continue;
                }
                ReadLegLimits(i, warnings);
            }

            string[] wheelNames = config.WheelLinkNames();
            float radiusSum = 0f;
            int radiusSamples = 0;
            for (int i = 0; i < RobotLayout.WheelCount; i++)
            {
                _wheels[i] = Resolve(byName, wheelNames[i], missing);
                if (_wheels[i] == null) continue;
                if (!IsDrivenRevolute(_wheels[i], missing))
                {
                    _wheels[i] = null;
                    continue;
                }
                _wheelSensors[i] = EnsureSensor(_wheels[i].gameObject);

                float radius = MeasureRadius(_wheels[i]);
                if (radius > 0f)
                {
                    radiusSum += radius;
                    radiusSamples++;
                }
                else
                {
                    // A wheel with no collider cannot touch the ground; that is a broken
                    // import, not a value to substitute a default for.
                    missing.Add(wheelNames[i] + " <collider>");
                }
            }
            WheelRadius = radiusSamples > 0 ? radiusSum / radiusSamples : 0f;

            StripImporterControlComponents(warnings);
            if (!selfCollision) DisableSelfCollision(warnings);

            _maxWheelSpeedRad = config.Drive.MaxWheelSpeedRadPerSec;
            _driveConfig = config.Drive;
            WarnOnForceLimitMismatch(config.Drive, warnings);
            ApplyDriveSettings(config.Drive, warnings);

            MissingLinks = missing.ToArray();
            ImportWarnings = warnings.ToArray();
            _ready = missing.Count == 0;
        }

        /// <summary>True once the configured drive gains have been read back from PhysX.</summary>
        public bool DrivesVerified { get { return _drivesVerified; } }

        /// <summary>
        /// False once the articulation has been destroyed or has not been built yet. A body
        /// with no degrees of freedom throws on every reduced-space accessor rather than
        /// returning a default, so this has to be checked before reading anything.
        /// </summary>
        bool IsArticulationAlive()
        {
            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                if (_legJoints[i] == null || _legJoints[i].dofCount < 1) return false;
            }
            for (int i = 0; i < RobotLayout.WheelCount; i++)
            {
                if (_wheels[i] == null || _wheels[i].dofCount < 1) return false;
            }
            return _baseBody != null && _rootBody != null;
        }

        /// <summary>
        /// Second line of defence against the URDF Importer's manual controller. It writes its
        /// own stiffness and damping into every xDrive on each Update, which zeroes the
        /// configured gains and turns the robot into a ragdoll without any error. The prefab
        /// builder already strips it; this catches a prefab rebuilt without that step.
        /// Matched by type name so the runtime assembly keeps no reference to the editor-only
        /// importer package.
        /// </summary>
        void StripImporterControlComponents(List<string> warnings)
        {
            MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                Type type = behaviour.GetType();
                if (type.FullName == null
                    || !type.FullName.StartsWith("Unity.Robotics.UrdfImporter.Control.",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                warnings.Add("removed " + type.Name
                    + " at runtime; it overwrites the configured drive gains every frame");
                Destroy(behaviour);
            }
        }

        /// <summary>
        /// Turns off collision between every pair of colliders belonging to this robot, one
        /// pair at a time rather than through the layer collision matrix, because the matrix is
        /// process-global and thirty-two robots share this process (SERVER_OPS_1.md section 2).
        ///
        /// ArticulationBody only filters a body against its direct parent. Every URDF link
        /// becomes its own body, fixed joints included, so Go2-W's calf ends up with three
        /// unfiltered siblings hanging off it: the lower shin, the wheel motor housing and the
        /// wheel. Measured from the URDF in the rest pose, the wheel collider's centre is
        /// 38 mm from the motor housing's while their radii sum to 146 mm, so the wheel
        /// permanently engulfs its own motor by 108 mm, and the shin overlaps both. Twelve
        /// such pairs exist, three per leg, and the solver spends every step trying to push
        /// them apart. The robot shakes, then gets thrown off the ground.
        ///
        /// Nothing reports this. Both colliders are legitimate URDF geometry and PhysX is
        /// behaving correctly; the URDF simply relies on a consumer that filters adjacent-link
        /// collisions, and Unity's importer does not.
        /// </summary>
        void DisableSelfCollision(List<string> warnings)
        {
            Collider[] colliders = GetComponentsInChildren<Collider>(true);
            int pairs = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                if (!IsIgnorable(colliders[i])) continue;
                for (int j = i + 1; j < colliders.Length; j++)
                {
                    if (!IsIgnorable(colliders[j])) continue;
                    Physics.IgnoreCollision(colliders[i], colliders[j], true);
                    pairs++;
                }
            }
            _selfCollisionPairsIgnored = pairs;
            if (pairs == 0)
                warnings.Add("physics.self_collision is false but no collider pairs were found "
                    + "to ignore; the robot's colliders may not have been imported");
        }

        /// <summary>
        /// Physics.IgnoreCollision logs an error for a collider that is disabled or on an
        /// inactive object, and the importer leaves some geometry inactive.
        /// </summary>
        static bool IsIgnorable(Collider collider)
        {
            return collider != null && collider.enabled && collider.gameObject.activeInHierarchy;
        }

        /// <summary>How many intra-robot collider pairs had collision switched off.</summary>
        public int SelfCollisionPairsIgnored { get { return _selfCollisionPairsIgnored; } }

        /// <summary>The friction coefficient currently on this robot's colliders.</summary>
        public float WheelFriction { get; private set; }

        /// <summary>
        /// Puts <paramref name="friction"/> on every collider of this robot so the contact with
        /// the ground actually has that coefficient.
        ///
        /// PhysX combines the two materials in a contact. The terrain carries the configured
        /// value; without this the robot carries none, PhysX substitutes its 0.6 default, and
        /// the default Average rule turns a configured terrain.friction of 1.0 into 0.8 at the
        /// contact. Setting both sides to the same number makes Average return it unchanged, so
        /// the config value means the tyre-ground coefficient it claims to be.
        ///
        /// The material is per robot, not shared: each of the 32 areas draws its own friction
        /// from its own seed, and one shared material would give them all the last one written
        /// (SERVER_OPS_1.md section 2).
        /// </summary>
        public void SetFriction(float friction)
        {
            if (_frictionMaterial == null)
            {
                _frictionMaterial = new PhysicsMaterial("WheelLegRobot");
                // Stated rather than inherited: the combine rule decides whether the number
                // above survives to the contact, and PhysX picks the higher-valued rule of the
                // two materials, so leaving it implicit makes the result depend on the terrain.
                _frictionMaterial.frictionCombine = PhysicsMaterialCombine.Average;

                Collider[] colliders = GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] == null) continue;
                    colliders[i].sharedMaterial = _frictionMaterial;
                }
            }

            WheelFriction = friction;
            _frictionMaterial.dynamicFriction = friction;
            _frictionMaterial.staticFriction = friction;
        }

        void OnDestroy()
        {
            // Created with new, so it is not owned by the scene and would leak across the
            // thousands of episodes a long run goes through.
            if (_frictionMaterial != null) Destroy(_frictionMaterial);
        }

        void FixedUpdate()
        {
            if (_drivesVerified || _driveConfig == null || !_ready || !IsArticulationAlive()) return;

            _driveWarnings.Clear();
            ApplyDriveSettings(_driveConfig, _driveWarnings);
            for (int i = 0; i < _driveWarnings.Count; i++) Debug.LogWarning(_driveWarnings[i]);

            ArticulationDrive leg = _legJoints[0].xDrive;
            ArticulationDrive wheel = _wheels[0].xDrive;
            _drivesVerified =
                Mathf.Approximately(leg.stiffness, _driveConfig.LegStiffness)
                && Mathf.Approximately(leg.damping, _driveConfig.LegDamping)
                && Mathf.Approximately(wheel.damping, _driveConfig.WheelDamping);
        }

        /// <summary>Articulation root, exposed so hierarchy-wide force readback can be measured.</summary>
        public ArticulationBody RootBody { get { return _rootBody; } }

        /// <summary>
        /// Refills the whole-articulation joint force buffer for this step. The reduced-space
        /// index of a body is not its position in the component array, so the mapping is taken
        /// from GetDofStartIndices, which is indexed by ArticulationBody.index.
        /// </summary>
        void ReadJointForces()
        {
            if (_rootBody == null) return;
            _rootBody.GetJointForces(_jointForceBuffer);
            _rootBody.GetDofStartIndices(_dofStartIndices);

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
                _legDofIndex[i] = DofStartOf(_legJoints[i]);
            for (int i = 0; i < RobotLayout.WheelCount; i++)
                _wheelDofIndex[i] = DofStartOf(_wheels[i]);
        }

        int DofStartOf(ArticulationBody body)
        {
            if (body == null) return -1;
            int index = body.index;
            return index >= 0 && index < _dofStartIndices.Count ? _dofStartIndices[index] : -1;
        }

        float JointForceAt(int dofIndex)
        {
            return dofIndex >= 0 && dofIndex < _jointForceBuffer.Count ? _jointForceBuffer[dofIndex] : 0f;
        }


        /// <summary>True once any sampled channel has reported a non-zero force.</summary>
        public bool TorqueProbeComplete { get { return _torqueProbeStepsLeft <= 0; } }

        public float MaxPerBodyDriveForce { get { return _maxPerBodyDriveForce; } }
        public float MaxPerBodyJointForce { get { return _maxPerBodyJointForce; } }
        public float MaxHierarchyJointForce { get { return _maxHierarchyJointForce; } }
        public float MaxHierarchyGravityForce { get { return _maxHierarchyGravityForce; } }

        /// <summary>
        /// Live drive gains of leg joint 0, read back from PhysX. Recorded next to the run
        /// because a drive whose gains failed to apply leaves the legs unactuated, which looks
        /// like a policy that has not learned rather than a robot that cannot act.
        /// </summary>
        public Vector3 LegDriveGainsReadback
        {
            get
            {
                ArticulationBody body = _legJoints[0];
                if (body == null) return Vector3.zero;
                ArticulationDrive drive = body.xDrive;
                return new Vector3(drive.stiffness, drive.damping, drive.forceLimit);
            }
        }

        public Vector3 WheelDriveGainsReadback
        {
            get
            {
                ArticulationBody wheel = _wheels[0];
                if (wheel == null) return Vector3.zero;
                ArticulationDrive drive = wheel.xDrive;
                return new Vector3(drive.stiffness, drive.damping, drive.forceLimit);
            }
        }

        void SampleTorqueChannels()
        {
            if (_torqueProbeStepsLeft <= 0 || _rootBody == null) return;
            _torqueProbeStepsLeft--;

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                ArticulationBody body = _legJoints[i];
                if (body == null || body.dofCount < 1) continue;
                _maxPerBodyDriveForce = Mathf.Max(_maxPerBodyDriveForce, Mathf.Abs(body.driveForce[0]));
                _maxPerBodyJointForce = Mathf.Max(_maxPerBodyJointForce, Mathf.Abs(body.jointForce[0]));
            }

            _maxHierarchyJointForce = Mathf.Max(_maxHierarchyJointForce, MaxAbs(_jointForceBuffer));

            _rootBody.GetJointGravityForces(_probeBuffer);
            _maxHierarchyGravityForce = Mathf.Max(_maxHierarchyGravityForce, MaxAbs(_probeBuffer));
        }

        static float MaxAbs(List<float> values)
        {
            float max = 0f;
            for (int i = 0; i < values.Count; i++) max = Mathf.Max(max, Mathf.Abs(values[i]));
            return max;
        }

        /// <summary>
        /// What the base link last counted as ground contact. The base_contact termination
        /// fires off this, and the base is 0.36 m above flat ground in the stand pose, so
        /// anything other than terrain appearing here is a self-collision leaking through the
        /// sensor's self-contact filter.
        /// </summary>
        public string BaseLastExternalContact
        {
            get { return _baseSensor != null ? _baseSensor.LastExternalContact : null; }
        }

        public string WheelLastExternalContact(int index)
        {
            WheelContactSensor sensor = _wheelSensors[index];
            return sensor != null ? sensor.LastExternalContact : null;
        }

        /// <summary>The URDF effort limit the importer wrote, before the config overwrote it.</summary>
        public float ImportedLegForceLimit(int index) { return _importedLegForceLimit[index]; }

        public ArticulationBody LegBody(int index) { return _legJoints[index]; }

        public ArticulationBody WheelBody(int index) { return _wheels[index]; }

        public float LegLowerLimitRad(int index)
        {
            return _legLowerRad[index];
        }

        public float LegUpperLimitRad(int index)
        {
            return _legUpperRad[index];
        }

        public void SetLegTargetsRad(float[] targetsRad)
        {
            // jointVelocity throws on a torn-down articulation, and the derating reads it.
            if (!_ready || !IsArticulationAlive()) return;
            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                float target = Mathf.Clamp(targetsRad[i], _legLowerRad[i], _legUpperRad[i]);
                ArticulationBody body = _legJoints[i];
                ArticulationDrive drive = body.xDrive;
                drive.target = target * Mathf.Rad2Deg;
                // The torque ceiling is re-derated here, at the control rate, because this is
                // the one place that owns xDrive; a second writer would race with the gain
                // verification in FixedUpdate.
                int type = i % RobotLayout.JointsPerLeg;
                drive.forceLimit = _driveConfig.DeratedLimit(
                    _driveConfig.LegForceLimits[type],
                    _driveConfig.LegNoLoadSpeed[type],
                    body.jointVelocity[0]);
                body.xDrive = drive;
            }
        }

        public void SetWheelTargetVelocitiesRad(float[] radPerSec)
        {
            if (!_ready || !IsArticulationAlive()) return;
            for (int i = 0; i < RobotLayout.WheelCount; i++)
            {
                float target = Mathf.Clamp(radPerSec[i], -_maxWheelSpeedRad, _maxWheelSpeedRad);
                ArticulationBody wheel = _wheels[i];
                ArticulationDrive drive = wheel.xDrive;
                drive.targetVelocity = target * Mathf.Rad2Deg;
                drive.forceLimit = _driveConfig.DeratedLimit(
                    _driveConfig.WheelForceLimit,
                    _driveConfig.WheelNoLoadSpeed,
                    wheel.jointVelocity[0]);
                wheel.xDrive = drive;
            }
        }

        public void ReadState(RobotState state, float fixedDeltaTime)
        {
            // ML-Agents calls CollectObservations one last time from Agent.OnDisable during
            // teardown, by which point the articulation may already be destroyed and every
            // reduced-space accessor throws. The exception would escape before the logger is
            // flushed, losing the tail of the run.
            if (!_ready || !IsArticulationAlive()) return;

            ReadJointForces();
            SampleTorqueChannels();

            Transform baseTransform = _baseBody.transform;
            state.BasePosition = baseTransform.position;
            state.BaseRotation = baseTransform.rotation;
            state.BaseLinearVelocityWorld = _baseBody.linearVelocity;
            state.BaseAngularVelocityWorld = _baseBody.angularVelocity;

            Quaternion toBody = Quaternion.Inverse(state.BaseRotation);
            state.UpLocal = toBody * Vector3.up;
            state.LinearVelocityLocal = toBody * state.BaseLinearVelocityWorld;
            state.AngularVelocityLocal = toBody * state.BaseAngularVelocityWorld;

            Vector3 forward = state.BaseRotation * Vector3.forward;
            Vector3 right = state.BaseRotation * Vector3.right;
            Vector3 up = state.BaseRotation * Vector3.up;
            // Sign convention, fixed here once because reward and baselines depend on it:
            // pitch > 0 is nose up, roll > 0 is right side down. Pitch uses atan2 against
            // the horizontal projection so it stays defined when the robot points at the
            // sky; roll uses two axes so it covers a full turn instead of saturating.
            state.Pitch = WrapPi(Mathf.Atan2(forward.y,
                Mathf.Sqrt(forward.x * forward.x + forward.z * forward.z)));
            state.Roll = WrapPi(Mathf.Atan2(-right.y, up.y));

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                ArticulationBody body = _legJoints[i];
                state.LegAngleRad[i] = body.jointPosition[0];
                state.LegVelocityRad[i] = body.jointVelocity[0];
                state.LegTorqueNm[i] = body.driveForce[0];
            }

            for (int i = 0; i < RobotLayout.WheelCount; i++)
            {
                ArticulationBody wheel = _wheels[i];
                float spin = wheel.jointVelocity[0];
                state.WheelVelocityRad[i] = spin;
                state.WheelTorqueNm[i] = wheel.driveForce[0];

                // Rim speed the wheel would impose on the body if it were rolling without
                // slipping, signed along body forward. Derived from the joint axis rather
                // than a per-leg sign table, so a mirrored URDF axis stays correct.
                Vector3 axis = wheel.transform.rotation * wheel.anchorRotation * Vector3.right;
                state.WheelSurfaceSpeed[i] =
                    Vector3.Dot(Vector3.Cross(axis, Vector3.up), forward) * spin * WheelRadius;

                WheelContactSensor sensor = _wheelSensors[i];
                state.WheelContact[i] = sensor.InContact;
                state.WheelContactForce[i] = sensor.ContactForceOver(fixedDeltaTime);
                sensor.ClearAccumulator();
            }

            state.BaseContact = _baseSensor != null && _baseSensor.InContact;
            if (_baseSensor != null) _baseSensor.ClearAccumulator();

            if (_inverseTotalMass > 0f)
            {
                Vector3 momentArm = Vector3.zero;
                Vector3 momentum = Vector3.zero;
                for (int i = 0; i < _allBodies.Length; i++)
                {
                    ArticulationBody body = _allBodies[i];
                    float mass = _bodyMasses[i];
                    momentArm += body.worldCenterOfMass * mass;
                    momentum += body.linearVelocity * mass;
                }
                state.ComPositionWorld = momentArm * _inverseTotalMass;
                state.ComVelocityWorld = momentum * _inverseTotalMass;
            }
            else
            {
                state.ComPositionWorld = state.BasePosition;
                state.ComVelocityWorld = state.BaseLinearVelocityWorld;
            }
        }

        /// <summary>
        /// State reset, never a scene reload: at 32 areas a per-episode scene load costs
        /// the whole throughput budget (SERVER_OPS_1.md section 4-1).
        /// </summary>
        public void ResetTo(Vector3 position, Quaternion rotation, float[] legAnglesRad,
            float[] legVelocities, float[] wheelVelocities)
        {
            if (!_ready) return;

            _rootBody.TeleportRoot(position, rotation);
            _rootBody.linearVelocity = Vector3.zero;
            _rootBody.angularVelocity = Vector3.zero;

            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                float angle = Mathf.Clamp(legAnglesRad[i], _legLowerRad[i], _legUpperRad[i]);
                ArticulationBody body = _legJoints[i];
                body.jointPosition = new ArticulationReducedSpace(angle);
                body.jointVelocity = new ArticulationReducedSpace(legVelocities[i]);
                // Without this the drive would spend the first steps of the episode pulling
                // the pose back to the previous episode's target.
                ArticulationDrive drive = body.xDrive;
                drive.target = angle * Mathf.Rad2Deg;
                body.xDrive = drive;
            }

            for (int i = 0; i < RobotLayout.WheelCount; i++)
            {
                ArticulationBody wheel = _wheels[i];
                // A continuous joint's angle accumulates without bound over a long run;
                // it is not observed, so zeroing it costs nothing and keeps the reduced
                // coordinate finite.
                wheel.jointPosition = new ArticulationReducedSpace(0f);
                wheel.jointVelocity = new ArticulationReducedSpace(wheelVelocities[i]);
                ArticulationDrive drive = wheel.xDrive;
                drive.targetVelocity = wheelVelocities[i] * Mathf.Rad2Deg;
                wheel.xDrive = drive;

                _wheelSensors[i].ClearAccumulator();
            }

            if (_baseSensor != null) _baseSensor.ClearAccumulator();

            // TeleportRoot moves the articulation inside the solver, but the Transform the
            // observation reads is only republished on the next simulation step. Without this
            // the first ReadState of an episode sees the pre-teleport pose, which reads as a
            // robot lying at the world origin and terminates the episode before it starts.
            // Physics.SyncTransforms is the wrong direction here: it pushes Transform changes
            // into the engine, while PublishTransform reads the engine back into the Transform.
            for (int i = 0; i < _allBodies.Length; i++) _allBodies[i].PublishTransform();
        }

        /// <summary>
        /// Names any leg joint whose configured torque ceiling exceeds what the URDF says the
        /// actuator can produce. Silently over-torquing a joint makes a policy that only works
        /// in simulation, and that failure is invisible in the learning curve.
        /// </summary>
        void WarnOnForceLimitMismatch(DriveConfig drive, List<string> warnings)
        {
            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                if (_legJoints[i] == null) continue;
                float imported = _importedLegForceLimit[i];
                if (imported <= 0f) continue;
                float configured = drive.LegForceLimits[i % RobotLayout.JointsPerLeg];
                if (configured <= imported + 1e-3f) continue;
                warnings.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0}: configured force limit {1} Nm exceeds the URDF effort limit {2} Nm",
                    _legJoints[i].name, configured, imported));
            }
        }

        void ApplyDriveSettings(DriveConfig drive, List<string> warnings)
        {
            for (int i = 0; i < RobotLayout.LegJointCount; i++)
            {
                ArticulationBody body = _legJoints[i];
                if (body == null) continue;
                ArticulationDrive d = body.xDrive;
                d.stiffness = drive.LegStiffness;
                d.damping = drive.LegDamping;
                // Per joint type, not one shared value: hip, thigh and calf are different
                // actuators and the URDF gives them different effort limits.
                d.forceLimit = drive.LegForceLimits[i % RobotLayout.JointsPerLeg];
                body.xDrive = d;
            }

            for (int i = 0; i < RobotLayout.WheelCount; i++)
            {
                ArticulationBody wheel = _wheels[i];
                if (wheel == null) continue;

                // SPEC.md section 3: the wheels are continuous joints. When the importer
                // drops that the wheel turns a few degrees and stops, which reads as a
                // failed policy instead of a failed import.
                if (wheel.twistLock != ArticulationDofLock.FreeMotion)
                {
                    warnings.Add(wheel.name + ": wheel joint was imported as " + wheel.twistLock
                        + " instead of continuous; forced to FreeMotion");
                    wheel.twistLock = ArticulationDofLock.FreeMotion;
                }

                ArticulationDrive d = wheel.xDrive;
                d.stiffness = drive.WheelStiffness;
                d.damping = drive.WheelDamping;
                d.forceLimit = drive.WheelForceLimit;
                d.targetVelocity = 0f;
                wheel.xDrive = d;
            }
        }

        void ReadLegLimits(int index, List<string> warnings)
        {
            ArticulationBody body = _legJoints[index];
            ArticulationDrive drive = body.xDrive;
            _importedLegForceLimit[index] = drive.forceLimit;
            if (body.twistLock == ArticulationDofLock.LimitedMotion && drive.upperLimit > drive.lowerLimit)
            {
                _legLowerRad[index] = drive.lowerLimit * Mathf.Deg2Rad;
                _legUpperRad[index] = drive.upperLimit * Mathf.Deg2Rad;
                return;
            }

            // The normalised joint observation and the action mapping both need a range.
            // A full revolution is the only fallback that does not invent a number, and
            // the warning names the joint whose import has to be fixed.
            _legLowerRad[index] = -Mathf.PI;
            _legUpperRad[index] = Mathf.PI;
            warnings.Add(string.Format(CultureInfo.InvariantCulture,
                "{0}: leg joint has no usable xDrive limit pair ({1}, {2}..{3} deg); "
                + "assuming a full revolution", body.name, body.twistLock,
                drive.lowerLimit, drive.upperLimit));
        }

        static bool IsDrivenRevolute(ArticulationBody body, List<string> missing)
        {
            if (body.jointType == ArticulationJointType.RevoluteJoint && body.dofCount >= 1) return true;
            missing.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} <not a revolute joint: {1}, {2} dof>", body.name, body.jointType, body.dofCount));
            return false;
        }

        static WheelContactSensor EnsureSensor(GameObject link)
        {
            WheelContactSensor sensor = link.GetComponent<WheelContactSensor>();
            return sensor != null ? sensor : link.AddComponent<WheelContactSensor>();
        }

        static ArticulationBody Resolve(Dictionary<string, ArticulationBody> byName,
            string linkName, List<string> missing)
        {
            ArticulationBody body;
            if (byName.TryGetValue(linkName, out body)) return body;
            missing.Add(linkName);
            return null;
        }

        /// <summary>
        /// isRoot is only meaningful once the body has joined the solver, so the hierarchy
        /// is the reliable test while the area is still being built.
        /// </summary>
        static ArticulationBody FindArticulationRoot(ArticulationBody[] bodies)
        {
            for (int i = 0; i < bodies.Length; i++)
            {
                Transform parent = bodies[i].transform.parent;
                if (parent == null || parent.GetComponentInParent<ArticulationBody>() == null)
                    return bodies[i];
            }
            return null;
        }

        /// <summary>
        /// Radius from the imported collider rather than a constant, so editing the URDF
        /// cannot silently desynchronise the slip term from the geometry. The wheel is a
        /// disc whose axis is horizontal in any upright pose, so the largest half extent
        /// of its world bounds is the radius.
        /// </summary>
        static float MeasureRadius(ArticulationBody wheel)
        {
            Collider[] colliders = wheel.GetComponentsInChildren<Collider>(true);
            float radius = 0f;
            for (int i = 0; i < colliders.Length; i++)
            {
                Vector3 extents = colliders[i].bounds.extents;
                float largest = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
                if (largest > radius) radius = largest;
            }
            return radius;
        }

        static float WrapPi(float radians)
        {
            return Mathf.Repeat(radians + Mathf.PI, 2f * Mathf.PI) - Mathf.PI;
        }
    }
}
