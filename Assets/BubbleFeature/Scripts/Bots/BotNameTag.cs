using TMPro;
using UnityEngine;

namespace BubbleFeature.Bots
{
    /// <summary>
    /// Floating username above a bot: a navy pill with the name and a dot in the bot's colour.
    /// One TextMeshPro + one sliced sprite per bot (pill sprite is made in code, shared).
    /// Faces the camera, stays level (no head bob) and fades out with distance.
    /// </summary>
    [DisallowMultipleComponent]
    public class BotNameTag : MonoBehaviour
    {
        private const string FontPath = "Fonts & Materials/Roboto-Bold SDF";
        private static TMP_FontAsset font;
        private static Sprite pillSprite;

        [SerializeField] private float fontSize = 1.8f;
        [Tooltip("Gap between the top of the head and the pill (m).")]
        [SerializeField] private float aboveHead = 0.07f;
        [Tooltip("Fully visible up to this distance (m)…")]
        [SerializeField] private float fadeStart = 9f;
        [Tooltip("…and gone from this distance (m).")]
        [SerializeField] private float fadeEnd = 15f;
        [Tooltip("Hidden when the bot is this close (m), fades in over the next 0.6 m.")]
        [SerializeField] private float nearHide = 0.9f;
        [SerializeField] private Color pillColor = new Color(0.11f, 0.12f, 0.2f, 0.85f);
        [SerializeField] private Vector2 padding = new Vector2(0.07f, 0.035f);

        private BotWanderer bot;
        private Transform root;
        private TextMeshPro text;
        private SpriteRenderer pill;
        private const float PillHeight = 32f / 160f;   // sprite height in units (32 px at 160 ppu)
        private float headTop = 1.75f;
        private float pillHeight = 0.25f;
        private float nextMeasure;
        private Camera cam;
        private string shownName;
        private Color shownColor;
        private float shownAlpha = -1f;

        /// <summary>Adds (or refreshes) the tag on a bot. Call after SetName/SetColor.</summary>
        public static BotNameTag Attach(BotWanderer bot)
        {
            if (bot == null) return null;
            var t = bot.GetComponent<BotNameTag>();
            if (t == null) t = bot.gameObject.AddComponent<BotNameTag>();
            t.Refresh();
            return t;
        }

        private void Awake() => bot = GetComponent<BotWanderer>();

        private void Start()
        {
            MeasureHead();
            Refresh();
        }

        private void Build()
        {
            if (root != null) return;
            if (font == null) font = Resources.Load<TMP_FontAsset>(FontPath);
            if (pillSprite == null) pillSprite = MakePillSprite();

            root = new GameObject("Name Tag").transform;
            root.SetParent(transform, false);

            var pillGo = new GameObject("Pill");
            pillGo.transform.SetParent(root, false);
            pill = pillGo.AddComponent<SpriteRenderer>();
            pill.sprite = pillSprite;
            pill.drawMode = SpriteDrawMode.Sliced;
            pill.sortingOrder = 0;
            pill.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            pill.receiveShadows = false;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(root, false);
            text = textGo.AddComponent<TextMeshPro>();
            if (font != null) text.font = font;
            text.fontSize = fontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(2f, 0.4f);
            text.raycastTarget = false;
            textGo.transform.localPosition = new Vector3(0f, 0f, -0.002f);   // just in front of the pill
            var r = text.GetComponent<MeshRenderer>();
            r.sortingOrder = 1;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Top of the head from the head bone; a hat adds a little.</summary>
        private void MeasureHead()
        {
            Transform head = null;
            bool hat = false;
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (head == null && n == "head") head = t;
                if (n.Contains("hat") || n.Contains("cap")) { var rr = t.GetComponent<Renderer>(); if (rr != null && rr.enabled && rr.gameObject.activeInHierarchy) hat = true; }
            }
            float top = 1.75f;
            if (head != null) top = head.position.y - transform.position.y + 0.21f;
            if (hat) top += 0.08f;
            headTop = Mathf.Clamp(top, 1.3f, 2.1f);
        }

        public void Refresh()
        {
            Build();
            string n = bot != null && !string.IsNullOrEmpty(bot.DisplayName) ? bot.DisplayName : name;
            Color c = bot != null ? bot.Color : Color.white;
            if (n == shownName && c == shownColor) return;
            shownName = n; shownColor = c; shownAlpha = -1f;

            text.text = $"<color=#{ColorUtility.ToHtmlStringRGB(c)}><size=140%>\u2022</size></color> {n}";
            text.color = Color.white;
            text.ForceMeshUpdate();
            Vector2 size = text.GetRenderedValues(true);
            float h = size.y + padding.y * 2f;
            float w = Mathf.Max(size.x + padding.x * 2f, h);
            // Scale the sprite so its round ends are exactly half the height (no squashed caps).
            float k = h / PillHeight;
            pill.transform.localScale = new Vector3(k, k, 1f);
            pill.size = new Vector2(w / k, PillHeight);
            pillHeight = h;
            // Centre the pill on the visible text (glyphs sit a bit off the rect centre).
            Vector3 c3 = text.textBounds.center;
            pill.transform.localPosition = new Vector3(c3.x, c3.y, 0f);
        }

        private void LateUpdate()
        {
            if (root == null) return;
            if (cam == null) { cam = Camera.main; if (cam == null) return; }
            if (bot != null && (bot.DisplayName != shownName || bot.Color != shownColor)) Refresh();
            if (Time.time >= nextMeasure) { nextMeasure = Time.time + 2f; MeasureHead(); }   // hats come and go (outfit menu)

            // Level above the bot's feet, not glued to the bobbing head.
            float lift = headTop + aboveHead + pillHeight * 0.5f;
            Vector3 pos = transform.position + Vector3.up * lift;
            Vector3 eye = cam.transform.position;
            float dist = Vector3.Distance(eye, pos);
            float alpha = (1f - Mathf.InverseLerp(fadeStart, fadeEnd, dist))
                        * Mathf.InverseLerp(nearHide, nearHide + 0.6f, dist);   // not in your face up close

            bool show = alpha > 0.01f;
            if (root.gameObject.activeSelf != show) root.gameObject.SetActive(show);
            if (!show) return;

            Vector3 away = pos - eye; away.y = 0f;   // upright, turned towards the viewer
            if (away.sqrMagnitude < 1e-4f) away = cam.transform.forward;
            root.SetPositionAndRotation(pos, Quaternion.LookRotation(away, Vector3.up));

            alpha = Mathf.Round(alpha * 20f) / 20f;   // only touch colours on visible steps
            if (!Mathf.Approximately(alpha, shownAlpha))
            {
                shownAlpha = alpha;
                text.alpha = alpha;
                Color pc = pillColor; pc.a *= alpha;
                pill.color = pc;
            }
        }

        /// <summary>White capsule (64x32, round ends) for a sliced SpriteRenderer.</summary>
        private static Sprite MakePillSprite()
        {
            const int w = 64, h = 32;
            const float rad = h * 0.5f;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "NameTagPill" };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, rad, w - rad);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, rad));
                byte a = (byte)(Mathf.Clamp01(rad - d + 0.5f) * 255f);
                px[y * w + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 160f, 0, SpriteMeshType.FullRect, new Vector4(rad, 0, rad, 0));
        }
    }
}
