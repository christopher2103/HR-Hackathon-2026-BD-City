using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Recolours the stylized buildings with a palette of real Rotterdam facade colours.
    /// Each building's colour measured from the aerial photo decides where it lands in the palette:
    /// warm photo colours become brick (dark, red or sand by lightness), cool/grey ones become plaster,
    /// concrete or grey; towers become glass/concrete/white. Landmarks keep their own colours.
    /// Windows only on towers/offices (tall buildings) and landmarks.
    /// </summary>
    public static class CityPalette
    {
        // ---- Palette ----
        static readonly Color DarkBrick = new Color(0.50f, 0.32f, 0.27f);
        static readonly Color RedBrick = new Color(0.68f, 0.40f, 0.32f);
        static readonly Color OrangeBrick = new Color(0.76f, 0.51f, 0.39f);
        static readonly Color SandBrick = new Color(0.84f, 0.73f, 0.56f);
        static readonly Color Cream = new Color(0.93f, 0.89f, 0.80f);
        static readonly Color White = new Color(0.95f, 0.95f, 0.93f);
        static readonly Color LightConcrete = new Color(0.78f, 0.77f, 0.74f);
        static readonly Color MidGrey = new Color(0.60f, 0.61f, 0.62f);
        static readonly Color DarkGrey = new Color(0.42f, 0.43f, 0.45f);
        static readonly Color Glass = new Color(0.68f, 0.74f, 0.78f);
        static readonly Color DarkGlass = new Color(0.54f, 0.59f, 0.64f);

        static readonly Color FlatRoof = new Color(0.52f, 0.52f, 0.51f);
        static readonly Color LightRoof = new Color(0.66f, 0.65f, 0.62f);
        static readonly Color TileRoof = new Color(0.62f, 0.34f, 0.27f);
        static readonly Color SlateRoof = new Color(0.33f, 0.34f, 0.37f);

        const float TowerHeight = 28f;     // from this height a building gets windows (towers / offices)

        class Building
        {
            public MeshFilter mf; public int comp; public Color photo; public float height; public int landmark = -1;
            public float warmth, light; public bool tower; public Color wall; public float windowStyle;
            public bool tileRoof; public Color roofFlat, roofPitched;
        }

        public static string Apply(string prefabPath)
        {
            string folder = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            string palettePath = $"{folder}/Textures/BuildingPalette.png";
            var oldPal = new Texture2D(2, 2); oldPal.LoadImage(File.ReadAllBytes(palettePath));
            int oldSize = oldPal.width;
            Color OldColor(Vector2 uv) => oldPal.GetPixel(Mathf.FloorToInt(uv.x * oldSize), Mathf.FloorToInt(uv.y * oldSize));

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var all = new List<Building>();
            var perMesh = new Dictionary<MeshFilter, (int[] comp, Dictionary<int, Building> map)>();

            // ---- Pass 1: measure each building ----
            foreach (var mf in root.transform.Find("Buildings").GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.name == "Cube Houses") continue;
                var mesh = mf.sharedMesh;
                var v = mesh.vertices; var t = mesh.triangles; var uv = mesh.uv;
                var comp = Components(v, t);
                var map = new Dictionary<int, Building>();
                var minY = new Dictionary<int, float>(); var maxY = new Dictionary<int, float>();
                var votes = new Dictionary<int, Dictionary<Color, float>>();
                for (int i = 0; i < v.Length; i++)
                {
                    int c = comp[i];
                    minY[c] = minY.TryGetValue(c, out float a) ? Mathf.Min(a, v[i].y) : v[i].y;
                    maxY[c] = maxY.TryGetValue(c, out float b) ? Mathf.Max(b, v[i].y) : v[i].y;
                }
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                    float area = n.magnitude * 0.5f;
                    if (area < 1e-6f) continue;
                    n /= area * 2f;
                    if (Mathf.Abs(n.y) > 0.3f) continue; // walls decide the colour
                    int c = comp[t[i]];
                    if (!votes.TryGetValue(c, out var d)) votes[c] = d = new Dictionary<Color, float>();
                    Color col = OldColor(uv[t[i]]);
                    d[col] = (d.TryGetValue(col, out float w) ? w : 0f) + area;
                }
                foreach (var c in minY.Keys)
                {
                    var bd = new Building { mf = mf, comp = c, height = maxY[c] - minY[c] };
                    Color photo = new Color(0.8f, 0.8f, 0.78f); float best = -1;
                    if (votes.TryGetValue(c, out var d)) foreach (var kv in d) if (kv.Value > best) { best = kv.Value; photo = kv.Key; }
                    bd.photo = photo;
                    for (int l = 0; l < CityStylizer.Landmarks.Length; l++)
                        if (Close(photo, CityStylizer.Landmarks[l].walls) || Close(photo, CityStylizer.Landmarks[l].roofs)) bd.landmark = l;
                    // The Blaak "pencil" tower next to the Cube Houses picked up their yellow earlier: not a landmark.
                    if (bd.landmark >= 0 && CityStylizer.Landmarks[bd.landmark].name == "Cube houses" && bd.height > 30f) bd.landmark = -1;
                    // Undo the earlier softening as well as possible: hue stays, saturation/lightness were compressed.
                    Color.RGBToHSV(photo, out float h, out float s, out float val);
                    bd.light = val;
                    float warmHue = Mathf.Cos((h * 360f - 25f) * Mathf.Deg2Rad); // 1 = orange-red, -1 = blue
                    bd.warmth = warmHue * s + (photo.r - photo.b) * 0.5f;
                    bd.tower = bd.height >= TowerHeight;
                    map[c] = bd; all.Add(bd);
                }
                perMesh[mf] = (comp, map);
            }

            // ---- Pass 2: pick palette colours (ranks within this area, so the mix stays varied) ----
            var low = all.FindAll(b => !b.tower && b.landmark < 0);
            var towers = all.FindAll(b => b.tower && b.landmark < 0);
            Rank(low, out var lowWarm, out var lowLight);
            Rank(towers, out var towWarm, out var towLight);
            var rng = new System.Random(prefabPath.GetHashCode());
            foreach (var b in all)
            {
                b.roofFlat = rng.NextDouble() < 0.5 ? FlatRoof : LightRoof;
                b.roofPitched = SlateRoof;
                if (b.landmark >= 0)
                {
                    var l = CityStylizer.Landmarks[b.landmark];
                    b.wall = l.walls; b.roofFlat = l.roofs; b.roofPitched = l.roofs;
                    b.windowStyle = l.name == "Stadhuis" ? 0.5f : (l.name == "Cube houses" ? 1f : (b.height >= 15f ? 1f : 0f));
                    continue;
                }
                if (b.tower)
                {
                    // Rotterdam towers: lots of white/cream and sand, brown brick, concrete, and some glass.
                    float w = towWarm[b], li = towLight[b];
                    if (w > 0.72f) b.wall = li > 0.45f ? White : Cream;
                    else if (w > 0.48f) b.wall = li > 0.6f ? SandBrick : li > 0.3f ? OrangeBrick : DarkBrick;
                    else if (w > 0.12f) b.wall = li > 0.55f ? LightConcrete : li > 0.25f ? MidGrey : Cream;
                    else b.wall = li > 0.35f ? Glass : DarkGlass;
                    b.windowStyle = 1f;
                }
                else
                {
                    float w = lowWarm[b], li = lowLight[b];
                    if (w > 0.45f)
                    {
                        // Brick family (most of Rotterdam's housing blocks).
                        b.wall = li < 0.3f ? DarkBrick : li < 0.6f ? RedBrick : li < 0.8f ? OrangeBrick : SandBrick;
                        if (b.height < 16f && rng.NextDouble() < 0.6) b.roofPitched = TileRoof;
                    }
                    else
                    {
                        b.wall = li < 0.2f ? DarkGrey : li < 0.45f ? MidGrey : li < 0.7f ? LightConcrete : li < 0.88f ? Cream : White;
                    }
                    b.windowStyle = 0f;
                }
                // A little per-building variation so neighbours never look copy-pasted.
                b.wall = Jitter(b.wall, rng, 0.035f);
                b.roofFlat = Jitter(b.roofFlat, rng, 0.03f);
            }

            // ---- Pass 3: new palette + UVs ----
            var colours = new List<Color>();
            var slots = new Dictionary<Color32, int>();
            int Slot(Color c, float alpha)
            {
                var key = (Color32)new Color(c.r, c.g, c.b, alpha);
                if (!slots.TryGetValue(key, out int i)) { i = colours.Count; colours.Add(key); slots[key] = i; }
                return i;
            }
            var triSlots = new Dictionary<MeshFilter, int[]>();
            foreach (var kv in perMesh)
            {
                var mesh = kv.Key.sharedMesh; var v = mesh.vertices; var t = mesh.triangles;
                var res = new int[t.Length / 3];
                for (int i = 0; i < t.Length; i += 3)
                {
                    var b = kv.Value.map[kv.Value.comp[t[i]]];
                    Vector3 n = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]).normalized;
                    float ny = Mathf.Abs(n.y);
                    res[i / 3] = ny <= 0.3f ? Slot(b.wall, b.windowStyle) : ny > 0.95f ? Slot(b.roofFlat, 0f) : Slot(b.roofPitched, 0f);
                }
                triSlots[kv.Key] = res;
            }
            int size = 8; while (size * size < colours.Count) size *= 2;
            Vector2 Uv(int s) => new Vector2((s % size + 0.5f) / size, (s / size + 0.5f) / size);
            foreach (var kv in triSlots)
            {
                var mesh = kv.Key.sharedMesh; var t = mesh.triangles;
                var uv = new Vector2[mesh.vertexCount];
                for (int i = 0; i < t.Length; i += 3) { var u = Uv(kv.Value[i / 3]); uv[t[i]] = u; uv[t[i + 1]] = u; uv[t[i + 2]] = u; }
                mesh.SetUVs(0, uv);
                EditorUtility.SetDirty(mesh);
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = i < colours.Count ? colours[i] : new Color(1, 1, 1, 0);
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(palettePath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex); Object.DestroyImmediate(oldPal);
            AssetDatabase.ImportAsset(palettePath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(palettePath);
            ti.filterMode = FilterMode.Point; ti.mipmapEnabled = false; ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.wrapMode = TextureWrapMode.Clamp; ti.npotScale = TextureImporterNPOTScale.None; ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = false;
            ti.SaveAndReimport();

            // Calmer glass: darker, wider windows (towers / offices).
            var mat = AssetDatabase.LoadAssetAtPath<Material>($"{folder}/Materials/BuildingsStylized.mat");
            mat.SetColor("_WindowColor", new Color(0.30f, 0.38f, 0.46f));
            mat.SetColor("_WindowSky", new Color(0.58f, 0.68f, 0.77f));
            mat.SetFloat("_FrameDarken", 0.9f);
            mat.SetVector("_Cell", new Vector4(2.8f, 3.4f, 0, 0));
            mat.SetVector("_Win", new Vector4(0.62f, 0.50f, 0, 0));
            EditorUtility.SetDirty(mat);

            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.SaveAssets();

            var counts = new Dictionary<string, int>();
            foreach (var b in all)
            {
                string k = b.landmark >= 0 ? "landmark" : b.tower ? "tower" : b.wall.r - b.wall.b > 0.12f ? "brick" : "plaster/grey";
                counts[k] = counts.TryGetValue(k, out int x) ? x + 1 : 1;
            }
            var sb = new System.Text.StringBuilder($"{Path.GetFileNameWithoutExtension(prefabPath)}: {all.Count} buildings, palette {colours.Count} colours —");
            foreach (var kv in counts) sb.Append($" {kv.Key} {kv.Value}");
            return sb.ToString();
        }

        static void Rank(List<Building> list, out Dictionary<Building, float> warm, out Dictionary<Building, float> light)
        {
            warm = new Dictionary<Building, float>(); light = new Dictionary<Building, float>();
            var byWarm = new List<Building>(list); byWarm.Sort((a, b) => a.warmth.CompareTo(b.warmth));
            var byLight = new List<Building>(list); byLight.Sort((a, b) => a.light.CompareTo(b.light));
            float n = Mathf.Max(1, list.Count - 1);
            for (int i = 0; i < byWarm.Count; i++) warm[byWarm[i]] = i / n;
            for (int i = 0; i < byLight.Count; i++) light[byLight[i]] = i / n;
        }

        static Color Jitter(Color c, System.Random rng, float amount)
        {
            float d = ((float)rng.NextDouble() * 2f - 1f) * amount;
            float h = ((float)rng.NextDouble() * 2f - 1f) * amount * 0.4f;
            return new Color(Mathf.Clamp01(c.r + d + h), Mathf.Clamp01(c.g + d), Mathf.Clamp01(c.b + d - h));
        }

        static bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.02f && Mathf.Abs(a.g - b.g) < 0.02f && Mathf.Abs(a.b - b.b) < 0.02f;

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
