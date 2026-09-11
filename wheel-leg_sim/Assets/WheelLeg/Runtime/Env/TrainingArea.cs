using UnityEngine;
using WheelLeg.Agent;
using WheelLeg.Config;
using WheelLeg.Core;
using WheelLeg.Logging;
using WheelLeg.Robot;

namespace WheelLeg.Env
{
    /// <summary>
    /// One independent training area: its own terrain, its own robot, its own random stream
    /// and its own log buffer. Thirty-two of these run in one process, so every piece of
    /// state here is an instance field; nothing is static and nothing is looked up globally.
    /// </summary>
    public sealed class TrainingArea : MonoBehaviour
    {
        SimConfig _config;
        int _areaId;
        DeterministicRandom _rng;
        ITerrainSeedSource _seedSource;

        TerrainSurface _terrain;
        RobotDriver _driver;
        WheelLegAgent _agent;
        FrameLogger _logger;

        int _terrainSeed;
        float _difficulty;
        bool _exhausted;

        public int AreaId { get { return _areaId; } }
        public SimConfig Config { get { return _config; } }
        public DeterministicRandom Rng { get { return _rng; } }
        public TerrainSurface Terrain { get { return _terrain; } }
        public RobotDriver Driver { get { return _driver; } }
        public FrameLogger Logger { get { return _logger; } }
        public int TerrainSeed { get { return _terrainSeed; } }
        public float Difficulty { get { return _difficulty; } }

        /// <summary>True once the seed source has no episodes left; the run can then end.</summary>
        public bool Exhausted { get { return _exhausted; } }

        /// <summary>
        /// Wires the area up. Called by the bootstrap before the agent is enabled, so the
        /// agent never runs a step against a half-built area.
        /// </summary>
        public void Configure(
            SimConfig config,
            int areaId,
            ITerrainSeedSource seedSource,
            TerrainSurface terrain,
            RobotDriver driver,
            WheelLegAgent agent,
            FrameLogger logger)
        {
            _config = config;
            _areaId = areaId;
            _seedSource = seedSource;
            _terrain = terrain;
            _driver = driver;
            _agent = agent;
            _logger = logger;

            // Derived from the one top-level seed so the whole process reproduces from it.
            _rng = new DeterministicRandom(DeterministicRandom.DeriveSeed(config.Run.Seed, "area", areaId));
        }

        /// <summary>
        /// Rebuilds the terrain for a new episode and returns the pose to spawn the robot at.
        /// Returns false when the seed source is exhausted, which ends an evaluation run.
        /// </summary>
        public bool BeginEpisode(out Vector3 spawnPosition, out Quaternion spawnRotation)
        {
            spawnPosition = transform.position;
            spawnRotation = Quaternion.identity;

            int seed;
            if (!_seedSource.TryNext(_areaId, out seed))
            {
                _exhausted = true;
                return false;
            }

            _terrainSeed = seed;
            // Difficulty is drawn from the area stream, not the terrain stream, so that
            // collapsing the difficulty range to [0, 0] leaves the terrain seed sequence
            // unchanged and the two knobs stay independent.
            _difficulty = _config.Terrain.Difficulty.Sample(_rng);
            _terrain.Build(_config.Terrain, _difficulty, _terrainSeed);

            // The robot has to carry the same friction the ground was just built with, or the
            // contact gets the average of it and PhysX's 0.6 default instead of the configured
            // value. This episode's terrain draw is the one that counts, so it is pushed here
            // rather than once at setup.
            _driver.SetFriction(_terrain.Friction);

            RobotConfig robot = _config.Robot;
            Vector3 local = new Vector3(0f, 0f, 0f);
            Vector3 world = transform.TransformPoint(local);
            float ground = _terrain.SurfaceHeightAt(world);
            float height = ground + robot.SpawnHeight + robot.Reset.BaseHeightOffset.Sample(_rng);

            spawnPosition = new Vector3(world.x, height, world.z);
            spawnRotation = Quaternion.Euler(
                robot.Reset.BaseRollPitchDeg.Sample(_rng),
                robot.Reset.BaseYawDeg.Sample(_rng),
                robot.Reset.BaseRollPitchDeg.Sample(_rng));
            return true;
        }

        public float GroundHeightUnder(Vector3 worldPoint)
        {
            return _terrain.SurfaceHeightAt(worldPoint);
        }
    }
}
