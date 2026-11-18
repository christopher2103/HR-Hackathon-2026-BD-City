using System.Collections;
using TMPro;
using UnityEngine;

namespace CityFeature
{
    /// <summary>
    /// When you arrive in a neighbourhood (after the loading screen), its name fades in in front of you for a moment,
    /// with a soft chime: "Blaak" + "Markthal · Cube Houses". Also used for short operator messages (Toast).
    /// World-space text that follows your view smoothly; no canvas, one TMP object.
    /// </summary>
    public class ArrivalTitle : MonoBehaviour
    {
        [System.Serializable]
        public class AreaTitle { public string neighbourhood; public string subtitle; }

        [SerializeField] private NeighbourhoodTeleporter teleporter;
        [SerializeField] private AreaTitle[] subtitles;
        [SerializeField] private float distance = 2.2f;
        [SerializeField] private float heightAboveEyes = 0.25f;
        [SerializeField] private float holdSeconds = 2.6f;
        [SerializeField] private float fadeSeconds = 0.6f;
        [SerializeField] private Color titleColor = new Color(1f, 1f, 1f, 1f);
        [SerializeField] private Color accentColor = new Color(0.95f, 0.55f, 0.25f, 1f);

        private TextMeshPro text;
        private Coroutine showing;

        private void Awake()
        {
            var go = new GameObject("Arrival Text");
            go.transform.SetParent(transform, false);
            text = go.AddComponent<TextMeshPro>();
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 3.2f;
            text.rectTransform.sizeDelta = new Vector2(3f, 1f);
            text.outlineWidth = 0.18f;
            text.outlineColor = new Color32(20, 26, 48, 200);
            text.color = titleColor;
            text.alpha = 0f;
            go.SetActive(false);
        }

        private void OnEnable() { if (teleporter != null) teleporter.Revealed += OnRevealed; }
        private void OnDisable() { if (teleporter != null) teleporter.Revealed -= OnRevealed; }

        private void OnRevealed(string place)
        {
            string sub = "";
            if (subtitles != null) foreach (var s in subtitles) if (s.neighbourhood == place) sub = s.subtitle;
            Show(place, sub, chime: true);
        }

        /// <summary>A short message in front of the player (operator feedback).</summary>
        public void Toast(string message) => Show(message, "", chime: false);

        private void Show(string title, string subtitle, bool chime)
        {
            string hex = ColorUtility.ToHtmlStringRGB(accentColor);
            text.text = string.IsNullOrEmpty(subtitle) ? $"<b>{title}</b>" : $"<b>{title}</b>\n<size=45%><color=#{hex}>{subtitle}</color></size>";
            if (showing != null) StopCoroutine(showing);
            showing = StartCoroutine(Run(chime));
        }

        private IEnumerator Run(bool chime)
        {
            Camera cam = Camera.main;
            if (cam == null) yield break;
            text.gameObject.SetActive(true);
            Place(cam, 1f);
            if (chime) BubbleFeature.Audio.DemoAudio.Play(BubbleFeature.Audio.Sfx.Chime, null, 0.6f);
            float total = fadeSeconds * 2f + holdSeconds, t = 0f;
            while (t < total)
            {
                t += Time.unscaledDeltaTime;
                float a = t < fadeSeconds ? t / fadeSeconds : t > total - fadeSeconds ? (total - t) / fadeSeconds : 1f;
                text.alpha = Mathf.SmoothStep(0f, 1f, a);
                Place(cam, 1f - Mathf.Exp(-3f * Time.unscaledDeltaTime));   // drifts after your view, gently
                yield return null;
            }
            text.alpha = 0f;
            text.gameObject.SetActive(false);
            showing = null;
        }

        private void Place(Camera cam, float k)
        {
            Transform head = cam.transform;
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            Vector3 target = head.position + fwd.normalized * distance + Vector3.up * heightAboveEyes;
            Transform t = text.transform;
            t.position = Vector3.Lerp(t.position, target, k);
            t.rotation = Quaternion.LookRotation(t.position - head.position);
        }
    }
}
