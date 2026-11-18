using System;
using System.Collections;
using System.Collections.Generic;
using BubbleFeature.Audio;
using BubbleFeature.Bubbles;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace CityFeature
{
    /// <summary>
    /// "Bubble Sky" mini game (menu > Bubble game!): you are taken to a floating platform high above Blaak and
    /// bubbles come at you from every side (some fly towards you, some drift away). Pop as many as you can in 90 s.
    /// Quick pops in a row build a combo multiplier, rare golden bubbles are worth 5. It speeds up as time runs out.
    /// The top 10 (4-letter initials) is saved on the device. Walking and teleporting are off on the platform
    /// (snap turn stays on). "Back to city" (or any Go to...) returns you to where you were.
    /// Operator: F6 clears the scoreboard.
    /// </summary>
    public class BubbleGame : MonoBehaviour
    {
        // ---------------- Settings ----------------
        [Header("Scene")]
        [SerializeField] private NeighbourhoodTeleporter teleporter;
        [SerializeField] private LoadingScreen loadingScreen;
        [SerializeField] private TutorialGuide tutorial;

        [Header("Platform")]
        [SerializeField] private string area = "Blaak";
        [Tooltip("Top of the deck (where your feet are), in the city's coordinates.")]
        [SerializeField] private Vector3 platformPosition = new Vector3(320f, 60f, 240f);
        [Tooltip("The player looks this way when arriving (degrees, 0 = north): towards the Markthal and Cube Houses.")]
        [SerializeField] private float arriveYaw = 0f;
        [SerializeField] private float platformRadius = 3.2f;
        [SerializeField] private Material detailMaterial;   // BDCity/FlatDetail (vertex colours)
        [SerializeField] private Material glassMaterial;

        [Header("Bubbles")]
        [SerializeField] private GiftBubble bubblePrefab;
        [SerializeField] private float roundSeconds = 90f;
        [SerializeField] private Vector2 spawnInterval = new Vector2(0.85f, 0.3f);   // start, end of the round
        [SerializeField] private Vector2 bubbleSpeed = new Vector2(1.3f, 3.2f);
        [SerializeField] private int maxBubbles = 18;
        [Range(0f, 1f)] [SerializeField] private float goldenChance = 0.07f;
        [SerializeField] private float comboWindow = 1.6f;

        [Header("UI")]
        [SerializeField] private Sprite panelSprite;
        [SerializeField] private Sprite pillSprite;
        [SerializeField] private TMP_FontAsset font;

        public bool IsActive { get; private set; }

        private const string ScoresKey = "BDCity.BubbleGame.Scores";
        private const string InitialsKey = "BDCity.BubbleGame.LastInitials";
        private static readonly Color Orange = new Color(0.95f, 0.55f, 0.25f), Navy = new Color(0.10f, 0.13f, 0.24f);
        private static readonly Color Gold = new Color(1f, 0.82f, 0.25f);

        private enum State { Off, Lobby, Countdown, Playing, Initials, Results }
        private State state = State.Off;

        private XROrigin origin;
        private Camera cam;
        private Transform platform;
        private TextMeshPro boardText, hudText, bigText;
        private GameObject panel;
        private int returnIndex;
        private BubbleFeature.Bots.BotSpawner bots;
        private bool leaving;
        private readonly List<LocomotionProvider> pausedProviders = new List<LocomotionProvider>();

        // Round
        private float elapsed, nextSpawn, lastPopTime = -10f;
        private int score, combo, popped, goldenPopped;
        private class Live { public GiftBubble b; public Vector3 vel; public float age, life; public bool golden; public Vector3 scale; public bool leaving; }
        private readonly List<Live> live = new List<Live>();
        private readonly List<TextMeshPro> popups = new List<TextMeshPro>();
        private readonly List<float> popupAge = new List<float>();

        // Scores
        [Serializable] private class Entry { public string initials; public int score; }
        [Serializable] private class Board { public List<Entry> entries = new List<Entry>(); }
        private Board board;
        private int lastRank = -1;

        // ---------------- Unity ----------------

        private void Awake()
        {
            origin = FindAnyObjectByType<XROrigin>();
            board = LoadBoard();
        }

        private void OnEnable() { if (teleporter != null) teleporter.Arrived += OnArrived; }
        private void OnDisable() { if (teleporter != null) teleporter.Arrived -= OnArrived; }

        // Someone travelled away (menu Go to..., operator reset): clean up quietly.
        private void OnArrived(string place)
        {
            if (IsActive && !leaving) Cleanup();
        }

        private void Update()
        {
            Keyboard k = Keyboard.current;
            if (k != null && k.f6Key.wasPressedThisFrame) ClearScores();
            if (!IsActive) return;
            if (cam == null) cam = Camera.main;

            if (state == State.Playing) TickRound();
            MoveBubbles();
            TickPopups();
            FollowView(hudText != null ? hudText.transform : null, 2.4f, 0.42f, 3f);
        }

        // ---------------- Enter / leave ----------------

        public void Enter()
        {
            if (IsActive || teleporter == null) return;
            StopAllCoroutines();
            StartCoroutine(EnterRoutine());
        }

        public void Leave()
        {
            if (!IsActive) return;
            leaving = true;
            Cleanup();
            teleporter.GoTo(Mathf.Max(0, returnIndex));   // fades, lands you back on the street, brings the bots
            leaving = false;
        }

        private IEnumerator EnterRoutine()
        {
            IsActive = true;
            cam = Camera.main;
            if (origin == null) origin = FindAnyObjectByType<XROrigin>();
            returnIndex = teleporter.CurrentIndex;
            if (tutorial != null && tutorial.IsRunning) tutorial.Skip();

            if (loadingScreen != null)
            {
                loadingScreen.Show("Bubble Sky");
                loadingScreen.SetStatus("Going up...");
                yield return loadingScreen.WaitUntilOpaque();
            }
            teleporter.ShowOnly(area);
            foreach (var f in FindObjectsByType<GiftBubbleFlight>(FindObjectsInactive.Exclude)) Destroy(f.gameObject);
            foreach (var r in FindObjectsByType<BubbleFeature.Gifts.GiftReveal>(FindObjectsInactive.Exclude)) Destroy(r.gameObject);
            if (platform == null) BuildPlatform();
            platform.gameObject.SetActive(true);
            UpdateBoard();
            SetLocomotion(false);
            // The bot spawner would "bring bots near you" and put them on a rooftop up here: pause it.
            bots = FindAnyObjectByType<BubbleFeature.Bots.BotSpawner>();
            if (bots != null) bots.enabled = false;
            PlacePlayer();
            yield return new WaitForSeconds(0.3f);
            if (loadingScreen != null) { loadingScreen.Hide(); yield return loadingScreen.WaitUntilHidden(); }
            ShowLobby();
        }

        private void Cleanup()
        {
            StopAllCoroutines();
            foreach (var l in live) if (l.b != null) Destroy(l.b.gameObject);
            live.Clear();
            foreach (var p in popups) if (p != null) p.gameObject.SetActive(false);
            ClosePanel();
            if (hudText != null) hudText.gameObject.SetActive(false);
            if (bigText != null) bigText.gameObject.SetActive(false);
            if (platform != null) platform.gameObject.SetActive(false);
            SetLocomotion(true);
            if (bots != null) bots.enabled = true;
            state = State.Off;
            IsActive = false;
        }

        private void PlacePlayer()
        {
            if (origin == null || cam == null) return;
            var cc = origin.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            Vector3 headOffset = cam.transform.position - origin.transform.position; headOffset.y = 0f;
            origin.transform.position = platformPosition - headOffset;
            Vector3 look = cam.transform.forward; look.y = 0f;
            Vector3 want = Quaternion.Euler(0f, arriveYaw, 0f) * Vector3.forward;
            if (look.sqrMagnitude > 0.001f) origin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(look, want, Vector3.up));
            headOffset = cam.transform.position - origin.transform.position; headOffset.y = 0f;
            origin.transform.position = platformPosition - headOffset;   // centre the head over the platform
            Physics.SyncTransforms();
            if (cc != null) cc.enabled = true;
        }

        // No walking or teleporting off the platform; snap turn stays.
        private void SetLocomotion(bool on)
        {
            if (!on)
            {
                pausedProviders.Clear();
                if (origin == null) return;
                foreach (var p in origin.GetComponentsInChildren<LocomotionProvider>(true))
                {
                    string n = p.GetType().Name;
                    if (!(n.Contains("Move") || n.Contains("Teleport")) || !p.enabled) continue;
                    p.enabled = false;
                    pausedProviders.Add(p);
                }
            }
            else
            {
                foreach (var p in pausedProviders) if (p != null) p.enabled = true;
                pausedProviders.Clear();
            }
        }

        // ---------------- Screens ----------------

        private void ShowLobby()
        {
            state = State.Lobby;
            OpenPanel("<b>Bubble Sky</b>\n<size=75%>Bubbles come at you from every side.\nPop as many as you can in 90 seconds!\nQuick pops = combo,  <color=#E8B830>golden</color> = +5</size>",
                ("Start!", StartRound), ("Back to city", Leave));
        }

        private void StartRound()
        {
            ClosePanel();
            StartCoroutine(CountdownRoutine());
        }

        private IEnumerator CountdownRoutine()
        {
            state = State.Countdown;
            score = 0; combo = 0; popped = 0; goldenPopped = 0; elapsed = 0f; nextSpawn = 0.3f; lastPopTime = -10f;
            EnsureHud();
            hudText.gameObject.SetActive(true);
            UpdateHud();
            foreach (string s in new[] { "3", "2", "1" })
            {
                ShowBig(s, Color.white);
                DemoAudio.Play(Sfx.Click);
                yield return new WaitForSeconds(0.8f);
            }
            ShowBig("POP!", Orange);
            DemoAudio.Play(Sfx.Chime, null, 0.7f);
            state = State.Playing;
            yield return new WaitForSeconds(0.7f);
            bigText.gameObject.SetActive(false);
        }

        private void EndRound()
        {
            state = State.Results;
            foreach (var l in live) l.leaving = true;   // the rest shrink away
            DemoAudio.Play(Sfx.Chime, null, 0.7f);
            StartCoroutine(EndRoutine());
        }

        private IEnumerator EndRoutine()
        {
            ShowBig($"Time's up!\n<size=55%>{score} points</size>", Orange);
            yield return new WaitForSeconds(2.2f);
            bigText.gameObject.SetActive(false);
            hudText.gameObject.SetActive(false);
            if (Qualifies(score)) ShowInitials();
            else ShowResults(-1);
        }

        private bool Qualifies(int s) => s > 0 && (board.entries.Count < 10 || s > board.entries[board.entries.Count - 1].score);

        // Four letter wheels (up/down), then Save.
        private void ShowInitials()
        {
            state = State.Initials;
            string last = PlayerPrefs.GetString(InitialsKey, "AAAA").PadRight(4, 'A');
            char[] letters = last.Substring(0, 4).ToUpperInvariant().ToCharArray();
            var p = OpenPanel($"<b>New high score: {score}!</b>\n<size=75%>Enter your initials</size>",
                ("Save", () => SaveScore(new string(letters))));
            var wheels = new GameObject("Wheels", typeof(RectTransform)).GetComponent<RectTransform>();
            wheels.SetParent(p.transform, false);
            wheels.anchoredPosition = new Vector2(0f, -40f);
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                float x = (i - 1.5f) * 120f;
                TextMeshProUGUI letter = MakeText(wheels, letters[i].ToString(), new Vector2(x, 0f), new Vector2(100f, 110f), 92f, Navy);
                void Step(int d)
                {
                    letters[idx] = (char)('A' + (letters[idx] - 'A' + d + 26) % 26);
                    letter.text = letters[idx].ToString();
                }
                MakeButton(wheels, "", new Vector2(x, 100f), new Vector2(96f, 70f), Color.white, () => Step(-1), arrowUp: true);
                MakeButton(wheels, "", new Vector2(x, -100f), new Vector2(96f, 70f), Color.white, () => Step(1), arrowUp: false);
            }
        }

        private void SaveScore(string initials)
        {
            PlayerPrefs.SetString(InitialsKey, initials);
            var e = new Entry { initials = initials, score = score };
            board.entries.Add(e);
            board.entries.Sort((a, b) => b.score.CompareTo(a.score));
            if (board.entries.Count > 10) board.entries.RemoveRange(10, board.entries.Count - 10);
            int rank = board.entries.IndexOf(e);
            SaveBoard();
            UpdateBoard();
            ShowResults(rank);
        }

        private void ShowResults(int rank)
        {
            state = State.Results;
            lastRank = rank;
            OpenPanel($"<b>{score} points</b>\n<size=70%>{popped} bubbles, {goldenPopped} golden</size>\n\n<size=80%>{BoardText(rank)}</size>", true,
                ("Play again", StartRound), ("Back to city", Leave));
        }

        // ---------------- The round ----------------

        private void TickRound()
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / roundSeconds);
            if (elapsed >= nextSpawn)
            {
                nextSpawn = elapsed + Mathf.Lerp(spawnInterval.x, spawnInterval.y, t) * UnityEngine.Random.Range(0.7f, 1.3f);
                if (live.Count < maxBubbles) Spawn(t);
            }
            if (elapsed - lastPopTime > comboWindow) combo = 0;
            UpdateHud();
            if (elapsed >= roundSeconds) EndRound();
        }

        private void Spawn(float t)
        {
            if (bubblePrefab == null || cam == null) return;
            Vector3 head = cam.transform.position;
            float yaw = UnityEngine.Random.Range(0f, 360f), pitch = UnityEngine.Random.Range(-12f, 32f);
            Vector3 dir = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
            bool golden = UnityEngine.Random.value < goldenChance;
            float speed = Mathf.Lerp(bubbleSpeed.x, bubbleSpeed.y, t) * UnityEngine.Random.Range(0.85f, 1.15f) * (golden ? 1.35f : 1f);
            bool incoming = UnityEngine.Random.value < 0.6f;

            Vector3 pos, vel;
            if (incoming)
            {
                pos = head + dir * UnityEngine.Random.Range(9f, 12f);
                // Fly past you (not through your face): aim at a point beside your head.
                Vector3 side = Vector3.Cross(Vector3.up, dir).normalized * UnityEngine.Random.Range(-2.2f, 2.2f);
                if (Mathf.Abs(side.magnitude) < 1.1f) side = side.normalized * 1.1f;
                Vector3 pass = head + side + Vector3.up * UnityEngine.Random.Range(-0.6f, 0.9f);
                vel = (pass - pos).normalized * speed;
            }
            else
            {
                pos = head + dir * UnityEngine.Random.Range(2.5f, 4f);
                vel = (dir + UnityEngine.Random.insideUnitSphere * 0.25f).normalized * speed * 0.8f;
            }
            if (pos.y < platformPosition.y + 0.5f) pos.y = platformPosition.y + 0.5f;   // never under the deck

            GiftBubble b = Instantiate(bubblePrefab, pos, Quaternion.identity);
            b.name = golden ? "Game Bubble (golden)" : "Game Bubble";
            var pop = b.GetComponent<GiftBubbleXRPop>();
            if (pop != null) pop.AimAssistMaxDistance = 12f;
            if (golden) b.SetFixedColor(Gold);
            var l = new Live { b = b, vel = vel, golden = golden, life = UnityEngine.Random.Range(9f, 12f) };
            l.scale = b.transform.localScale * (golden ? 1.0f : 1.3f);
            b.transform.localScale = l.scale * 0.01f;
            b.Popped += _ => OnPopped(l);
            live.Add(l);
        }

        private void MoveBubbles()
        {
            Vector3 head = cam != null ? cam.transform.position : platformPosition;
            for (int i = live.Count - 1; i >= 0; i--)
            {
                Live l = live[i];
                if (l.b == null) { live.RemoveAt(i); continue; }
                if (l.b.IsPopped) continue;   // its own pop animation runs
                l.age += Time.deltaTime;
                Transform tr = l.b.transform;
                tr.position += l.vel * Time.deltaTime;
                float d = Vector3.Distance(tr.position, head);
                if (l.age > l.life || d > 15f || (l.age > 2f && d < 0.5f)) l.leaving = true;
                // Grow in, shrink out.
                float s = l.leaving ? Mathf.MoveTowards(tr.localScale.x / l.scale.x, 0f, Time.deltaTime * 4f)
                                    : Mathf.MoveTowards(tr.localScale.x / l.scale.x, 1f, Time.deltaTime * 4f);
                tr.localScale = l.scale * s;
                if (l.leaving && s <= 0.01f) { Destroy(l.b.gameObject); live.RemoveAt(i); }
            }
        }

        private void OnPopped(Live l)
        {
            if (state != State.Playing || l.leaving) return;
            combo = elapsed - lastPopTime <= comboWindow ? combo + 1 : 1;
            lastPopTime = elapsed;
            int multiplier = Mathf.Min(4, 1 + (combo - 1) / 4);
            int points = (l.golden ? 5 : 1) * multiplier;
            score += points;
            popped++;
            if (l.golden) { goldenPopped++; DemoAudio.Play(Sfx.Sparkle, l.b.transform.position, 0.8f); }
            Popup(l.b.transform.position, multiplier > 1 ? $"+{points}  x{multiplier}" : $"+{points}", l.golden ? Gold : Color.white);
            UpdateHud();
        }

        // ---------------- HUD, popups, big text ----------------

        private void EnsureHud()
        {
            if (hudText == null) hudText = MakeWorldText("Bubble HUD", 1.5f);
            if (bigText == null) bigText = MakeWorldText("Bubble Big Text", 6f);
        }

        private void UpdateHud()
        {
            if (hudText == null) return;
            float left = Mathf.Max(0f, roundSeconds - elapsed);
            string time = $"{(int)left / 60}:{(int)left % 60:00}";
            string timeCol = left < 10f ? "#F28C40" : "#FFFFFF";
            int multiplier = Mathf.Min(4, 1 + (Mathf.Max(combo, 1) - 1) / 4);
            string comboText = combo >= 2 ? $"   <color=#F28C40>combo {combo}{(multiplier > 1 ? $" x{multiplier}" : "")}</color>" : "";
            hudText.text = $"<color={timeCol}>{time}</color>   <b>{score}</b> pts{comboText}";
        }

        private void ShowBig(string text, Color color)
        {
            EnsureHud();
            bigText.text = text;
            bigText.color = color;
            bigText.gameObject.SetActive(true);
            FollowView(bigText.transform, 3f, 0.1f, 1000f);
        }

        private void Popup(Vector3 at, string text, Color color)
        {
            TextMeshPro t = null;
            for (int i = 0; i < popups.Count; i++) if (!popups[i].gameObject.activeSelf) { t = popups[i]; popupAge[i] = 0f; break; }
            if (t == null)
            {
                if (popups.Count >= 10) { t = popups[0]; popupAge[0] = 0f; }
                else { t = MakeWorldText("Popup", 1.6f); popups.Add(t); popupAge.Add(0f); }
            }
            t.text = $"<b>{text}</b>";
            t.color = color;
            t.transform.position = at;
            t.gameObject.SetActive(true);
        }

        private void TickPopups()
        {
            for (int i = 0; i < popups.Count; i++)
            {
                var t = popups[i];
                if (!t.gameObject.activeSelf) continue;
                popupAge[i] += Time.deltaTime;
                float a = popupAge[i] / 0.9f;
                if (a >= 1f) { t.gameObject.SetActive(false); continue; }
                t.transform.position += Vector3.up * Time.deltaTime * 0.6f;
                if (cam != null) t.transform.rotation = Quaternion.LookRotation(t.transform.position - cam.transform.position);
                t.alpha = 1f - a * a;
            }
        }

        private TextMeshPro MakeWorldText(string name, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var t = go.AddComponent<TextMeshPro>();
            if (font != null) t.font = font;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.outlineWidth = 0.2f;
            t.outlineColor = new Color32(20, 26, 48, 220);
            t.rectTransform.sizeDelta = new Vector2(4f, 1f);
            go.SetActive(false);
            return go.GetComponent<TextMeshPro>();
        }

        // Keeps a thing in front of your view, following your turns smoothly (rate 1000 = snap).
        private void FollowView(Transform t, float distance, float height, float rate)
        {
            if (t == null || cam == null || !t.gameObject.activeInHierarchy) return;
            Transform head = cam.transform;
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            Vector3 target = head.position + fwd.normalized * distance + Vector3.up * height;
            t.position = Vector3.Lerp(t.position, target, rate >= 1000f ? 1f : 1f - Mathf.Exp(-rate * Time.deltaTime));
            t.rotation = Quaternion.LookRotation(t.position - head.position);
        }

        // ---------------- Panels (world-space UI, controller rays) ----------------

        private GameObject OpenPanel(string title, params (string label, Action onClick)[] buttons) => OpenPanel(title, false, buttons);

        private GameObject OpenPanel(string title, bool tall, params (string label, Action onClick)[] buttons)
        {
            ClosePanel();
            if (cam == null) cam = Camera.main;
            panel = new GameObject("Bubble Game Panel", typeof(RectTransform));
            panel.transform.SetParent(transform, false);
            var rt = (RectTransform)panel.transform;
            rt.sizeDelta = new Vector2(680f, 720f);
            panel.transform.localScale = Vector3.one * 0.0012f;
            var canvas = panel.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            panel.AddComponent<GraphicRaycaster>();
            panel.AddComponent<TrackedDeviceGraphicRaycaster>();
            var bg = panel.AddComponent<Image>();
            bg.sprite = panelSprite; bg.type = Image.Type.Sliced; bg.color = new Color(0.93f, 0.96f, 1f, 0.9f);

            var tt = MakeText(rt, title, tall ? new Vector2(0f, 40f) : new Vector2(0f, 190f), tall ? new Vector2(620f, 560f) : new Vector2(620f, 300f), tall ? 46f : 52f, Navy);
            tt.textWrappingMode = TextWrappingModes.Normal;
            if (tall) tt.verticalAlignment = VerticalAlignmentOptions.Top;
            for (int i = 0; i < buttons.Length; i++)
            {
                var (label, onClick) = buttons[i];
                float x = buttons.Length == 1 ? 0f : (i == 0 ? -150f : 150f);
                MakeButton(rt, label, new Vector2(x, -290f), new Vector2(280f, 86f), i == 0 ? Orange : Color.white, onClick);
            }
            // In front of you, slightly below eye level, facing you.
            Transform head = cam.transform;
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            panel.transform.position = head.position + fwd.normalized * 1.3f + Vector3.down * 0.15f;
            panel.transform.rotation = Quaternion.LookRotation(panel.transform.position - head.position);
            return panel;
        }

        private void ClosePanel()
        {
            if (panel != null) Destroy(panel);
            panel = null;
        }

        private TextMeshProUGUI MakeText(RectTransform parent, string text, Vector2 pos, Vector2 size, float fontSize, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = text; t.fontSize = fontSize; t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            return t;
        }

        private static Sprite arrow;

        private void MakeButton(RectTransform parent, string label, Vector2 pos, Vector2 size, Color color, Action onClick, bool? arrowUp = null)
        {
            var go = new GameObject(label.Length > 0 ? label : "Arrow", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            img.sprite = pillSprite; img.type = Image.Type.Sliced; img.color = color;
            var btn = go.AddComponent<Button>();
            var cb = btn.colors;
            cb.normalColor = Color.white; cb.highlightedColor = new Color(1f, 0.85f, 0.7f); cb.pressedColor = new Color(0.85f, 0.85f, 0.85f);
            btn.colors = cb;
            btn.onClick.AddListener(() => { DemoAudio.Play(Sfx.Click); onClick(); });
            if (arrowUp.HasValue)
            {
                var a = new GameObject("Icon", typeof(RectTransform));
                var art = (RectTransform)a.transform;
                art.SetParent(rt, false);
                art.sizeDelta = new Vector2(40f, 30f);
                art.localRotation = Quaternion.Euler(0f, 0f, arrowUp.Value ? 0f : 180f);
                var ai = a.AddComponent<Image>();
                ai.sprite = ArrowSprite(); ai.color = Navy; ai.raycastTarget = false;
            }
            else MakeText(rt, $"<b>{label}</b>", Vector2.zero, size, 36f, Navy);
        }

        // A small triangle, made once in code (no extra asset).
        private static Sprite ArrowSprite()
        {
            if (arrow != null) return arrow;
            const int w = 64, h = 48;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float half = (y + 0.5f) / h * (w * 0.5f);   // wide at the bottom (y = 0 is the bottom row)
                    half = w * 0.5f - half;
                    bool inside = Mathf.Abs(x + 0.5f - w * 0.5f) <= half;
                    tex.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
                }
            tex.Apply();
            arrow = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            return arrow;
        }

        // ---------------- Scoreboard ----------------

        private string BoardText(int highlight)
        {
            if (board.entries.Count == 0) return "No scores yet. Be the first!";
            var sb = new System.Text.StringBuilder("<b>TOP 10</b>\n");
            for (int i = 0; i < board.entries.Count; i++)
            {
                var e = board.entries[i];
                string line = $"{i + 1,2}.  {e.initials}   {e.score}";
                sb.AppendLine(i == highlight ? $"<color=#F28C40><b>{line}</b></color>" : line);
            }
            return sb.ToString();
        }

        private void UpdateBoard()
        {
            if (boardText != null) boardText.text = "<b>BUBBLE SKY</b>\n<size=70%>" + BoardText(lastRank) + "</size>";
        }

        [ContextMenu("Clear scoreboard")]
        public void ClearScores()
        {
            board = new Board();
            lastRank = -1;
            SaveBoard();
            UpdateBoard();
            Debug.Log("[BubbleGame] Scoreboard cleared.", this);
        }

        private static Board LoadBoard()
        {
            string json = PlayerPrefs.GetString(ScoresKey, "");
            if (string.IsNullOrEmpty(json)) return new Board();
            try { return JsonUtility.FromJson<Board>(json) ?? new Board(); } catch { return new Board(); }
        }

        private void SaveBoard()
        {
            PlayerPrefs.SetString(ScoresKey, JsonUtility.ToJson(board));
            PlayerPrefs.Save();
        }

        // ---------------- The platform (built once in code) ----------------

        private class Geo
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
            public void Tri(Vector3 a, Vector3 b, Vector3 cc, Color col)
            {
                col = col.linear; col.a = 1f;
                Vector3 nn = Vector3.Cross(b - a, cc - a).normalized; int i = v.Count;
                v.Add(a); v.Add(b); v.Add(cc);
                for (int k = 0; k < 3; k++) { n.Add(nn); c.Add(col); }
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }
            public Mesh ToMesh(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(v); m.SetNormals(n); m.SetColors(c); m.SetTriangles(t, 0); m.RecalculateBounds();
                return m;
            }
        }

        private void BuildPlatform()
        {
            var root = new GameObject("Bubble Sky Platform");
            root.transform.position = platformPosition;   // deck top = where your feet are
            platform = root.transform;
            float R = platformRadius;
            const int seg = 40;
            Color cream = new Color(0.94f, 0.92f, 0.86f), edge = Orange, under = new Color(0.62f, 0.66f, 0.74f), steel = new Color(0.62f, 0.64f, 0.66f);
            var g = new Geo();
            Vector3 P(float a, float r, float y) => new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                // Deck (rings of cream with a subtle darker inner ring) and orange edge band.
                Color deck = (i % 2 == 0) ? cream : cream * 0.97f;
                g.Tri(Vector3.zero, P(a1, R, 0f), P(a0, R, 0f), deck);
                g.Quad(P(a0, R, 0f), P(a1, R, 0f), P(a1, R, -0.18f), P(a0, R, -0.18f), edge);
                g.Quad(P(a0, R, -0.18f), P(a1, R, -0.18f), P(a1, R * 0.97f, -0.35f), P(a0, R * 0.97f, -0.35f), new Color(0.2f, 0.22f, 0.3f));
                // Underside: a soft cone, like a floating island.
                g.Tri(P(a0, R * 0.97f, -0.35f), P(a1, R * 0.97f, -0.35f), new Vector3(0f, -2.6f, 0f), under);
                // Glass balustrade posts and top rail.
                if (i % 4 == 0)
                {
                    Vector3 p0 = P(a0, R - 0.08f, 0f);
                    BoxAt(g, p0 + Vector3.up * 0.55f, new Vector3(0.06f, 1.1f, 0.06f), Quaternion.Euler(0f, -a0 * Mathf.Rad2Deg, 0f), steel);
                }
                Vector3 r0 = P(a0, R - 0.08f, 1.1f), r1 = P(a1, R - 0.08f, 1.1f);
                BoxAt(g, (r0 + r1) * 0.5f, new Vector3(0.07f, 0.06f, (r1 - r0).magnitude + 0.02f), Quaternion.LookRotation(r1 - r0), steel);
            }
            // Scoreboard on two posts at the back (south), facing the middle.
            Vector3 bc = new Vector3(0f, 0f, -R + 0.35f);
            foreach (float x in new[] { -0.95f, 0.95f }) BoxAt(g, bc + new Vector3(x, 1.0f, 0f), new Vector3(0.08f, 2.0f, 0.08f), Quaternion.identity, steel);
            BoxAt(g, bc + new Vector3(0f, 1.55f, 0f), new Vector3(2.2f, 1.45f, 0.08f), Quaternion.identity, Navy);
            BoxAt(g, bc + new Vector3(0f, 2.33f, 0f), new Vector3(2.3f, 0.12f, 0.12f), Quaternion.identity, edge);

            var body = new GameObject("Platform Mesh");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = g.ToMesh("BubbleSkyPlatform");
            body.AddComponent<MeshRenderer>().sharedMaterial = detailMaterial;

            // Glass ring (see-through, double sided).
            var glass = new Geo();
            for (int i = 0; i < seg; i++)
            {
                float a0 = i * Mathf.PI * 2f / seg, a1 = (i + 1) * Mathf.PI * 2f / seg;
                glass.Quad(P(a0, R - 0.08f, 0.08f), P(a0, R - 0.08f, 1.07f), P(a1, R - 0.08f, 1.07f), P(a1, R - 0.08f, 0.08f), new Color(0.75f, 0.88f, 0.92f));
            }
            var gl = new GameObject("Platform Glass");
            gl.transform.SetParent(root.transform, false);
            gl.AddComponent<MeshFilter>().sharedMesh = glass.ToMesh("BubbleSkyGlass");
            var glr = gl.AddComponent<MeshRenderer>(); glr.sharedMaterial = glassMaterial; glr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Floor to stand on, and an invisible ring that keeps you on it (ignored by controller rays).
            var floor = new GameObject("Platform Floor");
            floor.transform.SetParent(root.transform, false);
            for (int i = 0; i < 12; i++)
            {
                var f = new GameObject("Floor " + i);
                f.transform.SetParent(floor.transform, false);
                f.transform.localRotation = Quaternion.Euler(0f, i * 15f, 0f);
                f.transform.localPosition = new Vector3(0f, -0.15f, 0f);
                f.AddComponent<BoxCollider>().size = new Vector3(R * 2f * 0.98f, 0.3f, R * 2f * 0.26f);
            }
            for (int i = 0; i < 24; i++)
            {
                float a = i * Mathf.PI * 2f / 24f;
                var w = new GameObject("Edge " + i) { layer = 2 };
                w.transform.SetParent(root.transform, false);
                w.transform.localPosition = P(a, R - 0.05f, 0.7f);
                w.transform.localRotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);
                w.AddComponent<BoxCollider>().size = new Vector3(0.15f, 1.4f, R * 2f * Mathf.PI / 24f + 0.1f);
            }

            // Scoreboard text.
            var bt = new GameObject("Scoreboard Text");
            bt.transform.SetParent(root.transform, false);
            bt.transform.localPosition = bc + new Vector3(0f, 1.55f, 0.05f);
            bt.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);   // reads from the middle of the platform
            boardText = bt.AddComponent<TextMeshPro>();
            if (font != null) boardText.font = font;
            boardText.fontSize = 2.6f;
            boardText.color = Color.white;
            boardText.alignment = TextAlignmentOptions.Center;
            boardText.rectTransform.sizeDelta = new Vector2(2f, 1.35f);
            boardText.enableAutoSizing = true; boardText.fontSizeMin = 0.8f; boardText.fontSizeMax = 2.6f;
            root.SetActive(false);
        }

        private static void BoxAt(Geo g, Vector3 center, Vector3 size, Quaternion r, Color col)
        {
            Vector3 hx = r * new Vector3(size.x * 0.5f, 0, 0), hy = r * new Vector3(0, size.y * 0.5f, 0), hz = r * new Vector3(0, 0, size.z * 0.5f);
            Vector3 P(int x, int y, int z) => center + hx * x + hy * y + hz * z;
            g.Quad(P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), col);
            g.Quad(P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), P(-1, -1, -1), col);
            g.Quad(P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), col);
            g.Quad(P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), col);
            g.Quad(P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), P(-1, -1, 1), col);
        }
    }
}
