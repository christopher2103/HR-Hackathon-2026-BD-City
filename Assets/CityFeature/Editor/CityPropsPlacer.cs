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
    /// Puts trees and street lamps in a frozen neighbourhood at their real positions, taken from the
    /// Dutch BGT (PDOK): "vegetatieobject_punt" with type "boom" and "paal" with type "lichtmast".
    /// Models are simple low-poly ones generated here (3 tree variants + 1 lamp), so no asset store needed.
    /// The props are saved inside the neighbourhood prefab (child "Props").
    /// </summary>
    public static class CityPropsPlacer
    {
        private const string PropsFolder = "Assets/CityFeature/Props";
        private const string Api = "https://api.pdok.nl/kadaster/bgt/ogc/v1/collections";

        public static string Place(GameObject frozenInstance, Vector3 center, float radius = 300f)
        {
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            var trees = EnsureTreePrefabs();
            GameObject lamp = EnsureLampPrefab();

            // Bounding box in lon/lat around the centre.
            double2 Ll(Vector3 p)
            {
                double3 ecef = georef.TransformUnityPositionToEarthCenteredEarthFixed(new double3(p.x, p.y, p.z));
                return CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(ecef).xy;
            }
            double2 a = Ll(center + new Vector3(-radius, 0, -radius)), b = Ll(center + new Vector3(radius, 0, radius));
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                math.min(a.x, b.x), math.min(a.y, b.y), math.max(a.x, b.x), math.max(a.y, b.y));

            List<double2> treePoints = Fetch("vegetatieobject_punt", bbox, "boom");
            List<double2> lampPoints = Fetch("paal", bbox, "lichtmast");

            // Work on the prefab asset itself, so every scene using it gets the props.
            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(frozenInstance);
            Physics.SyncTransforms();
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            Transform old = root.transform.Find("Props");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var props = new GameObject("Props").transform;
            props.SetParent(root.transform, false);
            var treeRoot = new GameObject("Trees").transform; treeRoot.SetParent(props, false);
            var lampRoot = new GameObject("Lamps").transform; lampRoot.SetParent(props, false);

            int placedTrees = 0, placedLamps = 0, skipped = 0;
            var rng = new System.Random(frozenInstance.name.GetHashCode());
            foreach (double2 ll in treePoints)
            {
                if (!ToGround(georef, ll, center, radius, out Vector3 p)) { skipped++; continue; }
                var t = (GameObject)PrefabUtility.InstantiatePrefab(trees[rng.Next(trees.Length)], treeRoot);
                t.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
                t.transform.localScale = Vector3.one * (0.8f + (float)rng.NextDouble() * 0.5f);
                placedTrees++;
            }
            foreach (double2 ll in lampPoints)
            {
                if (!ToGround(georef, ll, center, radius, out Vector3 p)) { skipped++; continue; }
                var l = (GameObject)PrefabUtility.InstantiatePrefab(lamp, lampRoot);
                l.transform.SetPositionAndRotation(p, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
                placedLamps++;
            }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            return $"{frozenInstance.name}: {placedTrees} trees, {placedLamps} lamps (BGT: {treePoints.Count} trees, {lampPoints.Count} lamps; {skipped} skipped: outside/inside buildings/no ground)";
        }

        // Real position -> point on the frozen street (not on/in a building).
        private static bool ToGround(CesiumGeoreference georef, double2 lonLat, Vector3 center, float radius, out Vector3 point)
        {
            point = default;
            double3 ecef = CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(lonLat.x, lonLat.y, 0));
            double3 u = georef.TransformEarthCenteredEarthFixedPositionToUnity(ecef);
            var p = new Vector3((float)u.x, 0f, (float)u.z);
            if (new Vector2(p.x - center.x, p.z - center.z).magnitude > radius) return false;

            float groundY = float.NaN, topY = float.MinValue;
            foreach (RaycastHit hit in Physics.RaycastAll(new Vector3(p.x, center.y + 300f, p.z), Vector3.down, 600f, ~0, QueryTriggerInteraction.Ignore))
            {
                CityPart part = hit.collider.GetComponentInParent<CityPart>();
                if (part == null) continue;
                topY = Mathf.Max(topY, hit.point.y);
                if (part.isGround && (float.IsNaN(groundY) || hit.point.y > groundY)) groundY = hit.point.y;
            }
            if (float.IsNaN(groundY) || topY - groundY > 1f) return false; // no street here, or a building on top
            point = new Vector3(p.x, groundY, p.z);
            return true;
        }

        // All current (not removed) points of one BGT collection with the given type, paging through "next" links.
        private static List<double2> Fetch(string collection, string bbox, string plusType)
        {
            var result = new List<double2>();
            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(60) };
            string url = $"{Api}/{collection}/items?f=json&limit=1000&bbox={bbox}";
            var coordRx = new Regex("\"coordinates\":\\[([0-9.\\-]+),([0-9.\\-]+)\\]");
            for (int page = 0; page < 20 && url != null; page++)
            {
                string json = http.GetStringAsync(url).Result;
                // One chunk per feature (properties and geometry can come in any order inside it).
                string[] features = json.Split(new[] { "{\"type\":\"Feature\"" }, System.StringSplitOptions.None);
                for (int i = 1; i < features.Length; i++)
                {
                    string feature = features[i];
                    if (!feature.Contains($"\"plus_type\":\"{plusType}\"")) continue;
                    if (!feature.Contains("\"eind_registratie\":null")) continue; // removed objects
                    Match m = coordRx.Match(feature);
                    if (!m.Success) continue;
                    result.Add(new double2(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                           double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
                }
                Match next = Regex.Match(json, "\"rel\":\"next\"[^}]*?\"href\":\"([^\"]+)\"");
                url = next.Success ? next.Groups[1].Value.Replace("\\u0026", "&") : null;
            }
            return result;
        }

        // ---------- Low-poly models ----------

        private static GameObject[] EnsureTreePrefabs()
        {
            Directory.CreateDirectory(PropsFolder);
            Material bark = EnsureMaterial("M_Bark", new Color(0.33f, 0.24f, 0.17f), 0.1f);
            Material[] leaves =
            {
                EnsureMaterial("M_Leaves_A", new Color(0.30f, 0.50f, 0.20f), 0.05f),
                EnsureMaterial("M_Leaves_B", new Color(0.36f, 0.56f, 0.22f), 0.05f),
                EnsureMaterial("M_Leaves_C", new Color(0.24f, 0.44f, 0.20f), 0.05f),
            };
            var prefabs = new GameObject[3];
            for (int i = 0; i < 3; i++)
            {
                string path = $"{PropsFolder}/Tree_{(char)('A' + i)}.prefab";
                prefabs[i] = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefabs[i] != null) continue;

                Mesh mesh = BuildTree(1234 + i * 77, height: 7f + i * 1.5f, crownRadius: 2.4f + i * 0.4f, blobs: 3 + i);
                AssetDatabase.CreateAsset(mesh, $"{PropsFolder}/Tree_{(char)('A' + i)}.asset");
                var go = new GameObject($"Tree_{(char)('A' + i)}");
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = new[] { bark, leaves[i] };
                var trunk = go.AddComponent<CapsuleCollider>();
                trunk.center = new Vector3(0f, 1.5f, 0f); trunk.height = 3f; trunk.radius = 0.25f;
                go.isStatic = true;
                prefabs[i] = PrefabUtility.SaveAsPrefabAsset(go, path);
                Object.DestroyImmediate(go);
            }
            return prefabs;
        }

        private static GameObject EnsureLampPrefab()
        {
            string path = $"{PropsFolder}/StreetLamp.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            Material pole = EnsureMaterial("M_LampPole", new Color(0.22f, 0.24f, 0.26f), 0.4f);
            Material glow = EnsureMaterial("M_LampGlow", new Color(1f, 0.92f, 0.75f), 0.6f);
            glow.EnableKeyword("_EMISSION");
            glow.SetColor("_EmissionColor", new Color(1f, 0.85f, 0.6f) * 1.5f);
            glow.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;

            var b = new MeshBuilder(2);
            b.Cylinder(0, Vector3.zero, 0.09f, 0.06f, 6f, 8);                    // pole
            b.Box(0, new Vector3(0f, 5.95f, 0.45f), new Vector3(0.08f, 0.08f, 0.9f)); // arm
            b.Box(1, new Vector3(0f, 5.85f, 0.95f), new Vector3(0.28f, 0.12f, 0.5f)); // lamp head
            Mesh mesh = b.ToMesh("StreetLamp");
            AssetDatabase.CreateAsset(mesh, $"{PropsFolder}/StreetLamp.asset");

            var go = new GameObject("StreetLamp");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { pole, glow };
            var col = go.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 3f, 0f); col.height = 6f; col.radius = 0.1f;
            go.isStatic = true;
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }

        private static Material EnsureMaterial(string name, Color color, float smoothness)
        {
            string path = $"{PropsFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name, enableInstancing = true };
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smoothness);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        private static Mesh BuildTree(int seed, float height, float crownRadius, int blobs)
        {
            var rng = new System.Random(seed);
            var b = new MeshBuilder(2);
            float trunkTop = height * 0.55f;
            b.Cylinder(0, Vector3.zero, 0.22f, 0.12f, trunkTop + 0.5f, 7);
            for (int i = 0; i < blobs; i++)
            {
                float angle = (float)rng.NextDouble() * Mathf.PI * 2f;
                float off = i == 0 ? 0f : crownRadius * 0.45f;
                var c = new Vector3(Mathf.Cos(angle) * off, trunkTop + crownRadius * (0.55f + (float)rng.NextDouble() * 0.5f), Mathf.Sin(angle) * off);
                float r = crownRadius * (i == 0 ? 1f : 0.7f + (float)rng.NextDouble() * 0.2f);
                b.Blob(1, c, r, rng);
            }
            return b.ToMesh("Tree");
        }

        /// <summary>Tiny mesh builder: cylinders, boxes and lumpy spheres, one submesh per material.</summary>
        private class MeshBuilder
        {
            private readonly List<Vector3> v = new List<Vector3>();
            private readonly List<Vector3> n = new List<Vector3>();
            private readonly List<int>[] tris;

            public MeshBuilder(int subMeshes)
            {
                tris = new List<int>[subMeshes];
                for (int i = 0; i < subMeshes; i++) tris[i] = new List<int>();
            }

            public void Cylinder(int sub, Vector3 bottom, float r0, float r1, float h, int sides)
            {
                int start = v.Count;
                for (int i = 0; i <= sides; i++)
                {
                    float a = i * Mathf.PI * 2f / sides;
                    var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                    v.Add(bottom + dir * r0); n.Add(dir);
                    v.Add(bottom + dir * r1 + Vector3.up * h); n.Add(dir);
                }
                for (int i = 0; i < sides; i++)
                {
                    int k = start + i * 2;
                    tris[sub].AddRange(new[] { k, k + 1, k + 2, k + 2, k + 1, k + 3 });
                }
            }

            public void Box(int sub, Vector3 c, Vector3 size)
            {
                Vector3 h = size * 0.5f;
                Vector3[] dirs = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
                foreach (Vector3 d in dirs)
                {
                    Vector3 u = Mathf.Abs(d.y) > 0.5f ? Vector3.right : Vector3.up;
                    Vector3 w = Vector3.Cross(d, u);
                    int s = v.Count;
                    foreach (var (x, y) in new[] { (-1, -1), (-1, 1), (1, 1), (1, -1) })
                    {
                        v.Add(c + Vector3.Scale(d + u * x + w * y, h));
                        n.Add(d);
                    }
                    tris[sub].AddRange(new[] { s, s + 1, s + 2, s, s + 2, s + 3 });
                }
            }

            // Icosphere (1x subdivided) with random bumps: reads as a leafy crown.
            public void Blob(int sub, Vector3 c, float r, System.Random rng)
            {
                float t = (1f + Mathf.Sqrt(5f)) / 2f;
                var pts = new List<Vector3>
                {
                    new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                    new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                    new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
                };
                int[] f =
                {
                    0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
                    3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
                };
                var faces = new List<int>(f);
                var cache = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (cache.TryGetValue(key, out int m)) return m;
                    pts.Add((pts[a] + pts[b]) * 0.5f);
                    return cache[key] = pts.Count - 1;
                }
                var sub2 = new List<int>();
                for (int i = 0; i < faces.Count; i += 3)
                {
                    int a = faces[i], b = faces[i + 1], cc = faces[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, cc), ca = Mid(cc, a);
                    sub2.AddRange(new[] { a, ab, ca, b, bc, ab, cc, ca, bc, ab, bc, ca });
                }
                int start = v.Count;
                foreach (Vector3 p in pts)
                {
                    Vector3 d = p.normalized;
                    float bump = 1f + ((float)rng.NextDouble() - 0.5f) * 0.35f;
                    v.Add(c + new Vector3(d.x, d.y * 0.8f, d.z) * r * bump);
                    n.Add(d);
                }
                // Icosphere faces are wound clockwise seen from outside in Unity's left-handed space: flip.
                for (int i = 0; i < sub2.Count; i += 3)
                    tris[sub].AddRange(new[] { start + sub2[i], start + sub2[i + 2], start + sub2[i + 1] });
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name, subMeshCount = tris.Length };
                mesh.SetVertices(v);
                mesh.SetNormals(n);
                for (int i = 0; i < tris.Length; i++) mesh.SetTriangles(tris[i], i);
                mesh.RecalculateBounds();
                return mesh;
            }
        }
    }
}
