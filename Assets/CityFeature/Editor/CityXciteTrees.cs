using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Illustrated "cross-plane" trees in the X-CITE style (3 crossed alpha-cut pictures per tree),
    /// and a tool that swaps the trees in the frozen neighbourhood prefabs for them.
    /// </summary>
    public static class CityXciteTrees
    {
        const string Folder = "Assets/CityFeature/Props/XciteTrees";
        public static readonly string[] Names = { "Robinia", "Tree1", "Tree2", "Ash", "Maple", "Cross1", "Cross2", "Birch" };
        const float BaseHeight = 9f;

        [MenuItem("B&D City/Trees/Build X-CITE tree prefabs")]
        public static void BuildPrefabs()
        {
            foreach (var n in Names)
            {
                string png = $"{Folder}/XTree_{n}.png";
                var imp = (TextureImporter)AssetImporter.GetAtPath(png);
                imp.textureType = TextureImporterType.Default;
                imp.alphaIsTransparency = true;
                imp.mipmapEnabled = true;
                imp.mipMapsPreserveCoverage = true;
                imp.alphaTestReferenceValue = 0.5f;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.maxTextureSize = 1024;
                imp.sRGBTexture = true;
                imp.SaveAndReimport();
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(png);

                string matPath = $"{Folder}/M_XTree_{n}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null)
                {
                    mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    AssetDatabase.CreateAsset(mat, matPath);
                }
                mat.SetTexture("_BaseMap", tex);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_Cutoff", 0.5f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", 0f);
                mat.doubleSidedGI = true;
                mat.SetFloat("_Smoothness", 0f);
                mat.SetFloat("_SpecularHighlights", 0f);
                mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                mat.SetFloat("_EnvironmentReflections", 0f);
                mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                mat.renderQueue = 2450;
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                EditorUtility.SetDirty(mat);

                string meshPath = $"{Folder}/XTree_{n}.asset";
                var mesh = BuildCross(BaseHeight * tex.width / tex.height, BaseHeight);
                mesh.name = $"XTree_{n}";
                var old = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (old != null) { EditorUtility.CopySerialized(mesh, old); mesh = old; }
                else AssetDatabase.CreateAsset(mesh, meshPath);

                var go = new GameObject($"XTree_{n}");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                var col = go.AddComponent<CapsuleCollider>();
                col.radius = 0.3f; col.height = 3f; col.center = new Vector3(0, 1.5f, 0);
                go.isStatic = true;
                PrefabUtility.SaveAsPrefabAsset(go, $"{Folder}/XTree_{n}.prefab");
                Object.DestroyImmediate(go);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[CityXciteTrees] Built " + Names.Length + " tree prefabs");
        }

        /// <summary>Three vertical quads at 0/60/120 degrees, pivot at the trunk foot. Normals point up so
        /// every plane is lit the same from all sides (like X-CITE's flat illustrated look).</summary>
        static Mesh BuildCross(float width, float height)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var nr = new List<Vector3>(); var idx = new List<int>();
            for (int p = 0; p < 3; p++)
            {
                float a = p * Mathf.PI / 3f;
                var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * (width * 0.5f);
                int b = v.Count;
                v.Add(-d); v.Add(d); v.Add(-d + Vector3.up * height); v.Add(d + Vector3.up * height);
                uv.Add(new Vector2(0, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(0, 1)); uv.Add(new Vector2(1, 1));
                for (int k = 0; k < 4; k++) nr.Add(Vector3.up);
                idx.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }
            var m = new Mesh();
            m.SetVertices(v); m.SetUVs(0, uv); m.SetNormals(nr); m.SetTriangles(idx, 0);
            m.RecalculateBounds();
            return m;
        }

        /// <summary>Replace every tree under Props/Trees in a frozen prefab with a random X-CITE tree (even mix).</summary>
        public static int ReplaceTrees(string prefabPath, int seed = 1234)
        {
            var prefabs = new GameObject[Names.Length];
            for (int i = 0; i < Names.Length; i++)
                prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>($"{Folder}/XTree_{Names[i]}.prefab");

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var trees = root.transform.Find("Props/Trees");
            var rng = new System.Random(seed);
            var old = new List<Transform>();
            foreach (Transform t in trees) old.Add(t);
            foreach (var t in old)
            {
                var pos = t.position; float s = t.localScale.x;
                Object.DestroyImmediate(t.gameObject);
                var n = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[rng.Next(prefabs.Length)], trees);
                n.transform.position = pos;
                n.transform.rotation = Quaternion.Euler(0, (float)rng.NextDouble() * 360f, 0);
                float h = s * (0.85f + 0.3f * (float)rng.NextDouble());
                n.transform.localScale = Vector3.one * h;
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            return old.Count;
        }
    }
}
