using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Street furniture around the spawn spots, generated in code (low-poly, X-CITE style, vertex coloured):
    /// parked bikes along the facades, benches by the trees, bins next to benches and lamps, and a few
    /// bollards along kerbs. Placement reads the flat BGT ground map under each point (pavement vs road vs grass)
    /// and checks the distance to walls. Everything is merged into "Street Props/Props_x_y" chunks with the
    /// shared FlatDetail material (1 draw call per chunk, no colliders, bots/teleport unaffected). Re-runnable.
    /// Needs the area's frozen instance in the open scene.
    /// </summary>
    public static class CityStreetProps
    {
        const float Radius = 110f;
        const float ChunkSize = 100f;
        const int MaxBikeGroups = 11, MaxBenches = 28, MaxBollards = 70;

        enum Surface { Walk, Road, Bike, Green, Water, Other, None }

        static readonly (Color c, Surface s)[] MapColours =
        {
            (new Color(0.80f, 0.78f, 0.74f), Surface.Walk), (new Color(0.83f, 0.81f, 0.77f), Surface.Walk),
            (new Color(0.78f, 0.77f, 0.74f), Surface.Walk), (new Color(0.76f, 0.72f, 0.63f), Surface.Walk),
            (new Color(0.47f, 0.48f, 0.50f), Surface.Road), (new Color(0.60f, 0.60f, 0.61f), Surface.Road),
            (new Color(0.62f, 0.62f, 0.62f), Surface.Road), (new Color(0.56f, 0.53f, 0.51f), Surface.Road),
            (new Color(0.71f, 0.45f, 0.41f), Surface.Bike),
            (new Color(0.58f, 0.74f, 0.44f), Surface.Green), (new Color(0.45f, 0.63f, 0.38f), Surface.Green),
            (new Color(0.42f, 0.60f, 0.76f), Surface.Water), (new Color(0.70f, 0.69f, 0.66f), Surface.Other),
        };

        static readonly Color Wood = new Color(0.60f, 0.43f, 0.29f), Metal = new Color(0.17f, 0.18f, 0.19f);
        static readonly Color BinGreen = new Color(0.17f, 0.28f, 0.23f), BinLid = new Color(0.55f, 0.57f, 0.56f);
        static readonly Color BollardCol = new Color(0.30f, 0.17f, 0.13f), Tyre = new Color(0.07f, 0.07f, 0.08f);
        static readonly Color[] BikeColours =
        {
            new Color(0.08f, 0.08f, 0.09f), new Color(0.08f, 0.08f, 0.09f), new Color(0.08f, 0.08f, 0.09f),
            new Color(0.15f, 0.22f, 0.38f), new Color(0.62f, 0.16f, 0.15f), new Color(0.55f, 0.75f, 0.68f),
            new Color(0.88f, 0.85f, 0.76f), new Color(0.45f, 0.47f, 0.50f), new Color(0.20f, 0.35f, 0.25f),
        };

        // ---------- geometry ----------
        class Geo
        {
            public readonly List<Vector3> v = new List<Vector3>(); public readonly List<Vector3> n = new List<Vector3>();
            public readonly List<Color32> c = new List<Color32>(); public readonly List<int> t = new List<int>();
            public Matrix4x4 m = Matrix4x4.identity;
            public int Tris => t.Count / 3;
            public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color col)
            {
                a = m.MultiplyPoint3x4(a); b = m.MultiplyPoint3x4(b); cc = m.MultiplyPoint3x4(cc); d = m.MultiplyPoint3x4(d);
                Vector3 nn = Vector3.Cross(b - a, cc - a).normalized;
                col = col.linear; // linear colour space project
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) { n.Add(nn); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            // Box between two points (local), square section, no bottom face.
            public void Beam(Vector3 p0, Vector3 p1, float w, float h, Color col)
            {
                Vector3 dir = p1 - p0; float len = dir.magnitude; if (len < 1e-4f) return;
                Quaternion q = Quaternion.LookRotation(dir / len, Mathf.Abs(Vector3.Dot(dir / len, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up);
                Box((p0 + p1) * 0.5f, new Vector3(w, h, len), q, col, true);
            }
            public void Box(Vector3 center, Vector3 size, Quaternion rot, Color col, bool bottom = false)
            {
                Vector3 hx = rot * new Vector3(size.x * 0.5f, 0, 0), hy = rot * new Vector3(0, size.y * 0.5f, 0), hz = rot * new Vector3(0, 0, size.z * 0.5f);
                Vector3 P(int x, int y, int z) => center + hx * x + hy * y + hz * z;
                // Clockwise seen from outside (Unity front faces).
                Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), col);      // +y
                Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), col);  // -x
                Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), col);      // +x
                Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), col);  // -z
                Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), col);      // +z
                if (bottom) Quad(P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), col);
            }
            public void Prism(Vector3 baseCenter, float r, float h, int sides, Color side, Color top)
            {
                for (int i = 0; i < sides; i++)
                {
                    float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                    Vector3 d0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * r, d1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * r;
                    Quad(baseCenter + d0, baseCenter + d0 + Vector3.up * h, baseCenter + d1 + Vector3.up * h, baseCenter + d1, side);
                }
                for (int i = 1; i < sides - 1; i++)
                {
                    Vector3 P(int k) => baseCenter + Vector3.up * h + new Vector3(Mathf.Cos(k * Mathf.PI * 2f / sides), 0, Mathf.Sin(k * Mathf.PI * 2f / sides)) * r;
                    AddTri(P(0), P(i + 1), P(i), top);
                }
            }
            public void AddTri(Vector3 a, Vector3 b, Vector3 cc, Color col)
            {
                a = m.MultiplyPoint3x4(a); b = m.MultiplyPoint3x4(b); cc = m.MultiplyPoint3x4(cc);
                Vector3 nn = Vector3.Cross(b - a, cc - a).normalized;
                col = col.linear;
                int i = v.Count; v.Add(a); v.Add(b); v.Add(cc);
                for (int k = 0; k < 3; k++) { n.Add(nn); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }
            // Thin ring in the local XY plane (a wheel), visible from both sides.
            public void Ring(Vector3 center, float r, float w, int seg, Color col)
            {
                for (int i = 0; i < seg; i++)
                {
                    float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                    Vector3 o0 = center + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * r, o1 = center + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * r;
                    Vector3 i0 = center + new Vector3(Mathf.Cos(a0), Mathf.Sin(a0), 0) * (r - w), i1 = center + new Vector3(Mathf.Cos(a1), Mathf.Sin(a1), 0) * (r - w);
                    Quad(o0, o1, i1, i0, col); Quad(i0, i1, o1, o0, col);
                }
            }
        }

        static void Bench(Geo g)
        {
            g.Box(new Vector3(0, 0.44f, 0.02f), new Vector3(1.8f, 0.06f, 0.44f), Quaternion.identity, Wood, true);
            g.Box(new Vector3(0, 0.74f, -0.2f), new Vector3(1.8f, 0.34f, 0.05f), Quaternion.Euler(-12f, 0, 0), Wood, true);
            foreach (float x in new[] { -0.72f, 0.72f })
            {
                g.Box(new Vector3(x, 0.21f, 0.02f), new Vector3(0.06f, 0.42f, 0.4f), Quaternion.identity, Metal);
                g.Beam(new Vector3(x, 0.44f, -0.18f), new Vector3(x, 0.9f, -0.24f), 0.05f, 0.05f, Metal);
            }
        }

        static void Bin(Geo g)
        {
            g.Prism(Vector3.zero, 0.24f, 0.82f, 6, BinGreen, BinGreen);
            g.Prism(new Vector3(0, 0.82f, 0), 0.26f, 0.08f, 6, BinLid, BinLid);
        }

        static void Bollard(Geo g)
        {
            g.Prism(Vector3.zero, 0.07f, 0.82f, 6, BollardCol, BollardCol);
            g.Prism(new Vector3(0, 0.82f, 0), 0.085f, 0.05f, 6, BollardCol, BollardCol);
        }

        // Dutch city bike, length along local +x (front), wheels in the XY plane.
        static void Bike(Geo g, Color frame)
        {
            float r = 0.34f;
            Vector3 rear = new Vector3(-0.52f, r, 0), front = new Vector3(0.53f, r, 0);
            Vector3 bb = new Vector3(-0.02f, 0.3f, 0), seat = new Vector3(-0.22f, 0.86f, 0), head = new Vector3(0.36f, 0.88f, 0);
            g.Ring(rear, r, 0.05f, 10, Tyre); g.Ring(front, r, 0.05f, 10, Tyre);
            g.Beam(rear, bb, 0.035f, 0.035f, frame); g.Beam(rear, seat, 0.03f, 0.03f, frame);
            g.Beam(bb, seat, 0.04f, 0.04f, frame); g.Beam(bb, head, 0.045f, 0.045f, frame);
            g.Beam(seat + new Vector3(0.04f, -0.08f, 0), head + new Vector3(0, -0.04f, 0), 0.04f, 0.04f, frame);
            g.Beam(head, front, 0.035f, 0.035f, frame);
            g.Beam(head, head + new Vector3(-0.06f, 0.14f, 0), 0.03f, 0.03f, Metal);
            g.Box(head + new Vector3(-0.1f, 0.15f, 0), new Vector3(0.05f, 0.03f, 0.56f), Quaternion.identity, Metal);
            g.Box(seat + new Vector3(0, 0.04f, 0), new Vector3(0.24f, 0.06f, 0.13f), Quaternion.identity, Metal);
            g.Box(new Vector3(-0.55f, 0.72f, 0), new Vector3(0.36f, 0.025f, 0.14f), Quaternion.identity, Metal); // rear carrier
            g.Box(new Vector3(-0.52f, 0.56f, 0), new Vector3(0.62f, 0.02f, 0.08f), Quaternion.Euler(0, 0, -6f), frame); // mudguard-ish
        }

        // ---------- placement ----------
        class MapCache { public Dictionary<Material, Texture2D> tex = new Dictionary<Material, Texture2D>(); }

        static bool Sample(Vector3 p, MapCache cache, out Surface s, out float y)
        {
            s = Surface.None; y = 0f;
            float best = float.MinValue; RaycastHit bestHit = default; bool building = false;
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, p.y + 60f, p.z), Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part == null) continue;
                if (!part.isGround) { if (h.point.y > best) building = true; continue; }
                if (h.point.y > best) { best = h.point.y; bestHit = h; building = false; }
            }
            if (best == float.MinValue) return false;
            // A building above the ground here?
            foreach (var h in Physics.RaycastAll(bestHit.point + Vector3.up * 0.2f, Vector3.up, 30f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part != null && !part.isGround) { s = Surface.Other; y = best; return true; }
            }
            y = best;
            var mr = bestHit.collider.GetComponent<MeshRenderer>();
            if (mr == null) { s = Surface.Other; return true; }
            Material mat = mr.sharedMaterials[0];
            if (!cache.tex.TryGetValue(mat, out var tex))
            {
                Texture t = mat.GetTexture("_BaseMap");
                string path = t != null ? AssetDatabase.GetAssetPath(t) : null;
                tex = null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) { tex = new Texture2D(2, 2); tex.LoadImage(File.ReadAllBytes(path)); }
                cache.tex[mat] = tex;
            }
            if (tex == null || Vector3.Dot(bestHit.normal, Vector3.up) < 0.9f) { s = Surface.Other; return true; }
            Vector2 uv = bestHit.textureCoord;
            Color col = tex.GetPixelBilinear(uv.x, uv.y);
            float bd = float.MaxValue;
            foreach (var (c, ss) in MapColours)
            {
                float d = Mathf.Abs(c.r - col.r) + Mathf.Abs(c.g - col.g) + Mathf.Abs(c.b - col.b);
                if (d < bd) { bd = d; s = ss; }
            }
            if (bd > 0.12f) s = Surface.Other;
            return true;
        }

        // Nearest wall (building) around p, at knee height.
        static bool NearestWall(Vector3 p, float maxDist, out float dist, out Vector3 normal)
        {
            dist = float.MaxValue; normal = Vector3.zero;
            for (int i = 0; i < 16; i++)
            {
                Vector3 d = Quaternion.Euler(0, i * 22.5f, 0) * Vector3.forward;
                foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.6f, d, maxDist, ~0, QueryTriggerInteraction.Ignore))
                {
                    var part = h.collider.GetComponentInParent<CityPart>();
                    if (part == null || part.isGround) continue;
                    if (h.distance < dist) { dist = h.distance; normal = h.normal; normal.y = 0; normal.Normalize(); }
                }
            }
            return dist < maxDist;
        }

        public static string BuildAll()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in CityFacades.HeroSpots()) sb.AppendLine(Build(kv.Key, kv.Value.ToArray()));
            return sb.ToString();
        }

        public static string Build(string prefabPath, Vector2[] heroes)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string area = Path.GetFileName(folder);
            GameObject instance = null;
            var frozenRoot = GameObject.Find("Frozen Neighbourhoods");
            var wasActive = new Dictionary<GameObject, bool>();
            foreach (Transform c in frozenRoot.transform)
            {
                wasActive[c.gameObject] = c.gameObject.activeSelf;
                if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(c.gameObject) == prefabPath) instance = c.gameObject;
            }
            if (instance == null) return $"{area}: no scene instance";
            foreach (Transform c in frozenRoot.transform) c.gameObject.SetActive(c.gameObject == instance);
            Transform props = instance.transform.Find("Props");
            bool propsActive = props.gameObject.activeSelf;

            // Existing trees / lamps (positions), then switch them off for the ray tests.
            var trees = new List<Vector3>(); var lamps = new List<Vector3>();
            foreach (Transform t in props.Find("Trees")) trees.Add(t.position);
            foreach (Transform t in props.Find("Lamps")) lamps.Add(t.position);
            props.gameObject.SetActive(false);
            Physics.SyncTransforms();

            var cache = new MapCache();
            var rng = new System.Random(area.GetHashCode());
            var chunks = new Dictionary<Vector2Int, Geo>();
            Geo ChunkAt(Vector3 p)
            {
                var k = new Vector2Int(Mathf.FloorToInt(p.x / ChunkSize), Mathf.FloorToInt(p.z / ChunkSize));
                if (!chunks.TryGetValue(k, out var g)) chunks[k] = g = new Geo();
                return g;
            }
            var used = new List<(Vector3 p, float r)>();
            bool Free(Vector3 p, float r)
            {
                foreach (var u in used) if ((new Vector2(u.p.x - p.x, u.p.z - p.z)).magnitude < u.r + r) return false;
                foreach (var t in trees) if ((new Vector2(t.x - p.x, t.z - p.z)).magnitude < 0.6f + r) return false;
                foreach (var l in lamps) if ((new Vector2(l.x - p.x, l.z - p.z)).magnitude < 0.3f + r) return false;
                return true;
            }
            float HeroDist(Vector3 p) { float d = float.MaxValue; foreach (var h in heroes) d = Mathf.Min(d, Vector2.Distance(h, new Vector2(p.x, p.z))); return d; }
            void Place(Vector3 pos, float yaw, System.Action<Geo> build)
            {
                var g = ChunkAt(pos);
                g.m = Matrix4x4.TRS(pos, Quaternion.Euler(0, yaw, 0), Vector3.one);
                build(g);
            }
            int bikes = 0, benches = 0, bins = 0, bollards = 0, groups = 0;
            try
            {
                // ---- 1. Parked bikes along facades (pavement 1.5-3 m from a wall) ----
                var cands = new List<(Vector3 p, Vector3 n, float d)>();
                foreach (var h in heroes)
                    for (float x = h.x - Radius; x <= h.x + Radius; x += 3f)
                        for (float z = h.y - Radius; z <= h.y + Radius; z += 3f)
                        {
                            var p = new Vector3(x, 0, z);
                            float hd = HeroDist(p);
                            if (hd > Radius || hd < 4f) continue;
                            if (!Sample(p + Vector3.up * 20f, cache, out var s, out float y) || s != Surface.Walk) continue;
                            p.y = y;
                            if (!NearestWall(p, 3.2f, out float wd, out Vector3 wn) || wd < 1.4f) continue;
                            cands.Add((p, wn, hd));
                        }
                cands.Sort((a, b) => a.d.CompareTo(b.d));
                foreach (var cnd in cands)
                {
                    if (groups >= MaxBikeGroups * heroes.Length) break;
                    if (rng.NextDouble() < 0.45) continue;
                    Vector3 along = Vector3.Cross(Vector3.up, cnd.n);
                    int count = 2 + rng.Next(4);   // 2-5 bikes per row (about half of before)
                    float len = count * 0.7f;
                    // The whole row must be on pavement and free.
                    bool ok = Free(cnd.p, len * 0.5f + 1.5f);
                    for (int i = 0; ok && i < count; i++)
                    {
                        Vector3 q = cnd.p + along * ((i - (count - 1) * 0.5f) * 0.7f);
                        ok = Sample(q + Vector3.up * 20f, cache, out var s2, out float y2) && s2 == Surface.Walk && Mathf.Abs(y2 - cnd.p.y) < 0.4f;
                    }
                    if (!ok) continue;
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 q = cnd.p + along * ((i - (count - 1) * 0.5f) * 0.7f);
                        Sample(q + Vector3.up * 20f, cache, out _, out float yq); q.y = yq;
                        // Bike front points at the wall (local +x = -n).
                        float yaw = Mathf.Atan2(-cnd.n.z, -cnd.n.x) * -Mathf.Rad2Deg + (float)(rng.NextDouble() * 16 - 8);
                        Color fc = BikeColours[rng.Next(BikeColours.Length)];
                        var g = ChunkAt(q);
                        g.m = Matrix4x4.TRS(q, Quaternion.Euler(0, yaw, 0), Vector3.one) * Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(rng.Next(2) == 0 ? 4f : -4f, 0, 0), Vector3.one);
                        Bike(g, fc);
                        bikes++;
                    }
                    used.Add((cnd.p, len * 0.5f + 1.2f));
                    groups++;
                }

                // ---- 2. Benches by the trees, back to the tree; bins next to some ----
                var treeOrder = new List<Vector3>(trees);
                treeOrder.Sort((a, b) => HeroDist(a).CompareTo(HeroDist(b)));
                foreach (var t in treeOrder)
                {
                    if (benches >= MaxBenches * heroes.Length) break;
                    float hd = HeroDist(t);
                    if (hd > Radius || hd < 3f || rng.NextDouble() > 0.3) continue;
                    float a = (float)rng.NextDouble() * 360f;
                    for (int tries = 0; tries < 4; tries++, a += 90f)
                    {
                        Vector3 dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
                        Vector3 p = t + dir * 2.0f;
                        if (!Sample(p + Vector3.up * 20f, cache, out var s, out float y) || (s != Surface.Walk && s != Surface.Green)) continue;
                        p.y = y;
                        if (NearestWall(p, 1.8f, out _, out _) || !Free(p, 0.9f)) continue;
                        // Front (local +z) faces away from the tree.
                        Place(p, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg, Bench);
                        used.Add((p, 1.1f)); benches++;
                        if (rng.NextDouble() < 0.4)
                        {
                            Vector3 side = Vector3.Cross(Vector3.up, dir) * 1.3f;
                            Vector3 bp = p + side;
                            if (Sample(bp + Vector3.up * 20f, cache, out var s3, out float y3) && (s3 == Surface.Walk || s3 == Surface.Green) && Free(bp, 0.35f))
                            { bp.y = y3; Place(bp, 0, Bin); used.Add((bp, 0.35f)); bins++; }
                        }
                        break;
                    }
                }

                // ---- 3. Bins by some lamp posts ----
                foreach (var l in lamps)
                {
                    float hd = HeroDist(l);
                    if (hd > Radius || hd < 3f || rng.NextDouble() > 0.18) continue;
                    Vector3 p = l + Quaternion.Euler(0, rng.Next(4) * 90f, 0) * Vector3.forward * 0.75f;
                    if (!Sample(p + Vector3.up * 20f, cache, out var s, out float y) || s != Surface.Walk || !Free(p, 0.3f)) continue;
                    p.y = y; Place(p, 0, Bin); used.Add((p, 0.35f)); bins++;
                }

                // ---- 4. Bollards along pavement edges next to the road, close to the spawn ----
                foreach (var h in heroes)
                {
                    int here = 0;
                    for (float x = h.x - 55f; x <= h.x + 55f && here < MaxBollards; x += 1.7f)
                        for (float z = h.y - 55f; z <= h.y + 55f && here < MaxBollards; z += 1.7f)
                        {
                            var p = new Vector3(x, 0, z);
                            float hd = Vector2.Distance(h, new Vector2(x, z));
                            if (hd > 55f || hd < 5f) continue;
                            if (!Sample(p + Vector3.up * 20f, cache, out var s, out float y) || s != Surface.Walk) continue;
                            bool edge = false;
                            foreach (var d in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                                if (Sample(p + d * 1.0f + Vector3.up * 20f, cache, out var s2, out _) && s2 == Surface.Road &&
                                    Sample(p - d * 1.2f + Vector3.up * 20f, cache, out var s3, out _) && s3 == Surface.Walk &&
                                    Sample(p - d * 2.6f + Vector3.up * 20f, cache, out var s4, out _) && s4 == Surface.Walk) { edge = true; break; } // real pavement, not a kerb line
                            if (!edge || rng.NextDouble() < 0.35 || !Free(p, 0.5f)) continue;
                            p.y = y; Place(p, 0, Bollard); used.Add((p, 0.8f)); bollards++; here++;
                        }
                }
            }
            finally
            {
                props.gameObject.SetActive(propsActive);
                foreach (var kv in wasActive) kv.Key.SetActive(kv.Value);
                foreach (var t in cache.tex.Values) if (t != null) Object.DestroyImmediate(t);
            }

            // ---- Save into the prefab ----
            foreach (string f in Directory.GetFiles($"{folder}/Meshes", "Props_*.asset")) AssetDatabase.DeleteAsset(f.Replace('\\', '/'));
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            Transform old = root.transform.Find("Street Props");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var group = new GameObject("Street Props").transform;
            group.SetParent(root.transform, false);
            group.SetSiblingIndex(root.transform.Find("Props").GetSiblingIndex() + 1);
            Material mat = CityFacades.DetailMaterial();
            int tris = 0;
            foreach (var kv in chunks)
            {
                var g = kv.Value; if (g.Tris == 0) continue;
                string name = $"Props_{kv.Key.x}_{kv.Key.y}";
                var mesh = new Mesh { name = name };
                mesh.indexFormat = g.v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(g.v); mesh.SetNormals(g.n); mesh.SetColors(g.c); mesh.SetTriangles(g.t, 0);
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, $"{folder}/Meshes/{name}.asset");
                var go = new GameObject(name);
                go.transform.SetParent(group, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.On;
                go.isStatic = true;
                tris += g.Tris;
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{area}: {bikes} bikes in {groups} groups, {benches} benches, {bins} bins, {bollards} bollards — {tris / 1000f:F1}k tris in {chunks.Count} chunks";
        }
    }
}
