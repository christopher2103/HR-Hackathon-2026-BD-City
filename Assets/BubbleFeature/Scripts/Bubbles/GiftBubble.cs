using System;
using System.Collections;
using UnityEngine;
using BubbleFeature.Gifts;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// A gift bubble: a see-through shell with a glowing core that hides the gift.
    /// Animates (shell wobble + core pulse) and can be popped.
    /// Input lives in separate scripts (GiftBubbleXRPop, MouseBubblePopper),
    /// so this class doesn't depend on XR.
    /// All animation is simple scaling in code (cheap on Quest: no physics, no particles).
    /// </summary>
    public class GiftBubble : MonoBehaviour
    {
        [Header("Gift")]
        [SerializeField] private GiftData gift;

        [Header("Parts (children of the prefab)")]
        [SerializeField] private Transform shell; // transparent sphere
        [SerializeField] private Transform core;  // glowing sphere inside

        [Header("Shell wobble")]
        [SerializeField] private float wobbleAmount = 0.04f; // 4% squash and stretch
        [SerializeField] private float wobbleSpeed = 2.5f;

        [Header("Core pulse")]
        [SerializeField] private float pulseAmount = 0.15f;  // 15% size change
        [SerializeField] private float pulseSpeed = 3f;

        [Header("Pop")]
        [SerializeField] private float popSwellScale = 1.3f; // grows 30% ...
        [SerializeField] private float popDuration = 0.12f;  // ... in this many seconds, then vanishes

        private Vector3 shellBaseScale;
        private Vector3 coreBaseScale;
        private float phase; // random offset so bubbles don't wobble in sync

        /// <summary>Fired once when the bubble pops. Stage 4 uses this to reveal the gift.</summary>
        public event Action<GiftBubble> Popped;

        /// <summary>The gift inside this bubble (null = empty bubble).</summary>
        public GiftData Gift => gift;

        /// <summary>True once Pop() has been called.</summary>
        public bool IsPopped { get; private set; }

        /// <summary>Used by spawners to put a gift in the bubble.</summary>
        public void SetGift(GiftData newGift) => gift = newGift;

        private void Awake()
        {
            if (shell != null) shellBaseScale = shell.localScale;
            if (core != null) coreBaseScale = core.localScale;
            phase = UnityEngine.Random.Range(0f, 10f);
        }

        private void Update()
        {
            float t = Time.time + phase;

            // Jelly wobble: wider while flatter, then taller while thinner.
            if (shell != null)
            {
                float w = Mathf.Sin(t * wobbleSpeed) * wobbleAmount;
                shell.localScale = Vector3.Scale(shellBaseScale, new Vector3(1f + w, 1f - w, 1f + w));
            }

            // Core "breathes" to look like it glows.
            if (core != null)
            {
                float p = 1f + Mathf.Sin(t * pulseSpeed) * pulseAmount;
                core.localScale = coreBaseScale * p;
            }
        }

        /// <summary>Pops the bubble: stops it, quick swell, then it disappears.</summary>
        [ContextMenu("Pop (test)")]
        public void Pop()
        {
            if (IsPopped) return; // trigger + grip on the same frame shouldn't pop twice
            IsPopped = true;

            string what = gift == null ? "no gift" : $"{gift.itemType} from {gift.sender}: \"{gift.message}\"";
            Debug.Log($"[Bubble] Popped ({what})", this);

            // Stop moving and stop being clickable/pointable.
            GiftBubbleFlight flight = GetComponent<GiftBubbleFlight>();
            if (flight != null) flight.Stop();
            foreach (Collider c in GetComponentsInChildren<Collider>()) c.enabled = false;

            Popped?.Invoke(this);
            StartCoroutine(PopRoutine());
        }

        private IEnumerator PopRoutine()
        {
            Vector3 startScale = transform.localScale;
            float time = 0f;

            while (time < popDuration)
            {
                time += Time.deltaTime;
                transform.localScale = startScale * Mathf.Lerp(1f, popSwellScale, time / popDuration);
                yield return null;
            }

            Destroy(gameObject);
        }
    }
}
