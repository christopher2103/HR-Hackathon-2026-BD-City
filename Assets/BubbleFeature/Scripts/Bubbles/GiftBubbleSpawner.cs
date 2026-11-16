using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using BubbleFeature.Gifts;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Spawns a personal gift bubble in the distance that flies toward the player.
    /// Test input: A button (right controller) or G key spawns a new bubble.
    /// </summary>
    public class GiftBubbleSpawner : MonoBehaviour
    {
        [Header("What to spawn")]
        [SerializeField] private GiftBubble bubblePrefab;
        [Tooltip("Gifts handed out in order, one per bubble.")]
        [SerializeField] private GiftData[] gifts;

        [Header("Where")]
        [Tooltip("Leave empty to use Camera.main (the headset).")]
        [SerializeField] private Transform player;
        [SerializeField] private float spawnDistance = 8f;        // metres away
        [SerializeField] private float spawnHeightAboveEyes = 1f; // starts a bit up in the air
        [SerializeField] private float spawnAngleRange = 60f;     // degrees left/right of where you look

        [Header("When")]
        [SerializeField] private bool spawnOnStart = true;
        [SerializeField] private float startDelay = 1f;           // wait for head tracking to settle
        [SerializeField] private bool onlyOneAtATime = true;      // remove the old bubble on respawn

        private InputAction spawnAction;
        private GiftBubble current;
        private int nextGiftIndex;

        private void Awake()
        {
            // Test input defined in code, so there is nothing to set up in the Inspector.
            spawnAction = new InputAction("SpawnBubble", InputActionType.Button);
            spawnAction.AddBinding("<XRController>{RightHand}/primaryButton"); // A button
            spawnAction.AddBinding("<Keyboard>/g");
            spawnAction.performed += _ => Spawn();
        }

        private void OnEnable()  => spawnAction.Enable();
        private void OnDisable() => spawnAction.Disable();
        private void OnDestroy() => spawnAction.Dispose();

        private IEnumerator Start()
        {
            if (!spawnOnStart) yield break;
            yield return new WaitForSeconds(startDelay);
            Spawn();
        }

        /// <summary>Spawns one bubble in front of the player. Also callable from the Inspector.</summary>
        [ContextMenu("Spawn bubble")]
        public void Spawn()
        {
            Transform p = player != null ? player : (Camera.main != null ? Camera.main.transform : null);
            if (p == null)    { Debug.LogWarning("[Bubble] No player/camera found.", this); return; }
            if (bubblePrefab == null) { Debug.LogWarning("[Bubble] No bubble prefab assigned.", this); return; }

            if (onlyOneAtATime && current != null) Destroy(current.gameObject);

            // Random direction in front of the player, flattened to the ground plane.
            Vector3 forward = p.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            Vector3 direction = Quaternion.Euler(0f, Random.Range(-spawnAngleRange, spawnAngleRange), 0f) * forward.normalized;

            Vector3 position = p.position + direction * spawnDistance + Vector3.up * spawnHeightAboveEyes;
            current = Instantiate(bubblePrefab, position, Quaternion.identity);

            // Hand out the next gift.
            GiftData gift = null;
            if (gifts != null && gifts.Length > 0)
            {
                gift = gifts[nextGiftIndex % gifts.Length];
                nextGiftIndex++;
                current.SetGift(gift);
            }

            // Start the flight.
            GiftBubbleFlight flight = current.GetComponent<GiftBubbleFlight>();
            if (flight != null) flight.StartFlight(p);
            else Debug.LogWarning("[Bubble] Prefab has no GiftBubbleFlight component.", current);

            Debug.Log($"[Bubble] Spawned with gift: {(gift != null ? gift.name : "none")}", current);
        }
    }
}
