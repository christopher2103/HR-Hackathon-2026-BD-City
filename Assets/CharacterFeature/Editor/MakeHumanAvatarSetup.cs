using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace CharacterFeature.EditorTools
{
    /// <summary>
    /// One-click setup for a MakeHuman export (exported with ALL clothing options on, "Hide faces under clothes" off):
    ///  1. repairs the MakeHuman 1.2 text-FBX bug (property names written as "b'...'" -> the skeleton imports flat);
    ///  2. makes URP materials in the male's style and finds each part's texture;
    ///  3. builds a prefab with ProceduralWalk + AvatarWardrobe (slots Outfit / Hair / Shoes / Hat from the part names);
    ///  4. bakes which skin each clothing piece covers (so skin never pokes through);
    ///  5. makes a city bot variant (BotWanderer, simple walking) for the bot spawner.
    /// Use: select the .fbx in the Project window > B&D City > Avatar > Set up selected MakeHuman model.
    /// </summary>
    public static class MakeHumanAvatarSetup
    {
        static readonly string[] HairNames = { "afro", "bob", "braid", "long", "ponytail", "short", "hair", "bun", "dread" };
        static readonly string[] HatNames = { "fedora", "hat", "cap", "helmet", "beanie" };
        static readonly string[] BodyNames = { "_generic", "female1605", "male1591", "_muscle_", "proxy741", "basemesh" };

        [MenuItem("B&D City/Avatar/Set up selected MakeHuman model")]
        public static void SetupSelected()
        {
            foreach (var o in Selection.objects)
            {
                string p = AssetDatabase.GetAssetPath(o);
                if (p.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)) Debug.Log("[Avatar] " + Setup(p));
            }
        }

        public static string Setup(string fbxPath)
        {
            string name = Path.GetFileNameWithoutExtension(fbxPath);
            string folder = $"Assets/CharacterFeature/Humans/{name}";
            Directory.CreateDirectory(folder + "/Materials");
            AssetDatabase.Refresh();

            // 1. Text-FBX repair.
            bool repaired = RepairTextFbx(fbxPath);
            if (repaired) AssetDatabase.ImportAsset(fbxPath, ImportAssetOptions.ForceUpdate);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return $"{name}: no skinned meshes found";
            Transform head = model.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "head");
            if (head != null && head.position.y < 0.5f) return $"{name}: the skeleton is still flat after repair; export again as binary FBX";

            // 2. Materials.
            var textures = TextureNames(fbxPath);
            var importer = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var used = new HashSet<string>();
            string bodyName = null; bool female = false;
            foreach (var r in renderers)
            {
                string part = r.name.Replace("Mesh", "");
                string kind = Kind(part);
                if (kind == "body") { bodyName = r.name; female = part.Contains("female"); }
                if (r.sharedMaterial != null && r.sharedMaterial.shader == lit && AssetDatabase.GetAssetPath(r.sharedMaterial).EndsWith(".mat")
                    && r.sharedMaterial.GetTexture("_BaseMap") != null) continue;   // already set up (e.g. the original male)
                string tex = FindTexture(part, kind, textures, used);
                bool clip = kind == "hair" || part.StartsWith("eyebrow") || part.StartsWith("eyelash");
                string matPath = $"{folder}/Materials/M_{name}_{part}.mat";
                var m = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (m == null) { m = new Material(lit); AssetDatabase.CreateAsset(m, matPath); }
                m.SetTexture("_BaseMap", tex != null ? LoadTexture(tex, fbxPath) : null);
                m.SetColor("_BaseColor", Color.white);
                m.SetFloat("_Smoothness", 0.25f);
                m.SetFloat("_AlphaClip", clip ? 1 : 0);
                if (clip) { m.EnableKeyword("_ALPHATEST_ON"); m.SetFloat("_Cutoff", 0.5f); m.renderQueue = 2450; m.SetOverrideTag("RenderType", "TransparentCutout"); }
                m.SetFloat("_Cull", clip ? 0 : 2);
                EditorUtility.SetDirty(m);
                if (r.sharedMaterial != null) importer.AddRemap(new AssetImporter.SourceAssetIdentifier(r.sharedMaterial), m);
            }
            importer.isReadable = true;   // the wardrobe hides skin under clothes by editing the body's triangles at runtime
            importer.SaveAndReimport();
            model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);

            // 3. Prefab.
            var root = new GameObject(name);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            inst.name = "Model";
            var slots = new Dictionary<string, List<(string label, Renderer[] parts)>>
            {
                ["Outfit"] = new List<(string, Renderer[])>(), ["Hair"] = new List<(string, Renderer[])>(),
                ["Shoes"] = new List<(string, Renderer[])>(), ["Hat"] = new List<(string, Renderer[])> { ("No hat", new Renderer[0]) }
            };
            foreach (var r in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                string part = r.name.Replace("Mesh", "");
                switch (Kind(part))
                {
                    case "hide": r.gameObject.SetActive(false); break;
                    case "hair": slots["Hair"].Add((Label(part), new Renderer[] { r })); break;
                    case "shoes": slots["Shoes"].Add((Label(part), new Renderer[] { r })); break;
                    case "hat": slots["Hat"].Add((Label(part), new Renderer[] { r })); break;
                    case "clothes": slots["Outfit"].Add((Label(part), new Renderer[] { r })); break;
                }
            }
            var walk = root.AddComponent<BubbleFeature.Bots.ProceduralWalk>();
            var ws = new SerializedObject(walk);
            ws.FindProperty("armDownAngle").floatValue = female ? 72f : 78f;
            ws.FindProperty("stepLength").floatValue = female ? 0.55f : 0.65f;
            ws.FindProperty("elbowBend").floatValue = 10f;
            ws.ApplyModifiedPropertiesWithoutUndo();

            var wardrobe = root.AddComponent<AvatarWardrobe>();
            var so = new SerializedObject(wardrobe);
            var sp = so.FindProperty("slots"); sp.ClearArray();
            Color[] tints = { new Color(0.65f, 0.8f, 1f), new Color(1f, 0.7f, 0.7f), new Color(0.75f, 1f, 0.75f), new Color(0.55f, 0.55f, 0.6f) };
            Color[] hairTints = { new Color(0.45f, 0.3f, 0.2f), new Color(1f, 0.85f, 0.55f), new Color(0.35f, 0.35f, 0.35f) };
            Color[] shoeTints = { new Color(0.4f, 0.4f, 0.45f), new Color(1f, 0.6f, 0.6f) };
            int si = 0;
            foreach (var kv in slots)
            {
                if (kv.Value.Count == 0 || (kv.Key == "Hat" && kv.Value.Count == 1)) continue;
                sp.InsertArrayElementAtIndex(si);
                var s = sp.GetArrayElementAtIndex(si++);
                s.FindPropertyRelative("name").stringValue = kv.Key;
                var opts = s.FindPropertyRelative("options"); opts.ClearArray();
                for (int k = 0; k < kv.Value.Count; k++)
                {
                    opts.InsertArrayElementAtIndex(k);
                    var e = opts.GetArrayElementAtIndex(k);
                    e.FindPropertyRelative("label").stringValue = kv.Value[k].label;
                    var ps = e.FindPropertyRelative("parts"); ps.ClearArray();
                    for (int j = 0; j < kv.Value[k].parts.Length; j++) { ps.InsertArrayElementAtIndex(j); ps.GetArrayElementAtIndex(j).objectReferenceValue = kv.Value[k].parts[j]; }
                    var cs = e.FindPropertyRelative("colours"); cs.ClearArray();
                    var cols = kv.Key == "Hair" ? hairTints : kv.Key == "Shoes" ? shoeTints : kv.Key == "Hat" ? new[] { new Color(0.5f, 0.4f, 0.3f) } : tints;
                    if (kv.Value[k].parts.Length == 0) cols = new Color[0];
                    for (int j = 0; j < cols.Length; j++) { cs.InsertArrayElementAtIndex(j); cs.GetArrayElementAtIndex(j).colorValue = cols[j]; }
                }
            }
            so.ApplyModifiedPropertiesWithoutUndo();
            string prefabPath = $"{folder}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);

            // 4. Skin under clothes.
            string cover = bodyName != null ? BodyCoverBaker.Bake(prefabPath, bodyName) : "no body found";

            // 5. City bot variant.
            var botRoot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(female
                ? "Assets/BubbleFeature/Prefabs/Bots/CityFemaleBot.prefab" : "Assets/BubbleFeature/Prefabs/Bots/CityBot.prefab");
            var wander = botRoot.AddComponent<BubbleFeature.Bots.BotWanderer>();
            var tw = template != null ? template.GetComponent<BubbleFeature.Bots.BotWanderer>() : null;
            if (tw != null) EditorUtility.CopySerialized(tw, wander);
            string botPath = $"{folder}/{name}_Bot.prefab";
            PrefabUtility.SaveAsPrefabAsset(botRoot, botPath);
            Object.DestroyImmediate(botRoot);
            AssetDatabase.SaveAssets();

            return $"{name}: {(female ? "female" : "male")}, outfits {slots["Outfit"].Count}, hair {slots["Hair"].Count}, shoes {slots["Shoes"].Count}, hats {slots["Hat"].Count - 1}" +
                   $"{(repaired ? ", repaired text FBX" : "")}; {cover}; prefabs {prefabPath} + _Bot";
        }

        static string Kind(string part)
        {
            string p = part.ToLowerInvariant();
            if (BodyNames.Any(p.Contains)) return "body";
            if (p.StartsWith("teeth") || p.StartsWith("tongue")) return "hide";
            if (p.StartsWith("eyebrow") || p.StartsWith("eyelash") || p.Contains("-poly")) return "face";
            if (p.StartsWith("shoes") || p.Contains("boot") || p.Contains("sneaker")) return "shoes";
            if (HatNames.Any(p.StartsWith)) return "hat";
            if (HairNames.Any(p.StartsWith)) return "hair";
            return "clothes";
        }

        static string Label(string part)
        {
            string s = Regex.Replace(part, "^(female_|male_)", "");
            s = s.Replace("suit", "").Replace("_", " ");
            s = Regex.Replace(s, "0*1$", "");                 // first variant: no number
            s = Regex.Replace(s, "0*(\\d+)$", " $1");          // others: "Casual 2"
            s = Regex.Replace(s.Trim(), " +", " ");
            return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : part;
        }

        // Texture file names this FBX refers to (works for binary and text FBX).
        static List<string> TextureNames(string fbxPath)
        {
            string raw = System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(File.ReadAllBytes(fbxPath));
            return Regex.Matches(raw, @"[\w\-]+\.(png|jpg|jpeg|tga)", RegexOptions.IgnoreCase)
                        .Cast<Match>().Select(m => m.Value).Where(f => !f.Contains("_normal") && !f.Contains("_displacement") && !f.Contains("_ao") && !f.Contains("_specular"))
                        .Distinct().ToList();
        }

        static string FindTexture(string part, string kind, List<string> textures, HashSet<string> used)
        {
            string pick = textures.FirstOrDefault(t => t.StartsWith(part + "_diffuse") || Path.GetFileNameWithoutExtension(t) == part)
                       ?? textures.FirstOrDefault(t => t.StartsWith(part.Split('_')[0]) && kind != "body" && kind != "face");
            if (pick == null && part.StartsWith("fedora")) pick = textures.FirstOrDefault(t => t.StartsWith("fedora"));
            if (pick == null && kind == "body") pick = textures.FirstOrDefault(t => t.Contains("skinned") || t.Contains("skin"));
            if (pick == null && part.Contains("-poly")) pick = textures.FirstOrDefault(t => t.Contains("_eye") && !t.StartsWith("eyebrow") && !t.StartsWith("eyelash"));
            if (pick != null) used.Add(pick);
            return pick;
        }

        static Texture2D LoadTexture(string file, string fbxPath)
        {
            string dir = Path.GetDirectoryName(fbxPath).Replace('\\', '/');
            foreach (string sub in new[] { "/textures/", "/Textures/", "/" })
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + sub + file);
                if (t != null) return t;
            }
            foreach (string guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(file) + " t:Texture2D"))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(p) == file) return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
            }
            return null;
        }

        // MakeHuman 1.2 writes text FBX property names as Python bytes ("b'Lcl Translation'"); Unity can't read those.
        public static bool RepairTextFbx(string path)
        {
            byte[] head = new byte[20];
            using (var f = File.OpenRead(path)) f.Read(head, 0, head.Length);
            if (System.Text.Encoding.ASCII.GetString(head).StartsWith("Kaydara FBX Binary")) return false;
            var enc = System.Text.Encoding.GetEncoding("ISO-8859-1");
            string s = enc.GetString(File.ReadAllBytes(path));
            string fixedText = Regex.Replace(s, "\"b'([^']*)'\"", "\"$1\"");
            if (fixedText == s) return false;
            File.WriteAllBytes(path, enc.GetBytes(fixedText));
            return true;
        }
    }
}
