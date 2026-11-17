using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Gives the stylized building chunks windows: writes UV1 = (metres along the wall, metres above the
    /// building's foot, 1 = wall) per triangle and switches the material to BDCity/StylizedBuilding,
    /// which draws the windows procedurally. Palette alpha picks the style per colour (1 grid, 0.5 tall, 0 none).
    /// </summary>
    public static class CityWindows
    {
        const float MinBuildingHeight = 3.5f;   // sheds, bus shelters etc. get no windows
        const float WallMaxNormalY = 0.3f;

        public static string AddWindows(string prefabPath)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            int walls = 0, tris = 0;
            foreach (var mf in root.transform.Find("Buildings").GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                var v = mesh.vertices; var t = mesh.triangles;
                var comp = Components(v, t);
                var minY = new Dictionary<int, float>(); var maxY = new Dictionary<int, float>();
                for (int i = 0; i < v.Length; i++)
                {
                    int c = comp[i];
                    minY[c] = minY.TryGetValue(c, out float a) ? Mathf.Min(a, v[i].y) : v[i].y;
                    maxY[c] = maxY.TryGetValue(c, out float b) ? Mathf.Max(b, v[i].y) : v[i].y;
                }
                var uv1 = new Vector3[v.Length];
                for (int i = 0; i < t.Length; i += 3)
                {
                    tris++;
                    int a = t[i], b = t[i + 1], c = t[i + 2];
                    Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                    if (n.sqrMagnitude < 1e-10f) continue;
                    n.Normalize();
                    int k = comp[a];
                    if (Mathf.Abs(n.y) > WallMaxNormalY || maxY[k] - minY[k] < MinBuildingHeight) continue;
                    Vector3 tangent = Vector3.Cross(Vector3.up, n).normalized;
                    foreach (int idx in new[] { a, b, c })
                        uv1[idx] = new Vector3(Vector3.Dot(v[idx], tangent), v[idx].y - minY[k], 1f);
                    walls++;
                }
                mesh.SetUVs(1, uv1);
                EditorUtility.SetDirty(mesh);
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);

            // Palette with alpha = window style.
            string palettePath = $"{folder}/Textures/BuildingPalette.png";
            var pal = new Texture2D(2, 2); pal.LoadImage(File.ReadAllBytes(palettePath));
            var px = pal.GetPixels();
            var tex = new Texture2D(pal.width, pal.height, TextureFormat.RGBA32, false);
            Color tall = CityStylizer.Landmarks[3].walls; // Stadhuis: tall narrow windows
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                bool isTall = Mathf.Abs(c.r - tall.r) < 0.02f && Mathf.Abs(c.g - tall.g) < 0.02f && Mathf.Abs(c.b - tall.b) < 0.02f;
                c.a = isTall ? 0.5f : 1f;
                px[i] = c;
            }
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(palettePath, tex.EncodeToPNG());
            Object.DestroyImmediate(pal); Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(palettePath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(palettePath);
            ti.alphaIsTransparency = false; ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.SaveAndReimport();

            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/Materials/BuildingsStylized.mat");
            var palette = AssetDatabase.LoadAssetAtPath<Texture2D>(palettePath);
            mat.shader = Shader.Find("BDCity/StylizedBuilding");
            mat.SetTexture("_BaseMap", palette);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: {walls}/{tris} triangles got window coordinates";
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
