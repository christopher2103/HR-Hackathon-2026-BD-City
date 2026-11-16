using System;
using System.Collections;
using UnityEngine;
using BubbleFeature.Gifts;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// A gift bubble: a see-through shell with a glowing rainbow core that hides the gift.
    /// Animates (shell wobble, core pulse, slow rainbow colour) and can be popped.
    /// Input lives in separate scripts (GiftBubbleXRPop, MouseBubblePopper),
    /// so this class doesn't depend on XR.
    /// All animation is done in code (cheap on Quest: no physics, no particles, no Bloom).
    /// </summary>
    public class GiftBubble : MonoBehaviour
    {
        // URP Lit/Unlit colour property.
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

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

        [Header("Core rainbow")]
        [Tooltip("Core and its glow halo. Their colour cycles through the rainbow.")]
        [SerializeField] private Renderer[] rainbowRenderers;
        [SerializeField] private float secondsPerRainbow = 8f;    // one full colour cycle
        [Range(0f, 1f)]
        [SerializeField] private float rainbowSaturation = 0.6f;  // lower = softer, pastel colours

        [Header("Pop")]
        [SerializeField] private float popSwellScale = 1.3f; // grows 30% ...
        [SerializeField] private float popDuration = 0.12f;  // ... in this many seconds, then vanishes

        private Vector3 shellBaseScale;
        private Vector3 coreBaseScale;
        private float phase; // random offset so bubbles don't wobble or colour in sync
        private float[] baseAlphas; // keep each material's own transparency (glow halo)
        private MaterialPropertyBlock propertyBlock;

        /// <summary>Fired once when the bubble pops. Used to show the gift reveal.</summary>
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

            // Remember the alpha of each rainbow material (the halo is see-through).
            propertyBlock = new MaterialPropertyBlock();
            int count = rainbowRenderers != null ? rainbowRenderers.Length : 0;
            baseAlphas = new float[count];
            for (int i = 0; i < count; i++)
            {
                Material m = rainbowRenderers[i] != null ? rainbowRenderers[i].sharedMaterial : null;
                baseAlphas[i] = m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId).a : 1f;
            }
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

            UpdateRainbow(t);
        }

        // Slowly cycles the hue. Uses a property block, so all bubbles share one material.
        private void UpdateRainbow(float t)
        {
            if (rainbowRenderers == null || rainbowRenderers.Length == 0) return;

            float hue = Mathf.Repeat(t / secondsPerRainbow, 1f);
            Color color = Color.HSVToRGB(hue, rainbowSaturation, 1f);

            for (int i = 0; i < rainbowRenderers.Length; i++)
            {
                if (rainbowRenderers[i] == null) continue;
                color.a = baseAlphas[i];
                rainbowRenderers[i].GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor(BaseColorId, color);
                rainbowRenderers[i].SetPropertyBlock(propertyBlock);
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
