using UnityEngine;

namespace WheelLeg.Robot
{
    /// <summary>
    /// Contact accumulator for one link. Unity delivers collision messages to the
    /// GameObject that owns the ArticulationBody, so one of these on a wheel link sees
    /// every collider imported under that link without any wiring.
    /// All state is per instance: 32 areas run in one process and a shared counter would
    /// report a neighbour's ground contacts as this robot's.
    /// </summary>
    public sealed class WheelContactSensor : MonoBehaviour
    {
        Transform _articulationRoot;
        Vector3 _impulse;
        int _touchingColliders;
        int _contactEvents;
        int _stepsAccumulated;
        // Double, not float: a 12-hour run reaches fixedTime values where a float's ulp
        // exceeds the 0.005 s timestep, two consecutive steps then compare equal and the
        // step count silently stops advancing.
        double _lastStepTime = -1.0;

        /// <summary>True if this link touched anything outside the robot since the last clear.</summary>
        public bool InContact
        {
            get { return _contactEvents > 0 || _touchingColliders > 0; }
        }

        public Vector3 ContactForce
        {
            get { return ContactForceOver(Time.fixedDeltaTime); }
        }

        /// <summary>
        /// The accumulated impulse as a mean force. The caller passes its own timestep
        /// because Time.fixedDeltaTime is process-global while the configured physics
        /// timestep is what every logged quantity is defined against.
        /// </summary>
        public Vector3 ContactForceOver(float fixedDeltaTime)
        {
            if (_stepsAccumulated < 1 || fixedDeltaTime <= 0f) return Vector3.zero;
            return _impulse / (fixedDeltaTime * _stepsAccumulated);
        }

        public void ClearAccumulator()
        {
            _impulse = Vector3.zero;
            _touchingColliders = 0;
            _contactEvents = 0;
            _stepsAccumulated = 0;
            _lastStepTime = -1.0;
        }

        void Awake()
        {
            ResolveArticulationRoot();
        }

        void OnCollisionEnter(Collision collision)
        {
            if (IsSelfContact(collision)) return;
            _touchingColliders++;
            Accumulate(collision);
        }

        void OnCollisionStay(Collision collision)
        {
            if (IsSelfContact(collision)) return;
            Accumulate(collision);
        }

        void OnCollisionExit(Collision collision)
        {
            if (IsSelfContact(collision)) return;
            if (_touchingColliders > 0) _touchingColliders--;
        }

        /// <summary>
        /// What this link last counted as an external contact. Diagnostic only. If a link of
        /// this same robot ever appears here then self-collision is not being filtered, and
        /// every contact flag and the base_contact termination are contaminated.
        /// </summary>
        public string LastExternalContact { get; private set; }

        void Accumulate(Collision collision)
        {
            _contactEvents++;
            _impulse += collision.impulse;
            ArticulationBody other = collision.articulationBody;
            LastExternalContact =
                (collision.collider != null ? collision.collider.name : "<null collider>")
                + " [body=" + (other != null ? other.name : "null")
                + " rigidbody=" + (collision.rigidbody != null ? collision.rigidbody.name : "null") + "]";
            // Several colliders of the same link report inside one physics step, so the
            // divisor is elapsed time, not the number of events.
            if (_lastStepTime != Time.fixedTimeAsDouble)
            {
                _lastStepTime = Time.fixedTimeAsDouble;
                _stepsAccumulated++;
            }
        }

        /// <summary>
        /// A leg touching its own body is not ground contact; counting it would make the
        /// contact flag true while the robot is in the air.
        /// </summary>
        bool IsSelfContact(Collision collision)
        {
            ArticulationBody other = collision.articulationBody;
            if (other == null || _articulationRoot == null) return false;
            return other.transform.IsChildOf(_articulationRoot);
        }

        void ResolveArticulationRoot()
        {
            ArticulationBody body = GetComponentInParent<ArticulationBody>();
            while (body != null)
            {
                _articulationRoot = body.transform;
                Transform parent = body.transform.parent;
                body = parent != null ? parent.GetComponentInParent<ArticulationBody>() : null;
            }
        }
    }
}
