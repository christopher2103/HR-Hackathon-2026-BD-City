using UnityEngine;
using UnityEngine.UI;

namespace BubbleFeature.Menu
{
    /// <summary>
    /// Makes the menu feel like one of the gift bubbles: the rim slowly cycles through soft rainbow colours
    /// (same speed and softness as the bubble cores) and the panel "breathes" a tiny bit when it opens.
    /// Cheap: one Image colour per frame, no extra materials.
    /// </summary>
    public class MenuBubbleStyle : MonoBehaviour
    {
        [SerializeField] private Image rim;
        [SerializeField] private float secondsPerRainbow = 8f;
        [Range(0f, 1f)] [SerializeField] private float saturation = 0.45f;
        [SerializeField] private float rimAlpha = 0.95f;
        [Tooltip("Pop-in when the menu opens: starts this much smaller and wobbles into place.")]
        [SerializeField] private float openWobble = 0.12f;
        [SerializeField] private float openSeconds = 0.35f;

        private Vector3 baseScale;
        private float openedAt = -10f;

        private void Awake() => baseScale = transform.localScale;

        private void OnEnable() => openedAt = Time.unscaledTime;

        private void Update()
        {
            if (rim != null)
            {
                Color c = Color.HSVToRGB(Mathf.Repeat(Time.unscaledTime / secondsPerRainbow, 1f), saturation, 1f);
                c.a = rimAlpha;
                rim.color = c;
            }
            float t = (Time.unscaledTime - openedAt) / openSeconds;
            if (t < 1f)
            {
                // Bubble-like pop-in: overshoot and settle (damped wobble).
                float s = 1f - openWobble * Mathf.Cos(t * Mathf.PI * 2.5f) * (1f - t);
                transform.localScale = baseScale * s;
            }
            else if (transform.localScale != baseScale) transform.localScale = baseScale;
        }
    }
}
