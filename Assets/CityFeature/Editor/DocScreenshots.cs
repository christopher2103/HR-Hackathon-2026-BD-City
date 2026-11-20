using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BubbleFeature.Bots;
using BubbleFeature.Bubbles;
using BubbleFeature.Gifts;
using BubbleFeature.Menu;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CityFeature.EditorTools
{
    /// <summary>
    /// Takes the screenshots for the documentation (docs/images): enters Play mode in CityScene and walks through
    /// the four areas, a gift bubble flying in / popping / revealing, ambient bubbles, sending a gift to a bot,
    /// the menu, Lotte and Bubble Sky. Renders with its own camera (no headset needed). Log: Temp/doc_shots_log.txt.
    /// Run: B&D City > Docs > Take documentation screenshots (or create Temp/doc_shots_go and focus Unity).
    /// </summary>
    [InitializeOnLoad]
    public static class DocScreenshots
    {
        private const string Trigger = "Temp/doc_shots_go";
        private const string LogPath = "Temp/doc_shots_log.txt";
        private const string OutDir = "docs/images";
        private const string ScenePath = "Assets/CityFeature/Scenes/CityScene.unity";

        private static readonly List<(float delay, string name, Action act)> steps = new List<(float, string, Action)>();
        private static int index;
        private static double nextTime;
        private static bool waitingForPlay;
        private static string mode = "all";   // "all" or "bubbles" (retake of the bubble shots only)

        static DocScreenshots() { EditorApplication.delayCall += CheckTrigger; }

        private static void CheckTrigger()
        {
            if (!File.Exists(Trigger)) return;
            string m = File.ReadAllText(Trigger).Trim();
            mode = m == "bubbles" || m == "pop" ? m : "all";
            File.Delete(Trigger);
            Run();
        }

        [MenuItem("B&D City/Docs/Take documentation screenshots")]
        public static void Run()
        {
            File.WriteAllText(LogPath, $"start {DateTime.Now:HH:mm:ss}\n");
            Directory.CreateDirectory(OutDir);
            if (EditorApplication.isPlaying) { Begin(); return; }
            if (EditorSceneManager.GetActiveScene().path != ScenePath)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) { Log("cancelled"); return; }
                EditorSceneManager.OpenScene(ScenePath);
            }
            waitingForPlay = true;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.EnteredPlayMode && waitingForPlay) { waitingForPlay = false; Begin(); }
            if (s == PlayModeStateChange.ExitingPlayMode) EditorApplication.update -= Tick;
        }

        private static void Log(string s) => File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {s}\n");

        // ---------------- The shot list ----------------

        private static NeighbourhoodTeleporter Tele => UnityEngine.Object.FindAnyObjectByType<NeighbourhoodTeleporter>();
        private static Vector3 lastPop;

        private static void Begin()
        {
            steps.Clear();
            index = 0;

            if (mode == "pop") { PopSequence(); StartSteps(); return; }
            bool all = mode == "all";
            if (!all)
            {
                Add(8f, "go wijnhaven", () => Tele.GoTo(0));
                Add(9f, "area-wijnhaven-107", () => Shot("area-wijnhaven-107", Eye() - Fwd() * 2f + Vector3.up * 0.4f, Eye() + Fwd() * 10f + Vector3.up * 1.6f, 72f));
            }
            if (all) Add(8f, "lotte", () =>
            {
                var t = UnityEngine.Object.FindAnyObjectByType<TutorialGuide>();
                if (t == null) { Log("no TutorialGuide"); return; }
                if (!t.IsRunning) t.Play();
            });
            if (all) Add(9f, "lotte-shot", () =>
            {
                var t = UnityEngine.Object.FindAnyObjectByType<TutorialGuide>();
                var g = Field<Transform>(t, "guide");
                if (g == null) { Log("no guide transform"); return; }
                Vector3 head = g.position + Vector3.up * 1.45f;
                Vector3 dir = Flat(Eye() - g.position);
                if (dir == Vector3.zero) dir = g.forward;
                Vector3 pos = g.position + dir * 2.3f + Vector3.up * 1.55f + Vector3.Cross(Vector3.up, dir) * 0.6f;
                Shot("lotte-tutorial", pos, head, 55f);
                t.Skip();
            });

            string[] areas = { "area-wijnhaven-107", "area-lijnbaan", "area-blaak", "area-stadhuis" };
            for (int i = 0; i < (all ? areas.Length : 0); i++)
            {
                int ci = i;
                Add(1f, "go " + ci, () => Tele.GoTo(ci));
                Add(9f, areas[ci], () => Shot(areas[ci], Eye(), Eye() + Fwd() * 10f + Vector3.up * 1.2f, 72f));
            }

            // Personal gift bubble in Blaak.
            Add(1f, "go blaak", () => Tele.GoTo(2));
            Add(9f, "spawn", () => UnityEngine.Object.FindAnyObjectByType<GiftBubbleSpawner>().Spawn());
            Add(1.6f, "bubble-flying", () =>
            {
                var b = Personal();
                if (b == null) { Log("no bubble"); return; }
                Vector3 pos = Eye() - Fwd() * 0.8f + Vector3.up * 0.1f + Right() * 0.3f;
                Shot("bubble-flying", pos, b.transform.position, 34f);
            });
            Add(5f, "bubble-arrives", () =>
            {
                var b = Personal();
                if (b == null) { Log("no bubble"); return; }
                Vector3 to = Flat(b.transform.position - Eye());
                Vector3 pos = Eye() - to.normalized * 0.4f + Vector3.up * 0.05f;
                Shot("bubble-arrives", pos, b.transform.position + Vector3.up * 0.1f, 58f);
                lastPop = b.transform.position;
                b.Pop();
            });
            Add(0.3f, "bubble-pop", () =>
            {
                Vector3 to = Flat(lastPop - Eye());
                Shot("bubble-pop", lastPop - to.normalized * 1.0f + Vector3.up * 0.05f, lastPop + Vector3.up * 0.08f, 50f);
            });
            Add(1.9f, "gift-reveal", () =>
            {
                var r = Newest<GiftReveal>();
                Vector3 target = r != null ? r.transform.position : lastPop;
                Vector3 to = Flat(target - Eye());
                Shot("gift-reveal", target - to.normalized * 1.15f + Vector3.up * 0.05f, target - Vector3.up * 0.12f, 55f);
            });

            if (!all) { Add(1f, "done", () => { Log("DONE"); EditorApplication.ExitPlaymode(); }); StartSteps(); return; }

            // Ambient bubbles flying to the bots.
            Add(2f, "ambient-bubbles", () =>
            {
                var bots = UnityEngine.Object.FindObjectsByType<BotWanderer>(FindObjectsSortMode.None);
                var ambient = UnityEngine.Object.FindObjectsByType<AmbientBubbleMover>(FindObjectsSortMode.None);
                Vector3 centre = Eye() + Fwd() * 6f;
                var pts = bots.Select(b => b.transform.position).Concat(ambient.Select(a => a.transform.position)).ToList();
                if (pts.Count > 0) centre = pts.Aggregate(Vector3.zero, (a, p) => a + p) / pts.Count;
                Vector3 from = Flat(Eye() - centre).normalized;
                if (from == Vector3.zero) from = -Fwd();
                Shot("ambient-bubbles", centre + from * 11f + Vector3.up * 3.2f, centre + Vector3.up * 1.5f, 68f);
                Log($"bots {bots.Length}, ambient {ambient.Length}");
            });

            // Send a gift to a bot.
            Transform bot = null;
            Add(0.5f, "send", () =>
            {
                bot = UnityEngine.Object.FindObjectsByType<BotWanderer>(FindObjectsSortMode.None)
                    .OrderBy(b => (b.transform.position - Eye()).sqrMagnitude).Select(b => b.transform).FirstOrDefault();
                var gift = AssetDatabase.LoadAssetAtPath<GiftData>("Assets/BubbleFeature/Gifts/Gift_Heart_Necklace.asset");
                var sender = UnityEngine.Object.FindAnyObjectByType<GiftSender>();
                if (bot == null || gift == null || sender == null) { Log($"send missing bot={bot} gift={gift} sender={sender}"); return; }
                sender.Send(gift, bot, Eye() + Fwd() * 0.6f - Vector3.up * 0.3f);
            });
            Add(0.8f, "gift-sent", () =>
            {
                if (bot == null) return;
                Vector3 mid = Vector3.Lerp(Eye(), bot.position + Vector3.up * 1.6f, 0.5f);
                Shot("gift-sent", Eye() - Fwd() * 1.5f + Vector3.up * 0.4f + Right() * 0.6f, mid, 65f);
            });
            Add(3.2f, "gift-to-bot", () =>
            {
                if (bot == null) return;
                Vector3 head = bot.position + Vector3.up * 1.9f;
                Vector3 to = Flat(head - Eye());
                Shot("gift-to-bot", head - to.normalized * 3.2f + Vector3.up * 0.1f, head + Vector3.up * 0.2f, 55f);
            });

            // Menu.
            GiftMenu menu = null;
            Add(4f, "menu", () => { menu = UnityEngine.Object.FindAnyObjectByType<GiftMenu>(); menu?.Open(); });
            Add(1.2f, "menu-shot", () => Shot("menu", Eye() - Fwd() * 0.2f, Eye() + Fwd() * 2f - Vector3.up * 0.25f, 70f));
            Add(0.2f, "recipients", () => Invoke(menu, "ShowRecipients"));
            Add(1f, "menu-send-gift", () => Shot("menu-send-gift", Eye() - Fwd() * 0.2f, Eye() + Fwd() * 2f - Vector3.up * 0.25f, 70f));
            Add(0.2f, "outfit", () => { Invoke(menu, "ShowMain"); Invoke(menu, "ShowOutfit"); });
            Add(1.5f, "menu-outfit", () => Shot("menu-outfit", Eye() - Fwd() * 0.4f, Eye() + Fwd() * 2f + Right() * 0.4f - Vector3.up * 0.3f, 80f));
            Add(0.2f, "close", () => menu?.Close());

            // Bubble Sky.
            BubbleGame game = null;
            Add(1f, "game", () => { game = UnityEngine.Object.FindAnyObjectByType<BubbleGame>(); game?.Enter(); });
            Add(6f, "game-start", () => Invoke(game, "StartRound"));
            Add(9f, "bubble-sky", () => Shot("bubble-sky", Eye(), Eye() + Fwd() * 5f, 80f));
            Add(0.2f, "leave", () => game?.Leave());

            Add(6f, "done", () => { Log("DONE"); EditorApplication.ExitPlaymode(); });
            StartSteps();
        }

        private static void StartSteps()
        {
            nextTime = EditorApplication.timeSinceStartup + steps[0].delay;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Log($"begin, {steps.Count} steps");
        }

        // A close-up of one pop, frame by frame, from a fixed camera; then two more reveals (the scanned hats).
        private static Vector3 camPos, camLook;

        private static void PopSequence()
        {
            Add(8f, "go blaak", () => Tele.GoTo(2));
            PopRun(9f, "Gift_Watch_Ring", true, "pop");
            PopRun(3f, "Gift_Fishermans_Hat", false, "reveal-fishermans-hat");
            PopRun(3f, "Gift_NY_Cap", false, "reveal-ny-cap");
            Add(3f, "done", () => { Log("DONE"); EditorApplication.ExitPlaymode(); });
        }

        private static void PopRun(float firstDelay, string giftName, bool sequence, string prefix)
        {
            Add(firstDelay, "spawn " + giftName, () =>
            {
                UnityEngine.Object.FindAnyObjectByType<GiftBubbleSpawner>().Spawn();
                var gift = AssetDatabase.LoadAssetAtPath<GiftData>($"Assets/BubbleFeature/Gifts/{giftName}.asset");
                var b = Personal();
                if (b != null && gift != null) b.SetGift(gift); else Log($"spawn: bubble={b} gift={gift}");
            });
            Add(6.5f, prefix + " pop", () =>
            {
                var b = Personal();
                if (b == null) { Log("no bubble to pop"); return; }
                Vector3 bp = b.transform.position;
                Vector3 to = Flat(bp - Eye()).normalized;
                if (to == Vector3.zero) to = Fwd();
                camPos = bp - to * 1.15f + Right() * 0.15f + Vector3.up * 0.05f;
                camLook = bp + Vector3.up * 0.1f;
                if (sequence) Shot(prefix + "-1-bubble", camPos, camLook, 58f);
                b.Pop();
            });
            if (sequence)
            {
                Add(0.07f, prefix + "-2-swell", () => Shot(prefix + "-2-swell", camPos, camLook, 58f));
                Add(0.25f, prefix + "-3-burst", () => Shot(prefix + "-3-burst", camPos, camLook, 58f));
                Add(0.65f, prefix + "-4-gather", () => Shot(prefix + "-4-gather", camPos, camLook, 58f));
                Add(0.40f, prefix + "-5-formed", () => Shot(prefix + "-5-formed", camPos, camLook, 58f));
                Add(0.90f, prefix + "-6-reveal", () => Shot(prefix + "-6-reveal", camPos, camLook - Vector3.up * 0.05f, 58f));
            }
            else
            {
                Add(2.3f, prefix, () => Shot(prefix, camPos, camLook - Vector3.up * 0.05f, 58f));
            }
        }

        private static void Add(float delay, string name, Action act) => steps.Add((delay, name, act));

        private static void Tick()
        {
            if (!EditorApplication.isPlaying) { EditorApplication.update -= Tick; return; }
            if (EditorApplication.timeSinceStartup < nextTime || index >= steps.Count) return;
            var s = steps[index++];
            try { s.act(); Log("ok " + s.name); }
            catch (Exception e) { Log($"FAIL {s.name}: {e.GetType().Name} {e.Message}"); }
            if (index < steps.Count) nextTime = EditorApplication.timeSinceStartup + steps[index].delay;
            else EditorApplication.update -= Tick;
        }

        // ---------------- Helpers ----------------

        private static Camera Main => Camera.main;

        private static Vector3 Eye()
        {
            var origin = UnityEngine.Object.FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
            Vector3 c = Main != null ? Main.transform.position : Vector3.zero;
            float floor = origin != null ? origin.transform.position.y : c.y - 1.65f;
            return new Vector3(c.x, floor + 1.65f, c.z);
        }

        private static Vector3 Fwd()
        {
            Vector3 f = Main != null ? Flat(Main.transform.forward) : Vector3.forward;
            return f == Vector3.zero ? Vector3.forward : f.normalized;
        }

        private static Vector3 Right() => Vector3.Cross(Vector3.up, Fwd());
        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        private static GiftBubble Personal() =>
            UnityEngine.Object.FindObjectsByType<GiftBubble>(FindObjectsSortMode.None)
                .Where(b => !b.IsPopped && b.GetComponent<GiftBubbleFlight>() != null)
                .OrderBy(b => (b.transform.position - Eye()).sqrMagnitude).FirstOrDefault();

        private static T Newest<T>() where T : Component =>
            UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.InstanceID).LastOrDefault();

        private static T Field<T>(object o, string name) where T : class =>
            o?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(o) as T;

        private static void Invoke(object o, string method)
        {
            if (o == null) { Log("invoke on null: " + method); return; }
            var m = o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null) { Log("no method " + method); return; }
            m.Invoke(o, null);
        }

        private static void Shot(string name, Vector3 pos, Vector3 lookAt, float fov)
        {
            const int w = 1920, h = 1080;
            var go = new GameObject("DocCam");
            var cam = go.AddComponent<Camera>();
            if (Main != null) cam.CopyFrom(Main);
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, 3000f);
            var data = cam.GetUniversalAdditionalCameraData();
            var mainData = Main != null ? Main.GetUniversalAdditionalCameraData() : null;
            data.renderPostProcessing = mainData == null || mainData.renderPostProcessing;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation((lookAt - pos).normalized, Vector3.up);

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 4 };
            cam.targetTexture = rt;
            var req = new RenderPipeline.StandardRequest { destination = rt };
            if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
            else cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(OutDir, name + ".png"), tex.EncodeToPNG());

            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.Destroy(go);
            Log($"shot {name}");
        }
    }
}
