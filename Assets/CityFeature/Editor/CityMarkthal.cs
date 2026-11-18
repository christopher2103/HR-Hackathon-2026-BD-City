using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumForUnity;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Replaces the Markthal from the city data with a clean stylized model: a horseshoe arch with grey
    /// stone outside and a grid of square windows, the colourful market mural on the inside, and glass
    /// end walls (cable-net facade) so the mural shows through. Size and orientation come from the
    /// building's real outline (BGT) and height from the city's 3D data. Needs CityScene open + internet.
    /// </summary>
    public static class CityMarkthal
    {
        const string Folder = "Assets/CityFeature/Props/Markthal";
        const double Lat = 51.92000, Lon = 4.48694;
        const int Segments = 28;           // around the arch
        const float Exponent = 2.6f;       // superellipse: 2 = ellipse, higher = squarer horseshoe

        [MenuItem("B&D City/Build stylized Markthal (all areas)")]
        public static void BuildAll()
        {
            foreach (var a in new[] { "Wijnhaven107", "Lijnbaan", "Blaak" })
                Debug.Log("[CityMarkthal] " + Build($"Assets/CityFeature/Frozen/{a}/Frozen - {a}.prefab"));
        }

        public static string Build(string prefabPath)
        {
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            if (georef == null) return "Open CityScene first.";
            var poly = Footprint(georef, out Vector2 point);
            if (poly == null) return "Markthal outline not found";

            // Oriented box of the outline: long axis = arch length.
            float best = float.MaxValue; Vector2 axisW = Vector2.right, mn = default, mx = default;
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 d = poly[(i + 1) % poly.Count] - poly[i];
                if (d.sqrMagnitude < 0.01f) continue;
                d.Normalize();
                var n = new Vector2(-d.y, d.x);
                Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = -lo;
                foreach (var q in poly) { var pp = new Vector2(Vector2.Dot(q, d), Vector2.Dot(q, n)); lo = Vector2.Min(lo, pp); hi = Vector2.Max(hi, pp); }
                float area = (hi.x - lo.x) * (hi.y - lo.y);
                if (area < best) { best = area; axisW = d; mn = lo; mx = hi; }
            }
            Vector2 axisN = new Vector2(-axisW.y, axisW.x);
            Vector2 size = mx - mn;
            Vector2 centre2 = axisW * ((mn.x + mx.x) * 0.5f) + axisN * ((mn.y + mx.y) * 0.5f);
            // Width = short side, length = long side.
            Vector2 across = size.x < size.y ? axisW : axisN, along = size.x < size.y ? axisN : axisW;
            float width = Mathf.Min(size.x, size.y), length = Mathf.Max(size.x, size.y);

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var buildings = root.transform.Find("Buildings");

            // Height from the old geometry, then remove it.
            float top = float.MinValue; int removed = 0;
            foreach (var mf in buildings.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.name == "Cube Houses" || mf.name == "Markthal") continue;
                var m = mf.sharedMesh; var v = m.vertices; var t = m.triangles;
                var keep = new List<int>(t.Length);
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 c = (v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f;
                    var c2 = new Vector2(c.x, c.z);
                    if (Inside(c2, poly) || DistanceToPolygon(c2, poly) < 1.5f)
                    {
                        top = Mathf.Max(top, Mathf.Max(v[t[i]].y, Mathf.Max(v[t[i + 1]].y, v[t[i + 2]].y)));
                        removed++;
                        continue;
                    }
                    keep.Add(t[i]); keep.Add(t[i + 1]); keep.Add(t[i + 2]);
                }
                if (keep.Count == 0) { Object.DestroyImmediate(mf.gameObject); continue; }   // chunk held only the old Markthal
                if (keep.Count != t.Length)
                {
                    m.SetTriangles(keep, 0); EditorUtility.SetDirty(m);
                    var mc = mf.GetComponent<MeshCollider>(); if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = m; }
                }
            }
            var old = buildings.Find("Markthal");
            if (removed == 0 && old == null)
            {
                PrefabUtility.UnloadPrefabContents(root);
                return $"{Path.GetFileNameWithoutExtension(prefabPath)}: no Markthal in this area";
            }
            if (old != null)
            {
                // Rebuilding: keep the height measured the first time.
                var info = old.GetComponent<MarkthalInfo>();
                if (info != null) top = info.top;
                Object.DestroyImmediate(old.gameObject);
            }

            float groundY = 1.5f;
            foreach (var hit in Physics.RaycastAll(new Vector3(centre2.x, 80f, centre2.y), Vector3.down, 160f))
            {
                var part = hit.collider.GetComponentInParent<CityPart>();
                if (part != null && part.isGround) { groundY = hit.point.y; break; }
            }
            if (top < groundY + 20f) top = groundY + 40f;
            float height = top - groundY;

            // ---- Mesh ----
            float a = width * 0.5f, b = height;
            float ai = a - 10.5f, bi = b - 6.5f;          // the apartments live in a ~10 m thick shell
            var outer = Profile(a, b); var inner = Profile(ai, bi);
            Vector3 O = new Vector3(centre2.x, groundY, centre2.y);
            Vector3 X = new Vector3(across.x, 0, across.y), Z = new Vector3(along.x, 0, along.y);
            Vector3 P(Vector2 prof, float s) => O + X * prof.x + Vector3.up * prof.y + Z * s;
            float half = length * 0.5f;

            var verts = new List<Vector3>(); var uv0 = new List<Vector2>(); var uv1 = new List<Vector3>();
            var stone = new List<int>(); var mural = new List<int>(); var glass = new List<int>();
            void Quad(List<int> sub, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3, Vector3 w0, Vector3 w1, Vector3 w2, Vector3 w3, Vector3 outward)
            {
                int i0 = verts.Count;
                verts.Add(p0); verts.Add(p1); verts.Add(p2); verts.Add(p3);
                uv0.Add(t0); uv0.Add(t1); uv0.Add(t2); uv0.Add(t3);
                uv1.Add(w0); uv1.Add(w1); uv1.Add(w2); uv1.Add(w3);
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) > 0) sub.AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });
                else sub.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
            }
            // Arc lengths for window rows (outside) and mural mapping (inside).
            var arcO = Arc(outer); var arcI = Arc(inner);
            Vector2 stoneUv = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < Segments; i++)
            {
                // Outside: stone with square windows (UV1 = metres around the arch, metres along it).
                Vector2 o0 = outer[i], o1 = outer[i + 1];
                Vector3 outN = X * ((o0.x + o1.x) * 0.5f) + Vector3.up * ((o0.y + o1.y) * 0.5f);
                Quad(stone, P(o0, -half), P(o1, -half), P(o1, half), P(o0, half),
                     stoneUv, stoneUv, stoneUv, stoneUv,
                     new Vector3(0, arcO[i], 1), new Vector3(0, arcO[i + 1], 1), new Vector3(length, arcO[i + 1], 1), new Vector3(length, arcO[i], 1), outN);
                // Inside: the mural (faces point into the hall).
                Vector2 n0 = inner[i], n1 = inner[i + 1];
                Vector3 inN = -(X * ((n0.x + n1.x) * 0.5f) + Vector3.up * ((n0.y + n1.y) * 0.5f));
                float u0 = arcI[i] / arcI[Segments], u1 = arcI[i + 1] / arcI[Segments];
                Quad(mural, P(n0, -half), P(n1, -half), P(n1, half), P(n0, half),
                     new Vector2(u0, 0), new Vector2(u1, 0), new Vector2(u1, 1), new Vector2(u0, 1),
                     Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, inN);
                // End rings (stone, between outside and inside), both ends.
                foreach (float s in new[] { -half, half })
                {
                    Vector3 endN = Z * Mathf.Sign(s);
                    Quad(stone, P(o0, s), P(o1, s), P(n1, s), P(n0, s), stoneUv, stoneUv, stoneUv, stoneUv,
                         Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero, endN);
                }
            }
            // Glass end walls (cable-net facade), set in by 0.6 m. UV = metres for the net pattern.
            foreach (float s in new[] { -half + 0.6f, half - 0.6f })
            {
                Vector3 c = P(Vector2.zero, s);
                for (int i = 0; i < Segments; i++)
                {
                    int i0 = verts.Count;
                    Vector2 n0 = inner[i], n1 = inner[i + 1];
                    verts.Add(c); verts.Add(P(n0, s)); verts.Add(P(n1, s));
                    uv0.Add(Vector2.zero); uv0.Add(new Vector2(n0.x, n0.y) / 4f); uv0.Add(new Vector2(n1.x, n1.y) / 4f);
                    uv1.Add(Vector3.zero); uv1.Add(Vector3.zero); uv1.Add(Vector3.zero);
                    glass.AddRange(new[] { i0, i0 + 1, i0 + 2 });
                }
            }
            var mesh = new Mesh { name = "Markthal", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetUVs(0, uv0); mesh.SetUVs(1, uv1);
            mesh.subMeshCount = 3;
            mesh.SetTriangles(stone, 0); mesh.SetTriangles(mural, 1); mesh.SetTriangles(glass, 2);
            // Flat (low-poly) shading: every quad has its own vertices, so normals per face.
            mesh.RecalculateNormals(); mesh.RecalculateBounds();

            string meshPath = $"{Path.GetDirectoryName(prefabPath).Replace('\\', '/')}/Meshes/Markthal.asset";
            var oldMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (oldMesh != null) { EditorUtility.CopySerialized(mesh, oldMesh); mesh = oldMesh; } else AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject("Markthal");
            go.transform.SetParent(buildings, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = Materials();
            // Collider only for the shell (stone + mural), not the glass.
            var colMesh = new Mesh { name = "MarkthalCollider", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            colMesh.SetVertices(verts); var ct = new List<int>(stone); ct.AddRange(mural); colMesh.SetTriangles(ct, 0);
            string colPath = meshPath.Replace("Markthal.asset", "MarkthalCollider.asset");
            var oldCol = AssetDatabase.LoadAssetAtPath<Mesh>(colPath);
            if (oldCol != null) { EditorUtility.CopySerialized(colMesh, oldCol); colMesh = oldCol; } else AssetDatabase.CreateAsset(colMesh, colPath);
            go.AddComponent<MeshCollider>().sharedMesh = colMesh;
            go.AddComponent<CityPart>();
            go.AddComponent<MarkthalInfo>().top = top;
            go.isStatic = true;

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: Markthal {width:F0} x {length:F0} m, {height:F0} m high, removed {removed} old triangles";
        }

        static List<Vector2> Profile(float a, float b)
        {
            var p = new List<Vector2>();
            float e = 2f / Exponent;
            for (int i = 0; i <= Segments; i++)
            {
                float th = Mathf.PI * i / Segments;
                float c = Mathf.Cos(th), s = Mathf.Sin(th);
                p.Add(new Vector2(a * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), e), b * Mathf.Pow(Mathf.Abs(s), e)));
            }
            return p;
        }

        static float[] Arc(List<Vector2> p)
        {
            var r = new float[p.Count];
            for (int i = 1; i < p.Count; i++) r[i] = r[i - 1] + (p[i] - p[i - 1]).magnitude;
            return r;
        }

        static Material[] Materials()
        {
            Directory.CreateDirectory(Folder);
            // Stone: the stylized building shader with small square windows.
            string palPath = $"{Folder}/MarkthalPalette.png";
            if (!File.Exists(palPath))
            {
                var px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                px.SetPixel(0, 0, new Color(0.64f, 0.63f, 0.60f, 1f)); px.Apply();
                File.WriteAllBytes(palPath, px.EncodeToPNG()); Object.DestroyImmediate(px);
                AssetDatabase.ImportAsset(palPath);
                var ti = (TextureImporter)AssetImporter.GetAtPath(palPath);
                ti.filterMode = FilterMode.Point; ti.mipmapEnabled = false; ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.alphaSource = TextureImporterAlphaSource.FromInput; ti.SaveAndReimport();
            }
            var stone = LoadOrCreate($"{Folder}/M_Markthal_Stone.mat", "BDCity/StylizedBuilding");
            stone.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(palPath));
            stone.SetVector("_Cell", new Vector4(2.6f, 2.9f, 0, 0));
            stone.SetVector("_Win", new Vector4(0.42f, 0.42f, 0, 0));
            stone.SetColor("_WindowColor", new Color(0.30f, 0.38f, 0.46f));
            stone.SetColor("_WindowSky", new Color(0.58f, 0.68f, 0.77f));

            // Mural: generated "horn of plenty" — big fruit, flowers and leaves in bright colours.
            string muralPath = $"{Folder}/MarkthalMural.png";
            if (!File.Exists(muralPath)) File.WriteAllBytes(muralPath, MuralPng());
            AssetDatabase.ImportAsset(muralPath);
            var mural = LoadOrCreate($"{Folder}/M_Markthal_Mural.mat", "Universal Render Pipeline/Unlit");
            mural.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(muralPath));
            mural.SetColor("_BaseColor", new Color(0.92f, 0.92f, 0.92f));

            // Glass: transparent with the cable-net grid.
            string netPath = $"{Folder}/MarkthalNet.png";
            if (!File.Exists(netPath)) File.WriteAllBytes(netPath, NetPng());
            AssetDatabase.ImportAsset(netPath);
            var nti = (TextureImporter)AssetImporter.GetAtPath(netPath);
            nti.alphaIsTransparency = true; nti.wrapMode = TextureWrapMode.Repeat; nti.SaveAndReimport();
            var glass = LoadOrCreate($"{Folder}/M_Markthal_Glass.mat", "Universal Render Pipeline/Lit");
            glass.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(netPath));
            glass.SetColor("_BaseColor", new Color(0.80f, 0.90f, 0.96f, 1f));
            glass.SetFloat("_Surface", 1f); glass.SetFloat("_Blend", 0f);
            glass.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            glass.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            glass.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            glass.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            glass.SetFloat("_ZWrite", 0f); glass.SetFloat("_Cull", 0f);
            glass.SetFloat("_Smoothness", 0.85f);
            glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            glass.SetOverrideTag("RenderType", "Transparent");
            glass.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            glass.SetShaderPassEnabled("DepthOnly", false);
            glass.SetShaderPassEnabled("ShadowCaster", false);
            foreach (var m in new[] { stone, mural, glass }) EditorUtility.SetDirty(m);
            return new[] { stone, mural, glass };
        }

        static Material LoadOrCreate(string path, string shader)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find(shader)); AssetDatabase.CreateAsset(m, path); }
            else if (m.shader.name != shader) m.shader = Shader.Find(shader);
            return m;
        }

        static byte[] MuralPng()
        {
            const int W = 1024, H = 512;
            var px = new Color[W * H];
            // Background: sky blue at the top of the arch fading to warm orange at the sides.
            Color top = new Color(0.38f, 0.62f, 0.86f), side = new Color(0.98f, 0.62f, 0.30f);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)(W - 1);                  // around the arch: 0 and 1 at the floor, 0.5 at the top
                    float k = Mathf.Abs(u - 0.5f) * 2f;
                    px[y * W + x] = Color.Lerp(top, side, k * k);
                }
            var rng = new System.Random(7);
            Color[] fruit =
            {
                new Color(0.90f, 0.20f, 0.18f), new Color(0.98f, 0.55f, 0.12f), new Color(0.99f, 0.84f, 0.20f),
                new Color(0.40f, 0.72f, 0.25f), new Color(0.55f, 0.25f, 0.62f), new Color(0.95f, 0.45f, 0.62f),
                new Color(0.20f, 0.55f, 0.35f), new Color(0.98f, 0.95f, 0.85f)
            };
            for (int n = 0; n < 260; n++)
            {
                float cx = (float)rng.NextDouble() * W, cy = (float)rng.NextDouble() * H;
                float rx = 10f + (float)rng.NextDouble() * 38f, ry = rx * (0.6f + (float)rng.NextDouble() * 0.8f);
                float ang = (float)rng.NextDouble() * Mathf.PI;
                Color col = fruit[rng.Next(fruit.Length)];
                Color shade = col * 0.72f; shade.a = 1f;
                float ca = Mathf.Cos(ang), sa = Mathf.Sin(ang);
                int x0 = Mathf.Max(0, (int)(cx - rx - 3)), x1 = Mathf.Min(W - 1, (int)(cx + rx + 3));
                int y0 = Mathf.Max(0, (int)(cy - rx - 3)), y1 = Mathf.Min(H - 1, (int)(cy + rx + 3));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float dx = x - cx, dy = y - cy;
                        float lx = (dx * ca + dy * sa) / rx, ly = (-dx * sa + dy * ca) / ry;
                        float d = lx * lx + ly * ly;
                        if (d > 1f) continue;
                        // Simple cartoon shading: darker rim, light spot top-left.
                        Color c = d > 0.78f ? shade : col;
                        if ((lx + 0.35f) * (lx + 0.35f) + (ly - 0.35f) * (ly - 0.35f) < 0.06f) c = Color.Lerp(c, Color.white, 0.6f);
                        px[y * W + x] = c;
                    }
            }
            var tex = new Texture2D(W, H, TextureFormat.RGB24, true);
            tex.SetPixels(px); tex.Apply();
            var bytes = tex.EncodeToPNG(); Object.DestroyImmediate(tex);
            return bytes;
        }

        static byte[] NetPng()
        {
            const int S = 128;
            var px = new Color[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    bool line = x < 3 || y < 3;
                    px[y * S + x] = line ? new Color(0.35f, 0.38f, 0.42f, 0.95f) : new Color(0.85f, 0.93f, 0.98f, 0.18f);
                }
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, true);
            tex.SetPixels(px); tex.Apply();
            var bytes = tex.EncodeToPNG(); Object.DestroyImmediate(tex);
            return bytes;
        }

        static List<Vector2> Footprint(CesiumGeoreference georef, out Vector2 point)
        {
            var inv = CultureInfo.InvariantCulture;
            double r = 30, dLat = r / 111000.0, dLon = r / (111000.0 * System.Math.Cos(Lat * System.Math.PI / 180.0));
            string url = "https://api.pdok.nl/kadaster/bgt/ogc/v1/collections/pand/items?f=json&limit=100&bbox="
                         + string.Format(inv, "{0},{1},{2},{3}", Lon - dLon, Lat - dLat, Lon + dLon, Lat + dLat);
            string json = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(60) }.GetStringAsync(url).Result;
            double3 pu = georef.TransformEarthCenteredEarthFixedPositionToUnity(CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(new double3(Lon, Lat, 0)));
            point = new Vector2((float)pu.x, (float)pu.z);
            foreach (string f in json.Split(new[] { "{\"type\":\"Feature\"" }, System.StringSplitOptions.None))
            {
                if (!f.Contains("\"eind_registratie\":null")) continue;
                int c = f.IndexOf("\"coordinates\":"); if (c < 0) continue;
                int e = f.IndexOf("]]]", c);
                var nums = System.Text.RegularExpressions.Regex.Matches(f.Substring(c, e - c), "-?[0-9]+\\.[0-9]+");
                var poly = new List<Vector2>();
                for (int i = 0; i + 1 < nums.Count; i += 2)
                {
                    double3 u = georef.TransformEarthCenteredEarthFixedPositionToUnity(CesiumWgs84Ellipsoid.LongitudeLatitudeHeightToEarthCenteredEarthFixed(
                        new double3(double.Parse(nums[i].Value, inv), double.Parse(nums[i + 1].Value, inv), 0)));
                    poly.Add(new Vector2((float)u.x, (float)u.z));
                }
                if (poly.Count >= 3 && Inside(point, poly)) return poly;
            }
            return null;
        }

        static bool Inside(Vector2 p, List<Vector2> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if (((poly[i].y > p.y) != (poly[j].y > p.y)) && (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x))
                    inside = !inside;
            return inside;
        }

        static float DistanceToPolygon(Vector2 p, List<Vector2> poly)
        {
            float best = float.MaxValue;
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
