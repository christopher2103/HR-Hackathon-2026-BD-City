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
    /// Hats: MakeHuman hats are fitted to a bald head, so hair pokes through them. While a hat is worn,
    /// the hair above the hat's brim (inside the crown and sticking through the brim) is hidden;
    /// hair below the brim stays, so it looks like it comes out from under the hat.
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
            UpdateHair();
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
            UpdateHair();
        }

        // ---------- Hair under hats ----------

        // Per (hair mesh, hat mesh): the hair triangles that stay visible under that hat. Same for every avatar
        // that uses these meshes, so it is computed once per pair for the whole session.
        private static readonly Dictionary<(Mesh, Mesh), int[]> trimmedHair = new Dictionary<(Mesh, Mesh), int[]>();
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> hairOriginal = new Dictionary<SkinnedMeshRenderer, Mesh>();
        private readonly Dictionary<SkinnedMeshRenderer, Mesh> hairCopy = new Dictionary<SkinnedMeshRenderer, Mesh>();

        private void UpdateHair()
        {
            Slot hair = Find("Hair"), hat = Find("Hat");
            if (hair == null) return;
            SkinnedMeshRenderer hatR = null;
            if (hat != null && hat.options.Length > 0)
                foreach (var r in hat.options[hat.current].parts)
                    if (r is SkinnedMeshRenderer smr && r.gameObject.activeSelf) { hatR = smr; break; }

            foreach (var o in hair.options)
                foreach (var r in o.parts)
                {
                    if (!(r is SkinnedMeshRenderer h)) continue;
                    if (!hairOriginal.TryGetValue(h, out Mesh original)) { original = h.sharedMesh; hairOriginal[h] = original; }
                    if (original == null) continue;
                    if (hatR == null || !h.gameObject.activeSelf)
                    {
                        if (h.sharedMesh != original) h.sharedMesh = original;   // no hat: full hair
                        continue;
                    }
                    Mesh hatMesh = hatR.sharedMesh;
                    if (!trimmedHair.TryGetValue((original, hatMesh), out int[] keep))
                    {
                        keep = TrimHair(h, original, hatR);
                        trimmedHair[(original, hatMesh)] = keep;
                    }
                    if (keep == null) { h.sharedMesh = original; continue; }
                    if (!hairCopy.TryGetValue(h, out Mesh copy))
                    {
                        copy = Instantiate(original);
                        copy.name = original.name + " (under hat)";
                        hairCopy[h] = copy;
                    }
                    copy.SetTriangles(keep, 0, false);
                    h.sharedMesh = copy;
                }
        }

        // The hat's brim plane is fitted through its outer ring of vertices; hair above that plane and within
        // the brim's reach is hidden (it would be inside the crown or poke through the brim).
        private static int[] TrimHair(SkinnedMeshRenderer hair, Mesh hairMesh, SkinnedMeshRenderer hat)
        {
            if (!hairMesh.isReadable || hat.sharedMesh == null || !hat.sharedMesh.isReadable) return null;
            var hm = new Mesh(); hat.BakeMesh(hm, true);
            var am = new Mesh(); hair.BakeMesh(am, true);
            Matrix4x4 hatM = Matrix4x4.TRS(hat.transform.position, hat.transform.rotation, Vector3.one);
            Matrix4x4 hairM = Matrix4x4.TRS(hair.transform.position, hair.transform.rotation, Vector3.one);
            Vector3[] hv = hm.vertices, av = am.vertices;
            Destroy(hm); Destroy(am);
            if (hv.Length < 10) return null;
            for (int i = 0; i < hv.Length; i++) hv[i] = hatM.MultiplyPoint3x4(hv[i]);
            for (int i = 0; i < av.Length; i++) av[i] = hairM.MultiplyPoint3x4(av[i]);

            // Centre (horizontal) and the outer ring = the brim.
            Vector3 c = Vector3.zero; foreach (var p in hv) c += p; c /= hv.Length;
            var radial = new float[hv.Length];
            for (int i = 0; i < hv.Length; i++) radial[i] = new Vector2(hv[i].x - c.x, hv[i].z - c.z).magnitude;
            var sorted = (float[])radial.Clone(); Array.Sort(sorted);
            float ringFrom = sorted[(int)(sorted.Length * 0.7f)], brimR = sorted[sorted.Length - 1];
            // Least squares plane y = a x + b z + d through the ring (handles a tilted "cocked" hat).
            double sxx = 0, sxz = 0, szz = 0, sx = 0, sz = 0, n = 0, sxy = 0, szy = 0, sy = 0;
            for (int i = 0; i < hv.Length; i++)
            {
                if (radial[i] < ringFrom) continue;
                double x = hv[i].x - c.x, z = hv[i].z - c.z, y = hv[i].y;
                sxx += x * x; sxz += x * z; szz += z * z; sx += x; sz += z; n++; sxy += x * y; szy += z * y; sy += y;
            }
            if (n < 6) return null;
            if (!Solve3(sxx, sxz, sx, sxz, szz, sz, sx, sz, n, sxy, szy, sy, out double a, out double b, out double d)) return null;
            float BrimY(Vector3 p) => (float)(a * (p.x - c.x) + b * (p.z - c.z) + d);

            int[] t = hairMesh.GetTriangles(0);
            var keep = new List<int>(t.Length);
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 p = (av[t[i]] + av[t[i + 1]] + av[t[i + 2]]) / 3f;
                float r = new Vector2(p.x - c.x, p.z - c.z).magnitude;
                bool hidden = p.y > BrimY(p) - 0.006f && r < brimR + 0.015f;
                if (!hidden) { keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]); }
            }
            return keep.ToArray();
        }

        // 3x3 linear system (Cramer's rule).
        private static bool Solve3(double a11, double a12, double a13, double a21, double a22, double a23,
                                   double a31, double a32, double a33, double b1, double b2, double b3,
                                   out double x, out double y, out double z)
        {
            double det = a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
            x = y = z = 0;
            if (Math.Abs(det) < 1e-12) return false;
            x = (b1 * (a22 * a33 - a23 * a32) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3)) / det;
            y = (a11 * (b2 * a33 - a23 * b3) - b1 * (a21 * a33 - a23 * a31) + a13 * (a21 * b3 - b2 * a31)) / det;
            z = (a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * (a21 * a32 - a22 * a31)) / det;
            return true;
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
