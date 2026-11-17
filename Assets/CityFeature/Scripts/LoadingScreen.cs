using System.Collections;
using TMPro;
using UnityEngine;

namespace CityFeature
{
    /// <summary>
    /// VR loading screen: fades the view to black and shows a short status text in front of you,
    /// e.g. while a neighbourhood streams in after a teleport. Builds itself under the camera at start.
    /// Needs two materials so they survive Quest builds:
    ///  - Fade Material: URP Unlit, transparent, ZTest Always, render queue below the text;
    ///  - Text Material: TextMeshPro "Distance Field Overlay" (always drawn on top).
    /// </summary>
    public class LoadingScreen : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] private Material fadeMaterial;
        [SerializeField] private Material textMaterial;
        [Tooltip("Empty = Camera.main (the headset).")]
        [SerializeField] private Camera targetCamera;
        [SerializeField] private float fadeSeconds = 0.4f;
        [SerializeField] private float textDistance = 1.5f;
        [SerializeField] private float fontSize = 1.1f;
        [Tooltip("Start black, so the first neighbourhood loads out of sight.")]
        [SerializeField] private bool startVisible = true;

        private Transform root;
        private MeshRenderer fade;
        private TextMeshPro text;
        private MaterialPropertyBlock block;
        private float alpha;
        private float targetAlpha;
        private string title = "";
        private string status = "";

        public bool IsOpaque => alpha >= 0.999f;
        public bool IsHidden => alpha <= 0.001f;

        private void Awake()
        {
            if (targetCamera == null) targetCamera = Camera.main;
            if (targetCamera == null) { Debug.LogWarning("[Loading] No camera found.", this); enabled = false; return; }

            root = new GameObject("Loading Screen (runtime)").transform;
            root.SetParent(targetCamera.transform, false);

            // Black quad just in front of the eyes, big enough to cover the whole field of view.
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Fade";
            Destroy(quad.GetComponent<Collider>());
            quad.transform.SetParent(root, false);
            quad.transform.localPosition = new Vector3(0f, 0f, 0.12f);
            quad.transform.localScale = new Vector3(2f, 2f, 1f);
            fade = quad.GetComponent<MeshRenderer>();
            fade.sharedMaterial = fadeMaterial;
            fade.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            fade.receiveShadows = false;
            block = new MaterialPropertyBlock();

            // Status text further away (comfortable to read), drawn on top of the black.
            var textObject = new GameObject("Text");
            textObject.transform.SetParent(root, false);
            textObject.transform.localPosition = new Vector3(0f, 0f, textDistance);
            text = textObject.AddComponent<TextMeshPro>();
            if (textMaterial != null) text.fontSharedMaterial = textMaterial;
            text.rectTransform.sizeDelta = new Vector2(1.8f, 0.8f);
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.color = Color.white;

            alpha = targetAlpha = startVisible ? 1f : 0f;
            if (startVisible) title = "Loading the city...";
            Refresh();
        }

        private void Update()
        {
            if (Mathf.Approximately(alpha, targetAlpha)) return;
            alpha = Mathf.MoveTowards(alpha, targetAlpha, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeSeconds));
            Refresh();
        }

        /// <summary>Fade to black and show a title, e.g. "Going to Lijnbaan".</summary>
        public void Show(string newTitle)
        {
            title = newTitle;
            status = "";
            targetAlpha = 1f;
            Refresh();
        }

        /// <summary>Update the line under the title. progress01 below 0 hides the percentage.</summary>
        public void SetStatus(string newStatus, float progress01 = -1f)
        {
            status = progress01 >= 0f ? $"{newStatus} {Mathf.RoundToInt(progress01 * 100f)}%" : newStatus;
            Refresh();
        }

        /// <summary>Fade back to the world.</summary>
        public void Hide() => targetAlpha = 0f;

        public IEnumerator WaitUntilOpaque() { while (!IsOpaque) yield return null; }
        public IEnumerator WaitUntilHidden() { while (!IsHidden) yield return null; }

        private void Refresh()
        {
            if (root == null) return;
            bool visible = alpha > 0.001f;
            root.gameObject.SetActive(visible);
            if (!visible) return;

            block.SetColor(BaseColorId, new Color(0f, 0f, 0f, alpha));
            fade.SetPropertyBlock(block);

            text.text = string.IsNullOrEmpty(status) ? title : $"{title}\n<size=70%>{status}</size>";
            text.alpha = alpha;
        }
    }
}
