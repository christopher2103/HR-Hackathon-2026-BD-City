using System.Collections;
using UnityEngine;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Keeps a few ambient bubbles flying around the player to show that others are getting gifts.
    /// Each bubble flies to a random point (or to a random entry in Targets, e.g. bots),
    /// pops there, and a new one starts somewhere else after a short delay.
    /// Place this object on the floor (its Y is used as ground height).
    /// </summary>
    public class AmbientBubbleSpawner : MonoBehaviour
    {
        [Header("What")]
        [SerializeField] private AmbientBubbleMover bubblePrefab;
        [SerializeField] private int count = 6;

        [Header("Area (around the player)")]
        [Tooltip("Leave empty to use Camera.main (the headset).")]
        [SerializeField] private Transform player;
        [SerializeField] private float areaRadius = 15f;
        [SerializeField] private float minDistanceFromPlayer = 3f;  // never too close to your head
        [SerializeField] private float minTravelDistance = 5f;      // so each bubble flies a bit
        [SerializeField] private Vector2 spawnHeight = new Vector2(2.5f, 5f);   // metres above the floor
        [SerializeField] private Vector2 targetHeight = new Vector2(1.2f, 2.2f);

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

        private void Start()
        {
            if (bubblePrefab == null) { Debug.LogWarning("[Ambient] No bubble prefab assigned.", this); return; }
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
            Vector3 from = RandomPoint(spawnHeight);
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

            float y = transform.position.y + Random.Range(heightRange.x, heightRange.y);
            return new Vector3(center.x + offset.x, y, center.z + offset.y);
        }
    }
}
