using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Replaces the Cube Houses (Kijk-Kubus) in a frozen neighbourhood with a stylized model:
    /// yellow cubes standing on a corner (body diagonal vertical) on hexagonal pillars.
    /// Positions come from cube_sites.txt (x, z, top height, hexagon radius) — a hexagonal grid fitted
    /// to the city's 3D data — so they sit exactly where the real ones are.
    /// </summary>
    public static class CityCubeHouses
    {
        const string SitesFile = "Assets/CityFeature/Editor/cube_sites.txt";
        const string Folder = "Assets/CityFeature/Props/CubeHouses";
        const float LatticeAngle = -27.17f;   // direction between neighbouring cubes (degrees, x->z)
        static readonly Color Yellow = new Color(0.96f, 0.80f, 0.30f);
        static readonly Color Pillar = new Color(0.82f, 0.81f, 0.78f);

        [MenuItem("B&D City/Build stylized Cube Houses (all areas)")]
        public static void BuildAll()
        {
            foreach (var a in new[] { "Wijnhaven107", "Lijnbaan", "Blaak" })
                Debug.Log("[CityCubeHouses] " + Build($"Assets/CityFeature/Frozen/{a}/Frozen - {a}.prefab"));
        }

        public static string Build(string prefabPath)
        {
            var sites = new List<Vector4>();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var line in File.ReadAllLines(SitesFile))
            {
                var p = line.Split(',');
                if (p.Length < 4) continue;
                sites.Add(new Vector4(float.Parse(p[0], inv), float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)));
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var buildings = root.transform.Find("Buildings");

            // 1. Remove the city data's version of the cube houses from the building chunks.
            int removed = 0;
            foreach (var mf in buildings.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.name == "Cube Houses") continue;
                var mesh = mf.sharedMesh;
                var v = mesh.vertices; var t = mesh.triangles;
                var keep = new List<int>(t.Length);
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 c = (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f;
                    bool inCube = false;
                    foreach (var s in sites)
                    {
                        float r = s.w > 6f ? s.w + 2.5f : s.w + 0.3f;
                        if (new Vector2(c.x - s.x, c.z - s.y).magnitude < r) { inCube = true; break; }
                    }
                    if (inCube) { removed++; continue; }
                    keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]);
                }
                if (keep.Count == 0) { Object.DestroyImmediate(mf.gameObject); continue; }
                if (keep.Count != t.Length)
                {
                    mesh.SetTriangles(keep, 0);
                    EditorUtility.SetDirty(mesh);
                    var mc = mf.GetComponent<MeshCollider>();
                    if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = mesh; }
                }
            }
            // Leftover pieces of the old cube houses around the edges: drop whole parts that are mostly inside the complex.
            foreach (var mf in buildings.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.name == "Cube Houses") continue;
                int r = RemoveLeftovers(mf, sites);
                if (r > 0) removed += r;
            }
            bool hadModel = buildings.Find("Cube Houses") != null;
            if (removed == 0 && !hadModel)
            {
                PrefabUtility.UnloadPrefabContents(root);
                return $"{Path.GetFileNameWithoutExtension(prefabPath)}: no cube houses in this area";
            }

            // 2. Build the stylized model.
            Directory.CreateDirectory(Folder);
            var mat = Material();
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uv0 = new List<Vector2>(); var uv1 = new List<Vector3>(); var tris = new List<int>();
            Vector2 yellowUv = new Vector2(0.25f, 0.5f), pillarUv = new Vector2(0.75f, 0.5f);

            Quaternion tilt = Quaternion.FromToRotation(new Vector3(1, 1, 1).normalized, Vector3.up);
            // Yaw so the cube's hexagonal outline (seen from above) lines up with the grid.
            Vector3 side = tilt * new Vector3(0.5f, -0.5f, -0.5f);
            float sideAngle = Mathf.Atan2(side.z, side.x) * Mathf.Rad2Deg;
            float targetAngle = LatticeAngle + 30f;
            float yaw = sideAngle - targetAngle;
            yaw = yaw - 60f * Mathf.Round(yaw / 60f);
            Quaternion rot = Quaternion.AngleAxis(yaw, Vector3.up) * tilt;

            foreach (var s in sites)
            {
                float R = s.w * 0.98f;                     // hexagon outline radius
                float e = R / Mathf.Sqrt(2f / 3f);          // cube edge
                float diag = e * Mathf.Sqrt(3f);
                var centre = new Vector3(s.x, s.z - diag * 0.5f, s.y);
                AddCube(centre, e, rot, yellowUv, verts, norms, uv0, uv1, tris);

                float groundY = 1.5f;
                foreach (var hit in Physics.RaycastAll(new Vector3(s.x, 80f, s.y), Vector3.down, 160f, ~0, QueryTriggerInteraction.Ignore))
                {
                    var part = hit.collider.GetComponentInParent<CityPart>();
                    if (part != null && part.isGround) { groundY = hit.point.y; break; }
                }
                float bottom = s.z - diag;
                AddHexPrism(new Vector3(s.x, groundY - 0.5f, s.y), bottom + e * 0.35f, s.w > 6f ? 3.4f : 2.3f, LatticeAngle + 30f, pillarUv, verts, norms, uv0, uv1, tris);
            }

            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, name = "CubeHouses" };
            m.SetVertices(verts); m.SetNormals(norms); m.SetUVs(0, uv0); m.SetUVs(1, uv1); m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string meshPath = $"{folder}/Meshes/CubeHouses.asset";
            var old = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (old != null) { EditorUtility.CopySerialized(m, old); m = old; } else AssetDatabase.CreateAsset(m, meshPath);

            var existing = buildings.Find("Cube Houses");
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            var go = new GameObject("Cube Houses");
            go.transform.SetParent(buildings, false);
            go.AddComponent<MeshFilter>().sharedMesh = m;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.AddComponent<MeshCollider>().sharedMesh = m;
            go.AddComponent<CityPart>();
            go.isStatic = true;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: removed {removed} old triangles, added {sites.Count} cubes ({verts.Count} verts)";
        }

        static int RemoveLeftovers(MeshFilter mf, List<Vector4> sites)
        {
            var m = mf.sharedMesh; var v = m.vertices; var t = m.triangles;
            float Dist(Vector3 c)
            {
                float best = 1e9f;
                foreach (var s in sites) best = Mathf.Min(best, new Vector2(c.x - s.x, c.z - s.y).magnitude - (s.w > 6f ? 9f : 5f));
                return best;
            }
            var parent = new int[v.Length];
            for (int i = 0; i < v.Length; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            var weld = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < v.Length; i++)
            {
                var k = new Vector3Int(Mathf.RoundToInt(v[i].x * 20f), Mathf.RoundToInt(v[i].y * 20f), Mathf.RoundToInt(v[i].z * 20f));
                if (weld.TryGetValue(k, out int o)) Union(i, o); else weld[k] = i;
            }
            for (int i = 0; i < t.Length; i += 3) { Union(t[i], t[i + 1]); Union(t[i], t[i + 2]); }
            var total = new Dictionary<int, float>(); var inside = new Dictionary<int, float>();
            for (int i = 0; i < t.Length; i += 3)
            {
                int c = Find(t[i]);
                float area = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).magnitude * 0.5f;
                total[c] = (total.TryGetValue(c, out float a) ? a : 0f) + area;
                if (Dist((v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f) < 4f) inside[c] = (inside.TryGetValue(c, out float b) ? b : 0f) + area;
            }
            var kill = new HashSet<int>();
            foreach (var kv in inside) if (kv.Value > 0.5f * total[kv.Key]) kill.Add(kv.Key);
            if (kill.Count == 0) return 0;
            var keep = new List<int>(t.Length); int removed = 0;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (kill.Contains(Find(t[i]))) { removed++; continue; }
                keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]);
            }
            if (keep.Count == 0) { Object.DestroyImmediate(mf.gameObject); return removed; }
            m.SetTriangles(keep, 0);
            EditorUtility.SetDirty(m);
            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = m; }
            return removed;
        }

        static Material Material()
        {
            string palPath = $"{Folder}/CubeHousesPalette.png";
            var tex = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, new Color(Yellow.r, Yellow.g, Yellow.b, 1f));   // windows
            tex.SetPixel(1, 0, new Color(Pillar.r, Pillar.g, Pillar.b, 0f));   // no windows
            tex.Apply();
            File.WriteAllBytes(palPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(palPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(palPath);
            ti.filterMode = FilterMode.Point; ti.mipmapEnabled = false; ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.wrapMode = TextureWrapMode.Clamp; ti.npotScale = TextureImporterNPOTScale.None; ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.SaveAndReimport();

            string matPath = $"{Folder}/M_CubeHouses.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { mat = new Material(Shader.Find("BDCity/StylizedBuilding")); AssetDatabase.CreateAsset(mat, matPath); }
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(palPath));
            // Square windows, 2 x 2 per cube face.
            mat.SetVector("_Cell", new Vector4(3.0f, 3.0f, 0, 0));
            mat.SetVector("_Win", new Vector4(0.48f, 0.48f, 0, 0));
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static void AddCube(Vector3 centre, float e, Quaternion rot, Vector2 uv, List<Vector3> v, List<Vector3> n, List<Vector2> u0, List<Vector3> u1, List<int> t)
        {
            Vector3[] axes = { Vector3.right, Vector3.up, Vector3.forward };
            for (int a = 0; a < 3; a++)
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Vector3 normal = axes[a] * sgn;
                Vector3 ax1 = axes[(a + 1) % 3], ax2 = axes[(a + 2) % 3];
                Vector3 faceCentre = normal * (e * 0.5f);
                int b = v.Count;
                for (int k = 0; k < 4; k++)
                {
                    float s1 = (k == 1 || k == 2) ? 0.5f : -0.5f;
                    float s2 = (k >= 2) ? 0.5f : -0.5f;
                    Vector3 local = faceCentre + ax1 * (s1 * e) + ax2 * (s2 * e);
                    v.Add(centre + rot * local);
                    n.Add(rot * normal);
                    u0.Add(uv);
                    u1.Add(new Vector3((s1 + 0.5f) * e, (s2 + 0.5f) * e, 1f));
                }
                // Unity: Cross(b-a, c-a) points out of a front face.
                Vector3 fn = Vector3.Cross(v[b + 1] - v[b], v[b + 2] - v[b]);
                if (Vector3.Dot(fn, rot * normal) > 0) t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
        }

        static void AddHexPrism(Vector3 baseCentre, float topY, float radius, float angle0, Vector2 uv, List<Vector3> v, List<Vector3> n, List<Vector2> u0, List<Vector3> u1, List<int> t)
        {
            for (int k = 0; k < 6; k++)
            {
                float a0 = (angle0 + k * 60f) * Mathf.Deg2Rad, a1 = (angle0 + (k + 1) * 60f) * Mathf.Deg2Rad;
                var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * radius;
                var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * radius;
                Vector3 normal = ((p0 + p1) * 0.5f).normalized;
                int b = v.Count;
                var top = new Vector3(baseCentre.x, topY, baseCentre.z);
                v.Add(baseCentre + p0); v.Add(baseCentre + p1); v.Add(top + p1); v.Add(top + p0);
                for (int q = 0; q < 4; q++) { n.Add(normal); u0.Add(uv); u1.Add(Vector3.zero); }
                // Unity: Cross(b-a, c-a) points out of a front face.
                Vector3 fn = Vector3.Cross(v[b + 1] - v[b], v[b + 2] - v[b]);
                if (Vector3.Dot(fn, normal) > 0) t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
        }
    }
}
