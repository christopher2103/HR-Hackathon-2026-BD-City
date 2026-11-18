using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Makes the Far Ground cheap for Quest: ~80-95 Cesium tile meshes (2 materials each, ~250k tris)
    /// become a few merged, simplified chunks with ONE unlit material whose texture is a top-down bake
    /// of the original far ground (so it looks the same from the street).
    /// Re-runnable: it always reads the original tiles from the scene instance... unless they were already
    /// replaced, in which case it does nothing. The old Far_* assets stay on disk (unreferenced).
    /// </summary>
    public static class CityFarGround
    {
        const int BakeLayer = 30;
        const int BakeSize = 2048;
        const float ClusterXZ = 14f;   // interior vertices closer than this get merged
        const float ClusterY = 3f;     // ... but never across height steps (keeps quay walls / skirts)
        const int ChunksPerSide = 4;

        public static string Simplify(string prefabPath)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string area = Path.GetFileName(folder);

            // 1. The original tiles, from the scene instance of this prefab.
            GameObject instance = null;
            foreach (var go in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (go.parent != null && go.parent.name == "Frozen Neighbourhoods" &&
                    PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go.gameObject) == prefabPath) { instance = go.gameObject; break; }
            if (instance == null) return $"{area}: no scene instance found";
            Transform far = instance.transform.Find("Far Ground");
            var filters = far.GetComponentsInChildren<MeshFilter>(true);
            if (filters.Length <= ChunksPerSide * ChunksPerSide && filters.Length > 0 && filters[0].name.StartsWith("FarGround_"))
                return $"{area}: already simplified";

            // 2. Top-down colour bake of the original tiles (rendered with the scene's light, shown unlit later).
            var copy = Object.Instantiate(far.gameObject);
            copy.SetActive(true);
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = BakeLayer;
            Bounds b = default; bool first = true;
            foreach (var r in copy.GetComponentsInChildren<Renderer>(true)) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
            float size = Mathf.Max(b.size.x, b.size.z) + 8f;
            Vector2 min = new Vector2(b.center.x - size * 0.5f, b.center.z - size * 0.5f);
            string bakePath = $"{folder}/Textures/FarGround_bake.png";
            Bake(new Vector3(b.center.x, b.max.y + 200f, b.center.z), size, bakePath);
            Object.DestroyImmediate(copy);

            // 3. Merge all tiles (all submeshes) in world space, welding equal positions.
            var pos = new List<Vector3>();
            var weld = new Dictionary<Vector3Int, int>();
            var tris = new List<int>();
            long trisIn = 0;
            foreach (var mf in filters)
            {
                Mesh m = mf.sharedMesh; if (m == null) continue;
                Matrix4x4 l2w = mf.transform.localToWorldMatrix;
                Vector3[] v = m.vertices; int[] t = m.triangles;
                var map = new int[v.Length];
                for (int i = 0; i < v.Length; i++)
                {
                    Vector3 w = l2w.MultiplyPoint3x4(v[i]);
                    var key = new Vector3Int(Mathf.RoundToInt(w.x * 20f), Mathf.RoundToInt(w.y * 20f), Mathf.RoundToInt(w.z * 20f));
                    if (!weld.TryGetValue(key, out int idx)) { idx = pos.Count; pos.Add(w); weld[key] = idx; }
                    map[i] = idx;
                }
                for (int i = 0; i < t.Length; i++) tris.Add(map[t[i]]);
                trisIn += t.Length / 3;
            }

            // 4. Boundary vertices (edge used once) stay put, so the seam with the near Ground does not move.
            var edgeUse = new Dictionary<long, int>();
            long Edge(int a, int c) => a < c ? ((long)a << 32) | (uint)c : ((long)c << 32) | (uint)a;
            for (int i = 0; i < tris.Count; i += 3)
                for (int k = 0; k < 3; k++) { long e = Edge(tris[i + k], tris[i + (k + 1) % 3]); edgeUse[e] = edgeUse.TryGetValue(e, out int u) ? u + 1 : 1; }
            var boundary = new bool[pos.Count];
            foreach (var kv in edgeUse) if (kv.Value == 1) { boundary[(int)(kv.Key >> 32)] = true; boundary[(int)(kv.Key & 0xffffffff)] = true; }

            // 5. Vertex clustering of the interior.
            var cluster = new int[pos.Count];
            var cPos = new List<Vector3>(); var cCount = new List<int>();
            var cKey = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (boundary[i]) { cluster[i] = cPos.Count; cPos.Add(pos[i]); cCount.Add(1); continue; }
                var key = new Vector3Int(Mathf.FloorToInt(pos[i].x / ClusterXZ), Mathf.FloorToInt(pos[i].y / ClusterY), Mathf.FloorToInt(pos[i].z / ClusterXZ));
                if (!cKey.TryGetValue(key, out int c)) { c = cPos.Count; cPos.Add(Vector3.zero); cCount.Add(0); cKey[key] = c; }
                cluster[i] = c; cPos[c] += pos[i]; cCount[c]++;
            }
            for (int c = 0; c < cPos.Count; c++) if (cCount[c] > 1) cPos[c] /= cCount[c];

            // 6. Re-index, drop collapsed / duplicate triangles, split into chunks by centroid.
            var chunkTris = new Dictionary<int, List<int>>();
            var seen = new HashSet<(int, int, int)>();
            float chunk = size / ChunksPerSide;
            for (int i = 0; i < tris.Count; i += 3)
            {
                int a = cluster[tris[i]], c1 = cluster[tris[i + 1]], c2 = cluster[tris[i + 2]];
                if (a == c1 || a == c2 || c1 == c2) continue;
                Vector3 n = Vector3.Cross(cPos[c1] - cPos[a], cPos[c2] - cPos[a]);
                if (n.sqrMagnitude < 1e-6f) continue;
                int lo = Mathf.Min(a, Mathf.Min(c1, c2)), hi = Mathf.Max(a, Mathf.Max(c1, c2)), mid = a + c1 + c2 - lo - hi;
                if (!seen.Add((lo, mid, hi))) continue;
                Vector3 ctr = (cPos[a] + cPos[c1] + cPos[c2]) / 3f;
                int cx = Mathf.Clamp(Mathf.FloorToInt((ctr.x - min.x) / chunk), 0, ChunksPerSide - 1);
                int cz = Mathf.Clamp(Mathf.FloorToInt((ctr.z - min.y) / chunk), 0, ChunksPerSide - 1);
                int ck = cx * ChunksPerSide + cz;
                if (!chunkTris.TryGetValue(ck, out var list)) chunkTris[ck] = list = new List<int>();
                list.Add(a); list.Add(c1); list.Add(c2);
            }

            // 7. Material (unlit, the bake) and the new prefab content.
            string matPath = $"{folder}/Materials/FarGroundBaked.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")); AssetDatabase.CreateAsset(mat, matPath); }
            mat.shader = Shader.Find("Universal Render Pipeline/Unlit");
            mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(bakePath));
            mat.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(mat);

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            Transform farP = root.transform.Find("Far Ground");
            for (int i = farP.childCount - 1; i >= 0; i--) Object.DestroyImmediate(farP.GetChild(i).gameObject);
            long trisOut = 0;
            foreach (var kv in chunkTris)
            {
                var remap = new Dictionary<int, int>(); var v = new List<Vector3>(); var uv = new List<Vector2>(); var t = new List<int>();
                foreach (int ci in kv.Value)
                {
                    if (!remap.TryGetValue(ci, out int ni))
                    {
                        ni = v.Count; remap[ci] = ni;
                        Vector3 p = cPos[ci];
                        v.Add(p); uv.Add(new Vector2((p.x - min.x) / size, (p.z - min.y) / size));
                    }
                    t.Add(ni);
                }
                string name = $"FarGround_{kv.Key / ChunksPerSide}_{kv.Key % ChunksPerSide}";
                string meshPath = $"{folder}/Meshes/{name}.asset";
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, meshPath); }
                mesh.Clear();
                mesh.name = name;
                mesh.indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(t, 0);
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                EditorUtility.SetDirty(mesh);
                trisOut += t.Count / 3;

                var go = new GameObject(name);
                go.transform.SetParent(farP, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                go.AddComponent<CityPart>().isGround = true;
                go.isStatic = true;
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{area}: far ground {filters.Length} meshes / {trisIn / 1000}k tris -> {chunkTris.Count} chunks / {trisOut / 1000}k tris, 1 material";
        }

        static void Bake(Vector3 camPos, float size, string path)
        {
            var rt = new RenderTexture(BakeSize, BakeSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            var camGo = new GameObject("FarGroundBakeCam") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true; cam.orthographicSize = size * 0.5f; cam.aspect = 1f;
            cam.transform.SetPositionAndRotation(camPos, Quaternion.Euler(90f, 0f, 0f));
            cam.nearClipPlane = 1f; cam.farClipPlane = 1000f;
            cam.cullingMask = 1 << BakeLayer;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.62f, 0.60f, 0.56f);
            cam.targetTexture = rt; cam.enabled = false;
            var data = camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            data.renderPostProcessing = false; data.renderShadows = false;
            bool fog = RenderSettings.fog; RenderSettings.fog = false;
            cam.Render();
            RenderSettings.fog = fog;

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(BakeSize, BakeSize, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, BakeSize, BakeSize), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex); Object.DestroyImmediate(camGo); rt.Release(); Object.DestroyImmediate(rt);

            AssetDatabase.ImportAsset(path);
            var ti = (TextureImporter)AssetImporter.GetAtPath(path);
            ti.maxTextureSize = BakeSize; ti.mipmapEnabled = true; ti.wrapMode = TextureWrapMode.Clamp;
            ti.textureCompression = TextureImporterCompression.Compressed; ti.sRGBTexture = true;
            ti.SaveAndReimport();
        }
    }
}
