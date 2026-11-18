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
    /// X-CITE style ground: replaces the aerial photo on the frozen ground chunks with a flat colour map
    /// drawn from the Dutch large-scale base map (BGT): roads, bike paths, pavement, grass, water, bridges.
    /// The ground meshes already have longitude/latitude UVs, so the drawn map lines up exactly.
    /// Needs the CityScene open (for the georeference) and internet (api.pdok.nl).
    /// </summary>
    public static class CityGroundMap
    {
        const string Api = "https://api.pdok.nl/kadaster/bgt/ogc/v1/collections/";
        const int TexSize = 1024;

        // ---- Colours ----
        static readonly Color Background = new Color(0.80f, 0.78f, 0.74f);
        static readonly Color Asphalt = new Color(0.47f, 0.48f, 0.50f);
        static readonly Color BikePath = new Color(0.71f, 0.45f, 0.41f);
        static readonly Color Pavement = new Color(0.83f, 0.81f, 0.77f);
        static readonly Color Rail = new Color(0.56f, 0.53f, 0.51f);
        static readonly Color Parking = new Color(0.62f, 0.62f, 0.62f);
        static readonly Color RoadOther = new Color(0.60f, 0.60f, 0.61f);
        static readonly Color Grass = new Color(0.58f, 0.74f, 0.44f);
        static readonly Color Shrubs = new Color(0.45f, 0.63f, 0.38f);
        static readonly Color Paved = new Color(0.78f, 0.77f, 0.74f);
        static readonly Color Sand = new Color(0.87f, 0.82f, 0.66f);
        static readonly Color HalfPaved = new Color(0.76f, 0.72f, 0.63f);
        static readonly Color Water = new Color(0.42f, 0.60f, 0.76f);
        static readonly Color Bank = new Color(0.62f, 0.68f, 0.68f);
        static readonly Color Bridge = new Color(0.67f, 0.66f, 0.64f);
        static readonly Color Footprint = new Color(0.70f, 0.69f, 0.66f);

        internal class Feature { public List<List<double2>> rings = new List<List<double2>>(); public Color colour; public int level; public int order; public double4 bbox; }

        [MenuItem("B&D City/Flat X-CITE ground (all areas)")]
        public static void ApplyAll()
        {
            foreach (var a in new[] { "Wijnhaven107", "Lijnbaan", "Blaak" })
                Debug.Log("[CityGroundMap] " + Apply($"Assets/CityFeature/Frozen/{a}/Frozen - {a}.prefab"));
        }

        public static string Apply(string prefabPath)
        {
            var georef = Object.FindAnyObjectByType<CesiumGeoreference>();
            if (georef == null) return "Open CityScene first (needs the CesiumGeoreference).";
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var root = PrefabUtility.LoadPrefabContents(prefabPath);

            // Chunk extents in lon/lat, exactly as the freezer computed the UVs.
            var chunks = new List<(MeshRenderer r, double4 box)>();
            double4 all = new double4(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            foreach (var mf in root.transform.Find("Ground").GetComponentsInChildren<MeshFilter>(true))
            {
                double4 b = new double4(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
                foreach (var v in mf.sharedMesh.vertices)
                {
                    var w = mf.transform.TransformPoint(v);
                    double3 llh = CesiumWgs84Ellipsoid.EarthCenteredEarthFixedToLongitudeLatitudeHeight(georef.TransformUnityPositionToEarthCenteredEarthFixed(new double3(w.x, w.y, w.z)));
                    b = new double4(System.Math.Min(b.x, llh.x), System.Math.Min(b.y, llh.y), System.Math.Max(b.z, llh.x), System.Math.Max(b.w, llh.y));
                }
                chunks.Add((mf.GetComponent<MeshRenderer>(), b));
                all = new double4(System.Math.Min(all.x, b.x), System.Math.Min(all.y, b.y), System.Math.Max(all.z, b.z), System.Math.Max(all.w, b.w));
            }

            // Map data for the whole area.
            var features = new List<Feature>();
            var http = new System.Net.Http.HttpClient { Timeout = System.TimeSpan.FromSeconds(120) };
            string bbox = string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", all.x, all.y, all.z, all.w);
            string[] collections = { "onbegroeidterreindeel", "begroeidterreindeel", "ondersteunendwaterdeel", "waterdeel", "ondersteunendwegdeel", "wegdeel", "overbruggingsdeel", "pand" };
            for (int ci = 0; ci < collections.Length; ci++)
            {
                string url = $"{Api}{collections[ci]}/items?f=json&limit=1000&bbox={bbox}";
                int pages = 0;
                while (url != null && pages++ < 60)
                {
                    string json = http.GetStringAsync(url).Result;
                    foreach (string f in json.Split(new[] { "{\"type\":\"Feature\"" }, System.StringSplitOptions.None))
                    {
                        if (!f.Contains("\"geometry\"") || !f.Contains("\"eind_registratie\":null")) continue;
                        var feat = Parse(f, collections[ci]);
                        if (feat == null || feat.level < 0) continue;
                        feat.order = ci;
                        features.Add(feat);
                    }
                    var next = Regex.Match(json, "\"rel\":\"next\"[^}]*?\"href\":\"([^\"]+)\"");
                    if (!next.Success) next = Regex.Match(json, "\"href\":\"([^\"]+)\"[^}]*?\"rel\":\"next\"");
                    url = next.Success ? next.Groups[1].Value.Replace("\\u0026", "&") : null;
                }
            }
            // Lower first, then by layer: bridges and things above ground end on top.
            features.Sort((a, b) => a.level != b.level ? a.level.CompareTo(b.level) : a.order.CompareTo(b.order));

            // Draw one texture per chunk.
            int drawn = 0;
            foreach (var (r, box) in chunks)
            {
                var px = new Color32[TexSize * TexSize];
                Color32 bg = Background;
                for (int i = 0; i < px.Length; i++) px[i] = bg;
                foreach (var f in features)
                {
                    if (f.bbox.z < box.x || f.bbox.x > box.z || f.bbox.w < box.y || f.bbox.y > box.w) continue;
                    Fill(px, f, box);
                }
                var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
                tex.SetPixels32(px); tex.Apply();
                string name = r.gameObject.name;
                string texPath = $"{folder}/Textures/{name}_map.png";
                File.WriteAllBytes(texPath, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(texPath);
                var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
                ti.maxTextureSize = TexSize; ti.mipmapEnabled = true; ti.anisoLevel = 4; ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
                var mat = r.sharedMaterials[0];
                mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Smoothness", 0.05f);
                EditorUtility.SetDirty(mat);
                drawn++;
            }

            // Far ground: keep the photo but turn it into soft colour areas (low resolution, lighter, calmer).
            int far = 0;
            var farRoot = root.transform.Find("Far Ground");
            if (farRoot != null)
                foreach (var r in farRoot.GetComponentsInChildren<MeshRenderer>(true))
                    foreach (var mat in r.sharedMaterials)
                    {
                        if (mat == null || mat.name == "QuayStone") continue;
                        // Far tiles use Cesium's own shader: lower the resolution of whatever textures it has.
                        foreach (string prop in mat.GetTexturePropertyNames())
                        {
                            var tex = mat.GetTexture(prop);
                            string p = tex != null ? AssetDatabase.GetAssetPath(tex) : null;
                            if (!string.IsNullOrEmpty(p) && AssetImporter.GetAtPath(p) is TextureImporter fi && fi.maxTextureSize != 64)
                            { fi.maxTextureSize = 64; fi.SaveAndReimport(); }
                        }
                        far++;
                    }

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();
            return $"{Path.GetFileNameWithoutExtension(prefabPath)}: {features.Count} map features, {drawn} ground textures drawn, {far} far-ground materials softened";
        }

        internal static Feature Parse(string f, string collection)
        {
            string Prop(string key)
            {
                var m = Regex.Match(f, "\"" + key + "\":(\"([^\"]*)\"|(-?[0-9]+)|null)");
                return m.Success ? (m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : m.Groups[3].Value) : "";
            }
            var feat = new Feature();
            string lvl = Prop("relatieve_hoogteligging");
            feat.level = int.TryParse(lvl, out int l) ? l : 0;
            string functie = Prop("functie"), fysiek = Prop("fysiek_voorkomen");
            switch (collection)
            {
                case "wegdeel":
                    if (functie.StartsWith("rijbaan")) feat.colour = Asphalt;
                    else if (functie.Contains("fietspad")) feat.colour = BikePath;
                    else if (functie.StartsWith("voet") || functie.Contains("voetganger") || functie == "woonerf" || functie == "ruiterpad") feat.colour = Pavement;
                    else if (functie.Contains("OV") || functie.Contains("spoor") || functie.Contains("baan voor vliegverkeer")) feat.colour = Rail;
                    else if (functie.Contains("parkeer") || functie == "inrit") feat.colour = Parking;
                    else feat.colour = RoadOther;
                    break;
                case "ondersteunendwegdeel":
                    feat.colour = fysiek.Contains("groen") || fysiek.Contains("gras") ? Grass : Paved; break;
                case "begroeidterreindeel":
                    feat.colour = fysiek.Contains("struik") || fysiek.Contains("bos") || fysiek.Contains("houtwal") ? Shrubs : Grass; break;
                case "onbegroeidterreindeel":
                    feat.colour = fysiek.Contains("zand") ? Sand : fysiek.Contains("half") ? HalfPaved : Paved; break;
                case "waterdeel": feat.colour = Water; break;
                case "ondersteunendwaterdeel": feat.colour = Bank; break;
                case "overbruggingsdeel": feat.colour = Bridge; feat.level = System.Math.Max(feat.level, 1); break;
                case "pand": feat.colour = Footprint; break;
            }

            int c = f.IndexOf("\"coordinates\":", f.IndexOf("\"geometry\""));
            if (c < 0) return null;
            int i = f.IndexOf('[', c);
            int depth = 0; List<double2> ring = null;
            var num = new System.Text.StringBuilder(); var pair = new List<double>();
            for (; i < f.Length; i++)
            {
                char ch = f[i];
                if (ch == '[') { depth++; num.Clear(); pair.Clear(); }
                else if (ch == ']' || ch == ',')
                {
                    if (num.Length > 0) { pair.Add(double.Parse(num.ToString(), CultureInfo.InvariantCulture)); num.Clear(); }
                    if (ch == ']')
                    {
                        if (pair.Count >= 2)
                        {
                            if (ring == null) { ring = new List<double2>(); feat.rings.Add(ring); }
                            ring.Add(new double2(pair[0], pair[1]));
                            pair.Clear();
                        }
                        else ring = null; // a ring (or polygon) just closed
                        depth--;
                        if (depth == 0) break;
                    }
                }
                else if (char.IsDigit(ch) || ch == '-' || ch == '.' || ch == 'e' || ch == 'E' || ch == '+') num.Append(ch);
            }
            if (feat.rings.Count == 0) return null;
            feat.bbox = new double4(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);
            foreach (var rg in feat.rings) foreach (var p in rg)
                feat.bbox = new double4(System.Math.Min(feat.bbox.x, p.x), System.Math.Min(feat.bbox.y, p.y), System.Math.Max(feat.bbox.z, p.x), System.Math.Max(feat.bbox.w, p.y));
            return feat;
        }

        // Even-odd scanline fill of all rings of a feature (holes come out right).
        static void Fill(Color32[] px, Feature f, double4 box)
        {
            double sx = TexSize / (box.z - box.x), sy = TexSize / (box.w - box.y);
            var edges = new List<(double x0, double y0, double x1, double y1)>();
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var ring in f.rings)
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    double ax = (a.x - box.x) * sx, ay = (a.y - box.y) * sy, bx = (b.x - box.x) * sx, by = (b.y - box.y) * sy;
                    if (ay == by) continue;
                    edges.Add((ax, ay, bx, by));
                    minY = System.Math.Min(minY, System.Math.Min(ay, by)); maxY = System.Math.Max(maxY, System.Math.Max(ay, by));
                }
            int y0 = System.Math.Max(0, (int)System.Math.Floor(minY)), y1 = System.Math.Min(TexSize - 1, (int)System.Math.Ceiling(maxY));
            Color32 col = f.colour;
            var xs = new List<double>();
            for (int y = y0; y <= y1; y++)
            {
                double yc = y + 0.5;
                xs.Clear();
                foreach (var e in edges)
                    if ((e.y0 <= yc && e.y1 > yc) || (e.y1 <= yc && e.y0 > yc))
                        xs.Add(e.x0 + (yc - e.y0) * (e.x1 - e.x0) / (e.y1 - e.y0));
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int xa = System.Math.Max(0, (int)System.Math.Ceiling(xs[k] - 0.5)), xb = System.Math.Min(TexSize - 1, (int)System.Math.Floor(xs[k + 1] - 0.5));
                    int row = y * TexSize;
                    for (int x = xa; x <= xb; x++) px[row + x] = col;
                }
            }
        }
    }
}
