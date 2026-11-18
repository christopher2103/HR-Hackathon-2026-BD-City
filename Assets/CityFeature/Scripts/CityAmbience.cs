using System.Collections;
using UnityEngine;

namespace CityFeature
{
    /// <summary>
    /// Background sound per neighbourhood (quay with water and gulls, shopping street, open square).
    /// Crossfades when you travel. 2D loops at low volume, so they never compete with Lotte or the gift sounds.
    /// </summary>
    public class CityAmbience : MonoBehaviour
    {
        [System.Serializable]
        public class AreaSound { public string neighbourhood; public AudioClip loop; [Range(0f, 1f)] public float volume = 0.35f; }

        [SerializeField] private NeighbourhoodTeleporter teleporter;
        [SerializeField] private AreaSound[] areas;
        [SerializeField] private float fadeSeconds = 1.5f;

        private AudioSource a, b;   // two sources for crossfading
        private Coroutine fading;

        private void Awake()
        {
            a = MakeSource("Ambience A"); b = MakeSource("Ambience B");
        }

        private AudioSource MakeSource(string n)
        {
            var go = new GameObject(n); go.transform.SetParent(transform, false);
            var s = go.AddComponent<AudioSource>();
            s.loop = true; s.playOnAwake = false; s.spatialBlend = 0f; s.volume = 0f;
            return s;
        }

        private void OnEnable() { if (teleporter != null) teleporter.Arrived += OnArrived; }
        private void OnDisable() { if (teleporter != null) teleporter.Arrived -= OnArrived; }

        private void OnArrived(string place)
        {
            AreaSound area = null;
            foreach (var s in areas) if (s.neighbourhood == place) area = s;
            if (fading != null) StopCoroutine(fading);
            fading = StartCoroutine(Crossfade(area));
        }

        private IEnumerator Crossfade(AreaSound area)
        {
            if (area != null && a.clip == area.loop && a.isPlaying) { yield return FadeTo(a, area.volume); yield break; }
            (a, b) = (b, a);   // 'a' = new, 'b' = old
            if (area != null && area.loop != null)
            {
                a.clip = area.loop; a.volume = 0f;
                a.time = Random.Range(0f, area.loop.length * 0.9f);   // don't always start at the same gull
                a.Play();
            }
            float t = 0f, startB = b.volume, target = area != null ? area.volume : 0f;
            while (t < fadeSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = t / fadeSeconds;
                a.volume = Mathf.Lerp(0f, target, k);
                b.volume = Mathf.Lerp(startB, 0f, k);
                yield return null;
            }
            b.Stop();
        }

        private IEnumerator FadeTo(AudioSource s, float v)
        {
            float start = s.volume, t = 0f;
            while (t < fadeSeconds) { t += Time.unscaledDeltaTime; s.volume = Mathf.Lerp(start, v, t / fadeSeconds); yield return null; }
        }
    }
}
