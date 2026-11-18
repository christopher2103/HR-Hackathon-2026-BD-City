using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CharacterFeature.EditorTools
{
    /// <summary>
    /// Bakes which body triangles each clothing piece covers, for every AvatarWardrobe in the selected prefab.
    /// A triangle counts as covered when the clothing surface is close above it (within 7 cm outward)
    /// or just inside it (within 2 cm), at its centre and at least two of its corners.
    /// </summary>
    public static class BodyCoverBaker
    {
        const float Out = 0.07f, In = 0.02f;

        public static string Bake(string prefabPath, string bodyName)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var wardrobe = root.GetComponent<AvatarWardrobe>();
            SkinnedMeshRenderer body = null;
            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (r.name == bodyName) body = r;

            // Every clothing renderer used by the wardrobe.
            var parts = new List<Renderer>();
            var so = new SerializedObject(wardrobe);
            var slots = so.FindProperty("slots");
            for (int i = 0; i < slots.arraySize; i++)
            {
                // Hair never hides skin: it's see-through at the edges, a bald patch would show.
                if (slots.GetArrayElementAtIndex(i).FindPropertyRelative("name").stringValue == "Hair") continue;
                var opts = slots.GetArrayElementAtIndex(i).FindPropertyRelative("options");
                for (int k = 0; k < opts.arraySize; k++)
                {
                    var ps = opts.GetArrayElementAtIndex(k).FindPropertyRelative("parts");
                    for (int j = 0; j < ps.arraySize; j++)
                    {
                        var r = ps.GetArrayElementAtIndex(j).objectReferenceValue as Renderer;
                        if (r != null && !parts.Contains(r)) parts.Add(r);
                    }
                }
            }

            // Bind-pose snapshot of the body and every part (all in the same space).
            var bodyMesh = new Mesh(); body.BakeMesh(bodyMesh, true);
            Matrix4x4 bodyM = body.transform.localToWorldMatrix;
            var bv = bodyMesh.vertices; var bn = bodyMesh.normals;
            for (int i = 0; i < bv.Length; i++) { bv[i] = bodyM.MultiplyPoint3x4(bv[i]); bn[i] = bodyM.MultiplyVector(bn[i]).normalized; }
            int[] tris = body.sharedMesh.GetTriangles(0);

            bool backfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            var cover = new int[tris.Length / 3];
            var temp = new List<GameObject>();
            try
            {
                for (int p = 0; p < parts.Count && p < 31; p++)
                {
                    var smr = parts[p] as SkinnedMeshRenderer;
                    if (smr == null) continue;
                    var m = new Mesh(); smr.BakeMesh(m, true);
                    var go = new GameObject("cover_probe") { hideFlags = HideFlags.HideAndDontSave };
                    go.transform.SetPositionAndRotation(smr.transform.position + Vector3.up * 1000f, smr.transform.rotation);
                    go.transform.localScale = smr.transform.lossyScale;
                    var mc = go.AddComponent<MeshCollider>(); mc.sharedMesh = m;
                    temp.Add(go);
                    Physics.SyncTransforms();
                    bool Hit(Vector3 pos, Vector3 n)
                    {
                        Vector3 start = pos + Vector3.up * 1000f - n * In;
                        return mc.Raycast(new Ray(start, n), out _, In + Out);
                    }
                    int count = 0;
                    for (int t = 0; t < cover.Length; t++)
                    {
                        int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                        Vector3 cen = (bv[a] + bv[b] + bv[c]) / 3f, cn = (bn[a] + bn[b] + bn[c]).normalized;
                        if (!Hit(cen, cn)) continue;
                        int corners = (Hit(bv[a], bn[a]) ? 1 : 0) + (Hit(bv[b], bn[b]) ? 1 : 0) + (Hit(bv[c], bn[c]) ? 1 : 0);
                        if (corners < 2) continue;
                        cover[t] |= 1 << p; count++;
                    }
                    Debug.Log($"[BodyCover] {parts[p].name}: covers {count} body triangles");
                }
            }
            finally
            {
                Physics.queriesHitBackfaces = backfaces;
                foreach (var g in temp) Object.DestroyImmediate(g);
            }

            string dataPath = System.IO.Path.ChangeExtension(prefabPath, null) + "_BodyCover.asset";
            var data = AssetDatabase.LoadAssetAtPath<BodyCoverData>(dataPath);
            if (data == null) { data = ScriptableObject.CreateInstance<BodyCoverData>(); AssetDatabase.CreateAsset(data, dataPath); }
            data.triangles = tris; data.cover = cover;
            EditorUtility.SetDirty(data);

            so.FindProperty("body").objectReferenceValue = body;
            so.FindProperty("cover").objectReferenceValue = data;
            var cp = so.FindProperty("coverParts"); cp.ClearArray();
            for (int i = 0; i < parts.Count; i++) { cp.InsertArrayElementAtIndex(i); cp.GetArrayElementAtIndex(i).objectReferenceValue = parts[i]; }
            so.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"baked {parts.Count} clothing parts over {cover.Length} body triangles";
        }

        [MenuItem("B&D City/Avatar/Bake body cover (BD_Female)")]
        public static void BakeFemale() =>
            Debug.Log("[BodyCover] " + Bake("Assets/CharacterFeature/Female/Prefabs/BD_Female.prefab", "female_genericMesh"));
    }
}
