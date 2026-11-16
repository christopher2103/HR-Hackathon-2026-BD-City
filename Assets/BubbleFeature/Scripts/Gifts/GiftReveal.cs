using System.Collections;
using TMPro;
using UnityEngine;

namespace BubbleFeature.Gifts
{
    /// <summary>
    /// What you see after a bubble pops: "From: sender" + message, and the item (if any).
    /// The item is the gift's own itemPrefab (later: 3D scans / clothing library),
    /// or a placeholder (hat / shirt) when the gift has none.
    /// Imported models are auto-scaled to fit, so their original size doesn't matter.
    /// Pops in, faces the player, stays a few seconds, then shrinks away.
    /// </summary>
    public class GiftReveal : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Parts (children of the prefab)")]
        [SerializeField] private TMP_Text messageText;
        [SerializeField] private Transform itemAnchor;
        [Tooltip("Dark background behind the text (a Quad, child of the text). Resized to fit the text.")]
        [SerializeField] private Transform textPanel;
        [SerializeField] private Vector2 panelPadding = new Vector2(0.06f, 0.04f); // metres around the text

        [Header("Placeholders (used when the gift has no itemPrefab)")]
        [SerializeField] private GameObject accessoryPlaceholder;
        [SerializeField] private GameObject clothingPlaceholder;

        [Header("Item")]
        [SerializeField] private float itemSize = 0.3f;   // biggest side of the item, in metres
        [SerializeField] private float spinSpeed = 45f;   // degrees per second

        [Header("Timing")]
        [SerializeField] private float showSeconds = 6f;
        [SerializeField] private float scaleInSeconds = 0.25f;
        [SerializeField] private float scaleOutSeconds = 0.3f;

        private Transform viewer;

        /// <summary>Fill in the reveal and start its animation.</summary>
        public void Show(GiftData gift, Transform viewerTransform)
        {
            viewer = viewerTransform;
            transform.localScale = Vector3.one; // full size while measuring the item

            if (messageText != null)
            {
                messageText.text = $"From: {gift.sender}\n{gift.message}";
                FitPanelToText();
            }

            if (gift.HasItem) SpawnItem(gift);

            StartCoroutine(Lifetime());
        }

        // Size the dark panel to the text, so short and long messages both look tidy.
        // Font size/box/bold are set on the prefab's TextMeshPro (auto-size between a min and max).
        private void FitPanelToText()
        {
            if (textPanel == null) return;
            messageText.ForceMeshUpdate();
            Bounds b = messageText.textBounds; // in the text's local space
            textPanel.localScale = new Vector3(b.size.x + panelPadding.x, b.size.y + panelPadding.y, 1f);
            textPanel.localPosition = new Vector3(b.center.x, b.center.y, 0.005f); // just behind the text
        }

        private void SpawnItem(GiftData gift)
        {
            bool usePlaceholder = gift.itemPrefab == null;
            GameObject source = usePlaceholder
                ? (gift.itemType == GiftItemType.Accessory ? accessoryPlaceholder : clothingPlaceholder)
                : gift.itemPrefab;

            if (source == null || itemAnchor == null)
            {
                Debug.LogWarning($"[Reveal] No model for {gift.itemType} (assign a placeholder on the GiftReveal prefab).", this);
                return;
            }

            GameObject item = Instantiate(source, itemAnchor);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;

            // The reveal is only for looking: remove colliders so it doesn't block rays.
            foreach (Collider c in item.GetComponentsInChildren<Collider>()) Destroy(c);

            FitToSize(item);

            // Only tint placeholders; scanned/imported models keep their own textures.
            if (usePlaceholder) Tint(item, gift.itemColor);
        }

        // Scale the item so its largest side is itemSize, and centre it on the anchor.
        private void FitToSize(GameObject item)
        {
            Renderer[] renderers = item.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;

            Bounds bounds = GetBounds(renderers);
            float largest = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (largest > 0.0001f) item.transform.localScale *= itemSize / largest;

            bounds = GetBounds(renderers);
            item.transform.position += itemAnchor.position - bounds.center;
        }

        private static Bounds GetBounds(Renderer[] renderers)
        {
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static void Tint(GameObject item, Color color)
        {
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>())
            {
                r.GetPropertyBlock(block);
                block.SetColor(BaseColorId, color);
                r.SetPropertyBlock(block);
            }
        }

        private void Update()
        {
            // Turn to face the player (only around the vertical axis, so it stays upright).
            if (viewer != null)
            {
                Vector3 away = transform.position - viewer.position;
                away.y = 0f;
                if (away.sqrMagnitude > 0.0001f) transform.rotation = Quaternion.LookRotation(away);
            }

            // Slowly spin the item.
            if (itemAnchor != null) itemAnchor.Rotate(0f, spinSpeed * Time.deltaTime, 0f, Space.Self);
        }

        private IEnumerator Lifetime()
        {
            yield return ScaleTo(0f, 1f, scaleInSeconds);
            yield return new WaitForSeconds(showSeconds);
            yield return ScaleTo(1f, 0f, scaleOutSeconds);
            Destroy(gameObject);
        }

        private IEnumerator ScaleTo(float from, float to, float seconds)
        {
            float time = 0f;
            while (time < seconds)
            {
                time += Time.deltaTime;
                float k = Mathf.SmoothStep(0f, 1f, time / seconds);
                transform.localScale = Vector3.one * Mathf.Lerp(from, to, k);
                yield return null;
            }
            transform.localScale = Vector3.one * to;
        }
    }
}
