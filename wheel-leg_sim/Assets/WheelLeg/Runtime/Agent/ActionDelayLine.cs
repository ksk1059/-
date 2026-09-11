using WheelLeg.Robot;

namespace WheelLeg.Agent
{
    /// <summary>
    /// Holds each action for a fixed number of policy steps before it reaches the drives.
    ///
    /// The observation side of the loop already models latency
    /// (<c>observation.delay_steps</c>); this is the command side. On the real robot a chosen
    /// action does not arrive at the motor controller instantly, and the two delays are not
    /// the same number: one is sensing, the other is actuation. A policy trained with neither
    /// learns timing the hardware cannot keep.
    ///
    /// Always compiled in and always run, like the sensor model, so its first execution is not
    /// on the server where nobody is watching. <c>robot.action_delay_steps: 0</c> makes it an
    /// identity pass-through, which is what it ships as: the URDF carries no latency figure and
    /// inventing one would add an unfounded value rather than remove one (SPEC section 11).
    /// Measure the real control loop, then set the number.
    /// </summary>
    public sealed class ActionDelayLine
    {
        readonly float[][] _slots;
        int _cursor;
        int _warmupRemaining;

        public ActionDelayLine(int delaySteps)
        {
            if (delaySteps < 0) delaySteps = 0;
            // One extra slot so a delay of N can be served while step N is being written.
            _slots = new float[delaySteps + 1][];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = new float[RobotLayout.ActionSize];
        }

        /// <summary>Policy steps of delay this line applies. 0 means pass-through.</summary>
        public int DelaySteps { get { return _slots.Length - 1; } }

        /// <summary>
        /// Clears the pipeline. Carrying the previous episode's last commands into the first
        /// steps of a new one would make the reset pose depend on how the last episode ended.
        /// </summary>
        public void OnEpisodeBegin()
        {
            _cursor = 0;
            _warmupRemaining = _slots.Length - 1;
            for (int i = 0; i < _slots.Length; i++)
                System.Array.Clear(_slots[i], 0, _slots[i].Length);
        }

        /// <summary>
        /// Takes the action the policy just chose and returns the one that should reach the
        /// drives now. During the first <c>delay_steps</c> of an episode the pipeline is not
        /// full yet, so it serves the oldest action it has rather than a zero vector, which
        /// would command the stand pose regardless of what the policy asked for.
        /// </summary>
        public void Push(float[] chosen, float[] applied)
        {
            System.Array.Copy(chosen, _slots[_cursor], chosen.Length);

            int readIndex = _warmupRemaining > 0
                ? _cursor
                : (_cursor + 1) % _slots.Length;
            if (_warmupRemaining > 0) _warmupRemaining--;

            System.Array.Copy(_slots[readIndex], applied, applied.Length);
            _cursor = (_cursor + 1) % _slots.Length;
        }
    }
}
