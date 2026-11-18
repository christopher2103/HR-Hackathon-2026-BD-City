using System.Collections;
using System.Collections.Generic;
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
        // Smooth steering instead of "turn on the spot, walk straight, stop at a wall":
        //  - eases in/out of walking and turns while walking (no snapping),
        //  - looks ahead with a body-wide sphere cast and steers around benches, bikes, trees, lamps and stands,
        //  - keeps its distance from other bots, Lotte and the player,
        //  - only stands on the street (CityPart ground), never on top of a lamp, tree or prop,
        //  - stays on its own level (no wandering down quay ramps into the harbour).

        private static readonly List<BotWanderer> all = new List<BotWanderer>();
        private static readonly List<Transform> others = new List<Transform>();   // Lotte etc.

        /// <summary>Something bots should walk around (e.g. the tutorial NPC). Unregister when it is destroyed.</summary>
        public static void RegisterObstacle(Transform t) { if (t != null && !others.Contains(t)) others.Add(t); }
        public static void UnregisterObstacle(Transform t) => others.Remove(t);

        [Header("Smooth steering")]
        [SerializeField] private float acceleration = 1.6f;          // m/s per second
        [SerializeField] private float bodyRadius = 0.28f;
        [SerializeField] private float personalSpace = 1.1f;         // keep this far from other people
        [SerializeField] private float levelTolerance = 0.6f;        // stay within this height of the home spot

        private float speedNow;
        private Vector3 heading;
        private float groundVelocity;

        private void OnEnable() { if (!all.Contains(this)) all.Add(this); }
        private void OnDisable() => all.Remove(this);

        private IEnumerator SimpleWander()
        {
            heading = Flat(transform.forward);
            float y;
            if (GroundAt(transform.position, transform.position.y, 3f, out y)) transform.position = new Vector3(transform.position.x, y, transform.position.z);
            home = transform.position;
            while (true)
            {
                Vector3 goal = PickGoal();
                float timeout = 18f, stuck = 0f;
                while (timeout > 0f)
                {
                    Vector3 toGoal = goal - transform.position; toGoal.y = 0f;
                    float dist = toGoal.magnitude;
                    if (dist < 0.35f) break;

                    Vector3 want = Steer(toGoal / Mathf.Max(dist, 0.001f), out bool blocked);
                    if (blocked) { stuck += Time.deltaTime; if (stuck > 0.6f) break; } else stuck = 0f;

                    // Turn smoothly; slow down for sharp turns and near the goal.
                    heading = Vector3.RotateTowards(heading, want, turnSpeed * Mathf.Deg2Rad * Time.deltaTime, 0f);
                    float align = Mathf.Clamp01(Vector3.Dot(heading, want));
                    float target = blocked ? 0f : walkSpeed * Mathf.Lerp(0.25f, 1f, align) * Mathf.Clamp01(dist / 1.2f + 0.35f);
                    speedNow = Mathf.MoveTowards(speedNow, target, acceleration * Time.deltaTime);
                    Move(heading * speedNow * Time.deltaTime);
                    transform.rotation = Quaternion.LookRotation(heading, Vector3.up);

                    timeout -= Time.deltaTime;
                    yield return null;
                }
                // Ease out, then pause.
                while (speedNow > 0.02f)
                {
                    speedNow = Mathf.MoveTowards(speedNow, 0f, acceleration * 1.5f * Time.deltaTime);
                    Move(heading * speedNow * Time.deltaTime);
                    yield return null;
                }
                speedNow = 0f;
                float pause = Random.Range(pauseSeconds.x, pauseSeconds.y);
                while (pause > 0f) { Move(Vector3.zero); pause -= Time.deltaTime; yield return null; }
            }
        }

        // Moves horizontally if there is street there, and eases the height to the ground (no popping).
        private void Move(Vector3 step)
        {
            Vector3 p = transform.position + step + Separation();
            if (!GroundAt(p, transform.position.y, maxStep, out _)) p = transform.position;   // no street there: stay
            if (GroundAt(p, transform.position.y, maxStep + 0.2f, out float g))
                p.y = Mathf.SmoothDamp(transform.position.y, g, ref groundVelocity, 0.08f);
            transform.position = p;
        }

        // Hard minimum distance to other people (steering alone can still let two bots brush shoulders).
        private Vector3 Separation()
        {
            const float minGap = 0.75f;
            Vector3 push = Vector3.zero;
            void From(Vector3 o)
            {
                Vector3 d = transform.position - o; d.y = 0f;
                float m = d.magnitude;
                if (m < minGap && m > 0.001f) push += d / m * (minGap - m);
            }
            foreach (var b in all) if (b != this && b != null) From(b.transform.position);
            foreach (var t in others) if (t != null) From(t.position);
            return Vector3.ClampMagnitude(push * 0.5f, walkSpeed * Time.deltaTime * 1.5f);
        }

        // Desired direction: towards the goal, around things in the way, away from people that are too close.
        private Vector3 Steer(Vector3 toGoal, out bool blocked)
        {
            Vector3 dir = toGoal;
            // Personal space.
            Vector3 push = Vector3.zero;
            foreach (var b in all) if (b != this && b != null) push += Away(b.transform.position);
            foreach (var t in others) if (t != null) push += Away(t.position);
            if (Camera.main != null) push += Away(Camera.main.transform.position) * 1.5f;
            dir = Flat(dir + push);

            // Obstacles: try the wanted direction, then fan out left/right.
            float look = lookAhead + speedNow * 0.6f;
            foreach (float a in new[] { 0f, 25f, -25f, 50f, -50f, 80f, -80f })
            {
                Vector3 d = Quaternion.Euler(0f, a, 0f) * dir;
                if (Clear(d, look)) { blocked = false; return a == 0f ? dir : Vector3.Slerp(dir, d, 0.85f).normalized; }
            }
            blocked = true;
            return dir;
        }

        private Vector3 Away(Vector3 other)
        {
            Vector3 d = transform.position - other; d.y = 0f;
            float m = d.magnitude;
            if (m > personalSpace || m < 0.001f) return Vector3.zero;
            return d / m * (personalSpace - m) / personalSpace * 1.8f;
        }

        // Body-wide check at knee and chest height, plus: is there street (on our level) ahead?
        private bool Clear(Vector3 dir, float distance)
        {
            foreach (float h in new[] { 0.45f, 1.25f })
            {
                Vector3 from = transform.position + Vector3.up * h - dir * 0.05f;
                foreach (RaycastHit hit in Physics.SphereCastAll(from, bodyRadius, dir, distance, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.transform.IsChildOf(transform)) continue;
                    if (hit.distance <= 0f && hit.point == Vector3.zero) continue;   // started inside: ignore
                    var part = hit.collider.GetComponentInParent<CityFeature.CityPart>();
                    if (part != null && part.isGround && hit.normal.y > 0.7f) continue;      // walkable floor
                    return false;
                }
            }
            Vector3 ahead = transform.position + dir * Mathf.Min(distance, 1.2f);
            return GroundAt(ahead, transform.position.y, maxStep, out _);
        }

        // A random goal around home that is on the street, on our level, and reachable in a straight line.
        private Vector3 PickGoal()
        {
            for (int i = 0; i < 10; i++)
            {
                Vector2 o = Random.insideUnitCircle * wanderRadius;
                Vector3 g = home + new Vector3(o.x, 0f, o.y);
                if (!GroundAt(g, home.y, levelTolerance, out float y)) continue;
                g.y = y;
                Vector3 d = g - transform.position; d.y = 0f;
                if (d.magnitude < 1.5f) continue;
                if (Physics.SphereCast(transform.position + Vector3.up * 0.9f, bodyRadius, d.normalized, out RaycastHit hit, d.magnitude, ~0, QueryTriggerInteraction.Ignore)
                    && !hit.transform.IsChildOf(transform)) continue;
                return g;
            }
            return transform.position - heading * 2f;   // turn around
        }

        // Street height at p within 'tolerance' of 'level'. Only real ground counts (not lamps, trees, props, stands).
        private static bool GroundAt(Vector3 p, float level, float tolerance, out float groundY)
        {
            groundY = 0f;
            float best = float.MinValue;
            foreach (RaycastHit hit in Physics.RaycastAll(new Vector3(p.x, level + 3f, p.z), Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = hit.collider.GetComponentInParent<CityFeature.CityPart>();
                bool ground = part != null ? part.isGround : hit.normal.y > 0.8f && !(hit.collider is CapsuleCollider) && hit.collider.gameObject.layer != 2;
                if (!ground) continue;
                if (Mathf.Abs(hit.point.y - level) > tolerance) continue;
                best = Mathf.Max(best, hit.point.y);
            }
            if (best == float.MinValue) return false;
            groundY = best;
            return true;
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward; }

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
