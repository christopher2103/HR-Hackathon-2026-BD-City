using System.Collections.Generic;
using System.IO;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Saves the streamed city around a point as normal Unity assets (a prefab per neighbourhood), so the demo
    /// runs without internet and the area can be edited/improved.
    ///  - Ground: the terrain tiles near the spot, merged into 200 m chunks with a sharp PDOK aerial photo (~10 cm/px).
    ///  - Far ground: the remaining terrain tiles further out, as they are (low detail).
    ///  - Buildings: every building tile within a radius, with its own texture (512 px near, 256 px far);
    ///    colliders only near the spot (walls, teleport, bots).
    /// Run in Play mode once the city has fully loaded at the spot (frustum culling off), via Freeze(...).
    /// </summary>
    public static class CityFreezer
    {
        private const string Root = "Assets/CityFeature/Frozen";
        private const string Wms = "https://service.pdok.nl/hwh/luchtfotorgb/wms/v1_0?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS=Actueel_orthoHR&STYLES=&CRS=EPSG:4326&FORMAT=image/jpeg";

        public static string Freeze(string areaName, Vector3 center, float groundHalfSize = 300f, float chunkSize = 200f,
                                    float buildingRadius = 800f, float farGroundRadius = 1500f, float colliderRadius = 350f)
        {
            var log = new System.Text.StringBuilder();
            string folder = $"{Root}/{Safe(areaName)}";
            foreach (string sub in new[] { "", "/Meshes", "/Textures", "/Materials" }) Directory.CreateDirectory(folder + sub);

            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            Cesium3DTileset terrain = null, buildings = null;
            foreach (var ts in Object.FindObjectsByType<Cesium3DTileset>(FindObjectsSortMode.None))
                if (ts.name.StartsWith("Terrain")) terrain = ts; else if (ts.name.StartsWith("Buildings")) buildings = ts;

            var root = new GameObject($"Frozen - {areaName}");
            var groundRoot = new GameObject("Ground").transform; groundRoot.SetParent(root.transform);
            var farRoot = new GameObject("Far Ground").transform; farRoot.SetParent(root.transform);
            var buildingRoot = new GameObject("Buildings").transform; buildingRoot.SetParent(root.transform);

            var pendingTextures = new List<(string path, int maxSize, bool aniso)>();
            var pendingMaterials = new List<(Material mat, string prop, string texPath)>();

            AssetDatabase.StartAssetEditing();
            try
            {
                // ---------- Ground (near): merge per chunk, aerial photo ----------
                var chunks = new Dictionary<Vector2Int, List<MeshRenderer>>();
                var farTiles = new List<MeshRenderer>();
                foreach (MeshRenderer r in terrain.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!r.enabled) continue; // tile not shown right now (replaced by a more detailed one)
                    if (r.bounds.size.x > 5000f || r.bounds.size.z > 5000f) continue; // whole-world root tiles
                    Bounds b = r.bounds;
                    bool near = b.max.x >= center.x - groundHalfSize && b.min.x <= center.x + groundHalfSize &&
                                b.max.z >= center.z - groundHalfSize && b.min.z <= center.z + groundHalfSize;
                    if (near)
                    {
                        var key = new Vector2Int(Mathf.FloorToInt((b.center.x - center.x) / chunkSize), Mathf.FloorToInt((b.center.z - center.z) / chunkSize));
                        if (!chunks.TryGetValue(key, out var list)) chunks[key] = list = new List<MeshRenderer>();
                        list.Add(r);
                    }
                    else if (FlatDistance(b, center) <= farGroundRadius) farTiles.Add(r);
                }

                var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(90) };
                Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                foreach (var pair in chunks)
                {
                    string name = $"Ground_{pair.Key.x}_{pair.Key.y}";
                    Mesh mesh = Merge(pair.Value, out List<Vector3> worldVerts);

                    // UVs from longitude/latitude, so the WMS image (EPSG:4326) lines up exactly.
                    var lonLat = new List<double2>(worldVerts.Count);
                    double minLon = double.MaxValue, minLat = double.MaxValue, maxLon = double.MinValue, maxLat = double.MinValue;
                    foreach (Vector3 v in worldVerts)
                    {
                        double3 ecef = georef.TransformUnityPositionToEarthCenteredEarthFixed(new double3(v.x, v.y, v.z));
                        double3 llh = CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(ecef);
                        lonLat.Add(llh.xy);
                        minLon = System.Math.Min(minLon, llh.x); maxLon = System.Math.Max(maxLon, llh.x);
                        minLat = System.Math.Min(minLat, llh.y); maxLat = System.Math.Max(maxLat, llh.y);
                    }
                    var uvs = new List<Vector2>(lonLat.Count);
                    foreach (double2 ll in lonLat)
                        uvs.Add(new Vector2((float)((ll.x - minLon) / (maxLon - minLon)), (float)((ll.y - minLat) / (maxLat - minLat))));
                    mesh.SetUVs(0, uvs);
                    AssetDatabase.CreateAsset(mesh, $"{folder}/Meshes/{name}.asset");

                    // Aerial photo, about 10 cm per pixel (max 2048).
                    float extent = Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.z);
                    int px = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(extent / 0.1f)), 512, 2048);
                    string url = $"{Wms}&BBOX={minLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{minLon.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture)},{maxLon.ToString(System.Globalization.CultureInfo.InvariantCulture)}&WIDTH={px}&HEIGHT={px}";
                    string texPath = $"{folder}/Textures/{name}.jpg";
                    File.WriteAllBytes(texPath, http.GetByteArrayAsync(url).Result);
                    pendingTextures.Add((texPath, 2048, true));

                    var mat = new Material(lit) { name = name };
                    mat.SetFloat("_Smoothness", 0.15f);
                    AssetDatabase.CreateAsset(mat, $"{folder}/Materials/{name}.mat");
                    pendingMaterials.Add((mat, "_BaseMap", texPath));

                    var go = new GameObject(name);
                    go.transform.SetParent(groundRoot, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                    go.AddComponent<CityPart>().isGround = true;
                    go.AddComponent<TeleportationArea>().interactionLayers = 1 << 31;
                    go.isStatic = true;
                }
                log.AppendLine($"Ground chunks: {chunks.Count}");

                // ---------- Far ground + buildings: copy tiles with their own textures ----------
                int farCount = 0, buildingCount = 0, colliders = 0;
                var textureCache = new Dictionary<Texture, string>();
                foreach (MeshRenderer r in farTiles)
                {
                    CopyTile(r, farRoot, folder, $"Far_{farCount++}", 256, false, false, textureCache, pendingTextures, pendingMaterials);
                }
                foreach (MeshRenderer r in buildings.GetComponentsInChildren<MeshRenderer>())
                {
                    if (!r.enabled) continue;
                    float d = FlatDistance(r.bounds, center);
                    if (d > buildingRadius) continue;
                    bool near = d <= colliderRadius;
                    CopyTile(r, buildingRoot, folder, $"Building_{buildingCount++}", near ? 512 : 256, near, true, textureCache, pendingTextures, pendingMaterials);
                    if (near) colliders++;
                }
                log.AppendLine($"Far ground tiles: {farCount}, building tiles: {buildingCount} ({colliders} with colliders)");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            // Import textures with the right settings, then hook them up.
            AssetDatabase.Refresh();
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (path, maxSize, aniso) in pendingTextures)
                {
                    var ti = (TextureImporter)AssetImporter.GetAtPath(path);
                    if (ti == null) continue;
                    ti.maxTextureSize = maxSize;
                    ti.anisoLevel = aniso ? 8 : 2;
                    ti.wrapMode = TextureWrapMode.Clamp;
                    ti.mipmapEnabled = true;
                    ti.textureCompression = TextureImporterCompression.Compressed;
                    ti.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            foreach (var (mat, prop, texPath) in pendingMaterials)
            {
                mat.SetTexture(prop, AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();

            string prefabPath = $"{folder}/Frozen - {Safe(areaName)}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            log.AppendLine("Saved " + prefabPath);
            return log.ToString();
        }

        /// <summary>
        /// Makes a saved neighbourhood lighter for Quest: keeps every building tile near the spot, but further away
        /// only tall ones (the skyline); drops far ground beyond a radius. Deletes the removed meshes/materials/textures.
        /// </summary>
        public static string Thin(string prefabPath, Vector3 center, float keepAllWithin = 450f, float minHeightBeyond = 35f, float farGroundRadius = 1000f)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            var toDelete = new List<GameObject>();
            long before = 0, after = 0;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.GetComponentInParent<CityPart>() == null) continue;
                long verts = mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0;
                before += verts;
                Bounds b = mf.sharedMesh.bounds; // meshes are baked in world space
                float d = FlatDistance(b, center);
                string group = mf.transform.parent.name;
                bool remove = (group == "Buildings" && d > keepAllWithin && b.max.y - center.y < minHeightBeyond)
                           || (group == "Far Ground" && d > farGroundRadius);
                if (remove) toDelete.Add(mf.gameObject); else after += verts;
            }
            var assets = new HashSet<string>();
            foreach (GameObject go in toDelete)
            {
                var mf = go.GetComponent<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                if (mf.sharedMesh != null) assets.Add(AssetDatabase.GetAssetPath(mf.sharedMesh));
                if (mr.sharedMaterial != null)
                {
                    assets.Add(AssetDatabase.GetAssetPath(mr.sharedMaterial));
                    foreach (string prop in mr.sharedMaterial.GetTexturePropertyNames())
                    {
                        Texture t = mr.sharedMaterial.GetTexture(prop);
                        if (t != null) assets.Add(AssetDatabase.GetAssetPath(t));
                    }
                }
                Object.DestroyImmediate(go);
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.StartAssetEditing();
            try { foreach (string a in assets) if (a.StartsWith(folder)) AssetDatabase.DeleteAsset(a); }
            finally { AssetDatabase.StopAssetEditing(); }
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: removed {toDelete.Count} tiles, vertices {before / 1000}k -> {after / 1000}k";
        }

        /// <summary>
        /// Per-building thinning: inside every building tile, finds the separate buildings (connected triangles)
        /// and removes the low ones that are far away (keeps the skyline). Meshes are changed in place.
        /// </summary>
        public static string ThinBuildings(string prefabPath, Vector3 center, float keepAllWithin = 300f, float minHeight = 30f)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            long before = 0, after = 0;
            var empty = new List<GameObject>();
            foreach (MeshFilter mf in root.transform.Find("Buildings").GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                Vector3[] v = mesh.vertices; Vector3[] n = mesh.normals; Vector2[] uv = mesh.uv; int[] t = mesh.triangles;
                before += v.Length;

                // Union-find over welded positions (faces of one building often have their own vertices).
                var parent = new int[v.Length];
                for (int i = 0; i < v.Length; i++) parent[i] = i;
                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                void Union(int x, int y) { x = Find(x); y = Find(y); if (x != y) parent[x] = y; }
                var weld = new Dictionary<Vector3Int, int>();
                for (int i = 0; i < v.Length; i++)
                {
                    var key = new Vector3Int(Mathf.RoundToInt(v[i].x * 20f), Mathf.RoundToInt(v[i].y * 20f), Mathf.RoundToInt(v[i].z * 20f));
                    if (weld.TryGetValue(key, out int other)) Union(i, other); else weld[key] = i;
                }
                for (int i = 0; i < t.Length; i += 3) { Union(t[i], t[i + 1]); Union(t[i], t[i + 2]); }

                // Per building: lowest/highest point and closest distance to the spot.
                var minY = new Dictionary<int, float>(); var maxY = new Dictionary<int, float>(); var dist = new Dictionary<int, float>();
                for (int i = 0; i < v.Length; i++)
                {
                    int r = Find(i);
                    float d = new Vector2(v[i].x - center.x, v[i].z - center.z).magnitude;
                    if (!minY.ContainsKey(r)) { minY[r] = v[i].y; maxY[r] = v[i].y; dist[r] = d; }
                    else { minY[r] = Mathf.Min(minY[r], v[i].y); maxY[r] = Mathf.Max(maxY[r], v[i].y); dist[r] = Mathf.Min(dist[r], d); }
                }
                bool Keep(int r) => dist[r] <= keepAllWithin || maxY[r] - minY[r] >= minHeight;

                var map = new int[v.Length];
                for (int i = 0; i < map.Length; i++) map[i] = -1;
                var nv = new List<Vector3>(); var nn = new List<Vector3>(); var nuv = new List<Vector2>(); var nt = new List<int>();
                for (int i = 0; i < t.Length; i += 3)
                {
                    if (!Keep(Find(t[i]))) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        int idx = t[i + k];
                        if (map[idx] < 0)
                        {
                            map[idx] = nv.Count;
                            nv.Add(v[idx]);
                            nn.Add(n.Length == v.Length ? n[idx] : Vector3.up);
                            nuv.Add(uv.Length == v.Length ? uv[idx] : Vector2.zero);
                        }
                        nt.Add(map[idx]);
                    }
                }
                after += nv.Count;
                if (nv.Count == v.Length) continue;
                if (nv.Count == 0) { empty.Add(mf.gameObject); continue; }
                mesh.Clear();
                mesh.indexFormat = nv.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(nv); mesh.SetNormals(nn); mesh.SetUVs(0, nuv); mesh.SetTriangles(nt, 0);
                mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
                var col = mf.GetComponent<MeshCollider>();
                if (col != null) { col.sharedMesh = null; col.sharedMesh = mesh; }
            }
            foreach (GameObject go in empty) Object.DestroyImmediate(go);
            AssetDatabase.SaveAssets();
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: building vertices {before / 1000}k -> {after / 1000}k ({empty.Count} tiles emptied)";
        }

        // Copy one tile: mesh baked to world space (so it no longer depends on the georeference transform),
        // material copied with textures saved as files.
        private static void CopyTile(MeshRenderer r, Transform parent, string folder, string name, int texSize, bool collider, bool building,
                                     Dictionary<Texture, string> textureCache,
                                     List<(string, int, bool)> pendingTextures, List<(Material, string, string)> pendingMaterials)
        {
            Mesh mesh = Merge(new List<MeshRenderer> { r }, out _);
            AssetDatabase.CreateAsset(mesh, $"{folder}/Meshes/{name}.asset");

            var mat = new Material(r.sharedMaterial) { name = name };
            AssetDatabase.CreateAsset(mat, $"{folder}/Materials/{name}.mat");
            foreach (string prop in r.sharedMaterial.GetTexturePropertyNames())
            {
                Texture src = r.sharedMaterial.GetTexture(prop);
                if (src == null) continue;
                if (src.width <= 1) { mat.SetTexture(prop, null); continue; } // 1x1 defaults: shader default is fine
                if (!textureCache.TryGetValue(src, out string texPath))
                {
                    texPath = $"{folder}/Textures/{name}_{prop.Trim('_')}.jpg";
                    SaveTexture(src, texPath);
                    textureCache[src] = texPath;
                    pendingTextures.Add((texPath, texSize, building));
                }
                mat.SetTexture(prop, null);
                pendingMaterials.Add((mat, prop, texPath));
            }

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = building ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<CityPart>().isGround = !building;
            if (collider) go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
        }

        // Bake the renderers' meshes into one world-space mesh (flips the triangle order for mirrored transforms).
        private static Mesh Merge(List<MeshRenderer> renderers, out List<Vector3> worldVerts)
        {
            worldVerts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            foreach (MeshRenderer r in renderers)
            {
                Mesh src = r.GetComponent<MeshFilter>().sharedMesh;
                Matrix4x4 m = r.transform.localToWorldMatrix;
                Matrix4x4 n = m.inverse.transpose;
                bool flip = m.determinant < 0f;
                int offset = worldVerts.Count;
                var v = src.vertices; var nn = src.normals; var uv = src.uv;
                for (int i = 0; i < v.Length; i++)
                {
                    worldVerts.Add(m.MultiplyPoint3x4(v[i]));
                    normals.Add(nn.Length == v.Length ? n.MultiplyVector(nn[i]).normalized : Vector3.up);
                    uvs.Add(uv.Length == v.Length ? uv[i] : Vector2.zero);
                }
                int[] t = src.triangles;
                for (int i = 0; i < t.Length; i += 3)
                {
                    tris.Add(offset + t[i]);
                    tris.Add(offset + (flip ? t[i + 2] : t[i + 1]));
                    tris.Add(offset + (flip ? t[i + 1] : t[i + 2]));
                }
            }
            var mesh = new Mesh { indexFormat = worldVerts.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            mesh.SetVertices(worldVerts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        // Runtime textures from Cesium are not readable: copy them through the GPU.
        private static void SaveTexture(Texture src, string path)
        {
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(path, tex.EncodeToJPG(88));
            Object.DestroyImmediate(tex);
        }

        private static float FlatDistance(Bounds b, Vector3 p)
        {
            Vector3 c = b.ClosestPoint(new Vector3(p.x, b.center.y, p.z));
            return new Vector2(c.x - p.x, c.z - p.z).magnitude;
        }

        private static string Safe(string s) => s.Replace(" ", "");
    }
}
