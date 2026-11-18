using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Lotte's help stand: a small X-CITE style kiosk (counter, orange awning, "HELP ?" board), built as one
    /// vertex-coloured mesh with the FlatDetail material, and placed once per area as "Tutorial Stand" under
    /// each Spot (TutorialGuide looks it up there). The stand faces where the player lands.
    /// </summary>
    public static class CityTutorialStand
    {
        const string PrefabPath = "Assets/CityFeature/Props/TutorialStand.prefab";
        const string MeshPath = "Assets/CityFeature/Props/TutorialStand.asset";
        static readonly Color Orange = new Color(0.95f, 0.55f, 0.25f), Cream = new Color(0.94f, 0.91f, 0.85f);
        static readonly Color Wood = new Color(0.58f, 0.42f, 0.29f), Dark = new Color(0.20f, 0.21f, 0.23f), White = new Color(0.97f, 0.97f, 0.95f);

        class Geo
        {
            public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>(); public readonly List<Color32> c = new List<Color32>(); public readonly List<int> t = new List<int>();
            public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color col)
            {
                col = col.linear; col.a = 1f; // alpha < 1 means "glass" in FlatDetail
                Vector3 nn = Vector3.Cross(b - a, cc - a).normalized; int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
                for (int k = 0; k < 4; k++) { n.Add(nn); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
            }
            public void Box(Vector3 center, Vector3 size, Color col, Quaternion? rot = null)
            {
                Quaternion r = rot ?? Quaternion.identity;
                Vector3 hx = r * new Vector3(size.x * 0.5f, 0, 0), hy = r * new Vector3(0, size.y * 0.5f, 0), hz = r * new Vector3(0, 0, size.z * 0.5f);
                Vector3 P(int x, int y, int z) => center + hx * x + hy * y + hz * z;
                Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), col);
                Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), col);
                Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), col);
                Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), col);
                Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), col);
                Quad(P(-1, -1, 1), P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), col);
            }
        }

        [MenuItem("B&D City/Tutorial/Build help stand prefab")]
        public static GameObject BuildPrefab()
        {
            var g = new Geo();
            // Counter (front = +z, where the visitor stands).
            g.Box(new Vector3(0, 0.5f, 0), new Vector3(1.8f, 1.0f, 0.6f), Cream);
            g.Box(new Vector3(0, 0.8f, 0.01f), new Vector3(1.82f, 0.2f, 0.62f), Orange);
            g.Box(new Vector3(0, 1.03f, 0.03f), new Vector3(1.95f, 0.06f, 0.72f), Wood);
            g.Box(new Vector3(0, 0.05f, 0), new Vector3(1.84f, 0.1f, 0.64f), Dark);
            // Posts at the back, awning sloping to the front.
            foreach (float x in new[] { -0.88f, 0.88f }) g.Box(new Vector3(x, 1.75f, -0.26f), new Vector3(0.07f, 1.5f, 0.07f), Dark);
            float yBack = 2.5f, yFront = 2.2f, zBack = -0.32f, zFront = 0.62f, w = 1.05f;
            Vector3 a = new Vector3(-w, yBack, zBack), b = new Vector3(w, yBack, zBack), c = new Vector3(w, yFront, zFront), d = new Vector3(-w, yFront, zFront);
            g.Quad(a, d, c, b, Orange);                    // top
            g.Quad(a, b, c, d, Orange * 0.75f);            // underside
            // Striped valance (white / orange), both sides.
            int stripes = 8;
            for (int i = 0; i < stripes; i++)
            {
                float x0 = -w + 2f * w * i / stripes, x1 = -w + 2f * w * (i + 1) / stripes;
                Color sc = i % 2 == 0 ? White : Orange;
                g.Quad(new Vector3(x1, yFront - 0.2f, zFront), new Vector3(x1, yFront, zFront), new Vector3(x0, yFront, zFront), new Vector3(x0, yFront - 0.2f, zFront), sc);        // faces +z (front)
                g.Quad(new Vector3(x0, yFront - 0.2f, zFront), new Vector3(x0, yFront, zFront), new Vector3(x1, yFront, zFront), new Vector3(x1, yFront - 0.2f, zFront), sc * 0.8f); // faces -z
            }
            // Sign board on top.
            g.Box(new Vector3(0, 2.86f, -0.3f), new Vector3(1.2f, 0.46f, 0.06f), White);
            g.Box(new Vector3(0, 2.6f, -0.3f), new Vector3(0.06f, 0.12f, 0.06f), Dark);

            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
            if (mesh == null) { mesh = new Mesh(); AssetDatabase.CreateAsset(mesh, MeshPath); }
            mesh.Clear(); mesh.name = "TutorialStand";
            mesh.SetVertices(g.v); mesh.SetNormals(g.n); mesh.SetColors(g.c); mesh.SetTriangles(g.t, 0); mesh.RecalculateBounds();
            EditorUtility.SetDirty(mesh);

            var root = new GameObject("Tutorial Stand");
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = root.AddComponent<MeshRenderer>();
            mr.sharedMaterial = CityFacades.DetailMaterial();
            mr.shadowCastingMode = ShadowCastingMode.On;
            var col = root.AddComponent<BoxCollider>();   // you can't walk through the counter (not ground: no CityPart)
            col.center = new Vector3(0, 0.5f, 0); col.size = new Vector3(1.9f, 1.0f, 0.7f);
            // Board text, on both sides.
            foreach (bool front in new[] { true, false })
            {
                var tgo = new GameObject(front ? "Sign Front" : "Sign Back");
                tgo.transform.SetParent(root.transform, false);
                tgo.transform.localPosition = new Vector3(0, 2.86f, front ? -0.265f : -0.335f);
                tgo.transform.localRotation = front ? Quaternion.Euler(0, 180f, 0) : Quaternion.identity;
                var tmp = tgo.AddComponent<TextMeshPro>();
                tmp.text = "<b>HELP  ?</b>";
                tmp.fontSize = 3.2f;
                tmp.color = Orange;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.rectTransform.sizeDelta = new Vector2(1.1f, 0.4f);
            }
            // Ignore Raycast: controller rays (bubbles, menu) and teleport aim pass through; you still bump into it.
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            return prefab;
        }

        /// <summary>Places (or moves) the stand of one area: a free, flat spot about 'distance' from where the
        /// player lands, to the side of the landing view direction, turned to face the landing point.</summary>
        public static string Place(string spotName, Vector3 landing, float lookYaw, float distance = 6.5f)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) ?? BuildPrefab();
            var spot = GameObject.Find(spotName)?.transform;
            if (spot == null) return $"{spotName}: not found";
            Physics.SyncTransforms();
            foreach (float d in new[] { distance, distance + 2f, distance - 1.5f, distance + 4f })
            foreach (float a in new[] { 50f, -50f, 35f, -35f, 70f, -70f, 25f, -25f })
            {
                Vector3 p = landing + Quaternion.Euler(0, lookYaw + a, 0) * Vector3.forward * d;
                if (!GroundAt(p, out Vector3 g) || Mathf.Abs(g.y - landing.y) > 0.6f) continue;
                // Flat enough and clear around (nothing within 1.6 m at knee and head height), and in view.
                bool ok = true;
                foreach (var corner in new[] { new Vector3(1.1f, 0, 0.6f), new Vector3(-1.1f, 0, 0.6f), new Vector3(1.1f, 0, -0.8f), new Vector3(-1.1f, 0, -0.8f) })
                    if (!GroundAt(g + corner, out Vector3 gc) || Mathf.Abs(gc.y - g.y) > 0.25f) { ok = false; break; }
                for (int i = 0; ok && i < 8; i++)
                {
                    Vector3 dir = Quaternion.Euler(0, i * 45f, 0) * Vector3.forward;
                    if (Physics.Raycast(g + Vector3.up * 0.5f, dir, 1.6f) || Physics.Raycast(g + Vector3.up * 2f, dir, 1.6f)) ok = false;
                }
                if (ok && Physics.Linecast(landing + Vector3.up * 1.6f, g + Vector3.up * 1.6f)) ok = false;
                if (!ok) continue;

                Transform old = spot.Find("Tutorial Stand");
                if (old != null) Object.DestroyImmediate(old.gameObject);
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, spot);
                inst.name = "Tutorial Stand";
                Vector3 face = landing - g; face.y = 0;
                inst.transform.SetPositionAndRotation(g, Quaternion.LookRotation(face.normalized));
                Undo.RegisterCreatedObjectUndo(inst, "Place tutorial stand");
                return $"{spotName}: stand at {g:F1}, {d:F1} m from landing, {a:F0}° from view";
            }
            return $"{spotName}: no free spot found";
        }

        static bool GroundAt(Vector3 p, out Vector3 ground)
        {
            ground = default; float best = float.MinValue, top = float.MinValue;
            foreach (var h in Physics.RaycastAll(new Vector3(p.x, p.y + 40f, p.z), Vector3.down, 80f, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = h.collider.GetComponentInParent<CityPart>();
                if (part == null) { top = Mathf.Max(top, h.point.y); continue; }   // props, other stands
                if (part.isGround) best = Mathf.Max(best, h.point.y); else top = Mathf.Max(top, h.point.y);
            }
            if (best == float.MinValue || top > best + 0.3f) return false;     // something stands here
            ground = new Vector3(p.x, best, p.z);
            return true;
        }
    }
}
