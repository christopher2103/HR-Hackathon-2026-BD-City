using System.Collections;
using UnityEngine;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Keeps a few ambient bubbles flying around the player to show that others are getting gifts.
    /// Each bubble flies to a random point (or to a random entry in Targets, e.g. bots),
    /// pops there, and a new one starts somewhere else after a short delay.
    /// Place this object on the floor (its Y is used as ground height), or set Floor Reference
    /// (e.g. the XR Origin) when the ground height changes, like in the city.
    /// </summary>
    public class AmbientBubbleSpawner : MonoBehaviour
    {
        [Header("What")]
        [SerializeField] private AmbientBubbleMover bubblePrefab;
        [SerializeField] private int count = 6;
        [Tooltip("Off in the city: Restart() is called when you arrive in a neighbourhood.")]
        [SerializeField] private bool spawnOnStart = true;

        [Header("Area (around the player)")]
        [Tooltip("Leave empty to use Camera.main (the headset).")]
        [SerializeField] private Transform player;
        [Tooltip("Its Y is the floor height (e.g. the XR Origin). Empty = this object's Y.")]
        [SerializeField] private Transform floorReference;
        [SerializeField] private float areaRadius = 15f;
        [SerializeField] private float minDistanceFromPlayer = 3f;  // never too close to your head
        [SerializeField] private float minTravelDistance = 5f;      // so each bubble flies a bit
        [SerializeField] private Vector2 spawnHeight = new Vector2(2.5f, 5f);   // metres above the floor
        [SerializeField] private Vector2 targetHeight = new Vector2(1.2f, 2.2f);

        [Header("Out of sight")]
        [Tooltip("New bubbles start where the player can't see them (out of view or behind a building), so they never pop into existence.")]
        [SerializeField] private bool spawnOutOfView = true;
        [Tooltip("Bubbles further away than this are removed (when out of sight) and start again near the player.")]
        [SerializeField] private float maxDistanceFromPlayer = 25f;
        [SerializeField] private float checkInterval = 0.5f;

        [Header("Targets (optional, e.g. bots)")]
        [Tooltip("If filled, bubbles fly to these instead of random points.")]
        [SerializeField] private Transform[] targets;
        [SerializeField] private float targetHeadHeight = 1.7f;    // aim at the bot's head
        [Range(0f, 1f)]
        [Tooltip("Share of bubbles that fly to a target; the rest fly to random spots.")]
        [SerializeField] private float targetChance = 0.6f;

        [Header("Timing")]
        [SerializeField] private Vector2 respawnDelay = new Vector2(0.5f, 2.5f); // seconds, random
        [SerializeField] private float startSpread = 4f;           // first bubbles start spread over this many seconds

        /// <summary>Set the targets at runtime (BotSpawner calls this with its bots).</summary>
        public void SetTargets(Transform[] newTargets) => targets = newTargets;

        private Transform Player => player != null ? player : (Camera.main != null ? Camera.main.transform : null);
        private float nextCheck;

        // Bubbles that drifted far away (e.g. after the player walked off) start again near the player.
        private void Update()
        {
            if (Time.time < nextCheck) return;
            nextCheck = Time.time + checkInterval;
            Transform p = Player;
            if (p == null) return;

            foreach (AmbientBubbleMover bubble in GetComponentsInChildren<AmbientBubbleMover>())
            {
                Vector3 b = bubble.transform.position;
                float flat = new Vector2(b.x - p.position.x, b.z - p.position.z).magnitude;
                if (flat > maxDistanceFromPlayer && !BubbleFeature.PlayerView.CanSee(b))
                    bubble.Despawn(); // Finished -> a new one starts nearby
            }
        }

        private void Start()
        {
            if (spawnOnStart) Restart();
        }

        /// <summary>Removes all current bubbles and starts a fresh set around the player.</summary>
        public void Restart()
        {
            if (bubblePrefab == null) { Debug.LogWarning("[Ambient] No bubble prefab assigned.", this); return; }
            StopAllCoroutines();
            foreach (AmbientBubbleMover old in GetComponentsInChildren<AmbientBubbleMover>())
            {
                old.Finished -= OnFinished;
                Destroy(old.gameObject);
            }
            for (int i = 0; i < count; i++)
                StartCoroutine(SpawnAfter(Random.Range(0f, startSpread)));
        }

        private IEnumerator SpawnAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            SpawnOne();
        }

        private void SpawnOne()
        {
            Vector3 from = SpawnPoint();
            AmbientBubbleMover bubble = Instantiate(bubblePrefab, from, Quaternion.identity, transform);
            bubble.Finished += OnFinished;

            Transform target = PickTarget();
            if (target != null)
            {
                bubble.Launch(from, target, Vector3.up * targetHeadHeight);
            }
            else
            {
                // Pick a random point that is far enough from the start.
                Vector3 to = RandomPoint(targetHeight);
                for (int i = 0; i < 10 && Vector3.Distance(from, to) < minTravelDistance; i++)
                    to = RandomPoint(targetHeight);
                bubble.Launch(from, to);
            }
        }

        private void OnFinished(AmbientBubbleMover bubble)
        {
            bubble.Finished -= OnFinished;
            if (isActiveAndEnabled)
                StartCoroutine(SpawnAfter(Random.Range(respawnDelay.x, respawnDelay.y)));
        }

        private Transform PickTarget()
        {
            if (targets == null || targets.Length == 0) return null;
            if (Random.value > targetChance) return null; // this one flies to a random spot
            for (int i = 0; i < 10; i++)
            {
                Transform t = targets[Random.Range(0, targets.Length)];
                if (t != null) return t;
            }
            return null;
        }

        // Start point: random around the player, but (if wanted) somewhere the player can't see right now.
        private Vector3 SpawnPoint()
        {
            Vector3 point = RandomPoint(spawnHeight);
            if (!spawnOutOfView) return point;
            for (int i = 0; i < 15 && BubbleFeature.PlayerView.CanSee(point); i++)
                point = RandomPoint(spawnHeight);
            if (BubbleFeature.PlayerView.CanSee(point))
            {
                // Everything around is in view (open square): start behind the player.
                Transform p = Player;
                Vector3 center = p != null ? p.position : transform.position;
                Vector3 offset = BubbleFeature.PlayerView.HiddenDirection() * Random.Range(minDistanceFromPlayer, areaRadius);
                point = new Vector3(center.x + offset.x, point.y, center.z + offset.z);
            }
            return point;
        }

        // Random point in a circle around the player, at least minDistanceFromPlayer away.
        private Vector3 RandomPoint(Vector2 heightRange)
        {
            Transform p = Player;
            Vector3 center = p != null ? p.position : transform.position;

            Vector2 offset = Vector2.zero;
            for (int i = 0; i < 10; i++)
            {
                offset = Random.insideUnitCircle * areaRadius;
                if (offset.magnitude >= minDistanceFromPlayer) break;
            }
            if (offset.magnitude < minDistanceFromPlayer)
                offset = offset.normalized * minDistanceFromPlayer;

            float floorY = floorReference != null ? floorReference.position.y : transform.position.y;
            float y = floorY + Random.Range(heightRange.x, heightRange.y);
            return new Vector3(center.x + offset.x, y, center.z + offset.y);
        }
    }
}
