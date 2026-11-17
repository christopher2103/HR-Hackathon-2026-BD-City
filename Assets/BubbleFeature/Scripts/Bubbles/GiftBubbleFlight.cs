using UnityEngine;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Moves a gift bubble: first it flies to a point about 1 m in front of the player,
    /// then it slowly circles around the player (and follows them when they walk).
    /// Pure math in Update: no physics, cheap on Quest.
    /// </summary>
    public class GiftBubbleFlight : MonoBehaviour
    {
        public enum FlightState { Idle, FlyingIn, Circling }

        [Header("Target")]
        [Tooltip("Who the bubble flies to. Leave empty to use Camera.main (the headset).")]
        [SerializeField] private Transform target;

        [Header("Fly in")]
        [SerializeField] private float flySpeed = 2f;              // metres per second at full speed
        [SerializeField] private float slowDownDistance = 1.5f;    // start braking this close to the goal
        [SerializeField] private float minSpeed = 0.2f;            // so it always arrives

        [Header("Circle")]
        [SerializeField] private float circleRadius = 1f;          // metres from your head
        [SerializeField] private float heightBelowEyes = 0.2f;     // a bit lower than your eyes
        [SerializeField] private float secondsPerLap = 15f;        // slow full circle
        [SerializeField] private float followSmoothing = 5f;       // how quickly it catches up when you walk

        private float angle; // degrees around the player, 0 = world forward (+Z)

        public FlightState State { get; private set; } = FlightState.Idle;

        /// <summary>Start flying toward the target (or the headset if none is given).</summary>
        public void StartFlight(Transform newTarget = null)
        {
            if (newTarget != null) target = newTarget;
            if (target == null && Camera.main != null) target = Camera.main.transform;

            if (target == null)
            {
                Debug.LogWarning("[Bubble] No target to fly to (no Camera.main found).", this);
                State = FlightState.Idle;
                return;
            }
            State = FlightState.FlyingIn;
        }

        /// <summary>Stop moving (used when the bubble pops).</summary>
        public void Stop() => State = FlightState.Idle;

        private void Update()
        {
            if (target == null) return;

            switch (State)
            {
                case FlightState.FlyingIn: FlyIn();  break;
                case FlightState.Circling: Circle(); break;
            }
        }

        // Centre of the circle: the player's head, a little lower.
        private Vector3 Center => target.position + Vector3.down * heightBelowEyes;

        private void FlyIn()
        {
            // Goal = 1 m in front of where the player is looking (ignoring up/down tilt).
            Vector3 forward = target.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            Vector3 goal = Center + forward.normalized * circleRadius;

            float distance = Vector3.Distance(transform.position, goal);

            // Full speed far away, slowing down smoothly near the goal.
            float speed = Mathf.Max(flySpeed * Mathf.Clamp01(distance / slowDownDistance), minSpeed);
            transform.position = Vector3.MoveTowards(transform.position, goal, speed * Time.deltaTime);

            if (distance < 0.05f)
            {
                // Start circling from the current spot so there is no jump.
                Vector3 offset = transform.position - Center;
                angle = Mathf.Atan2(offset.x, offset.z) * Mathf.Rad2Deg;
                State = FlightState.Circling;
                Debug.Log("[Bubble] Arrived, now circling.", this);
            }
        }

        private void Circle()
        {
            angle += 360f / secondsPerLap * Time.deltaTime;

            Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * circleRadius;
            Vector3 goal = Center + offset;

            // Smooth follow: walking or head bobbing doesn't make the bubble jump.
            float t = 1f - Mathf.Exp(-followSmoothing * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, goal, t);
        }
    }
}
