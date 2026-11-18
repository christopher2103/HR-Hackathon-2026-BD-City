using System;
using System.Collections.Generic;
using UnityEngine;

namespace CharacterFeature
{
    /// <summary>
    /// Simple outfit system for a MakeHuman avatar that was exported with ALL its clothing options on.
    /// Every option is already fitted to the body and skinned to the same skeleton, so changing clothes
    /// is just showing one option per slot and hiding the rest (no re-rigging at runtime, cheap on Quest).
    /// Each option can have colour variants (tint on the material, no extra textures).
    /// The choice can be saved on the device (PlayerPrefs) under a key of your choice.
    /// </summary>
    public class AvatarWardrobe : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Serializable]
        public class Option
        {
            public string label;
            [Tooltip("Renderers (child objects) that make up this option. Empty = 'none' (e.g. no hat).")]
            public Renderer[] parts;
            [Tooltip("Colour variants (tint multiplied with the texture). Empty = only the original colours.")]
            public Color[] colours;
        }

        [Serializable]
        public class Slot
        {
            public string name;
            public Option[] options;
            [HideInInspector] public int current;
            [HideInInspector] public int colour;   // -1 = original colours
        }

        [SerializeField] private Slot[] slots;

        [Header("Hide skin under clothes")]
        [SerializeField] private SkinnedMeshRenderer body;
        [Tooltip("Clothing renderers in bit order of the cover data (filled by the bake).")]
        [SerializeField] private Renderer[] coverParts;
        [SerializeField] private BodyCoverData cover;
        [Tooltip("Save/load the choice under this key. Empty = don't save (e.g. bots).")]
        [SerializeField] private string saveKey = "";
        [Tooltip("Pick a random outfit when this avatar appears (bots).")]
        [SerializeField] private bool randomOnStart;

        public event Action Changed;
        public IReadOnlyList<Slot> Slots => slots;

        private MaterialPropertyBlock block;

        private void Awake()
        {
            block = new MaterialPropertyBlock();
            foreach (var s in slots) s.colour = -1;
            if (randomOnStart) Randomize(false);
            else if (!Load()) ApplyAll();
        }

        // ---------- Changing clothes ----------

        public void Set(string slotName, int option, int colour = -1)
        {
            var s = Find(slotName);
            if (s == null || s.options.Length == 0) return;
            s.current = (option % s.options.Length + s.options.Length) % s.options.Length;
            s.colour = colour;
            Apply(s);
            UpdateBody();
            Save();
            Changed?.Invoke();
        }

        public void Next(string slotName, int step = 1)
        {
            var s = Find(slotName);
            if (s != null) Set(slotName, s.current + step);
        }

        /// <summary>Cycles through the colours of the current option: original, then each variant.</summary>
        public void NextColour(string slotName)
        {
            var s = Find(slotName);
            if (s == null || s.options.Length == 0) return;
            var o = s.options[s.current];
            int count = o.colours != null ? o.colours.Length : 0;
            if (count == 0) return;
            s.colour = s.colour + 1 >= count ? -1 : s.colour + 1;
            Apply(s);
            Save();
            Changed?.Invoke();
        }

        /// <summary>Save/load under this key from now on (e.g. one key per body the player can pick).</summary>
        public void UseSaveKey(string key)
        {
            saveKey = key;
            if (!Load()) ApplyAll();
        }

        public string Describe(string slotName)
        {
            var s = Find(slotName);
            if (s == null || s.options.Length == 0) return "-";
            return s.options[s.current].label;
        }

        public void Randomize(bool save = true)
        {
            foreach (var s in slots)
            {
                if (s.options.Length == 0) continue;
                s.current = UnityEngine.Random.Range(0, s.options.Length);
                var c = s.options[s.current].colours;
                s.colour = c != null && c.Length > 0 ? UnityEngine.Random.Range(-1, c.Length) : -1;
            }
            ApplyAll();
            if (save) Save();
            Changed?.Invoke();
        }

        public void ApplyAll()
        {
            foreach (var s in slots) Apply(s);
            UpdateBody();
        }

        private Mesh bodyMesh;
        private readonly List<int> visible = new List<int>();

        // Hide the body triangles that are under clothes that are currently worn.
        private void UpdateBody()
        {
            if (body == null || cover == null || cover.triangles == null) return;
            int worn = 0;
            for (int i = 0; i < coverParts.Length && i < 31; i++)
                if (coverParts[i] != null && coverParts[i].gameObject.activeSelf) worn |= 1 << i;
            if (bodyMesh == null)
            {
                bodyMesh = Instantiate(body.sharedMesh);   // own copy, so other avatars keep their own
                bodyMesh.name = body.sharedMesh.name + " (dressed)";
                body.sharedMesh = bodyMesh;
            }
            visible.Clear();
            int[] t = cover.triangles;
            for (int i = 0; i < cover.cover.Length; i++)
            {
                if ((cover.cover[i] & worn) != 0) continue;
                visible.Add(t[i * 3]); visible.Add(t[i * 3 + 1]); visible.Add(t[i * 3 + 2]);
            }
            bodyMesh.SetTriangles(visible, 0, false);
        }

        private void Apply(Slot s)
        {
            // Hide every option of the slot, then show the chosen one.
            for (int i = 0; i < s.options.Length; i++)
                foreach (var r in s.options[i].parts)
                    if (r != null) r.gameObject.SetActive(false);
            if (s.options.Length == 0) return;
            var o = s.options[s.current];
            bool tint = s.colour >= 0 && o.colours != null && s.colour < o.colours.Length;
            foreach (var r in o.parts)
            {
                if (r == null) continue;
                r.gameObject.SetActive(true);
                if (block == null) block = new MaterialPropertyBlock();
                r.GetPropertyBlock(block);
                if (tint) block.SetColor(BaseColorId, o.colours[s.colour]); else block.Clear();
                r.SetPropertyBlock(block);
            }
        }

        private Slot Find(string slotName)
        {
            foreach (var s in slots) if (s.name == slotName) return s;
            return null;
        }

        // ---------- Saving ----------

        private void Save()
        {
            if (string.IsNullOrEmpty(saveKey)) return;
            var parts = new List<string>();
            foreach (var s in slots) parts.Add($"{s.name}:{s.current}:{s.colour}");
            PlayerPrefs.SetString(saveKey, string.Join(";", parts));
            PlayerPrefs.Save();
        }

        private bool Load()
        {
            if (string.IsNullOrEmpty(saveKey) || !PlayerPrefs.HasKey(saveKey)) return false;
            foreach (string entry in PlayerPrefs.GetString(saveKey).Split(';'))
            {
                var p = entry.Split(':');
                if (p.Length != 3) continue;
                var s = Find(p[0]);
                if (s == null || s.options.Length == 0) continue;
                if (int.TryParse(p[1], out int o)) s.current = Mathf.Clamp(o, 0, s.options.Length - 1);
                if (int.TryParse(p[2], out int c)) s.colour = c;
            }
            ApplyAll();
            return true;
        }
    }
}
