using System.Collections.Generic;
using System.IO;
using BubbleFeature.Audio;
using BubbleFeature.Gifts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// One-click demo polish for CityScene (each step re-runnable):
    ///  Audio (sound effects + area ambience), arrival titles, demo operator, comfort vignette,
    ///  warm afternoon light + haze, Quest render settings, extra gifts from "Potential Gifts".
    /// </summary>
    public static class CityDemoSetup
    {
        const string AudioDir = "Assets/CityFeature/Audio/";
        const string PG = "Assets/BubbleFeature/Potential Gifts/";
        const string GiftModels = "Assets/BubbleFeature/Gifts/Models/";

        [MenuItem("B&D City/Demo/Set up everything")]
        public static string All()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Audio());
            sb.AppendLine(Titles());
            sb.AppendLine(Operator());
            sb.AppendLine(Vignette());
            sb.AppendLine(Light());
            sb.AppendLine(QuestSettings());
            sb.AppendLine(Gifts());
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            return sb.ToString();
        }

        static T Find<T>() where T : Object => Object.FindAnyObjectByType<T>(FindObjectsInactive.Include);

        static GameObject Root(string name)
        {
            var go = GameObject.Find(name);
            if (go == null) { go = new GameObject(name); Undo.RegisterCreatedObjectUndo(go, name); }
            return go;
        }

        static T Comp<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : go.AddComponent<T>();
        }

        // ---------------- Audio ----------------
        public static string Audio()
        {
            AudioClip Clip(string n, bool ambience)
            {
                string path = AudioDir + n + ".wav";
                var imp = (AudioImporter)AssetImporter.GetAtPath(path);
                imp.forceToMono = true;
                imp.loadInBackground = ambience;
                var s = imp.defaultSampleSettings;
                s.loadType = ambience ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = ambience ? 0.45f : 0.7f;
                s.preloadAudioData = true;
                imp.defaultSampleSettings = s;
                imp.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            var go = Root("Demo Audio");
            var da = Comp<DemoAudio>(go);
            var so = new SerializedObject(da);
            so.FindProperty("pop").objectReferenceValue = Clip("sfx_bubble_pop", false);
            so.FindProperty("sparkle").objectReferenceValue = Clip("sfx_gift_sparkle", false);
            so.FindProperty("click").objectReferenceValue = Clip("sfx_ui_click", false);
            so.FindProperty("menuOpen").objectReferenceValue = Clip("sfx_menu_open", false);
            so.FindProperty("chime").objectReferenceValue = Clip("sfx_arrival_chime", false);
            so.ApplyModifiedProperties();

            var amb = Comp<CityAmbience>(Root("City Ambience"));
            var so2 = new SerializedObject(amb);
            so2.FindProperty("teleporter").objectReferenceValue = Find<NeighbourhoodTeleporter>();
            var arr = so2.FindProperty("areas");
            var rows = new (string n, string clip, float v)[]
            {
                ("Wijnhaven 107", "amb_wijnhaven_quay", 0.35f), ("Lijnbaan", "amb_lijnbaan_street", 0.3f),
                ("Blaak", "amb_blaak_square", 0.32f), ("Stadhuis", "amb_lijnbaan_street", 0.22f),
            };
            arr.arraySize = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                var e = arr.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("neighbourhood").stringValue = rows[i].n;
                e.FindPropertyRelative("loop").objectReferenceValue = Clip(rows[i].clip, true);
                e.FindPropertyRelative("volume").floatValue = rows[i].v;
            }
            so2.ApplyModifiedProperties();
            return "Audio: 5 effects + 3 ambience loops wired";
        }

        // ---------------- Arrival titles ----------------
        public static string Titles()
        {
            var t = Comp<ArrivalTitle>(Root("Arrival Title"));
            var so = new SerializedObject(t);
            so.FindProperty("teleporter").objectReferenceValue = Find<NeighbourhoodTeleporter>();
            var arr = so.FindProperty("subtitles");
            var rows = new (string n, string s)[]
            {
                ("Wijnhaven 107", "Hogeschool Rotterdam"), ("Lijnbaan", "Shopping street"),
                ("Blaak", "Markthal · Cube Houses"), ("Stadhuis", "Rotterdam City Hall"),
            };
            arr.arraySize = rows.Length;
            for (int i = 0; i < rows.Length; i++)
            {
                arr.GetArrayElementAtIndex(i).FindPropertyRelative("neighbourhood").stringValue = rows[i].n;
                arr.GetArrayElementAtIndex(i).FindPropertyRelative("subtitle").stringValue = rows[i].s;
            }
            so.ApplyModifiedProperties();
            return "Titles: 4 areas";
        }

        // ---------------- Demo operator ----------------
        public static string Operator()
        {
            var op = Comp<DemoOperator>(Root("Demo Operator"));
            var so = new SerializedObject(op);
            so.FindProperty("teleporter").objectReferenceValue = Find<NeighbourhoodTeleporter>();
            so.FindProperty("tutorial").objectReferenceValue = Find<TutorialGuide>();
            so.FindProperty("giftBubbles").objectReferenceValue = Find<BubbleFeature.Bubbles.GiftBubbleSpawner>();
            so.FindProperty("menu").objectReferenceValue = Find<BubbleFeature.Menu.GiftMenu>();
            so.FindProperty("title").objectReferenceValue = Find<ArrivalTitle>();
            so.ApplyModifiedProperties();
            return "Operator: F1 / both grips+triggers 2 s = reset";
        }

        // ---------------- Comfort vignette ----------------
        public static string Vignette()
        {
            var cam = Camera.main;
            var existing = cam.GetComponentInChildren<UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController>(true);
            GameObject v;
            if (existing != null) v = existing.gameObject;
            else
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Samples/XR Interaction Toolkit/3.6.1/Starter Assets/TunnelingVignette/TunnelingVignette.prefab");
                v = (GameObject)PrefabUtility.InstantiatePrefab(prefab, cam.transform);
                Undo.RegisterCreatedObjectUndo(v, "Vignette");
            }
            var ctrl = v.GetComponent<UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.TunnelingVignetteController>();
            var rig = GameObject.Find("XR Origin (XR Rig)");
            var list = new List<UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.LocomotionVignetteProvider>();
            foreach (var p in rig.GetComponentsInChildren<UnityEngine.XR.Interaction.Toolkit.Locomotion.LocomotionProvider>(true))
            {
                string n = p.GetType().Name;
                if (n.Contains("Grab") || n.Contains("Climb") || n.Contains("Snap") || n.Contains("Gravity") || n.Contains("Jump")) continue;   // instant / always-on providers: no vignette
                list.Add(new UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort.LocomotionVignetteProvider { locomotionProvider = p, enabled = true });
            }
            ctrl.locomotionVignetteProviders = list;
            var param = ctrl.defaultParameters;
            param.apertureSize = 0.75f; param.featheringEffect = 0.25f; param.easeInTime = 0.25f; param.easeOutTime = 0.35f;
            param.vignetteColor = new Color(0.08f, 0.1f, 0.16f, 1f);
            ctrl.defaultParameters = param;
            EditorUtility.SetDirty(ctrl);
            return $"Vignette: on {list.Count} providers ({string.Join(", ", list.ConvertAll(l => l.locomotionProvider.GetType().Name))})";
        }

        // ---------------- Light ----------------
        public static string Light()
        {
            var sun = GameObject.Find("Directional Light").GetComponent<UnityEngine.Light>();
            Undo.RecordObject(sun, "Sun"); Undo.RecordObject(sun.transform, "Sun");
            sun.transform.rotation = Quaternion.Euler(33f, 35f, 0f);   // afternoon sun from the south-west
            sun.color = new Color(1f, 0.87f, 0.70f);
            sun.intensity = 1.25f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 220f;
            RenderSettings.fogEndDistance = 1300f;
            RenderSettings.fogColor = new Color(0.80f, 0.84f, 0.88f);
            RenderSettings.ambientIntensity = 1.05f;
            return "Light: warm afternoon sun, linear haze 220-1300 m";
        }

        // ---------------- Quest render settings ----------------
        public static string QuestSettings()
        {
            var mob = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>("Assets/Settings/Mobile_RPAsset.asset");
            mob.msaaSampleCount = 4;
            mob.supportsHDR = false;
            EditorUtility.SetDirty(mob);
            AssetDatabase.SaveAssets();
            return $"Quest (Mobile) URP: MSAA {mob.msaaSampleCount}x, HDR {mob.supportsHDR}, render scale {mob.renderScale}";
        }

        // ---------------- Gifts ----------------
        public static string Gifts()
        {
            Directory.CreateDirectory(GiftModels);
            var cap = MakeGiftPrefab("NY_Cap", PG + "newyork cap.fbx", PG + "New York Cap_1.png");
            var top = MakeGiftPrefab("OffShoulder_Top", PG + "The low poly models only/OffTheShoulderShirt.fbx",
                PG + "off-the-shoulder-shirt-ngchipv/textures/M_OffTheShoulderShirt_Base_color.png");
            var blouse = MakeGiftPrefab("Blue_Blouse", PG + "[does not work i think] blue-blouse-with-stripes-3d-scanning/source/Blusa_azl low final/Blusa_azl low final.obj",
                PG + "[does not work i think] blue-blouse-with-stripes-3d-scanning/textures/bluss_2.png");

            var gifts = new List<GiftData>
            {
                Data("Gifts/Gift_NY_Cap", "Daan", "A cap for sunny days on the Maas!", GiftItemType.Accessory, cap),
                Data("Gifts/Gift_OffShoulder_Top", "Sanne", "Saw this on the Lijnbaan and thought of you!", GiftItemType.Clothing, top),
                Data("Gifts/Gift_Blue_Blouse", "Noor", "Stripes suit you!", GiftItemType.Clothing, blouse),
            };
            var items = new List<GiftData>
            {
                Data("Gifts/Items/Item_NY_Cap", "You", "", GiftItemType.Accessory, cap),
                Data("Gifts/Items/Item_OffShoulder_Top", "You", "", GiftItemType.Clothing, top),
                Data("Gifts/Items/Item_Blue_Blouse", "You", "", GiftItemType.Clothing, blouse),
            };

            // Bubble spawner: mix the new gifts in after the first (message-only) one.
            var sp = Find<BubbleFeature.Bubbles.GiftBubbleSpawner>();
            var so = new SerializedObject(sp);
            var list = so.FindProperty("gifts");
            var current = new List<Object>();
            for (int i = 0; i < list.arraySize; i++) current.Add(list.GetArrayElementAtIndex(i).objectReferenceValue);
            var ordered = new List<Object>();
            if (current.Count > 0) ordered.Add(current[0]);
            int g = 0;
            for (int i = 1; i < current.Count; i++)
            {
                if (g < gifts.Count && !current.Contains(gifts[g])) ordered.Add(gifts[g]);
                g++;
                ordered.Add(current[i]);
            }
            for (; g < gifts.Count; g++) if (!ordered.Contains(gifts[g])) ordered.Add(gifts[g]);
            list.arraySize = ordered.Count;
            for (int i = 0; i < ordered.Count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = ordered[i];
            so.ApplyModifiedProperties();

            // Menu catalogue.
            var menu = Find<BubbleFeature.Menu.GiftMenu>();
            var mso = new SerializedObject(menu);
            AddUnique(mso.FindProperty("accessoryOptions"), items[0]);
            AddUnique(mso.FindProperty("clothingOptions"), items[1]);
            AddUnique(mso.FindProperty("clothingOptions"), items[2]);
            mso.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return $"Gifts: cap, off-the-shoulder top, blue blouse added (spawner now {ordered.Count} gifts)";
        }

        static void AddUnique(SerializedProperty arr, Object o)
        {
            for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == o) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = o;
        }

        static GiftData Data(string rel, string sender, string message, GiftItemType type, GameObject prefab)
        {
            string path = $"Assets/BubbleFeature/{rel}.asset";
            var d = AssetDatabase.LoadAssetAtPath<GiftData>(path);
            if (d == null) { d = ScriptableObject.CreateInstance<GiftData>(); AssetDatabase.CreateAsset(d, path); }
            d.sender = sender; d.message = message; d.itemType = type; d.itemPrefab = prefab; d.itemColor = Color.white;
            EditorUtility.SetDirty(d);
            return d;
        }

        // A Quest-friendly copy: one URP Lit material with only the colour texture (max 1024 px).
        static GameObject MakeGiftPrefab(string name, string modelPath, string texPath)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(texPath);
            ti.maxTextureSize = 1024; ti.sRGBTexture = true; ti.mipmapEnabled = true;
            var android = ti.GetPlatformTextureSettings("Android");
            android.overridden = true; android.maxTextureSize = 1024; android.format = TextureImporterFormat.ASTC_6x6;
            ti.SetPlatformTextureSettings(android);
            ti.SaveAndReimport();
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            string matPath = $"{GiftModels}M_Gift_{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { mat = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(mat, matPath); }
            mat.SetTexture("_BaseMap", tex); mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", 0.2f); mat.SetFloat("_Cull", 0f); mat.doubleSidedGI = true;   // cloth: both sides visible
            EditorUtility.SetDirty(mat);

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = "Gift_" + name;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
            var prefab = PrefabUtility.SaveAsPrefabAsset(inst, $"{GiftModels}Gift_{name}.prefab");
            Object.DestroyImmediate(inst);
            return prefab;
        }
    }
}
