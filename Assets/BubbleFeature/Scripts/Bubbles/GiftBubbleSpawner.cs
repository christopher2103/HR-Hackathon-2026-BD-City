using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using BubbleFeature.Gifts;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Spawns a personal gift bubble in the distance that flies toward the player,
    /// and shows the gift reveal when that bubble pops.
    /// Test input: A button (right controller) or G key spawns a new bubble.
    /// </summary>
    public class GiftBubbleSpawner : MonoBehaviour
    {
        [Header("What to spawn")]
        [SerializeField] private GiftBubble bubblePrefab;
        [Tooltip("Gifts handed out in order, one per bubble.")]
        [SerializeField] private GiftData[] gifts;

        [Header("Reveal")]
        [Tooltip("Shown where the bubble popped. Leave empty to skip the reveal.")]
        [SerializeField] private GiftReveal revealPrefab;

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

        [Header("Test input")]
        [Tooltip("SpawnTestBubble from BDCityControls (rebindable). Empty = built-in A button / G key.")]
        [SerializeField] private InputActionReference spawnActionReference;

        private InputAction spawnAction;
        private bool ownsAction; // true when we created the action ourselves
        private GiftBubble current;
        private GiftReveal currentReveal;
        private int nextGiftIndex;

        private Transform Player => player != null ? player : (Camera.main != null ? Camera.main.transform : null);

        private void Awake()
        {
            if (spawnActionReference != null)
            {
                spawnAction = spawnActionReference.action; // shared, rebindable action
            }
            else
            {
                // Fallback: test input defined in code.
                spawnAction = new InputAction("SpawnBubble", InputActionType.Button);
                spawnAction.AddBinding("<XRController>{RightHand}/primaryButton"); // A button
                spawnAction.AddBinding("<Keyboard>/g");
                ownsAction = true;
            }
        }

        private void OnEnable()
        {
            spawnAction.performed += OnSpawnPressed;
            spawnAction.Enable();
        }

        private void OnDisable()
        {
            spawnAction.performed -= OnSpawnPressed;
            if (ownsAction) spawnAction.Disable(); // don't switch off a shared action
        }

        private void OnDestroy()
        {
            if (ownsAction) spawnAction.Dispose();
        }

        private void OnSpawnPressed(InputAction.CallbackContext _) => Spawn();

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
            Transform p = Player;
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
            current.MessageReady += OnMessageReady;

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

        // Show the gift where the tiny bubbles came together.
        private void OnMessageReady(GiftData gift, Vector3 position)
        {
            if (revealPrefab == null || gift == null) return;

            if (currentReveal != null) Destroy(currentReveal.gameObject); // one reveal at a time

            currentReveal = Instantiate(revealPrefab, position, Quaternion.identity);
            currentReveal.Show(gift, Player);
        }
    }
}
