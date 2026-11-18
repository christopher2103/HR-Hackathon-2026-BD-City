using UnityEngine;

namespace BubbleFeature.Audio
{
    public enum Sfx { Pop, Sparkle, Click, MenuOpen, Chime }

    /// <summary>
    /// The demo's sound effects in one place. Put one in the scene (with the clips assigned); anything can call
    /// DemoAudio.Play(Sfx.Pop, position). With a position the sound is 3D (comes from there), without it plays
    /// "in your head" (UI). Uses a small pool of AudioSources: no allocations, cheap on Quest.
    /// If no DemoAudio is in the scene, calls are simply ignored.
    /// </summary>
    public class DemoAudio : MonoBehaviour
    {
        [SerializeField] private AudioClip pop, sparkle, click, menuOpen, chime;
        [Range(0f, 1f)] [SerializeField] private float volume = 0.8f;
        [SerializeField] private int voices = 8;

        private static DemoAudio instance;
        private AudioSource[] pool;
        private int next;

        private void Awake()
        {
            instance = this;
            pool = new AudioSource[voices];
            for (int i = 0; i < voices; i++)
            {
                var go = new GameObject("Sfx Voice " + i);
                go.transform.SetParent(transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.rolloffMode = AudioRolloffMode.Logarithmic;
                s.minDistance = 1.5f; s.maxDistance = 40f;
                s.dopplerLevel = 0f;
                pool[i] = s;
            }
        }

        private void OnDestroy() { if (instance == this) instance = null; }

        public static void Play(Sfx sfx, Vector3? at = null, float gain = 1f, float pitch = 1f)
        {
            if (instance == null) return;
            instance.PlayInternal(sfx, at, gain, pitch);
        }

        private void PlayInternal(Sfx sfx, Vector3? at, float gain, float pitch)
        {
            AudioClip clip = sfx switch
            {
                Sfx.Pop => pop, Sfx.Sparkle => sparkle, Sfx.Click => click, Sfx.MenuOpen => menuOpen, _ => chime
            };
            if (clip == null) return;
            AudioSource s = pool[next]; next = (next + 1) % pool.Length;
            Transform head = Camera.main != null ? Camera.main.transform : transform;
            s.transform.position = at ?? head.position;
            s.spatialBlend = at.HasValue ? 1f : 0f;
            s.pitch = pitch * (sfx == Sfx.Pop ? Random.Range(0.92f, 1.1f) : 1f); // pops never sound identical
            s.clip = clip;
            s.volume = volume * gain;
            s.Play();
        }
    }
}
