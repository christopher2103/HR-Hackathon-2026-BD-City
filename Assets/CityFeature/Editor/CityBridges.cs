using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// The terrain data has no bridges (the decks are missing, you see water with lamp posts in it).
    /// This builds simple low-poly bridge decks from the base map (BGT): every road/pavement/bike path part
    /// that lies above ground level (relatieve_hoogteligging >= 1) plus the bridge parts themselves.
    /// Parts that touch are grouped into one bridge; its deck height runs in a straight line between the
    /// quay heights at both ends. Lamps and trees standing on a bridge are lifted onto the deck.
    /// </summary>
    public static class CityBridges
    {
        const float Thickness = 1.0f;
        static readonly Color Side = new Color(0.62f, 0.61f, 0.58f);

        class Part { public List<Vector2> ring; public Color colour; public int order; public int cluster; public Vector4 box; }

        [MenuItem("B&D City/Bridges (all areas)")]
        public static void BuildAll()
        {
            foreach (var a in new[] { "Wijnhaven107", "Lijnbaan", "Blaak" })
                Debug.Log("[CityBridges] " + Build($"Assets/CityFeature/Frozen/{a}/Frozen - {a}.prefab"));
        }

        public static string Build(string prefabPath)
        {
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            if (georef == null) return "Open CityScene first.";
            var root = PrefabUtility.LoadPrefabContents(prefabPath);

            // Area + a physics copy of the ground only (for quay heights).
            Bounds area = default; bool first = true;
            var probe = new GameObject("BridgeProbe") { hideFlags = HideFlags.HideAndDontSave };
            foreach (var mf in root.transform.Find("Ground").GetComponentsInChildren<MeshFilter>(true))
            {
                var b = mf.GetComponent<MeshRenderer>().bounds;
                if (first) { area = b; first = false; } else area.Encapsulate(b);
                var g = new GameObject("g") { hideFlags = HideFlags.HideAndDontSave };
                g.transform.SetParent(probe.transform, false);
                g.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
                g.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }
            Physics.SyncTransforms();
            float waterY = -1.7f;
            var water = root.transform.Find("Water");
            if (water != null) waterY = water.GetComponent<MeshFilter>().sharedMesh.bounds.center.y;

            // ---- Map data ----
            double2 ll0 = LonLat(georef, area.min), ll1 = LonLat(georef, area.max);
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                System.Math.Min(ll0.x, ll1.x), System.Math.Min(ll0.y, ll1.y), System.Math.Max(ll0.x, ll1.x), System.Math.Max(ll0.y, ll1.y));
            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(120) };
            var parts = new List<Part>();
            string[] collections = { "overbruggingsdeel", "ondersteunendwegdeel", "wegdeel" };
            for (int ci = 0; ci < collections.Length; ci++)
            {
                string url = $"https://api.pdok.nl/kadaster/bgt/ogc/v1/collections/{collections[ci]}/items?f=json&limit=1000&bbox={bbox}";
                int pages = 0;
                while (url != null && pages++ < 40)
                {
                    string json = http.GetStringAsync(url).Result;
                    foreach (string f in json.Split(new[] { "{\"type\":\"Feature\"" }, System.StringSplitOptions.None))
                    {
                        if (!f.Contains("\"geometry\"") || !f.Contains("\"eind_registratie\":null")) continue;
                        var feat = CityGroundMap.Parse(f, collections[ci]);
                        if (feat == null || feat.level < 1) continue;
                        // Only the outer ring: bridge parts rarely have holes.
                        var ring = new List<Vector2>();
                        foreach (var p in feat.rings[0])
                        {
                            double3 u = georef.TransformEarthCenteredEarthFixedPositionToUnity(CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(p.x, p.y, 0)));
                            var q = new Vector2((float)u.x, (float)u.z);
                            if (ring.Count == 0 || (ring[ring.Count - 1] - q).sqrMagnitude > 0.0004f) ring.Add(q);
                        }
                        if (ring.Count > 3 && (ring[0] - ring[ring.Count - 1]).sqrMagnitude < 0.0004f) ring.RemoveAt(ring.Count - 1);
                        if (ring.Count < 3) continue;
                        var part = new Part { ring = ring, colour = collections[ci] == "overbruggingsdeel" ? new Color(0.67f, 0.66f, 0.64f) : feat.colour, order = ci };
                        Vector2 mn = ring[0], mx = ring[0];
                        foreach (var q in ring) { mn = Vector2.Min(mn, q); mx = Vector2.Max(mx, q); }
                        if (mx.x < area.min.x || mn.x > area.max.x || mx.y < area.min.z || mn.y > area.max.z) continue;
                        part.box = new Vector4(mn.x, mn.y, mx.x, mx.y);
                        parts.Add(part);
                    }
                    var next = Regex.Match(json, "\"rel\":\"next\"[^}]*?\"href\":\"([^\"]+)\"");
                    if (!next.Success) next = Regex.Match(json, "\"href\":\"([^\"]+)\"[^}]*?\"rel\":\"next\"");
                    url = next.Success ? next.Groups[1].Value.Replace("\\u0026", "&") : null;
                }
            }

            // ---- Group touching parts into bridges ----
            var parent = new int[parts.Count];
            for (int i = 0; i < parts.Count; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            for (int i = 0; i < parts.Count; i++)
                for (int j = i + 1; j < parts.Count; j++)
                {
                    Vector4 a = parts[i].box, b = parts[j].box;
                    if (a.x - 0.5f > b.z || b.x - 0.5f > a.z || a.y - 0.5f > b.w || b.y - 0.5f > a.w) continue;
                    if (Touch(parts[i].ring, parts[j].ring)) { int ra = Find(i), rb = Find(j); if (ra != rb) parent[ra] = rb; }
                }
            var clusters = new Dictionary<int, List<Part>>();
            for (int i = 0; i < parts.Count; i++)
            {
                int r = Find(i);
                if (!clusters.TryGetValue(r, out var l)) clusters[r] = l = new List<Part>();
                l.Add(parts[i]);
            }

            // ---- Deck height per bridge: straight line between the quays at both ends ----
            var colours = new List<Color>(); var slot = new Dictionary<Color32, int>();
            int Slot(Color c) { var k = (Color32)new Color(c.r, c.g, c.b, 0f); if (!slot.TryGetValue(k, out int i)) { i = colours.Count; colours.Add(k); slot[k] = i; } return i; }
            Slot(Side);
            var verts = new List<Vector3>(); var slots = new List<int>(); var tris = new List<int>();
            var decks = new List<(List<Part> parts, Vector2 o, Vector2 axis, float t0, float t1, float h0, float h1)>();
            int bridges = 0;
            foreach (var cl in clusters.Values)
            {
                // Main direction of the bridge.
                var pts = new List<Vector2>(); foreach (var p in cl) pts.AddRange(p.ring);
                Vector2 o = Vector2.zero; foreach (var q in pts) o += q; o /= pts.Count;
                float sxx = 0, sxy = 0, syy = 0;
                foreach (var q in pts) { var d = q - o; sxx += d.x * d.x; sxy += d.x * d.y; syy += d.y * d.y; }
                float ang = 0.5f * Mathf.Atan2(2 * sxy, sxx - syy);
                var axis = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)); var side = new Vector2(-axis.y, axis.x);
                float t0 = float.MaxValue, t1 = float.MinValue, w0 = float.MaxValue, w1 = float.MinValue;
                foreach (var q in pts) { float t = Vector2.Dot(q - o, axis), w = Vector2.Dot(q - o, side); t0 = Mathf.Min(t0, t); t1 = Mathf.Max(t1, t); w0 = Mathf.Min(w0, w); w1 = Mathf.Max(w1, w); }
                float? h0 = Quay(o, axis, side, t0 - 2.5f, -1f, w0, w1, waterY), h1 = Quay(o, axis, side, t1 + 2.5f, 1f, w0, w1, waterY);
                if (h0 == null && h1 == null) continue;   // nothing to connect to (e.g. a viaduct far from our ground)
                float a0 = h0 ?? h1.Value, a1 = h1 ?? h0.Value;
                decks.Add((cl, o, axis, t0, t1, a0, a1));
                bridges++;

                float H(Vector2 p) => Mathf.Lerp(a0, a1, Mathf.InverseLerp(t0, t1, Vector2.Dot(p - o, axis)));
                foreach (var p in cl)
                {
                    float lift = p.order == 0 ? 0.02f : 0.06f;   // road surface on top of the bridge structure
                    var tri = Triangulate(p.ring);
                    int s = Slot(p.colour);
                    foreach (int k in tri) { var q = p.ring[k]; verts.Add(new Vector3(q.x, H(q) + lift, q.y)); slots.Add(s); tris.Add(verts.Count - 1); }
                    // Fix winding: top faces up.
                    for (int k = tris.Count - tri.Count; k < tris.Count; k += 3)
                        if (Vector3.Cross(verts[tris[k + 1]] - verts[tris[k]], verts[tris[k + 2]] - verts[tris[k]]).y < 0) { int tmp = tris[k + 1]; tris[k + 1] = tris[k + 2]; tris[k + 2] = tmp; }
                    // Side skirt (only the structure parts, so the edge of the bridge reads as a slab).
                    if (p.order != 0) continue;
                    int sideSlot = Slot(Side);
                    for (int i = 0; i < p.ring.Count; i++)
                    {
                        Vector2 a = p.ring[i], b = p.ring[(i + 1) % p.ring.Count];
                        Vector3 ta = new Vector3(a.x, H(a) + lift, a.y), tb = new Vector3(b.x, H(b) + lift, b.y);
                        Vector3 ba = ta + Vector3.down * Thickness, bb = tb + Vector3.down * Thickness;
                        int k0 = verts.Count;
                        verts.Add(ta); verts.Add(tb); verts.Add(bb); verts.Add(ba);
                        for (int q = 0; q < 4; q++) slots.Add(sideSlot);
                        // Both sides, so the slab is visible from every direction.
                        tris.AddRange(new[] { k0, k0 + 1, k0 + 2, k0, k0 + 2, k0 + 3, k0, k0 + 2, k0 + 1, k0, k0 + 3, k0 + 2 });
                    }
                }
            }
            Object.DestroyImmediate(probe);

            var old = root.transform.Find("Bridges");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            int lifted = 0;
            if (tris.Count > 0)
            {
                // Palette (no windows: alpha 0) and UVs.
                int size = 4; while (size * size < colours.Count) size *= 2;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
                var px = new Color[size * size];
                for (int i = 0; i < px.Length; i++) px[i] = i < colours.Count ? colours[i] : new Color(1, 1, 1, 0);
                tex.SetPixels(px); tex.Apply();
                string palPath = $"{folder}/Textures/BridgePalette.png";
                File.WriteAllBytes(palPath, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(palPath);
                var ti = (TextureImporter)AssetImporter.GetAtPath(palPath);
                ti.filterMode = FilterMode.Point; ti.mipmapEnabled = false; ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.npotScale = TextureImporterNPOTScale.None; ti.alphaSource = TextureImporterAlphaSource.FromInput; ti.SaveAndReimport();
                var uv = new List<Vector2>(); var uv1 = new List<Vector3>();
                foreach (int s in slots) { uv.Add(new Vector2((s % size + 0.5f) / size, (s / size + 0.5f) / size)); uv1.Add(Vector3.zero); }

                var mesh = new Mesh { name = "Bridges", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.SetVertices(verts); mesh.SetUVs(0, uv); mesh.SetUVs(1, uv1); mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                string meshPath = $"{folder}/Meshes/Bridges.asset";
                var oldMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (oldMesh != null) { EditorUtility.CopySerialized(mesh, oldMesh); mesh = oldMesh; } else AssetDatabase.CreateAsset(mesh, meshPath);

                string matPath = $"{folder}/Materials/Bridges.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { mat = new Material(Shader.Find("BDCity/StylizedBuilding")); AssetDatabase.CreateAsset(mat, matPath); }
                mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(palPath));
                EditorUtility.SetDirty(mat);

                var go = new GameObject("Bridges");
                go.transform.SetParent(root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.AddComponent<CityPart>().isGround = true;
                go.AddComponent<TeleportationArea>().interactionLayers = 1 << 31;
                go.isStatic = true;

                // Lamps and trees that stand on a bridge go up onto the deck.
                foreach (var group in new[] { "Props/Lamps", "Props/Trees" })
                {
                    var t = root.transform.Find(group);
                    if (t == null) continue;
                    foreach (Transform prop in t)
                    {
                        var p2 = new Vector2(prop.position.x, prop.position.z);
                        foreach (var d in decks)
                        {
                            bool on = false;
                            foreach (var part in d.parts) if (Inside(p2, part.ring)) { on = true; break; }
                            if (!on) continue;
                            float h = Mathf.Lerp(d.h0, d.h1, Mathf.InverseLerp(d.t0, d.t1, Vector2.Dot(p2 - d.o, d.axis))) + 0.06f;
                            if (prop.position.y < h - 0.3f) { prop.position = new Vector3(prop.position.x, h, prop.position.z); lifted++; }
                            break;
                        }
                    }
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: {parts.Count} raised map parts, {bridges} bridges built, {lifted} lamps/trees lifted onto them";
        }

        // Quay height just beyond one end of the bridge: median of a few ground samples above the water.
        static float? Quay(Vector2 o, Vector2 axis, Vector2 side, float t, float dir, float w0, float w1, float waterY)
        {
            var hs = new List<float>();
            for (int step = 0; step < 4; step++)
                for (int k = 0; k <= 4; k++)
                {
                    Vector2 p = o + axis * (t + dir * step * 1.5f) + new Vector2(-axis.y, axis.x) * Mathf.Lerp(w0, w1, k / 4f);
                    foreach (var hit in Physics.RaycastAll(new Vector3(p.x, 100f, p.y), Vector3.down, 200f))
                        if (hit.collider.gameObject.hideFlags == HideFlags.HideAndDontSave && hit.point.y > waterY + 1.2f) { hs.Add(hit.point.y); break; }
                }
            if (hs.Count < 3) return null;
            hs.Sort();
            return hs[hs.Count / 2];
        }

        static bool Touch(List<Vector2> a, List<Vector2> b)
        {
            foreach (var p in a) if (Inside(p, b) || Dist(p, b) < 0.5f) return true;
            foreach (var p in b) if (Inside(p, a)) return true;
            return false;
        }

        static float Dist(Vector2 p, List<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count], ab = b - a;
                float t = ab.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, (a + ab * t - p).magnitude);
            }
            return best;
        }

        static bool Inside(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if (((poly[i].y > p.y) != (poly[j].y > p.y)) && (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                    inside = !inside;
            return inside;
        }

        // Ear clipping for a simple polygon; returns indices into ring.
        static List<int> Triangulate(List<Vector2> ring)
        {
            var result = new List<int>();
            int n = ring.Count;
            var idx = new List<int>(n);
            float area = 0;
            for (int i = 0; i < n; i++) { var a = ring[i]; var b = ring[(i + 1) % n]; area += a.x * b.y - b.x * a.y; }
            for (int i = 0; i < n; i++) idx.Add(area > 0 ? i : n - 1 - i);   // make counter-clockwise
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * n)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ip = idx[(i + idx.Count - 1) % idx.Count], ic = idx[i], inx = idx[(i + 1) % idx.Count];
                    Vector2 a = ring[ip], b = ring[ic], c = ring[inx];
                    if ((b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x) <= 1e-6f) continue;   // reflex
                    bool empty = true;
                    foreach (int k in idx)
                    {
                        if (k == ip || k == ic || k == inx) continue;
                        if (InTri(ring[k], a, b, c)) { empty = false; break; }
                    }
                    if (!empty) continue;
                    result.Add(ip); result.Add(ic); result.Add(inx);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3) { result.Add(idx[0]); result.Add(idx[1]); result.Add(idx[2]); }
            else if (idx.Count > 3) for (int i = 1; i + 1 < idx.Count; i++) { result.Add(idx[0]); result.Add(idx[i]); result.Add(idx[i + 1]); }
            return result;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        static double2 LonLat(CesiumGeoreference georef, Vector3 w)
        {
            double3 llh = CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(georef.TransformUnityPositionToEarthCenteredEarthFixed(new double3(w.x, w.y, w.z)));
            return llh.xy;
        }
    }
}
