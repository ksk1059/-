using WheelLeg.Config;
using WheelLeg.Core;

namespace WheelLeg.Control
{
    /// <summary>
    /// A controller consumes exactly the observation the policy consumes and produces
    /// exactly the action the policy produces. Baselines implement this so they can be
    /// run on the same terrain through the same logger, selected by config alone
    /// (SPEC.md section 8). No controller may read privileged state.
    /// </summary>
    public interface IController
    {
        string Name { get; }

        void Initialize(SimConfig config, DeterministicRandom rng);

        /// <summary>Called once per episode, before the first action.</summary>
        void OnEpisodeBegin();

        /// <summary>
        /// Writes <see cref="Robot.RobotLayout.ActionSize"/> values in [-1, 1] into
        /// <paramref name="actionOut"/>.
        /// </summary>
        /// <param name="observation">
        /// The <see cref="Robot.RobotLayout.ObservationSize"/> vector, already noised,
        /// delayed and quantised exactly as the policy receives it.
        /// </param>
        /// <param name="dt">Seconds since the previous decision.</param>
        void ComputeAction(float[] observation, float dt, float[] actionOut);
    }

    /// <summary>
    /// Offsets into the observation vector, so controllers read named quantities
    /// instead of magic indices. Layout is defined by RobotLayout.ObservationSize.
    /// </summary>
    public static class ObservationLayout
    {
        public const int Up = 0;                 // 3
        public const int LinearVelocity = 3;     // 3
        public const int AngularVelocity = 6;    // 3
        public const int LegAngle = 9;           // 12, normalised to [-1, 1] over joint limits
        public const int LegVelocity = 21;       // 12
        public const int WheelVelocity = 33;     // 4
        public const int WheelContact = 37;      // 4
        public const int Command = 41;           // 3: vx, vy, wz
        public const int PreviousAction = 44;    // 16
        public const int End = 60;
    }
}
