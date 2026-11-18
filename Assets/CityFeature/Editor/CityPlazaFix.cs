using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Cleans up the Blaak plaza terrain (coarse elevation data made it look like a crater field):
    ///  - the Blaak metro hole becomes a clean round shaft (flat rim, vertical wall, flat floor; still dry),
    ///  - the shallow diagonal ditch west of it is filled up to plaza level,
    ///  - the little footbridge over that ditch (the "brick slab" bots stood on) is removed.
    /// Works on every frozen prefab whose Ground covers the plaza (Blaak and Wijnhaven 107). Re-runnable.
    /// </summary>
    public static class CityPlazaFix
    {
        static readonly Vector2 HoleCenter = new Vector2(340f, 326f);
        const float HoleRadius = 13f;
        const float HoleFloorY = -9.5f;
        const float LipWidth = 5f;                  // flatten the crater lip this far around the shaft
        static readonly Rect DitchBox = new Rect(286f, 280f, 52f, 66f);   // x, z, width, depth
        static readonly Rect BridgeBox = new Rect(294f, 292f, 26f, 22f);
        const float PlazaMinY = 3.55f;

        public static string Fix(string prefabPath)
        {
            string area = Path.GetFileNameWithoutExtension(prefabPath);
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            int moved = 0, meshes = 0;
            foreach (var mf in root.transform.Find("Ground").GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null) continue;
                var l2w = mf.transform.localToWorldMatrix; var w2l = mf.transform.worldToLocalMatrix;
                Vector3[] v = mesh.vertices;
                var w = new Vector3[v.Length];
                bool touches = false;
                for (int i = 0; i < v.Length; i++)
                {
                    w[i] = l2w.MultiplyPoint3x4(v[i]);
                    var p = new Vector2(w[i].x, w[i].z);
                    if ((p - HoleCenter).magnitude < HoleRadius + LipWidth + 2f || DitchBox.Contains(p)) touches = true;
                }
                if (!touches) continue;

                // Plaza reference heights (good, flat vertices near the plaza).
                var good = new List<Vector3>();
                foreach (var p in w)
                {
                    var q = new Vector2(p.x, p.z);
                    if (p.y >= PlazaMinY && p.y < 6f && (q - HoleCenter).magnitude > HoleRadius + LipWidth &&
                        q.x > DitchBox.xMin - 25f && q.x < DitchBox.xMax + 25f && q.y > DitchBox.yMin - 25f && q.y < DitchBox.yMax + 25f)
                        good.Add(p);
                }
                float Plaza(Vector2 q)
                {
                    float sw = 0f, sy = 0f;
                    foreach (var g in good)
                    {
                        float d2 = (new Vector2(g.x, g.z) - q).sqrMagnitude;
                        if (d2 > 900f) continue;
                        float wt = 1f / (d2 + 1f);
                        sw += wt; sy += g.y * wt;
                    }
                    return sw > 0f ? sy / sw : 4f;
                }

                bool changed = false;
                for (int i = 0; i < w.Length; i++)
                {
                    var q = new Vector2(w[i].x, w[i].z);
                    Vector2 off = q - HoleCenter; float d = off.magnitude;
                    Vector3 nw = w[i];
                    if (d < HoleRadius + LipWidth)
                    {
                        Vector2 dir = d > 0.01f ? off / d : Vector2.right;
                        bool top = w[i].y > (HoleFloorY + PlazaMinY) * 0.5f;
                        if (top)
                        {
                            Vector2 r = d < HoleRadius ? HoleCenter + dir * HoleRadius : q;
                            nw = new Vector3(r.x, Plaza(r), r.y);
                        }
                        else
                        {
                            Vector2 r = d > HoleRadius ? HoleCenter + dir * HoleRadius : q;
                            nw = new Vector3(r.x, HoleFloorY, r.y);
                        }
                    }
                    else if (DitchBox.Contains(q) && w[i].y > -1f && w[i].y < PlazaMinY)
                    {
                        nw = new Vector3(w[i].x, Plaza(q), w[i].z);
                    }
                    if ((nw - w[i]).sqrMagnitude > 1e-6f) { v[i] = w2l.MultiplyPoint3x4(nw); moved++; changed = true; }
                }
                // (also runs when nothing moved, so the normals fix below is applied on a re-run)

                // Re-split flat (map) / steep (stone) faces now that the shape changed.
                var all = new List<int>();
                for (int s = 0; s < mesh.subMeshCount; s++) all.AddRange(mesh.GetTriangles(s));
                var flat = new List<int>(); var steep = new List<int>();
                for (int i = 0; i < all.Count; i += 3)
                {
                    Vector3 n = Vector3.Cross(v[all[i + 1]] - v[all[i]], v[all[i + 2]] - v[all[i]]);
                    if (n.sqrMagnitude < 1e-8f) continue;
                    (Mathf.Abs(n.normalized.y) < 0.9f ? steep : flat).AddRange(new[] { all[i], all[i + 1], all[i + 2] });
                }
                mesh.vertices = v;
                mesh.subMeshCount = 2;
                mesh.SetTriangles(flat, 0); mesh.SetTriangles(steep, 1);
                mesh.RecalculateNormals(); mesh.RecalculateBounds();
                // Flat map look: any vertex that touches a walkable (flat) face gets a straight-up normal,
                // so the plaza doesn't show smooth-shading "creases" from the neighbouring walls.
                var normals = mesh.normals;
                for (int i = 0; i < flat.Count; i++) normals[flat[i]] = Vector3.up;
                mesh.normals = normals;
                EditorUtility.SetDirty(mesh);
                var col = mf.GetComponent<MeshCollider>();
                if (col != null) { col.sharedMesh = null; col.sharedMesh = mesh; }
                meshes++;
            }

            // The footbridge over the (now filled) ditch.
            int removed = 0;
            Transform bridges = root.transform.Find("Bridges");
            if (bridges != null)
                foreach (var mf in bridges.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = mf.sharedMesh; if (mesh == null) continue;
                    var l2w = mf.transform.localToWorldMatrix;
                    Vector3[] v = mesh.vertices;
                    bool changed = false;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        int[] t = mesh.GetTriangles(s);
                        var keep = new List<int>(t.Length);
                        for (int i = 0; i < t.Length; i += 3)
                        {
                            Vector3 c = l2w.MultiplyPoint3x4((v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f);
                            if (BridgeBox.Contains(new Vector2(c.x, c.z))) { removed++; changed = true; continue; }
                            keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]);
                        }
                        if (changed) mesh.SetTriangles(keep, s);
                    }
                    if (!changed) continue;
                    mesh.RecalculateBounds();
                    EditorUtility.SetDirty(mesh);
                    var col = mf.GetComponent<MeshCollider>();
                    if (col != null) { col.sharedMesh = null; col.sharedMesh = mesh; }
                }

            // Low ramp covers that stood in the ditch (thin wedges sticking out of the filled plaza).
            int wedges = 0;
            Transform buildings = root.transform.Find("Buildings");
            if (buildings != null)
                foreach (var mf in buildings.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = mf.sharedMesh; if (mesh == null || !mf.name.StartsWith("Buildings")) continue;
                    var l2w = mf.transform.localToWorldMatrix;
                    Vector3[] v = mesh.vertices; int[] t = mesh.triangles;
                    var parent = new int[v.Length];
                    for (int i = 0; i < v.Length; i++) parent[i] = i;
                    int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                    var weld = new Dictionary<Vector3Int, int>();
                    for (int i = 0; i < v.Length; i++)
                    {
                        var key = new Vector3Int(Mathf.RoundToInt(v[i].x * 20f), Mathf.RoundToInt(v[i].y * 20f), Mathf.RoundToInt(v[i].z * 20f));
                        if (weld.TryGetValue(key, out int o)) parent[Find(i)] = Find(o); else weld[key] = i;
                    }
                    for (int i = 0; i < t.Length; i += 3) { parent[Find(t[i + 1])] = Find(t[i]); parent[Find(t[i + 2])] = Find(t[i]); }
                    var bmin = new Dictionary<int, Vector3>(); var bmax = new Dictionary<int, Vector3>();
                    for (int i = 0; i < v.Length; i++)
                    {
                        int c = Find(i); Vector3 p = l2w.MultiplyPoint3x4(v[i]);
                        bmin[c] = bmin.TryGetValue(c, out var a) ? Vector3.Min(a, p) : p;
                        bmax[c] = bmax.TryGetValue(c, out var b) ? Vector3.Max(b, p) : p;
                    }
                    var drop = new HashSet<int>();
                    foreach (int c in bmin.Keys)
                    {
                        Vector3 ctr = (bmin[c] + bmax[c]) * 0.5f;
                        if (DitchBox.Contains(new Vector2(ctr.x, ctr.z)) && bmax[c].y < 7f) drop.Add(c);
                    }
                    if (drop.Count == 0) continue;
                    var keep = new List<int>(t.Length);
                    for (int i = 0; i < t.Length; i += 3)
                        if (!drop.Contains(Find(t[i]))) { keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]); }
                    mesh.SetTriangles(keep, 0);
                    mesh.RecalculateBounds();
                    EditorUtility.SetDirty(mesh);
                    var col = mf.GetComponent<MeshCollider>();
                    if (col != null) { col.sharedMesh = null; col.sharedMesh = mesh; }
                    wedges += drop.Count;
                }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{area}: {moved} ground vertices moved in {meshes} meshes, {removed} footbridge triangles removed, {wedges} low ditch structures removed";
        }
    }
}
