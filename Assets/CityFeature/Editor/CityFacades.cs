using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using TMPro;
using UnityEngine.Rendering;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Real 3D facade detail around the spawn spots (replaces the old painted window shader):
    ///  - punched windows with a protruding frame, glass set back, reveals and a stone sill (full detail &lt; 80 m,
    ///    a lighter version up to the hero radius),
    ///  - towers: ribbon windows (spandrel ledge + glass band + fins) or a punched grid,
    ///  - Stadhuis: tall windows on high floors,
    ///  - street level: shopfronts (stall riser, glass, mullions, door, sign band) with awnings, or a dark plinth,
    ///  - a light coping / cornice on every flat wall top.
    /// All detail is vertex-coloured and merged per building chunk (Facades/Facade_x_y): 1 draw call per chunk.
    /// The building walls that got 3D detail are flagged (UV1.z = 2) so the shader leaves them plain.
    /// Re-runnable: removes the previous Facades first.
    /// Needs the area's frozen instance in the open scene (used for ray tests: street-facing, blocked by a neighbour).
    /// </summary>
    public static class CityFacades
    {
        public const float HeroRadius = 140f;
        const float FullDetailRadius = 70f;
        const float FinRadius = 110f;
        const string MaterialPath = "Assets/CityFeature/Materials/FlatDetail.mat";

        enum Kind { Tower, Brick, Plaster, Stadhuis, School }

        // Buildings that get the "School" look (Hogeschool Rotterdam, Wijnhaven 107): a point inside the footprint.
        static readonly Vector2[] SchoolPoints = { new Vector2(0f, 0f) };
        const string SchoolSignText = "WIJNHAVEN 107";
        static readonly List<(Vector3 pos, Quaternion rot)> pendingSigns = new List<(Vector3, Quaternion)>();

        class Building { public int comp; public Kind kind; public Color wall; public float minY, maxY; public Vector3 center, bmin = Vector3.positiveInfinity, bmax = Vector3.negativeInfinity; public int seed; public bool ribbon, glassy, school; }

        class Facet
        {
            public Building b; public Vector3 n, t; public float d;
            public float u0 = float.MaxValue, u1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            public List<Vector2> tri = new List<Vector2>(); public List<int> verts = new List<int>(); public bool entrance;
            public Vector3 P(float u, float y, float z) => t * u + Vector3.up * y + n * (d + z);
            public bool Inside(float u, float y)
            {
                for (int i = 0; i < tri.Count; i += 3)
                {
                    Vector2 a = tri[i], b = tri[i + 1], c = tri[i + 2];
                    float d1 = Sign(u, y, a, b), d2 = Sign(u, y, b, c), d3 = Sign(u, y, c, a);
                    bool neg = d1 < -1e-4f || d2 < -1e-4f || d3 < -1e-4f, pos = d1 > 1e-4f || d2 > 1e-4f || d3 > 1e-4f;
                    if (!(neg && pos)) return true;
                }
                return false;
            }
            static float Sign(float x, float y, Vector2 a, Vector2 b) => (x - b.x) * (a.y - b.y) - (a.x - b.x) * (y - b.y);
            public bool RectInside(float ua, float ub, float ya, float yb)
            {
                float um = (ua + ub) * 0.5f, ym = (ya + yb) * 0.5f;
                return Inside(ua, ya) && Inside(ub, ya) && Inside(ua, yb) && Inside(ub, yb) &&
                       Inside(um, ya) && Inside(um, yb) && Inside(ua, ym) && Inside(ub, ym);
            }
        }

        // ---------- geometry builder ----------
        class Geo
        {
            public readonly List<Vector3> v = new List<Vector3>(); public readonly List<Vector3> n = new List<Vector3>();
            public readonly List<Color32> c = new List<Color32>(); public readonly List<int> t = new List<int>();
            public int Tris => t.Count / 3;
            public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Vector3 normal, Color ca, Color cb)
            {
                ca = ca.linear; cb = cb.linear; // the project renders in linear space; palette colours are sRGB
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) n.Add(normal);
                c.Add(ca); c.Add(ca); c.Add(cb); c.Add(cb);
                bool flip = Vector3.Dot(Vector3.Cross(b - a, cc - a), normal) < 0f;
                if (!flip) { t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3); }
                else { t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2); }
            }
            public void Tri(Vector3 a, Vector3 b, Vector3 cc, Vector3 normal, Color col)
            {
                col = col.linear;
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc);
                for (int k = 0; k < 3; k++) { n.Add(normal); c.Add(col); }
                if (Vector3.Dot(Vector3.Cross(b - a, cc - a), normal) >= 0f) { t.Add(i); t.Add(i + 1); t.Add(i + 2); }
                else { t.Add(i); t.Add(i + 2); t.Add(i + 1); }
            }
        }

        const int Front = 1, Top = 2, Bottom = 4, Left = 8, Right = 16;

        // Axis-aligned box in facet space (u along the wall, y up, z out of the wall).
        static void Box(Geo g, Facet f, float u0, float u1, float y0, float y1, float z0, float z1, int faces, Color col)
        {
            Vector3 P(float u, float y, float z) => f.P(u, y, z);
            if ((faces & Front) != 0) g.Quad(P(u0, y0, z1), P(u1, y0, z1), P(u1, y1, z1), P(u0, y1, z1), f.n, col, col);
            if ((faces & Top) != 0) g.Quad(P(u0, y1, z0), P(u1, y1, z0), P(u1, y1, z1), P(u0, y1, z1), Vector3.up, col, col);
            if ((faces & Bottom) != 0) g.Quad(P(u0, y0, z0), P(u1, y0, z0), P(u1, y0, z1), P(u0, y0, z1), Vector3.down, col, col);
            if ((faces & Left) != 0) g.Quad(P(u0, y0, z0), P(u0, y0, z1), P(u0, y1, z1), P(u0, y1, z0), -f.t, col, col);
            if ((faces & Right) != 0) g.Quad(P(u1, y0, z0), P(u1, y0, z1), P(u1, y1, z1), P(u1, y1, z0), f.t, col, col);
        }

        static void Glass(Geo g, Facet f, float u0, float u1, float y0, float y1, float z, Color bottom, Color top)
        {
            g.Quad(f.P(u0, y0, z), f.P(u1, y0, z), f.P(u1, y1, z), f.P(u0, y1, z), f.n, bottom, top);
        }

        static void Window(Geo g, Facet f, float u0, float u1, float y0, float y1, bool full, Color frame, Color sill, Color gb, Color gt)
        {
            const float fw = 0.11f, depth = 0.13f, gz = 0.015f;
            Glass(g, f, u0, u1, y0, y1, gz, gb, gt);
            float d = full ? depth : 0.06f;
            // Frame ring (front faces).
            Box(g, f, u0 - fw, u1 + fw, y1, y1 + fw, 0, d, Front, frame);
            Box(g, f, u0 - fw, u1 + fw, y0 - fw, y0, 0, d, Front, frame);
            Box(g, f, u0 - fw, u0, y0, y1, 0, d, Front, frame);
            Box(g, f, u1, u1 + fw, y0, y1, 0, d, Front, frame);
            float sl = 0.06f;
            if (full)
            {
                // Outer sides and the reveals (the depth you see in stereo).
                g.Quad(f.P(u0 - fw, y0 - fw, 0), f.P(u0 - fw, y0 - fw, d), f.P(u0 - fw, y1 + fw, d), f.P(u0 - fw, y1 + fw, 0), -f.t, frame, frame);
                g.Quad(f.P(u1 + fw, y0 - fw, 0), f.P(u1 + fw, y0 - fw, d), f.P(u1 + fw, y1 + fw, d), f.P(u1 + fw, y1 + fw, 0), f.t, frame, frame);
                g.Quad(f.P(u0 - fw, y1 + fw, 0), f.P(u1 + fw, y1 + fw, 0), f.P(u1 + fw, y1 + fw, d), f.P(u0 - fw, y1 + fw, d), Vector3.up, frame, frame);
                Color rev = frame * 0.9f; rev.a = 1f;
                g.Quad(f.P(u0, y0, gz), f.P(u0, y0, d), f.P(u0, y1, d), f.P(u0, y1, gz), f.t, rev, rev);
                g.Quad(f.P(u1, y0, gz), f.P(u1, y0, d), f.P(u1, y1, d), f.P(u1, y1, gz), -f.t, rev, rev);
                g.Quad(f.P(u0, y1, gz), f.P(u1, y1, gz), f.P(u1, y1, d), f.P(u0, y1, d), Vector3.down, rev, rev);
                g.Quad(f.P(u0, y0, gz), f.P(u1, y0, gz), f.P(u1, y0, d), f.P(u0, y0, d), Vector3.up, rev, rev);
                Box(g, f, u0 - fw - sl, u1 + fw + sl, y0 - fw - 0.08f, y0 - fw, 0, 0.2f, Front | Top | Bottom | Left | Right, sill);
            }
            else Box(g, f, u0 - fw - sl, u1 + fw + sl, y0 - fw - 0.08f, y0 - fw, 0, 0.14f, Front, sill);
        }

        // ---------- palette ----------
        static readonly Color FrameCream = new Color(0.93f, 0.91f, 0.86f), FrameDark = new Color(0.24f, 0.25f, 0.27f);
        static readonly Color Stone = new Color(0.80f, 0.78f, 0.73f), Riser = new Color(0.26f, 0.26f, 0.27f);
        static Color G(float r, float g, float b, float a) => new Color(r, g, b, a);
        static readonly Color ResGlassB = G(0.10f, 0.13f, 0.17f, 0.88f), ResGlassT = G(0.26f, 0.33f, 0.42f, 0.45f);
        static readonly Color OffGlassB = G(0.18f, 0.25f, 0.33f, 0.80f), OffGlassT = G(0.40f, 0.50f, 0.60f, 0.35f);
        static readonly Color ShopGlassB = G(0.17f, 0.16f, 0.14f, 0.93f), ShopGlassT = G(0.26f, 0.30f, 0.34f, 0.62f);
        static readonly Color DoorGlassB = G(0.10f, 0.11f, 0.12f, 0.95f), DoorGlassT = G(0.22f, 0.26f, 0.30f, 0.70f);
        static readonly Color[] ShopFrames = { new Color(0.18f, 0.19f, 0.20f), new Color(0.12f, 0.24f, 0.20f), new Color(0.32f, 0.13f, 0.13f), new Color(0.88f, 0.86f, 0.80f), new Color(0.20f, 0.22f, 0.30f) };
        static readonly Color[] Signs = { new Color(0.16f, 0.33f, 0.30f), new Color(0.70f, 0.22f, 0.18f), new Color(0.93f, 0.75f, 0.30f), new Color(0.20f, 0.24f, 0.40f), new Color(0.92f, 0.90f, 0.85f), new Color(0.13f, 0.13f, 0.14f), new Color(0.85f, 0.45f, 0.55f) };
        static readonly Color[] Awnings = { new Color(0.20f, 0.38f, 0.30f), new Color(0.62f, 0.18f, 0.18f), new Color(0.20f, 0.26f, 0.42f), new Color(0.86f, 0.62f, 0.26f), new Color(0.80f, 0.42f, 0.30f), new Color(0.90f, 0.88f, 0.80f), new Color(0.15f, 0.15f, 0.16f) };

        static Color Lighten(Color c, float k) { var r = Color.Lerp(c, Color.white, k); r.a = 1f; return r; }
        static Color Darken(Color c, float k) { var r = c * k; r.a = 1f; return r; }

        // ---------- entry points ----------
        public static string BuildAll()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in HeroSpots()) sb.AppendLine(Build(kv.Key, kv.Value.ToArray()));
            return sb.ToString();
        }

        /// <summary>Prefab path -> spawn spots (XZ) that use it (Lijnbaan also hosts Stadhuis).</summary>
        public static Dictionary<string, List<Vector2>> HeroSpots()
        {
            var result = new Dictionary<string, List<Vector2>>();
            var tp = Object.FindAnyObjectByType<NeighbourhoodTeleporter>(FindObjectsInactive.Include);
            foreach (var nb in tp.Neighbourhoods)
            {
                if (nb.frozen == null || nb.spot == null) continue;
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(nb.frozen);
                if (!result.TryGetValue(path, out var list)) result[path] = list = new List<Vector2>();
                list.Add(new Vector2(nb.spot.position.x, nb.spot.position.z));
            }
            return result;
        }

        public static string Build(string prefabPath, Vector2[] heroes)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string area = Path.GetFileName(folder);

            // Scene instance for ray tests: only this area on, without trees/lamps.
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
            bool propsActive = props != null && props.gameObject.activeSelf;
            if (props != null) props.gameObject.SetActive(false);
            Transform oldFacades = instance.transform.Find("Facades");
            if (oldFacades != null) oldFacades.gameObject.SetActive(false);
            Physics.SyncTransforms();

            pendingSigns.Clear();
            var stats = new Dictionary<string, int>();
            void Count(string k, int v) => stats[k] = (stats.TryGetValue(k, out int x) ? x : 0) + v;
            int totalTris = 0, chunks = 0;
            try
            {
                Material mat = DetailMaterial();
                // Old facade meshes of this area.
                foreach (string f in Directory.GetFiles($"{folder}/Meshes", "Facade_*.asset")) AssetDatabase.DeleteAsset(f.Replace('\\', '/'));

                var palTex = new Texture2D(2, 2);
                palTex.LoadImage(File.ReadAllBytes($"{folder}/Textures/BuildingPalette.png"));
                int ps = palTex.width;
                Color Pal(Vector2 uv) => palTex.GetPixel(Mathf.FloorToInt(uv.x * ps), Mathf.FloorToInt(uv.y * ps));

                var root = PrefabUtility.LoadPrefabContents(prefabPath);
                Transform old = root.transform.Find("Facades");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                var facadesRoot = new GameObject("Facades").transform;
                facadesRoot.SetParent(root.transform, false);
                facadesRoot.SetSiblingIndex(root.transform.Find("Buildings").GetSiblingIndex() + 1);

                foreach (MeshFilter mf in root.transform.Find("Buildings").GetComponentsInChildren<MeshFilter>(true))
                {
                    if (!mf.name.StartsWith("Buildings_")) continue;
                    Mesh mesh = mf.sharedMesh;
                    Vector3[] v = mesh.vertices; int[] t = mesh.triangles; Vector2[] uv = mesh.uv;
                    var uv1 = new List<Vector3>(); mesh.GetUVs(1, uv1);
                    if (uv1.Count != v.Length) { uv1.Clear(); for (int i = 0; i < v.Length; i++) uv1.Add(Vector3.zero); }
                    for (int i = 0; i < uv1.Count; i++) if (uv1[i].z > 1.5f) uv1[i] = new Vector3(uv1[i].x, uv1[i].y, 1f);

                    // Is this chunk near a hero spot at all?
                    Bounds bb = mesh.bounds;
                    float chunkDist = float.MaxValue;
                    foreach (var h in heroes) chunkDist = Mathf.Min(chunkDist, Vector2.Distance(h, new Vector2(Mathf.Clamp(h.x, bb.min.x, bb.max.x), Mathf.Clamp(h.y, bb.min.z, bb.max.z))));

                    var geo = new Geo();
                    if (chunkDist < HeroRadius)
                    {
                        var comp = Components(v, t);
                        var blds = new Dictionary<int, Building>();
                        for (int i = 0; i < v.Length; i++)
                        {
                            if (!blds.TryGetValue(comp[i], out var b)) blds[comp[i]] = b = new Building { comp = comp[i], minY = v[i].y, maxY = v[i].y };
                            b.minY = Mathf.Min(b.minY, v[i].y); b.maxY = Mathf.Max(b.maxY, v[i].y); b.center += v[i]; b.bmin = Vector3.Min(b.bmin, v[i]); b.bmax = Vector3.Max(b.bmax, v[i]);
                        }
                        var counts = new Dictionary<int, int>();
                        foreach (int c in comp) counts[c] = counts.TryGetValue(c, out int x) ? x + 1 : 1;
                        foreach (var b in blds.Values) b.center /= counts[b.comp];

                        // Facets.
                        var facets = new Dictionary<(int, int, int, int), Facet>();
                        for (int i = 0; i < t.Length; i += 3)
                        {
                            Vector3 a = v[t[i]], b2 = v[t[i + 1]], c2 = v[t[i + 2]];
                            Vector3 cr = Vector3.Cross(b2 - a, c2 - a);
                            if (cr.sqrMagnitude < 1e-6f) continue;
                            Vector3 nn = cr.normalized;
                            if (Mathf.Abs(nn.y) > 0.1f) continue;
                            Vector3 nh = new Vector3(nn.x, 0f, nn.z).normalized;
                            float dd = nh.x * a.x + nh.z * a.z;
                            var key = (comp[t[i]], Mathf.RoundToInt(nh.x * 30f), Mathf.RoundToInt(nh.z * 30f), Mathf.RoundToInt(dd * 3f));
                            var bld = blds[comp[t[i]]];
                            if (!facets.TryGetValue(key, out var f))
                            {
                                facets[key] = f = new Facet { b = bld, n = nh, t = new Vector3(nh.z, 0f, -nh.x), d = dd };
                                if (bld.wall == default) { Color pc = Pal(uv[t[i]]); bld.wall = pc; }
                            }
                            foreach (int k in new[] { t[i], t[i + 1], t[i + 2] })
                            {
                                float u = Vector3.Dot(v[k], f.t);
                                f.tri.Add(new Vector2(u, v[k].y)); f.verts.Add(k);
                                f.u0 = Mathf.Min(f.u0, u); f.u1 = Mathf.Max(f.u1, u); f.y0 = Mathf.Min(f.y0, v[k].y); f.y1 = Mathf.Max(f.y1, v[k].y);
                            }
                        }
                        foreach (var b in blds.Values) Classify(b);
                        foreach (var sp in SchoolPoints)
                        {
                            Building best = null; float bestArea = float.MaxValue;
                            foreach (var b in blds.Values)
                            {
                                if (sp.x < b.bmin.x || sp.x > b.bmax.x || sp.y < b.bmin.z || sp.y > b.bmax.z) continue;
                                float ar = (b.bmax.x - b.bmin.x) * (b.bmax.z - b.bmin.z);
                                if (ar < bestArea) { bestArea = ar; best = b; }
                            }
                            if (best == null) continue;
                            best.school = true; best.kind = Kind.School;
                            // Main entrance: the widest wall that faces the spawn spot.
                            Facet main = null;
                            foreach (var f in facets.Values)
                            {
                                if (f.b != best) continue;
                                Vector3 mid = f.P((f.u0 + f.u1) * 0.5f, f.y0, 0f);
                                Vector3 toHero = new Vector3(heroes[0].x - mid.x, 0, heroes[0].y - mid.z).normalized;
                                if (Vector3.Dot(toHero, f.n) < 0.5f) continue;
                                if (main == null || f.u1 - f.u0 > main.u1 - main.u0) main = f;
                            }
                            if (main != null) main.entrance = true;
                        }

                        foreach (var f in facets.Values)
                        {
                            if (f.b.maxY - f.b.minY < 3.5f || f.u1 - f.u0 < 2f || f.y1 - f.y0 < 2.2f) continue;
                            Vector3 mid = f.P((f.u0 + f.u1) * 0.5f, f.y0, 0f);
                            float dist = float.MaxValue;
                            foreach (var h in heroes) dist = Mathf.Min(dist, Vector2.Distance(h, new Vector2(mid.x, mid.z)));
                            if (dist > HeroRadius) continue;
                            int before = geo.Tris;
                            DetailFacet(geo, f, dist, Count);
                            if (geo.Tris > before) foreach (int k in f.verts) uv1[k] = new Vector3(uv1[k].x, uv1[k].y, 2f);
                        }
                    }
                    mesh.SetUVs(1, uv1);
                    EditorUtility.SetDirty(mesh);
                    if (geo.Tris == 0) continue;

                    string name = mf.name.Replace("Buildings_", "Facade_");
                    var fm = new Mesh { name = name };
                    fm.indexFormat = geo.v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                    fm.SetVertices(geo.v); fm.SetNormals(geo.n); fm.SetColors(geo.c); fm.SetTriangles(geo.t, 0);
                    fm.RecalculateBounds();
                    AssetDatabase.CreateAsset(fm, $"{folder}/Meshes/{name}.asset");
                    var go = new GameObject(name);
                    go.transform.SetParent(facadesRoot, false);
                    go.AddComponent<MeshFilter>().sharedMesh = fm;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = ShadowCastingMode.On;
                    go.isStatic = true;
                    totalTris += geo.Tris; chunks++;
                }
                Object.DestroyImmediate(palTex);
                foreach (var (pos, rot) in pendingSigns)
                {
                    var sign = new GameObject("School Sign");
                    sign.transform.SetParent(facadesRoot, false);
                    sign.transform.SetPositionAndRotation(pos, rot);
                    var tgo = new GameObject("Name");
                    tgo.transform.SetParent(sign.transform, false);
                    tgo.transform.localPosition = new Vector3(0.5f, 0f, 0f);
                    var tmp = tgo.AddComponent<TextMeshPro>();
                    tmp.text = "<b>" + SchoolSignText + "</b>";
                    tmp.fontSize = 4.2f; tmp.color = new Color(0.12f, 0.13f, 0.16f);
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.rectTransform.sizeDelta = new Vector2(6f, 0.7f);
                    var lq = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.DestroyImmediate(lq.GetComponent<Collider>());
                    lq.name = "Logo Panel";
                    lq.transform.SetParent(sign.transform, false);
                    lq.transform.localPosition = new Vector3(-3.2f, 0f, 0f);
                    lq.transform.localScale = new Vector3(0.65f, 0.65f, 1f);
                    lq.GetComponent<MeshRenderer>().sharedMaterial = SchoolLogoMaterial();
                    lq.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                    Count("school signs", 1);
                }
                pendingSigns.Clear();
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                PrefabUtility.UnloadPrefabContents(root);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                if (props != null) props.gameObject.SetActive(propsActive);
                foreach (var kv in wasActive) kv.Key.SetActive(kv.Value);
            }
            var sb = new System.Text.StringBuilder($"{area}: {totalTris / 1000f:F0}k tris in {chunks} facade chunks —");
            foreach (var kv in stats) sb.Append($" {kv.Key} {kv.Value}");
            return sb.ToString();
        }

        static void Classify(Building b)
        {
            float h = b.maxY - b.minY;
            Color w = b.wall;
            b.seed = Mathf.RoundToInt(b.center.x * 7.3f) * 7919 + Mathf.RoundToInt(b.center.z * 3.1f);
            var rng = new System.Random(b.seed);
            if (w.a > 0.25f && w.a < 0.75f) b.kind = Kind.Stadhuis;
            else if (h >= 28f) b.kind = Kind.Tower;
            else if (w.r - w.b > 0.12f) b.kind = Kind.Brick;
            else b.kind = Kind.Plaster;
            b.glassy = w.b > w.r + 0.04f;
            b.ribbon = b.kind == Kind.Tower && (b.glassy || rng.NextDouble() < 0.55);
            b.wall.a = 1f;
        }

        // ---------- one facet ----------
        static void DetailFacet(Geo g, Facet f, float dist, System.Action<string, int> count)
        {
            Building b = f.b;
            bool full = dist < FullDetailRadius;
            var rng = new System.Random(b.seed ^ Mathf.RoundToInt(f.d * 13f) ^ Mathf.RoundToInt(f.n.x * 1000f));
            float umid = (f.u0 + f.u1) * 0.5f;

            // Street level under this facet.
            float ground = f.y0;
            Vector3 probe = f.P(umid, f.y0 + 4f, 1.2f);
            foreach (var hit in Physics.RaycastAll(probe, Vector3.down, 14f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = hit.collider.GetComponentInParent<CityPart>();
                if (part != null && part.isGround) { ground = Mathf.Max(ground, hit.point.y); }
            }
            bool atStreet = f.y0 <= ground + 0.6f;
            float floorH = b.kind == Kind.Tower ? 3.4f : b.kind == Kind.Stadhuis ? 4.6f : b.kind == Kind.Brick ? 3.0f : 3.2f;

            // ---- street level ----
            float upperStart = ground + 0.9f; // first window sill
            if (atStreet && b.kind == Kind.School)
            {
                upperStart = ground + 4.2f + 0.9f;
                SchoolPlinth(g, f, ground, full);
                count("school plinths", 1);
            }
            else if (atStreet && b.kind != Kind.Stadhuis && b.maxY - ground > 5.5f)
            {
                float gf = b.kind == Kind.Tower ? 4.6f : 4.0f;
                int shops = Shopfronts(g, f, ground, gf, b.kind != Kind.Tower, full, rng);
                if (shops > 0) { count("shopfronts", shops); upperStart = ground + gf + 0.9f; }
                else Box(g, f, f.u0, f.u1, ground - 0.2f, ground + 0.55f, 0f, 0.05f, Front | Top, Darken(b.wall, 0.72f)); // plinth
            }
            else if (atStreet && b.kind == Kind.Stadhuis)
                Box(g, f, f.u0, f.u1, ground - 0.2f, ground + 1.1f, 0f, 0.12f, Front | Top, Darken(b.wall, 0.8f));

            // ---- upper floors ----
            if (b.kind == Kind.Tower && b.ribbon)
                count("ribbon floors", Ribbons(g, f, upperStart - 0.9f, floorH, dist < FinRadius, b));
            else
            {
                float winW, winH, cell;
                Color frame, gb, gt;
                switch (b.kind)
                {
                    case Kind.Tower: winW = 1.7f; winH = 2.0f; cell = 2.8f; frame = rng.NextDouble() < 0.5 ? FrameDark : Lighten(b.wall, 0.35f); gb = OffGlassB; gt = OffGlassT; break;
                    case Kind.School: winW = 2.4f; winH = 1.7f; cell = 3.0f; frame = FrameDark; gb = OffGlassB; gt = OffGlassT; floorH = 3.4f; break;
                    case Kind.Stadhuis: winW = 1.3f; winH = 2.8f; cell = 2.4f; frame = FrameCream; gb = ResGlassB; gt = ResGlassT; break;
                    case Kind.Brick: winW = 1.2f; winH = 1.8f; cell = 2.6f; frame = FrameCream; gb = ResGlassB; gt = ResGlassT; break;
                    default: winW = 1.6f; winH = 1.5f; cell = 3.0f; frame = rng.NextDouble() < 0.6 ? FrameDark : FrameCream; gb = ResGlassB; gt = ResGlassT; break;
                }
                if (b.kind == Kind.Stadhuis) upperStart = ground + 1.6f;
                Color sillCol = b.kind == Kind.Plaster || b.kind == Kind.School ? Darken(frame, 0.95f) : Stone;
                int windows = 0;
                float usable = f.u1 - f.u0 - 1.2f;
                int cols = Mathf.FloorToInt(usable / cell);
                if (cols < 1 && usable >= winW + 0.3f) cols = 1;
                float startU = umid - cols * cell * 0.5f + cell * 0.5f;
                for (float y = upperStart; y + winH + 0.5f <= f.y1; y += floorH)
                {
                    if (y + winH < f.y0) continue;
                    for (int ci = 0; ci < cols; ci++)
                    {
                        float uc = startU + ci * cell;
                        float a = uc - winW * 0.5f, bU = uc + winW * 0.5f;
                        if (!f.RectInside(a - 0.25f, bU + 0.25f, y - 0.3f, y + winH + 0.2f)) continue;
                        if (Blocked(f, uc, y + winH * 0.5f)) continue;
                        Window(g, f, a, bU, y, y + winH, full, frame, sillCol, gb, gt);
                        windows++;
                    }
                }
                count(full ? "windows (full)" : "windows (lite)", windows);
            }

            // ---- coping on a flat wall top ----
            float top = f.y1;
            if (f.Inside(f.u0 + 0.15f, top - 0.1f) && f.Inside(f.u1 - 0.15f, top - 0.1f) && f.Inside(umid, top - 0.1f))
            {
                Color cap = b.kind == Kind.Brick ? Stone : Lighten(b.wall, 0.4f);
                Box(g, f, f.u0 - 0.06f, f.u1 + 0.06f, top - 0.35f, top + 0.06f, 0f, 0.15f, Front | Top | Bottom | Left | Right, cap);
                count("copings", 1);
            }
        }

        // Hogeschool look at street level: a continuous glazed plinth with aluminium mullions; on the main wall
        // a glazed entrance with a deep canopy and a name band above it (text + logo panel added in Build).
        static void SchoolPlinth(Geo g, Facet f, float ground, bool full)
        {
            Color alu = FrameDark, stone = new Color(0.85f, 0.84f, 0.80f);
            float gTop = ground + 3.7f, umid = (f.u0 + f.u1) * 0.5f;
            float ea = umid - 2.6f, eb = umid + 2.6f;          // entrance doors
            int mfaces = full ? Front | Left | Right : Front;
            for (float u = f.u0 + 0.4f; u + 1.6f <= f.u1 - 0.4f; u += 1.8f)
            {
                float ua = u, ub = u + 1.8f;
                if (f.entrance && ub > ea - 0.4f && ua < eb + 0.4f) continue;   // the entrance goes here
                if (!f.RectInside(ua, ub, ground + 0.05f, gTop + 0.3f) || Blocked(f, (ua + ub) * 0.5f, ground + 1.6f)) continue;
                Box(g, f, ua, ub, ground - 0.1f, ground + 0.35f, 0f, 0.08f, Front | Top, stone);
                Glass(g, f, ua, ub, ground + 0.35f, gTop, 0.02f, OffGlassB, OffGlassT);
                Box(g, f, ub - 0.05f, ub + 0.05f, ground + 0.35f, gTop, 0f, 0.1f, mfaces, alu);
                Box(g, f, ua, ub, gTop, gTop + 0.12f, 0f, 0.1f, Front | Bottom, alu);
            }
            if (!f.entrance || !f.RectInside(ea - 0.5f, eb + 0.5f, ground + 0.05f, ground + 5f)) { f.entrance = false; return; }
            // Entrance: two double glass doors, a glazed panel above, canopy and a white name band.
            Glass(g, f, ea, eb, ground + 0.02f, ground + 2.6f, 0.03f, DoorGlassB, DoorGlassT);
            Glass(g, f, ea, eb, ground + 2.7f, gTop, 0.02f, OffGlassB, OffGlassT);
            foreach (float bu in new[] { ea, umid - 1.3f, umid, umid + 1.3f, eb })
                Box(g, f, bu - 0.06f, bu + 0.06f, ground, gTop, 0f, 0.12f, Front | Left | Right, alu);
            Box(g, f, ea, eb, ground + 2.6f, ground + 2.7f, 0f, 0.12f, Front | Bottom, alu);
            Box(g, f, ea - 1.2f, eb + 1.2f, gTop + 0.05f, gTop + 0.35f, 0f, 2.4f, Front | Top | Bottom | Left | Right, stone);
            // Name board standing on the front edge of the canopy (a band on the wall would hide behind the canopy).
            Box(g, f, ea - 1.4f, eb + 1.4f, gTop + 0.3f, gTop + 1.2f, 2.22f, 2.4f, Front | Top | Bottom | Left | Right, new Color(0.96f, 0.96f, 0.94f));
            Vector3 signPos = f.P(umid, gTop + 0.75f, 2.41f);
            pendingSigns.Add((signPos, Quaternion.LookRotation(-f.n)));   // TMP reads towards -z: look into the wall
        }

        static int Shopfronts(Geo g, Facet f, float ground, float gf, bool awnings, bool full, System.Random rng)
        {
            int made = 0;
            float u = f.u0 + 0.3f;
            while (u < f.u1 - 3f)
            {
                float w = Mathf.Min(5.5f + (float)rng.NextDouble() * 3f, f.u1 - 0.3f - u);
                if (w < 3f) break;
                float ua = u, ub = u + w; u = ub;
                float uc = (ua + ub) * 0.5f;
                if (!f.RectInside(ua, ub, ground + 0.05f, ground + gf)) continue;
                if (!StreetFacing(f, uc, ground + 1.6f) || Blocked(f, uc, ground + 1.6f)) continue;

                Color frame = ShopFrames[rng.Next(ShopFrames.Length)];
                Color sign = Signs[rng.Next(Signs.Length)];
                float gTop = ground + gf - 1.05f;          // top of the glass
                // Stall riser, glass, door, mullions, transom.
                Box(g, f, ua, ub, ground - 0.1f, ground + 0.45f, 0f, 0.08f, Front | Top, Riser);
                bool doorLeft = rng.NextDouble() < 0.5;
                float da = doorLeft ? ua + 0.25f : ub - 1.35f, db = da + 1.1f;
                Glass(g, f, ua + 0.06f, ub - 0.06f, ground + 0.45f, gTop, 0.02f, ShopGlassB, ShopGlassT);
                Glass(g, f, da, db, ground + 0.02f, ground + 2.35f, 0.03f, DoorGlassB, DoorGlassT);
                int mfaces = full ? Front | Left | Right : Front;
                var bars = new List<float> { ua + 0.05f, ub - 0.05f, da - 0.05f, db + 0.05f };
                float span = (doorLeft ? ub - db : da - ua);
                int nm = Mathf.FloorToInt(span / 1.9f);
                for (int i = 1; i <= nm; i++) bars.Add(doorLeft ? db + span * i / (nm + 1) : ua + span * i / (nm + 1));
                foreach (float bu in bars) Box(g, f, bu - 0.05f, bu + 0.05f, ground + 0.45f, gTop, 0f, 0.1f, mfaces, frame);
                Box(g, f, da - 0.05f, db + 0.05f, ground + 2.35f, ground + 2.45f, 0f, 0.1f, Front | Bottom, frame);
                Box(g, f, ua, ub, gTop, gTop + 0.12f, 0f, 0.1f, Front | Bottom, frame);
                // Sign band.
                float s0 = gTop + 0.12f, s1 = ground + gf - 0.15f;
                Box(g, f, ua + 0.02f, ub - 0.02f, s0, s1, 0f, 0.16f, Front | Top | Bottom | Left | Right, sign);
                // Awning.
                if (awnings && rng.NextDouble() < 0.6)
                    Awning(g, f, ua + 0.2f, ub - 0.2f, gTop, Awnings[rng.Next(Awnings.Length)], full);
                made++;
            }
            return made;
        }

        static void Awning(Geo g, Facet f, float ua, float ub, float yWall, Color col, bool full)
        {
            float zF = 1.35f, yF = yWall - 0.55f, val = 0.26f;
            Color under = Darken(col, 0.7f), valCol = Lighten(col, 0.08f);
            Vector3 a = f.P(ua, yWall, 0.05f), b = f.P(ub, yWall, 0.05f), c = f.P(ub, yF, zF), d = f.P(ua, yF, zF);
            Vector3 up = Vector3.Cross(b - a, d - a).normalized; if (up.y < 0f) up = -up;
            g.Quad(a, b, c, d, up, col, col);
            g.Quad(a, b, c, d, -up, under, under);
            g.Quad(f.P(ua, yF - val, zF), f.P(ub, yF - val, zF), f.P(ub, yF, zF), f.P(ua, yF, zF), f.n, valCol, valCol);
            if (!full) return;
            g.Quad(f.P(ua, yF - val, zF), f.P(ub, yF - val, zF), f.P(ub, yF, zF), f.P(ua, yF, zF), -f.n, under, under);
            foreach (float uu in new[] { ua, ub })
            {
                Vector3 n = uu == ua ? -f.t : f.t;
                g.Tri(f.P(uu, yWall, 0.05f), f.P(uu, yF, zF), f.P(uu, yF - val, zF), n, col);
                g.Tri(f.P(uu, yWall, 0.05f), f.P(uu, yF, zF), f.P(uu, yF - val, zF), -n, under);
            }
        }

        static int Ribbons(Geo g, Facet f, float firstSlab, float floorH, bool fins, Building b)
        {
            const float cellW = 1.5f, spandrel = 0.95f;
            int floors = 0;
            Color span = b.glassy ? Darken(b.wall, 0.9f) : Lighten(b.wall, 0.12f);
            Color fin = b.glassy ? Lighten(b.wall, 0.25f) : Darken(b.wall, 0.85f);
            int cells = Mathf.FloorToInt((f.u1 - f.u0 - 0.4f) / cellW);
            if (cells < 2) return 0;
            float start = (f.u0 + f.u1) * 0.5f - cells * cellW * 0.5f;
            for (float y = firstSlab; y + floorH <= f.y1 - 0.3f; y += floorH)
            {
                if (y < f.y0 - 0.1f) continue;
                int segStart = -1;
                bool any = false;
                for (int ci = 0; ci <= cells; ci++)
                {
                    bool ok = ci < cells && f.RectInside(start + ci * cellW, start + (ci + 1) * cellW, y, y + floorH)
                              && !Blocked(f, start + (ci + 0.5f) * cellW, y + spandrel + 1f);
                    if (ok && segStart < 0) segStart = ci;
                    if (!ok && segStart >= 0)
                    {
                        float ua = start + segStart * cellW, ub = start + ci * cellW;
                        Box(g, f, ua, ub, y, y + spandrel, 0f, 0.16f, Front | Top | Bottom | Left | Right, span);
                        Glass(g, f, ua, ub, y + spandrel, y + floorH, 0.015f, OffGlassB, OffGlassT);
                        if (fins)
                            for (int k = segStart + 2; k < ci; k += 2)
                            {
                                float fu = start + k * cellW;
                                Box(g, f, fu - 0.06f, fu + 0.06f, y + spandrel, y + floorH, 0f, 0.14f, Front | Left | Right, fin);
                            }
                        segStart = -1; any = true;
                    }
                }
                if (any) floors++;
            }
            return floors;
        }

        // Something (a neighbour) right in front of the wall here?
        static bool Blocked(Facet f, float u, float y)
        {
            Vector3 o = f.P(u, y, 0.25f);
            foreach (var h in Physics.RaycastAll(o, f.n, 1.2f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part != null && !part.isGround) return true;
            }
            return false;
        }

        // Open space in front (a street or square, not a backyard gap).
        static bool StreetFacing(Facet f, float u, float y)
        {
            Vector3 o = f.P(u, y, 0.4f);
            foreach (var h in Physics.RaycastAll(o, f.n, 7f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part != null && !part.isGround) return false;
            }
            return true;
        }

        /// <summary>Empty logo panel on the school's name band: put a logo image you may use in its Base Map.</summary>
        static Material SchoolLogoMaterial()
        {
            const string path = "Assets/CityFeature/Materials/M_Logo_School.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetColor("_BaseColor", Color.white);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        public static Material DetailMaterial()
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (m != null) return m;
            if (!AssetDatabase.IsValidFolder("Assets/CityFeature/Materials")) AssetDatabase.CreateFolder("Assets/CityFeature", "Materials");
            m = new Material(Shader.Find("BDCity/FlatDetail")) { enableInstancing = true };
            AssetDatabase.CreateAsset(m, MaterialPath);
            return m;
        }

        static int[] Components(Vector3[] v, int[] t)
        {
            var parent = new int[v.Length];
            for (int i = 0; i < v.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            var weld = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < v.Length; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(v[i].x * 20f), Mathf.RoundToInt(v[i].y * 20f), Mathf.RoundToInt(v[i].z * 20f));
                if (weld.TryGetValue(key, out int o)) Union(i, o); else weld[key] = i;
            }
            for (int i = 0; i < t.Length; i += 3) { Union(t[i], t[i + 1]); Union(t[i], t[i + 2]); }
            var r = new int[v.Length];
            for (int i = 0; i < v.Length; i++) r[i] = Find(i);
            return r;
        }
    }
}
