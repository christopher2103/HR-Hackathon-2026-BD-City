using System.Collections;
using UnityEngine;
using UnityEngine.AI;

namespace BubbleFeature.Bots
{
    /// <summary>
    /// A placeholder bot standing in for another visitor: walks to random points
    /// around its home spot, pauses a moment, and repeats.
    /// Two ways of walking:
    ///  - NavMesh (test scene with a baked/built NavMesh);
    ///  - Simple (streamed city, no NavMesh): walks in a straight line, follows the ground with a
    ///    raycast, and turns around at walls, big drops (quay edges) and the edge of its area.
    /// Its name is used by the "Send gift" menu.
    /// </summary>
    public class BotWanderer : MonoBehaviour
    {
        [SerializeField] private string displayName = "Bot";
        [SerializeField] private float wanderRadius = 6f;           // metres around its home spot
        [SerializeField] private Vector2 pauseSeconds = new Vector2(1f, 4f);

        [Header("Simple walking (no NavMesh)")]
        [SerializeField] private bool useNavMesh = true;
        [SerializeField] private float walkSpeed = 1.1f;
        [SerializeField] private float turnSpeed = 240f;            // degrees per second
        [SerializeField] private float lookAhead = 0.8f;            // metres: wall check distance
        [SerializeField] private float maxStep = 0.4f;              // kerbs are fine, quay edges are not

        private NavMeshAgent agent;
        private Vector3 home;

        public string DisplayName => displayName;
        public Color Color { get; private set; } = Color.white;  // used by the gift menu buttons
        public void SetColor(Color color) => Color = color;
        public void SetName(string newName) { displayName = newName; name = $"Bot ({newName})"; }

        /// <summary>Call right after spawning (before Start). Simple mode switches the NavMeshAgent off.</summary>
        public void SetWalkMode(bool navMesh, float radius)
        {
            useNavMesh = navMesh;
            wanderRadius = radius;
        }

        private void Awake() => agent = GetComponent<NavMeshAgent>();

        private void Start() => StartCoroutine(Run());

        /// <summary>
        /// Puts the bot somewhere else (e.g. near the player, out of sight) and starts wandering around there.
        /// </summary>
        public void Relocate(Vector3 position)
        {
            StopAllCoroutines();
            if (useNavMesh && agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(position);
            else transform.position = position;
            transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            home = transform.position;

            if (!useNavMesh || agent == null)
            {
                if (agent != null) agent.enabled = false;
                yield return SimpleWander();
                yield break;
            }

            yield return null; // let the NavMesh finish building
            while (true)
            {
                if (agent.isOnNavMesh && TryRandomPoint(out Vector3 point))
                {
                    agent.SetDestination(point);
                    // Walk until arrived (or stuck for too long).
                    float timeout = 20f;
                    while (timeout > 0f && (agent.pathPending || agent.remainingDistance > agent.stoppingDistance + 0.1f))
                    {
                        timeout -= Time.deltaTime;
                        yield return null;
                    }
                }
                yield return new WaitForSeconds(Random.Range(pauseSeconds.x, pauseSeconds.y));
            }
        }

        // ---------- Simple walking ----------

        private IEnumerator SimpleWander()
        {
            while (true)
            {
                Vector2 offset = Random.insideUnitCircle * wanderRadius;
                Vector3 goal = home + new Vector3(offset.x, 0f, offset.y);

                float timeout = 15f;
                while (timeout > 0f)
                {
                    Settle();
                    Vector3 toGoal = goal - transform.position;
                    toGoal.y = 0f;
                    if (toGoal.magnitude < 0.3f) break;

                    // Turn towards the goal.
                    Quaternion want = Quaternion.LookRotation(toGoal.normalized, Vector3.up);
                    transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * Time.deltaTime);

                    // Only walk when (roughly) facing it, and when the way ahead is clear.
                    if (Quaternion.Angle(transform.rotation, want) < 30f)
                    {
                        Vector3 next = transform.position + transform.forward * walkSpeed * Time.deltaTime;
                        if (!Blocked() && GroundBelow(next, out float groundY))
                            transform.position = new Vector3(next.x, groundY, next.z);
                        else
                            break; // wall, drop or no ground: pick another goal
                    }

                    timeout -= Time.deltaTime;
                    yield return null;
                }
                float pause = Random.Range(pauseSeconds.x, pauseSeconds.y);
                while (pause > 0f) { Settle(); pause -= Time.deltaTime; yield return null; }
            }
        }

        // Streamed ground can get more detailed (and higher/lower) after we spawned: stand on what is there now.
        private void Settle()
        {
            Vector3 from = transform.position + Vector3.up * 4f;
            float best = float.MinValue;
            foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.transform.IsChildOf(transform)) continue;
                best = Mathf.Max(best, hit.point.y);
            }
            if (best != float.MinValue && Mathf.Abs(best - transform.position.y) > 0.02f)
                transform.position = new Vector3(transform.position.x, best, transform.position.z);
        }

        // Anything at knee or chest height in front of us (buildings, the player)?
        private bool Blocked()
        {
            foreach (float h in new[] { 0.5f, 1.3f })
            {
                Vector3 from = transform.position + Vector3.up * h;
                foreach (RaycastHit hit in Physics.RaycastAll(from, transform.forward, lookAhead, ~0, QueryTriggerInteraction.Ignore))
                    if (!hit.transform.IsChildOf(transform)) return true;
            }
            return false;
        }

        // Ground under a point, within one step up or down of where we stand now.
        private bool GroundBelow(Vector3 p, out float groundY)
        {
            groundY = 0f;
            float best = float.MinValue;
            Vector3 from = new Vector3(p.x, transform.position.y + maxStep + 0.5f, p.z);
            foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, maxStep * 2f + 0.5f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.transform.IsChildOf(transform)) continue;
                if (Mathf.Abs(hit.point.y - transform.position.y) > maxStep) continue;
                best = Mathf.Max(best, hit.point.y);
            }
            if (best == float.MinValue) return false;
            groundY = best;
            return true;
        }

        // ---------- NavMesh ----------

        private bool TryRandomPoint(out Vector3 point)
        {
            for (int i = 0; i < 10; i++)
            {
                Vector2 offset = Random.insideUnitCircle * wanderRadius;
                Vector3 candidate = home + new Vector3(offset.x, 0f, offset.y);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 1.5f, NavMesh.AllAreas))
                {
                    point = hit.position;
                    return true;
                }
            }
            point = transform.position;
            return false;
        }
    }
}
