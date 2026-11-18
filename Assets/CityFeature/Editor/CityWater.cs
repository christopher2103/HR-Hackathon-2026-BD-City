using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Low-poly water on every water surface of the Dutch base map (BGT 'waterdeel') inside a frozen area:
    /// a jittered triangle grid at the measured water level, animated by BDCity/LowPolyWater.
    /// Only real water is used, so e.g. the open metro station at Blaak stays dry.
    /// Needs CityScene open (georeference + the frozen area active for ground raycasts) and internet.
    /// </summary>
    public static class CityWater
    {
        const float Cell = 4f;
        const float Overlap = 2.5f;   // reach a bit under the quay edges so no gaps show
        const string MaterialPath = "Assets/CityFeature/Materials/M_LowPolyWater.mat";

        [MenuItem("B&D City/Low-poly water (all areas)")]
        public static void BuildAll()
        {
            foreach (var a in new[] { "Wijnhaven107", "Lijnbaan", "Blaak" })
                Debug.Log("[CityWater] " + Build($"Assets/CityFeature/Frozen/{a}/Frozen - {a}.prefab"));
        }

        public static string Build(string prefabPath)
        {
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            if (georef == null) return "Open CityScene first.";
            var root = PrefabUtility.LoadPrefabContents(prefabPath);

            // Area = the near ground chunks.
            var groundMeshes = new List<(Mesh mesh, Matrix4x4 m)>();
            Bounds area = default; bool first = true;
            foreach (var mf in root.transform.Find("Ground").GetComponentsInChildren<MeshFilter>(true))
            {
                groundMeshes.Add((mf.sharedMesh, mf.transform.localToWorldMatrix));
                var b = mf.GetComponent<MeshRenderer>().bounds;
                if (first) { area = b; first = false; } else area.Encapsulate(b);
            }
            double2 ll0 = LonLat(georef, new Vector3(area.min.x, 0, area.min.z)), ll1 = LonLat(georef, new Vector3(area.max.x, 0, area.max.z));
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                System.Math.Min(ll0.x, ll1.x), System.Math.Min(ll0.y, ll1.y), System.Math.Max(ll0.x, ll1.x), System.Math.Max(ll0.y, ll1.y));

            // Water polygons (current version, at ground level).
            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(120) };
            var waters = new List<List<List<Vector2>>>();
            string url = $"https://api.pdok.nl/kadaster/bgt/ogc/v1/collections/waterdeel/items?f=json&limit=1000&bbox={bbox}";
            int pages = 0;
            while (url != null && pages++ < 30)
            {
                string json = http.GetStringAsync(url).Result;
                foreach (string f in json.Split(new[] { "{\"type\":\"Feature\"" }, System.StringSplitOptions.None))
                {
                    if (!f.Contains("\"geometry\"") || !f.Contains("\"eind_registratie\":null")) continue;
                    var feat = CityGroundMap.Parse(f, "waterdeel");
                    if (feat == null || feat.level != 0) continue;
                    var rings = new List<List<Vector2>>();
                    foreach (var ring in feat.rings)
                    {
                        var r = new List<Vector2>(ring.Count);
                        foreach (var p in ring)
                        {
                            double3 u = georef.TransformEarthCenteredEarthFixedPositionToUnity(CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(p.x, p.y, 0)));
                            r.Add(new Vector2((float)u.x, (float)u.z));
                        }
                        rings.Add(r);
                    }
                    waters.Add(rings);
                }
                var next = Regex.Match(json, "\"rel\":\"next\"[^}]*?\"href\":\"([^\"]+)\"");
                if (!next.Success) next = Regex.Match(json, "\"href\":\"([^\"]+)\"[^}]*?\"rel\":\"next\"");
                url = next.Success ? next.Groups[1].Value.Replace("\\u0026", "&") : null;
            }

            // Physics copy of the ground for height sampling (works without the scene instance).
            var probe = new GameObject("WaterProbe") { hideFlags = HideFlags.HideAndDontSave };
            foreach (var (mesh, m) in groundMeshes)
            {
                var g = new GameObject("g") { hideFlags = HideFlags.HideAndDontSave };
                g.transform.SetParent(probe.transform, false);
                g.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
                g.AddComponent<MeshCollider>().sharedMesh = mesh;
            }
            Physics.SyncTransforms();

            var verts = new List<Vector3>(); var tris = new List<int>();
            int bodies = 0, flattened = 0;
            foreach (var rings in waters)
            {
                Vector2 mn = new Vector2(float.MaxValue, float.MaxValue), mx = -mn;
                foreach (var r in rings) foreach (var p in r) { mn = Vector2.Min(mn, p); mx = Vector2.Max(mx, p); }
                mn = Vector2.Max(mn - Vector2.one * Overlap, new Vector2(area.min.x, area.min.z));
                mx = Vector2.Min(mx + Vector2.one * Overlap, new Vector2(area.max.x, area.max.z));
                if (mn.x >= mx.x || mn.y >= mx.y) continue;

                // Water level: what the ground mesh shows inside the polygon (a bit above the median to hide bumps).
                var heights = new List<float>();
                for (float x = mn.x; x <= mx.x; x += 6f)
                    for (float z = mn.y; z <= mx.y; z += 6f)
                    {
                        var p = new Vector2(x, z);
                        if (!InsideAny(p, rings)) continue;
                        foreach (var hit in Physics.RaycastAll(new Vector3(x, 100f, z), Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore))
                            if (hit.collider.transform.IsChildOf(probe.transform)) { heights.Add(hit.point.y); break; }
                    }
                if (heights.Count < 3) continue;
                heights.Sort();
                float level = heights[Mathf.Clamp((int)(heights.Count * 0.65f), 0, heights.Count - 1)] + 0.15f;

                // Push anything of the ground that sticks out of the water (moored ships, terrain bumps) under it.
                foreach (var (gm, gmat) in groundMeshes)
                {
                    var gv = gm.vertices; bool changed = false;
                    for (int k = 0; k < gv.Length; k++)
                    {
                        Vector3 w = gmat.MultiplyPoint3x4(gv[k]);
                        if (w.y < level - 0.5f || w.x < mn.x || w.x > mx.x || w.z < mn.y || w.z > mx.y) continue;
                        var p2 = new Vector2(w.x, w.z);
                        if (!InsideAny(p2, rings) || DistanceToRings(p2, rings) < 1.0f) continue;
                        w.y = level - 0.6f;
                        gv[k] = gmat.inverse.MultiplyPoint3x4(w);
                        changed = true; flattened++;
                    }
                    if (changed) { gm.vertices = gv; gm.RecalculateNormals(); gm.RecalculateBounds(); EditorUtility.SetDirty(gm); }
                }

                // Jittered grid, kept where it covers water.
                int nx = Mathf.CeilToInt((mx.x - mn.x) / Cell), nz = Mathf.CeilToInt((mx.y - mn.y) / Cell);
                Vector3 V(int i, int j)
                {
                    float gx = Mathf.Floor(mn.x / Cell) * Cell + i * Cell, gz = Mathf.Floor(mn.y / Cell) * Cell + j * Cell;
                    uint h = (uint)(Mathf.RoundToInt(gx / Cell) * 73856093) ^ (uint)(Mathf.RoundToInt(gz / Cell) * 19349663);
                    float jx = ((h & 1023) / 1023f - 0.5f) * Cell * 0.45f, jz = (((h >> 10) & 1023) / 1023f - 0.5f) * Cell * 0.45f;
                    return new Vector3(gx + jx, level, gz + jz);
                }
                int before = tris.Count;
                for (int i = 0; i <= nx; i++)
                    for (int j = 0; j <= nz; j++)
                    {
                        Vector3 a = V(i, j), b = V(i + 1, j), c = V(i + 1, j + 1), d = V(i, j + 1);
                        bool flip = ((i + j) & 1) == 0;
                        var t1 = flip ? new[] { a, d, b } : new[] { a, d, c };
                        var t2 = flip ? new[] { b, d, c } : new[] { a, c, b };
                        foreach (var t in new[] { t1, t2 })
                        {
                            Vector3 cen = (t[0] + t[1] + t[2]) / 3f;
                            var c2 = new Vector2(cen.x, cen.z);
                            if (!InsideAny(c2, rings) && DistanceToRings(c2, rings) > Overlap) continue;
                            int k = verts.Count;
                            verts.Add(t[0]); verts.Add(t[1]); verts.Add(t[2]);
                            // Upward facing: Cross(b-a, c-a) must point up.
                            if (Vector3.Cross(t[1] - t[0], t[2] - t[0]).y > 0) tris.AddRange(new[] { k, k + 1, k + 2 });
                            else tris.AddRange(new[] { k, k + 2, k + 1 });
                        }
                    }
                if (tris.Count > before) bodies++;
            }
            Object.DestroyImmediate(probe);
            if (flattened > 0)
                foreach (var mc in root.transform.Find("Ground").GetComponentsInChildren<MeshCollider>(true)) { var m = mc.sharedMesh; mc.sharedMesh = null; mc.sharedMesh = m; }

            var existing = root.transform.Find("Water");
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
            if (tris.Count == 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                PrefabUtility.UnloadPrefabContents(root);
                return $"{Path.GetFileNameWithoutExtension(prefabPath)}: no water here";
            }
            var meshOut = new Mesh { name = "Water", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            meshOut.SetVertices(verts); meshOut.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count]; for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            meshOut.normals = normals;
            meshOut.RecalculateBounds();
            // Room for the waves so it isn't culled too early.
            var bb = meshOut.bounds; bb.Expand(new Vector3(0, 1f, 0)); meshOut.bounds = bb;
            string meshPath = $"{Path.GetDirectoryName(prefabPath).Replace('\\', '/')}/Meshes/Water.asset";
            var oldMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (oldMesh != null) { EditorUtility.CopySerialized(meshOut, oldMesh); meshOut = oldMesh; } else AssetDatabase.CreateAsset(meshOut, meshPath);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null) { mat = new Material(Shader.Find("BDCity/LowPolyWater")); AssetDatabase.CreateAsset(mat, MaterialPath); }

            var go = new GameObject("Water");
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = meshOut;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<MeshCollider>().sharedMesh = meshOut;   // the teleport ray stops on the water, not on the harbour floor
            go.AddComponent<CityPart>();
            go.isStatic = true;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: {waters.Count} water polygons, {bodies} with water in the area, {tris.Count / 3} triangles, {flattened} ground points pushed under the water";
        }

        static double2 LonLat(CesiumGeoreference georef, Vector3 w)
        {
            double3 llh = CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(georef.TransformUnityPositionToEarthCenteredEarthFixed(new double3(w.x, w.y, w.z)));
            return llh.xy;
        }

        static bool InsideAny(Vector2 p, List<List<Vector2>> rings)
        {
            bool inside = false;   // even-odd over all rings: holes (islands) come out right
            foreach (var poly in rings)
                for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                    if (((poly[i].y > p.y) != (poly[j].y > p.y)) && (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                        inside = !inside;
            return inside;
        }

        static float DistanceToRings(Vector2 p, List<List<Vector2>> rings)
        {
            float best = float.MaxValue;
            foreach (var poly in rings)
                for (int i = 0; i < poly.Count; i++)
                {
                    Vector2 a = poly[i], b = poly[(i + 1) % poly.Count], ab = b - a;
                    float t = ab.sqrMagnitude > 0 ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                    best = Mathf.Min(best, (a + ab * t - p).magnitude);
                }
            return best;
        }
    }
}
