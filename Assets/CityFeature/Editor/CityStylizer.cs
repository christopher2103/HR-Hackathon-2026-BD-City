using System.Collections.Generic;
using System.IO;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Turns a frozen neighbourhood into the stylized look:
    ///  - Buildings: photo textures replaced by one soft colour per building (walls and roofs separately), taken from
    ///    the main colour of its own photo; landmarks get a signature accent colour. All buildings share one tiny
    ///    palette texture and are merged into ~150 m chunks (far fewer draw calls on Quest).
    ///  - Ground: steep faces (quay walls, edges) no longer show the stretched aerial photo but a plain stone colour.
    /// The old building meshes, materials and textures are deleted (re-freeze to get the photo look back).
    /// </summary>
    public static class CityStylizer
    {
        public struct Landmark
        {
            public string name; public double lat, lon; public float radius; public Color walls, roofs;
            public Landmark(string n, double la, double lo, float r, Color w, Color rf) { name = n; lat = la; lon = lo; radius = r; walls = w; roofs = rf; }
        }

        public static readonly Landmark[] Landmarks =
        {
            new Landmark("Markthal",    51.92000, 4.48694, 25f, new Color(0.91f, 0.62f, 0.42f), new Color(0.78f, 0.50f, 0.36f)), // warm orange: the colourful market mural
            new Landmark("Cube houses", 51.92033, 4.49033, 45f, new Color(0.96f, 0.80f, 0.30f), new Color(0.96f, 0.80f, 0.30f)), // the real yellow cubes
            new Landmark("Witte Huis",  51.91883, 4.49175, 12f, new Color(0.97f, 0.96f, 0.93f), new Color(0.55f, 0.58f, 0.62f)), // white, grey slate roof
            new Landmark("Stadhuis",    51.92278, 4.47972, 45f, new Color(0.86f, 0.74f, 0.55f), new Color(0.42f, 0.52f, 0.48f)), // sandstone, green-grey roof
        };

        private static readonly Color QuayStone = new Color(0.55f, 0.52f, 0.48f);

        public static string Stylize(string prefabPath, Vector3 center, float chunkSize = 150f, float colliderRadius = 350f)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            var landmarkPositions = new List<(Landmark, Vector3)>();
            foreach (Landmark l in Landmarks)
            {
                double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(l.lon, l.lat, 0));
                double3 u = georef.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
                landmarkPositions.Add((l, new Vector3((float)u.x, 0f, (float)u.z)));
            }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            Transform buildings = root.transform.Find("Buildings");
            var palette = new List<Color>();
            var paletteIndex = new Dictionary<int, int>();
            int ColorSlot(Color c)
            {
                int key = Mathf.RoundToInt(c.r * 31) * 1024 + Mathf.RoundToInt(c.g * 31) * 32 + Mathf.RoundToInt(c.b * 31);
                if (!paletteIndex.TryGetValue(key, out int i)) { i = palette.Count; palette.Add(c); paletteIndex[key] = i; }
                return i;
            }

            // Collect per-chunk geometry with a palette slot per vertex.
            var chunkVerts = new Dictionary<Vector2Int, List<Vector3>>();
            var chunkNormals = new Dictionary<Vector2Int, List<Vector3>>();
            var chunkSlots = new Dictionary<Vector2Int, List<int>>();
            var chunkTris = new Dictionary<Vector2Int, List<int>>();
            var oldAssets = new HashSet<string>();
            var landmarkHits = new Dictionary<string, int>();
            long vertsIn = 0;

            foreach (MeshFilter mf in buildings.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                var mr = mf.GetComponent<MeshRenderer>();
                Texture2D photo = ReadableCopy(mr.sharedMaterial != null ? mr.sharedMaterial.GetTexture("_baseColorTexture") : null);
                oldAssets.Add(AssetDatabase.GetAssetPath(mesh));
                if (mr.sharedMaterial != null)
                {
                    oldAssets.Add(AssetDatabase.GetAssetPath(mr.sharedMaterial));
                    Texture t = mr.sharedMaterial.GetTexture("_baseColorTexture");
                    if (t != null) oldAssets.Add(AssetDatabase.GetAssetPath(t));
                }

                Vector3[] v = mesh.vertices; Vector3[] n = mesh.normals; Vector2[] uv = mesh.uv; int[] tri = mesh.triangles;
                vertsIn += v.Length;
                int[] comp = Components(v, tri);

                // Average photo colour per building, walls and roofs separately (area weighted).
                var wallSum = new Dictionary<int, Vector4>(); var roofSum = new Dictionary<int, Vector4>();
                var compMin = new Dictionary<int, Vector3>(); var compMax = new Dictionary<int, Vector3>();
                for (int i = 0; i < tri.Length; i += 3)
                {
                    Vector3 a = v[tri[i]], b = v[tri[i + 1]], c = v[tri[i + 2]];
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    float area = cross.magnitude * 0.5f;
                    if (area < 1e-4f) continue;
                    bool roof = Mathf.Abs(cross.normalized.y) > 0.6f;
                    Color col = photo != null && uv.Length == v.Length
                        ? photo.GetPixelBilinear((uv[tri[i]].x + uv[tri[i + 1]].x + uv[tri[i + 2]].x) / 3f, (uv[tri[i]].y + uv[tri[i + 1]].y + uv[tri[i + 2]].y) / 3f)
                        : new Color(0.8f, 0.78f, 0.72f);
                    var sums = roof ? roofSum : wallSum;
                    int k = comp[tri[i]];
                    sums.TryGetValue(k, out Vector4 s);
                    sums[k] = s + new Vector4(col.r * area, col.g * area, col.b * area, area);
                }
                for (int i = 0; i < v.Length; i++)
                {
                    int k = comp[i];
                    compMin[k] = compMin.TryGetValue(k, out var mn) ? Vector3.Min(mn, v[i]) : v[i];
                    compMax[k] = compMax.TryGetValue(k, out var mx) ? Vector3.Max(mx, v[i]) : v[i];
                }

                // Colour per building -> palette slots.
                var wallSlot = new Dictionary<int, int>(); var roofSlot = new Dictionary<int, int>();
                foreach (int k in compMin.Keys)
                {
                    Color wall = Soften(Avg(wallSum, k, Avg(roofSum, k, new Color(0.8f, 0.78f, 0.72f))), false);
                    Color roof = Soften(Avg(roofSum, k, wall), true);
                    Vector3 c = (compMin[k] + compMax[k]) * 0.5f;
                    wallSlot[k] = ColorSlot(wall);
                    roofSlot[k] = ColorSlot(roof);
                }

                // Append to chunks (by building centre, so a building is never split; a vertex belongs to one building).
                var dstIndex = new int[v.Length];
                for (int i = 0; i < dstIndex.Length; i++) dstIndex[i] = -1;
                for (int i = 0; i < tri.Length; i += 3)
                {
                    int k = comp[tri[i]];
                    Vector3 c = (compMin[k] + compMax[k]) * 0.5f;
                    var key = new Vector2Int(Mathf.FloorToInt((c.x - center.x) / chunkSize), Mathf.FloorToInt((c.z - center.z) / chunkSize));
                    if (!chunkVerts.ContainsKey(key))
                    {
                        chunkVerts[key] = new List<Vector3>(); chunkNormals[key] = new List<Vector3>();
                        chunkSlots[key] = new List<int>(); chunkTris[key] = new List<int>();
                    }
                    for (int j = 0; j < 3; j++)
                    {
                        int src = tri[i + j];
                        if (dstIndex[src] < 0)
                        {
                            dstIndex[src] = chunkVerts[key].Count;
                            Vector3 normal = n.Length == v.Length ? n[src] : Vector3.up;
                            chunkVerts[key].Add(v[src]);
                            chunkNormals[key].Add(normal);
                            chunkSlots[key].Add(Mathf.Abs(normal.y) > 0.6f ? roofSlot[k] : wallSlot[k]);
                        }
                        chunkTris[key].Add(dstIndex[src]);
                    }
                }
                if (photo != null) Object.DestroyImmediate(photo);
            }

            // Palette texture (point filtered, one texel per colour).
            int size = 8;
            while (size * size < palette.Count) size *= 2;
            var tex = new Texture2D(size, size, TextureFormat.RGB24, false);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = i < palette.Count ? palette[i] : Color.white;
            tex.SetPixels(pixels); tex.Apply();
            string palettePath = $"{folder}/Textures/BuildingPalette.png";
            File.WriteAllBytes(palettePath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(palettePath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(palettePath);
            ti.filterMode = FilterMode.Point; ti.mipmapEnabled = false; ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.wrapMode = TextureWrapMode.Clamp; ti.sRGBTexture = true; ti.npotScale = TextureImporterNPOTScale.None;
            ti.SaveAndReimport();

            string matPath = $"{folder}/Materials/BuildingsStylized.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, matPath); }
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(palettePath));
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.12f);
            EditorUtility.SetDirty(mat);

            // Replace the building objects with the chunks.
            var stylized = new GameObject("Buildings").transform;
            Object.DestroyImmediate(buildings.gameObject);
            stylized.SetParent(root.transform, false);
            long vertsOut = 0;
            foreach (var key in chunkVerts.Keys)
            {
                var uvs = new List<Vector2>(chunkSlots[key].Count);
                foreach (int slot in chunkSlots[key]) uvs.Add(new Vector2((slot % size + 0.5f) / size, (slot / size + 0.5f) / size));
                var mesh = new Mesh { name = $"Buildings_{key.x}_{key.y}" };
                mesh.indexFormat = chunkVerts[key].Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(chunkVerts[key]); mesh.SetNormals(chunkNormals[key]); mesh.SetUVs(0, uvs); mesh.SetTriangles(chunkTris[key], 0);
                mesh.RecalculateBounds();
                AssetDatabase.CreateAsset(mesh, $"{folder}/Meshes/{mesh.name}.asset");
                vertsOut += mesh.vertexCount;

                var go = new GameObject(mesh.name);
                go.transform.SetParent(stylized, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.AddComponent<CityPart>().isGround = false;
                Vector3 c = mesh.bounds.center;
                if (new Vector2(c.x - center.x, c.z - center.z).magnitude - mesh.bounds.extents.magnitude <= colliderRadius)
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                go.isStatic = true;
            }
            stylized.SetSiblingIndex(2);

            // Ground: steep faces get a plain stone material instead of the stretched photo.
            Material stone = StoneMaterial(folder);
            int steepFaces = 0;
            foreach (string group in new[] { "Ground", "Far Ground" })
            {
                Transform g = root.transform.Find(group);
                if (g == null) continue;
                foreach (MeshFilter mf in g.GetComponentsInChildren<MeshFilter>(true))
                    steepFaces += SplitSteep(mf, stone);
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);

            AssetDatabase.StartAssetEditing();
            try { foreach (string a in oldAssets) if (!string.IsNullOrEmpty(a) && a.StartsWith(folder)) AssetDatabase.DeleteAsset(a); }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.SaveAssets();

            var lm = new System.Text.StringBuilder();
            foreach (var kv in landmarkHits) lm.Append($"{kv.Key} ({kv.Value} parts) ");
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: buildings {vertsIn / 1000}k -> {vertsOut / 1000}k verts in {chunkVerts.Count} chunks, palette {palette.Count} colours, steep ground faces {steepFaces}; landmarks: {lm}";
        }

        /// <summary>Re-run the steep-ground split on an already stylized neighbourhood.</summary>
        public static string FixGround(string prefabPath)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            Material stone = StoneMaterial(folder);
            int steep = 0;
            foreach (string group in new[] { "Ground", "Far Ground" })
            {
                Transform g = root.transform.Find(group);
                if (g == null) continue;
                foreach (MeshFilter mf in g.GetComponentsInChildren<MeshFilter>(true)) steep += SplitSteep(mf, stone);
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: {steep} steep ground faces now stone";
        }

        /// <summary>
        /// Gives the landmarks their accent colour, exactly on their real footprint (BGT building outlines from PDOK).
        /// Works on the stylized building chunks: changes the palette entry of the triangles inside the footprint.
        /// </summary>
        public static string RecolorLandmarks(string prefabPath)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(60) };
            var footprints = new List<(Landmark, List<Vector2>)>();
            foreach (Landmark l in Landmarks)
            {
                double dLat = l.radius / 111000.0, dLon = l.radius / (111000.0 * System.Math.Cos(l.lat * System.Math.PI / 180.0));
                string bbox = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3}", l.lon - dLon, l.lat - dLat, l.lon + dLon, l.lat + dLat);
                string json = http.GetStringAsync($"https://api.pdok.nl/kadaster/bgt/ogc/v1/collections/pand/items?f=json&limit=200&bbox={bbox}").Result;
                foreach (string feature in json.Split(new[] { "{\"type\":\"Feature\"" }, System.StringSplitOptions.None))
                {
                    if (!feature.Contains("\"eind_registratie\":null")) continue;
                    int c = feature.IndexOf("\"coordinates\":");
                    if (c < 0) continue;
                    int end = feature.IndexOf("]]]", c);
                    var nums = System.Text.RegularExpressions.Regex.Matches(feature.Substring(c, end - c), "-?[0-9]+\\.[0-9]+");
                    var poly = new List<Vector2>();
                    for (int i = 0; i + 1 < nums.Count; i += 2)
                    {
                        double lon = double.Parse(nums[i].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double lat = double.Parse(nums[i + 1].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double3 u = georef.TransformEarthCenteredEarthFixedPositionToUnity(CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(lon, lat, 0)));
                        poly.Add(new Vector2((float)u.x, (float)u.z));
                    }
                    if (poly.Count < 3) continue;
                    // Single building (Markthal, Witte Huis, Stadhuis): the outline containing the point.
                    // Cube houses: every outline whose centre is within the radius.
                    double3 pu = georef.TransformEarthCenteredEarthFixedPositionToUnity(CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(l.lon, l.lat, 0)));
                    var point = new Vector2((float)pu.x, (float)pu.z);
                    Vector2 centroid = Vector2.zero; foreach (Vector2 q in poly) centroid += q; centroid /= poly.Count;
                    bool take = l.name == "Cube houses" ? (centroid - point).magnitude <= l.radius : Inside(point, poly);
                    if (take) footprints.Add((l, poly));
                }
            }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            string palettePath = $"{folder}/Textures/BuildingPalette.png";
            var palTex = new Texture2D(2, 2); palTex.LoadImage(File.ReadAllBytes(palettePath));
            int size = palTex.width;
            var pixels = new List<Color>(palTex.GetPixels());
            int used = pixels.Count; while (used > 0 && pixels[used - 1] == Color.white) used--;
            var slots = new Dictionary<Color, int>();
            int Slot(Color c) { if (!slots.TryGetValue(c, out int i)) { i = used++; pixels[i] = c; slots[c] = i; } return i; }
            Vector2 Uv(int slot) => new Vector2((slot % size + 0.5f) / size, (slot / size + 0.5f) / size);

            // Palette slots that hold a landmark colour: anything outside the footprints with those colours
            // (from an earlier, rougher pass) goes back to a neutral soft colour.
            var landmarkColours = new HashSet<int>();
            for (int i = 0; i < used; i++)
                foreach (Landmark l in Landmarks)
                    if (Close(pixels[i], l.walls) || Close(pixels[i], l.roofs)) landmarkColours.Add(i);
            Color neutralWall = new Color(0.85f, 0.82f, 0.77f), neutralRoof = new Color(0.58f, 0.56f, 0.53f);

            var counts = new Dictionary<string, int>();
            foreach (MeshFilter mf in root.transform.Find("Buildings").GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                Vector3[] v = mesh.vertices; Vector3[] n = mesh.normals; Vector2[] uv = mesh.uv; int[] t = mesh.triangles;
                bool changed = false;
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 c3 = (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f;
                    var c = new Vector2(c3.x, c3.z);
                    int currentSlot = Mathf.FloorToInt(uv[t[i]].y * size) * size + Mathf.FloorToInt(uv[t[i]].x * size);
                    bool inAny = false;
                    foreach (var (l, poly) in footprints) if (Inside(c, poly) || DistanceToPolygon(c, poly) <= 1.0f) { inAny = true; break; }
                    if (!inAny && landmarkColours.Contains(currentSlot))
                    {
                        for (int k = 0; k < 3; k++) { int idx = t[i + k]; uv[idx] = Uv(Slot(Mathf.Abs(n[idx].y) > 0.6f ? neutralRoof : neutralWall)); }
                        counts["neutralised"] = counts.TryGetValue("neutralised", out int z) ? z + 1 : 1;
                        changed = true;
                        continue;
                    }
                    foreach (var (l, poly) in footprints)
                    {
                        if (!Inside(c, poly) && DistanceToPolygon(c, poly) > 1.0f) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int idx = t[i + k];
                            uv[idx] = Uv(Slot(Mathf.Abs(n[idx].y) > 0.6f ? l.roofs : l.walls));
                        }
                        counts[l.name] = counts.TryGetValue(l.name, out int x) ? x + 1 : 1;
                        changed = true;
                        break;
                    }
                }
                if (changed) { mesh.SetUVs(0, uv); EditorUtility.SetDirty(mesh); }
            }
            palTex.SetPixels(pixels.ToArray()); palTex.Apply();
            File.WriteAllBytes(palettePath, palTex.EncodeToPNG());
            Object.DestroyImmediate(palTex);
            AssetDatabase.ImportAsset(palettePath);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            var sb = new System.Text.StringBuilder($"{Path.GetFileNameWithoutExtension(prefabPath)}: footprints {footprints.Count}; triangles ");
            foreach (var kv in counts) sb.Append($"{kv.Key}={kv.Value} ");
            return sb.ToString();
        }

        private static bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;

        private static bool Inside(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        private static float DistanceToPolygon(Vector2 p, List<Vector2> poly)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Vector2 a = poly[j], b = poly[i], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                best = Mathf.Min(best, (a + ab * t - p).magnitude);
            }
            return best;
        }

        // Splits a ground mesh into flat (photo) and steep (stone) parts. Returns the number of steep faces.
        private static int SplitSteep(MeshFilter mf, Material stone)
        {
            Mesh mesh = mf.sharedMesh;
            Vector3[] v = mesh.vertices; int[] t = mesh.triangles; // all submeshes together
            var mr0 = mf.GetComponent<MeshRenderer>();
            Material photoMat = mr0.sharedMaterials[0];
            var flat = new List<int>(); var steep = new List<int>();
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 nrm = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).normalized;
                (Mathf.Abs(nrm.y) < 0.9f ? steep : flat).AddRange(new[] { t[i], t[i + 1], t[i + 2] }); // > ~25 degrees: not a street
            }
            if (steep.Count == 0) return 0;
            mesh.subMeshCount = 2;
            mesh.SetTriangles(flat, 0);
            mesh.SetTriangles(steep, 1);
            EditorUtility.SetDirty(mesh);
            var mr = mf.GetComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { photoMat, stone };
            var col = mf.GetComponent<MeshCollider>();
            if (col != null) { col.sharedMesh = null; col.sharedMesh = mesh; }
            return steep.Count / 3;
        }

        private static Material StoneMaterial(string folder)
        {
            string path = $"{folder}/Materials/QuayStone.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", QuayStone);
            m.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        // Soft, pastel version of a photo colour: less saturated, lighter; roofs a bit darker.
        private static Color Soften(Color c, bool roof)
        {
            Color.RGBToHSV(c, out float h, out float s, out float val);
            s = Mathf.Clamp(s * 0.7f, 0.04f, roof ? 0.30f : 0.38f);
            val = roof ? Mathf.Clamp(Mathf.Lerp(val, 0.6f, 0.5f), 0.42f, 0.72f) : Mathf.Clamp(Mathf.Lerp(val, 0.86f, 0.55f), 0.62f, 0.94f);
            return Color.HSVToRGB(h, s, val);
        }

        private static Color Avg(Dictionary<int, Vector4> sums, int k, Color fallback)
        {
            if (!sums.TryGetValue(k, out Vector4 s) || s.w <= 0f) return fallback;
            return new Color(s.x / s.w, s.y / s.w, s.z / s.w);
        }

        // Connected parts (= buildings) of a mesh, welding equal positions.
        private static int[] Components(Vector3[] v, int[] t)
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
            var result = new int[v.Length];
            for (int i = 0; i < v.Length; i++) result[i] = Find(i);
            return result;
        }

        private static Texture2D ReadableCopy(Texture t)
        {
            if (t == null) return null;
            string path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var tex = new Texture2D(2, 2);
            return tex.LoadImage(File.ReadAllBytes(path)) ? tex : null;
        }
    }
}
