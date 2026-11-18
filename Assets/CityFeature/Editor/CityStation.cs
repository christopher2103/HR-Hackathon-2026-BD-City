using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Blaak station: a glass balustrade (steel posts + top rail + glass panels) around the metro shaft that
    /// CityPlazaFix made, and a station sign ("Station Blaak") with an empty logo panel. The logo panel uses its own
    /// material (Materials/M_Logo_Station.mat): put an official logo image (that you have the right to use) in its
    /// Base Map. Saved as "Station Decor" in every frozen prefab that contains the shaft. Re-runnable.
    /// Needs the area's frozen instance in the open scene (ground/building ray tests).
    /// </summary>
    public static class CityStation
    {
        static readonly Vector2 HoleCenter = new Vector2(340f, 326f);
        const float RailRadius = 13.35f;
        const float PostSpacing = 2.0f;
        const float GlassHeight = 1.0f;
        static readonly Color Steel = new Color(0.62f, 0.64f, 0.66f), SignBlue = new Color(0.13f, 0.20f, 0.38f), Pole = new Color(0.25f, 0.26f, 0.28f);

        class Geo
        {
            public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>(); public readonly List<Color32> c = new List<Color32>(); public readonly List<int> t = new List<int>();
            public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color col)
            {
                col = col.linear; col.a = 1f;
                Vector3 nn = Vector3.Cross(b - a, cc - a).normalized; int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) { n.Add(nn); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            public void Box(Vector3 center, Vector3 size, Quaternion r, Color col)
            {
                Vector3 hx = r * new Vector3(size.x * 0.5f, 0, 0), hy = r * new Vector3(0, size.y * 0.5f, 0), hz = r * new Vector3(0, 0, size.z * 0.5f);
                Vector3 P(int x, int y, int z) => center + hx * x + hy * y + hz * z;
                Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), col);
                Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), col);
                Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), col);
                Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), col);
                Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), col);
            }
            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetTriangles(t, 0); m.RecalculateBounds();
                return m;
            }
        }

        public static string Build(string prefabPath, Vector2 lookFrom)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string area = Path.GetFileName(folder);
            GameObject instance = null;
            var frozenRoot = GameObject.Find("Frozen Neighbourhoods");
            var wasActive = new Dictionary<GameObject, bool>();
            foreach (Transform c in frozenRoot.transform)
            {
                wasActive[c.gameObject] = c.gameObject.activeSelf;
                if (PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(c.gameObject) == prefabPath) instance = c.gameObject;
            }
            if (instance == null) return $"{area}: no scene instance";
            foreach (Transform c in frozenRoot.transform) c.gameObject.SetActive(c.gameObject == instance);
            Transform props = instance.transform.Find("Props");
            bool propsWas = props.gameObject.activeSelf; props.gameObject.SetActive(false);
            Physics.SyncTransforms();

            var steel = new Geo(); var glass = new Geo(); var sign = new Geo();
            int panels = 0, posts = 0;
            Vector3 signPos = default; Quaternion signRot = default; bool haveSign = false;
            try
            {
                if (!Ground(new Vector3(HoleCenter.x + RailRadius, 0, HoleCenter.y), out _) && !Ground(new Vector3(HoleCenter.x - RailRadius, 0, HoleCenter.y), out _))
                    return $"{area}: shaft not in this area";

                int count = Mathf.RoundToInt(2f * Mathf.PI * RailRadius / PostSpacing);
                var pts = new Vector3[count]; var ok = new bool[count];
                for (int i = 0; i < count; i++)
                {
                    float a = i * Mathf.PI * 2f / count;
                    var p = new Vector3(HoleCenter.x + Mathf.Cos(a) * RailRadius, 0, HoleCenter.y + Mathf.Sin(a) * RailRadius);
                    ok[i] = Ground(p, out pts[i]);
                }
                for (int i = 0; i < count; i++)
                {
                    int j = (i + 1) % count;
                    if (!ok[i] || !ok[j]) continue;
                    Vector3 a = pts[i], b = pts[j];
                    if (Blocked(a + Vector3.up * 0.6f, b + Vector3.up * 0.6f) || Blocked(a + Vector3.up * 1.0f, b + Vector3.up * 1.0f)) continue;
                    // Glass panel (double-sided material), steel posts and top rail.
                    Vector3 a0 = a + Vector3.up * 0.08f, b0 = b + Vector3.up * 0.08f;
                    glass.Quad(a0, a0 + Vector3.up * GlassHeight, b0 + Vector3.up * GlassHeight, b0, new Color(0.75f, 0.88f, 0.92f));
                    Vector3 dir = b - a;
                    Quaternion r = Quaternion.LookRotation(dir.normalized, Vector3.up);
                    steel.Box((a + b) * 0.5f + Vector3.up * (GlassHeight + 0.11f), new Vector3(0.05f, 0.05f, dir.magnitude + 0.05f), r, Steel);
                    steel.Box((a + b) * 0.5f + Vector3.up * 0.06f, new Vector3(0.05f, 0.05f, dir.magnitude), r, Steel);
                    steel.Box(a + Vector3.up * 0.57f, new Vector3(0.06f, 1.14f, 0.06f), r, Steel);
                    panels++; posts++;
                }

                // Station sign: on the rim, the free side closest to where people come from.
                Vector2 toLook = (lookFrom - HoleCenter).normalized;
                float baseAngle = Mathf.Atan2(toLook.y, toLook.x) * Mathf.Rad2Deg;
                foreach (float off in new[] { 0f, 20f, -20f, 40f, -40f, 60f, -60f, 80f, -80f, 100f, -100f })
                {
                    float a = (baseAngle + off) * Mathf.Deg2Rad;
                    var p = new Vector3(HoleCenter.x + Mathf.Cos(a) * (RailRadius + 2.2f), 0, HoleCenter.y + Mathf.Sin(a) * (RailRadius + 2.2f));
                    if (!Ground(p, out Vector3 g)) continue;
                    bool clear = true;
                    for (int k = 0; k < 8 && clear; k++)
                        if (Blocked(g + Vector3.up * 1.5f, g + Vector3.up * 1.5f + Quaternion.Euler(0, k * 45f, 0) * Vector3.forward * 2.5f)) clear = false;
                    if (!clear) continue;
                    Vector3 face = new Vector3(lookFrom.x - g.x, 0, lookFrom.y - g.z).normalized;
                    signPos = g; signRot = Quaternion.LookRotation(face); haveSign = true;
                    break;
                }
                if (haveSign)
                {
                    sign.Box(new Vector3(0, 1.225f, -0.12f), new Vector3(0.12f, 2.45f, 0.12f), Quaternion.identity, Pole); // behind the board
                    sign.Box(new Vector3(0, 2.5f, -0.09f), new Vector3(0.5f, 0.1f, 0.08f), Quaternion.identity, Pole);
                    sign.Box(new Vector3(0, 2.75f, 0), new Vector3(1.9f, 0.6f, 0.1f), Quaternion.identity, SignBlue);
                }
            }
            finally
            {
                props.gameObject.SetActive(propsWas);
                foreach (var kv in wasActive) kv.Key.SetActive(kv.Value);
            }

            // ---- Save ----
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            Transform old = root.transform.Find("Station Decor");
            if (old != null) Object.DestroyImmediate(old.gameObject);
            var decor = new GameObject("Station Decor").transform;
            decor.SetParent(root.transform, false);
            Material detail = CityFacades.DetailMaterial();
            Add(decor, "Balustrade", steel.ToMesh("Station_Balustrade"), detail, folder, ShadowCastingMode.On);
            Add(decor, "Balustrade Glass", glass.ToMesh("Station_Glass"), GlassMaterial(), folder, ShadowCastingMode.Off);
            if (haveSign)
            {
                var s = Add(decor, "Station Sign", sign.ToMesh("Station_Sign"), detail, folder, ShadowCastingMode.On);
                s.transform.SetPositionAndRotation(signPos, signRot);
                // Mesh was built around the origin: move it into place via the transform.
                Material logo = LogoMaterial();
                foreach (bool front in new[] { true, false })
                {
                    // Seen from its own side, the logo is on the left and the name on the right.
                    float z = front ? 0.052f : -0.052f, side = front ? 1f : -1f;
                    var lq = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Object.DestroyImmediate(lq.GetComponent<Collider>());
                    lq.name = front ? "Logo Front" : "Logo Back";
                    lq.transform.SetParent(s.transform, false);
                    lq.transform.localPosition = new Vector3(0.63f * side, 2.75f, z);
                    lq.transform.localRotation = front ? Quaternion.Euler(0, 180f, 0) : Quaternion.identity;
                    lq.transform.localScale = new Vector3(0.48f, 0.48f, 1f);
                    lq.GetComponent<MeshRenderer>().sharedMaterial = logo;
                    lq.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

                    var tgo = new GameObject(front ? "Name Front" : "Name Back");
                    tgo.transform.SetParent(s.transform, false);
                    tgo.transform.localPosition = new Vector3(-0.25f * side, 2.75f, z * 1.05f);
                    tgo.transform.localRotation = front ? Quaternion.Euler(0, 180f, 0) : Quaternion.identity;
                    var tmp = tgo.AddComponent<TextMeshPro>();
                    tmp.text = "<b>Station Blaak</b>";
                    tmp.fontSize = 1.65f;
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.color = Color.white;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.rectTransform.sizeDelta = new Vector2(1.2f, 0.5f);
                }
            }
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{area}: balustrade {panels} glass panels, sign {(haveSign ? "placed" : "NOT placed (no free spot)")}";
        }

        static GameObject Add(Transform parent, string name, Mesh mesh, Material mat, string folder, ShadowCastingMode shadows)
        {
            string path = $"{folder}/Meshes/{mesh.name}.asset";
            if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = shadows;
            go.isStatic = true;
            return go;
        }

        static Material GlassMaterial()
        {
            const string path = "Assets/CityFeature/Materials/M_StationGlass.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetFloat("_Surface", 1f); m.SetFloat("_Blend", 0f); m.SetFloat("_Cull", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f); m.SetFloat("_Smoothness", 0.85f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetColor("_BaseColor", new Color(0.70f, 0.86f, 0.90f, 0.28f));
            m.renderQueue = (int)RenderQueue.Transparent;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static Material LogoMaterial()
        {
            const string path = "Assets/CityFeature/Materials/M_Logo_Station.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            m = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            m.SetColor("_BaseColor", Color.white);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static bool Ground(Vector3 p, out Vector3 g)
        {
            g = default; float best = float.MinValue, top = float.MinValue;
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, 60f, p.z), Vector3.down, 120f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part == null) continue;
                if (part.isGround) best = Mathf.Max(best, h.point.y); else top = Mathf.Max(top, h.point.y);
            }
            if (best == float.MinValue || top > best + 0.3f) return false;
            g = new Vector3(p.x, best, p.z);
            return true;
        }

        static bool Blocked(Vector3 a, Vector3 b)
        {
            foreach (var h in Physics.RaycastAll(a, (b - a).normalized, (b - a).magnitude, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part != null && !part.isGround) return true;
            }
            return false;
        }
    }
}
