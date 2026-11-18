using System;
using System.Collections;
using System.Collections.Generic;
using BubbleFeature;
using BubbleFeature.Bubbles;
using BubbleFeature.Menu;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace CityFeature
{
    /// <summary>
    /// Tutorial NPC ("Lotte"): the first time you arrive in the city she walks up to you and teaches the
    /// controls one by one, waiting until you have tried each one: turning, walking, teleporting,
    /// popping a gift bubble and the menu. Her lines appear in a speech balloon above her head, typed out
    /// at speaking pace. Remembered on the device; "Replay tutorial" in the menu starts it again.
    /// After the first tutorial she waits at her help stand ("Tutorial Stand" under each area's Spot):
    /// walk up to her and she asks if you need help; point at "Yes" and she gives the tutorial again.
    /// Desktop testing: N skips the current step, Y / U answer yes / no at the stand.
    /// </summary>
    public class TutorialGuide : MonoBehaviour
    {
        private const string DoneKey = "BDCity.TutorialDone";
        private const string StandName = "Tutorial Stand";

        [Header("Scene")]
        [SerializeField] private NeighbourhoodTeleporter teleporter;
        [SerializeField] private GiftBubbleSpawner giftBubbles;
        [SerializeField] private GiftMenu menu;

        [Header("Guide")]
        [SerializeField] private GameObject guidePrefab;
        [SerializeField] private string guideName = "Lotte";
        [SerializeField] private float walkSpeed = 1.2f;
        [SerializeField] private float talkDistance = 2.0f;

        [Header("Speech balloon")]
        [SerializeField] private Material balloonMaterial;   // URP Unlit, white
        [SerializeField] private Material markerMaterial;    // URP Unlit, any colour (teleport target)
        [SerializeField] private float charsPerSecond = 15f; // about normal speaking pace
        [SerializeField] private float balloonHeight = 2.05f;
        [SerializeField] private float fontSize = 0.9f;

        [Header("Waiting for the player")]
        [SerializeField] private float hintAfter = 12f;
        [SerializeField] private float giveUpAfter = 45f;

        [Header("Help stand (after the first tutorial)")]
        [Tooltip("She asks \"Need help?\" when you come this close.")]
        [SerializeField] private float askDistance = 3.2f;
        [Tooltip("After an answer she asks again only once you walked this far away and came back.")]
        [SerializeField] private float resetDistance = 6f;
        [Tooltip("Where she stands, in the stand's local space (behind the counter).")]
        [SerializeField] private Vector3 standLocalSpot = new Vector3(0f, 0f, -0.75f);

        [Header("Speech balloon when she is out of view")]
        [Tooltip("She counts as 'in view' within this many degrees of where you look (left/right).")]
        [SerializeField] private float followAngle = 50f;
        [Tooltip("The balloon then floats this far from your head, this many degrees towards her side.")]
        [SerializeField] private float followDistance = 1.6f;
        [SerializeField] private float edgeAngle = 28f;
        private bool balloonFollowSnap = true;

        public bool IsRunning { get; private set; }
        /// <summary>True when the tutorial will start by itself on the next arrival (first visit).</summary>
        public bool WillAutoStart => !IsRunning && PlayerPrefs.GetInt(DoneKey, 0) == 0;
        private bool Done => PlayerPrefs.GetInt(DoneKey, 0) == 1;

        private XROrigin origin;
        private Camera cam;
        private Transform guide;
        private Transform balloon, balloonBack, balloonTail;
        private TextMeshPro balloonText, balloonName;
        private Transform marker;
        private Coroutine routine;
        private Coroutine standRoutine;
        private Transform stand;
        private GameObject buttons;
        private int answer;           // 0 = none yet, 1 = yes, 2 = no
        private bool skipStep;

        private void Awake()
        {
            origin = FindAnyObjectByType<XROrigin>();
        }

        private void OnEnable()
        {
            if (teleporter == null) return;
            teleporter.Revealed += OnRevealed;
            teleporter.Arrived += OnArrived;
        }

        private void OnDisable()
        {
            if (teleporter == null) return;
            teleporter.Revealed -= OnRevealed;
            teleporter.Arrived -= OnArrived;
        }

        private void OnRevealed(string place)
        {
            if (WillAutoStart) Play();
            else ShowAtStand();
        }

        // Travelling away: stop the tutorial (it starts again next time if it wasn't finished) and leave the stand.
        // Only the stand of the area we arrive in is shown.
        private void OnArrived(string place)
        {
            if (IsRunning) Stop(false);
            LeaveStand();
            if (teleporter == null) return;
            foreach (var n in teleporter.Neighbourhoods)
            {
                if (n.spot == null) continue;
                Transform s = n.spot.Find(StandName);
                if (s != null) s.gameObject.SetActive(n.name == place);
            }
        }

        public void Play()
        {
            if (IsRunning) Stop(false);
            if (standRoutine != null) { StopCoroutine(standRoutine); standRoutine = null; }
            HideButtons();
            routine = StartCoroutine(Run(Done));
        }

        /// <summary>Demo operator: forget that the tutorial was done, so the next visitor gets it again.</summary>
        public void ResetProgress()
        {
            Stop(false);
            PlayerPrefs.DeleteKey(DoneKey);
            PlayerPrefs.Save();
        }

        /// <summary>Skip from the menu: counts as done, and she goes back to her stand.</summary>
        public void Skip()
        {
            Stop(true);
            ShowAtStand();
        }

        private void Stop(bool markDone)
        {
            if (routine != null) StopCoroutine(routine);
            routine = null;
            IsRunning = false;
            if (markDone) { PlayerPrefs.SetInt(DoneKey, 1); PlayerPrefs.Save(); }
            LeaveStand();
        }

        // Removes her (and everything that belongs to her) from the scene.
        private void LeaveStand()
        {
            if (standRoutine != null) { StopCoroutine(standRoutine); standRoutine = null; }
            if (walking != null) { StopCoroutine(walking); walking = null; }
            if (guide != null) { BubbleFeature.Bots.BotWanderer.UnregisterObstacle(guide); Destroy(guide.gameObject); }
            if (balloon != null) Destroy(balloon.gameObject);
            if (marker != null) Destroy(marker.gameObject);
            if (buttons != null) Destroy(buttons);
            guide = null; balloon = null; marker = null; buttons = null;
        }

        private void Update()
        {
            Keyboard k = Keyboard.current;
            if (IsRunning && k != null && k.nKey.wasPressedThisFrame) skipStep = true;
            if (buttons != null && buttons.activeSelf && k != null)
            {
                if (k.yKey.wasPressedThisFrame) answer = 1;
                if (k.uKey.wasPressedThisFrame) answer = 2;
            }
            if (balloon != null && guide != null && cam != null) PlaceBalloon();
        }

        // Above her head while you can see her. When she is out of view (behind you, far to the side),
        // the balloon floats at the edge of your view on her side, with an arrow pointing to her,
        // so you never miss what she says.
        private void PlaceBalloon()
        {
            Vector3 overHead = guide.position + Vector3.up * balloonHeight;
            Transform head = cam.transform;
            Vector3 look = Flatten(head.forward), toHer = Flatten(overHead - head.position);
            float angle = Vector3.SignedAngle(look, toHer, Vector3.up);
            bool inView = Mathf.Abs(angle) < followAngle;
            Vector3 target = overHead;
            if (!inView)
            {
                Vector3 dir = Quaternion.Euler(0f, Mathf.Sign(angle) * edgeAngle, 0f) * look;
                target = head.position + dir * followDistance + Vector3.up * 0.05f;
            }
            float k = balloonFollowSnap ? 1f : 1f - Mathf.Exp(-8f * Time.deltaTime);
            balloonFollowSnap = false;
            balloon.position = Vector3.Lerp(balloon.position, target, k);
            Vector3 toCam = balloon.position - head.position;
            if (toCam.sqrMagnitude > 0.01f) balloon.rotation = Quaternion.LookRotation(toCam);
            if (balloonTail != null) balloonTail.gameObject.SetActive(inView);
            if (balloonName != null)
                balloonName.text = inView ? $"<b>{guideName}</b>" : angle < 0f ? $"<b>< {guideName}</b>" : $"<b>{guideName} ></b>";
        }

        // ---------------- The help stand ----------------

        private Transform FindStand()
        {
            if (teleporter == null) return null;
            foreach (var n in teleporter.Neighbourhoods)
                if (n.name == teleporter.CurrentName && n.spot != null) return n.spot.Find(StandName);
            return null;
        }

        private Vector3 StandSpot => Ground(stand.TransformPoint(standLocalSpot));

        private void ShowAtStand()
        {
            if (!Done || IsRunning) return;
            stand = FindStand();
            if (stand == null) return;
            cam = Camera.main;
            if (origin == null) origin = FindAnyObjectByType<XROrigin>();
            if (guide == null) SpawnGuideAt(StandSpot, Quaternion.LookRotation(Flatten(stand.forward)));
            if (standRoutine != null) StopCoroutine(standRoutine);
            standRoutine = StartCoroutine(StandLoop());
        }

        private IEnumerator StandLoop()
        {
            while (guide != null)
            {
                // Wait at the counter, looking out.
                guide.rotation = Quaternion.LookRotation(Flatten(stand.forward));
                while (Flat(PlayerFeet - guide.position) > askDistance) yield return null;

                FacePlayer();
                yield return Say("Hi again! Do you need some help?");
                ShowButtons();
                answer = 0;
                float t = 0f;
                while (answer == 0 && t < 30f && Flat(PlayerFeet - guide.position) < resetDistance)
                {
                    t += Time.deltaTime;
                    FacePlayer();
                    yield return null;
                }
                HideButtons();

                if (answer == 1)
                {
                    yield return Say("Sure! Let's go through it together.");
                    HideBalloon();
                    standRoutine = null;
                    routine = StartCoroutine(Run(true));
                    yield break;
                }
                if (answer == 2) yield return Say("Okay! I'm right here if you need me.");
                HideBalloon();

                // Ask again only after they walked away and came back.
                while (Flat(PlayerFeet - guide.position) < resetDistance) yield return null;
            }
            standRoutine = null;
        }

        // "Yes" / "No" buttons under the speech balloon: a world-space canvas the controller rays can click.
        private void ShowButtons()
        {
            if (buttons == null) buttons = BuildButtons();
            // Just below the balloon's bottom edge.
            float bottom = balloonBack != null ? balloonBack.localScale.y * 0.5f : 0.2f;
            buttons.transform.localPosition = new Vector3(0f, -(bottom + 0.2f), -0.01f);
            buttons.SetActive(true);
        }

        private void HideButtons()
        {
            if (buttons != null) buttons.SetActive(false);
        }

        private GameObject BuildButtons()
        {
            EnsureBalloon();
            var go = new GameObject("Help Buttons", typeof(RectTransform));
            go.transform.SetParent(balloon, false);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one * 0.001f;   // 1 unit = 1 mm
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(620f, 130f);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            go.AddComponent<GraphicRaycaster>();             // mouse (desktop testing)
            go.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR controller rays
            MakeButton(rt, "Yes, please!", new Vector2(-160f, 0f), new Color(0.95f, 0.55f, 0.25f), () => answer = 1);
            MakeButton(rt, "No, thanks", new Vector2(160f, 0f), new Color(0.30f, 0.33f, 0.40f), () => answer = 2);
            go.SetActive(false);
            return go;
        }

        private static void MakeButton(RectTransform parent, string label, Vector2 pos, Color color, UnityEngine.Events.UnityAction onClick)
        {
            var b = new GameObject(label, typeof(RectTransform));
            var brt = (RectTransform)b.transform;
            brt.SetParent(parent, false);
            brt.sizeDelta = new Vector2(290f, 110f);
            brt.anchoredPosition = pos;
            var img = b.AddComponent<Image>();
            img.color = color;
            var btn = b.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
            btn.colors = colors;
            btn.onClick.AddListener(() => BubbleFeature.Audio.DemoAudio.Play(BubbleFeature.Audio.Sfx.Click));
            btn.onClick.AddListener(onClick);

            var t = new GameObject("Text", typeof(RectTransform));
            var trt = (RectTransform)t.transform;
            trt.SetParent(brt, false);
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = trt.offsetMax = Vector2.zero;
            var tmp = t.AddComponent<TextMeshProUGUI>();
            tmp.text = label;
            tmp.fontSize = 44f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.raycastTarget = false;
        }

        // ---------------- The lesson ----------------

        private IEnumerator Run(bool replay)
        {
            IsRunning = true;
            cam = Camera.main;
            if (origin == null) origin = FindAnyObjectByType<XROrigin>();
            if (guide == null)
            {
                yield return new WaitForSeconds(0.8f);
                SpawnGuide();
            }
            yield return Walk(PointInFrontOfPlayer(talkDistance));
            FacePlayer();

            if (!replay)
            {
                yield return Say($"Hi! I'm {guideName}. Welcome to B&D City!");
                yield return Say("I'll show you how everything works. It only takes a minute.");
            }

            // 1. Turning
            yield return Say("First, look around. Push the right stick left or right to turn.");
            float startYaw = origin.transform.eulerAngles.y, headYaw = cam.transform.eulerAngles.y;
            float turned = 0f;
            yield return WaitFor(() =>
            {
                turned = Mathf.Max(Mathf.Abs(Mathf.DeltaAngle(startYaw, origin.transform.eulerAngles.y)),
                                   Mathf.Abs(Mathf.DeltaAngle(headYaw, cam.transform.eulerAngles.y)) * 0.6f);
                return turned > 50f;
            }, "Push the right stick to the left or to the right.");
            yield return Say("Great!");

            // 2. Walking
            Vector3 away = PointNearPlayer(4.5f, 0f);
            yield return Walk(away);
            FacePlayer();
            yield return Say("Now come over to me. Push the left stick forward to walk.");
            yield return WaitFor(() => Flat(PlayerFeet - guide.position) < 2.2f, "Push the left stick forward, towards me.");
            yield return Say("Nice walking!");

            // 3. Teleporting
            Vector3 target = PointNearPlayer(7f, 70f);
            ShowMarker(target);
            Vector3 next = target + (PlayerFeet - target).normalized * 1.8f;
            Walk(Ground(next));
            yield return Say("For longer distances you can teleport. Push the right stick forward, aim at the circle and let go.");
            Vector3 last = origin.transform.position; bool jumped = false;
            yield return WaitFor(() =>
            {
                Vector3 now = origin.transform.position;
                if (Flat(now - last) > 1.5f) jumped = true;   // a jump in one frame = a teleport
                last = now;
                return jumped || Flat(PlayerFeet - target) < 1.5f;
            }, "Push the right stick forward, point at the circle on the ground, then let go.");
            HideMarker();
            yield return Say("Whoosh! That's teleporting.");
            yield return Walk(PointInFrontOfPlayer(talkDistance));
            FacePlayer();

            // 4. Gift bubbles
            var before = new HashSet<GiftBubble>(FindObjectsByType<GiftBubble>(FindObjectsInactive.Exclude));
            var mine = new List<GiftBubble>();
            if (giftBubbles != null) giftBubbles.Spawn();
            yield return Say("Look, someone sent you a gift bubble! It's flying your way.");
            yield return Say("Point at it with a controller and pull the trigger to pop it.");
            yield return WaitFor(() =>
            {
                foreach (var b in FindObjectsByType<GiftBubble>(FindObjectsInactive.Exclude))
                    if (!before.Contains(b) && !mine.Contains(b)) mine.Add(b);
                foreach (var b in mine)
                    if (b == null || b.IsPopped) return true;   // popped (and maybe already cleaned up)
                return false;
            }, "Aim the ray at the bubble, then pull the trigger.", giveUp: 60f);
            yield return new WaitForSeconds(3f);   // let the gift reveal play
            yield return Say("Gifts can be a message or a little present, like a hat or a ring.");

            // 5. The menu
            if (menu != null)
            {
                yield return Say("Now press the menu button on your left controller.");
                yield return WaitFor(() => menu.IsOpen, "The menu button is the small button on your left controller.");
                yield return Say("Here you can send gifts to others, or use 'Go to...' to travel to another part of the city.");
                yield return Say("Press the menu button again to close it.");
                yield return WaitFor(() => !menu.IsOpen, "Press the menu button on your left controller to close it.", giveUp: 25f);
            }

            // Bye: back to her stand if this area has one, otherwise she walks off.
            PlayerPrefs.SetInt(DoneKey, 1); PlayerPrefs.Save();
            stand = FindStand();
            if (stand != null)
            {
                yield return Say("That's it! I'll be at my stand if you need me again. Have fun exploring!");
                HideBalloon();
                yield return Walk(StandSpot);
                IsRunning = false;
                routine = null;
                standRoutine = StartCoroutine(StandLoop());
                yield break;
            }
            yield return Say("That's it! Have fun exploring Rotterdam. See you around!");
            HideBalloon();
            Vector3 leave = guide.position + (guide.position - PlayerFeet).normalized * 15f;
            float t = 0f;
            Walk(Ground(leave));
            while (t < 14f && guide != null && (t < 3f || PlayerView.CanSee(guide.position + Vector3.up)))
            { t += Time.deltaTime; yield return null; }
            Stop(false);
        }

        // ---------------- Speaking ----------------

        private IEnumerator Say(string line)
        {
            EnsureBalloon();
            if (!balloon.gameObject.activeSelf) balloonFollowSnap = true;
            balloon.gameObject.SetActive(true);
            balloonText.text = line;
            balloonText.maxVisibleCharacters = 0;
            // Size the balloon for the whole line up front, so it doesn't grow while typing.
            balloonText.ForceMeshUpdate();
            Vector2 size = balloonText.GetPreferredValues(line, 1.25f, 0f);
            size.x = Mathf.Min(size.x, 1.25f);
            balloonText.rectTransform.sizeDelta = new Vector2(1.25f, size.y);
            balloonBack.localScale = new Vector3(size.x + 0.14f, size.y + 0.16f, 1f);
            balloonBack.localPosition = new Vector3(0f, 0f, 0.002f);
            balloonTail.localPosition = new Vector3(0f, -(size.y + 0.16f) * 0.5f, 0.002f);
            balloonName.rectTransform.localPosition = new Vector3(-(size.x + 0.14f) * 0.5f + 0.02f, (size.y + 0.16f) * 0.5f + 0.06f, 0f);
            FacePlayer();

            skipStep = false;
            float shown = 0f;
            int total = line.Length;
            while (balloonText.maxVisibleCharacters < total && !skipStep)
            {
                shown += Time.deltaTime * charsPerSecond;
                char c = line[Mathf.Clamp((int)shown - 1, 0, total - 1)];
                if (c == '.' || c == '!' || c == '?') shown -= Time.deltaTime * charsPerSecond * 0.6f; // small pause at sentence ends
                balloonText.maxVisibleCharacters = Mathf.Clamp((int)shown, 0, total);
                yield return null;
            }
            balloonText.maxVisibleCharacters = total;
            // Time to read it before the next line: about half a second plus a bit per word.
            float hold = 0.6f + line.Split(' ').Length * 0.12f;
            float h = 0f;
            skipStep = false;
            while (h < hold && !skipStep) { h += Time.deltaTime; yield return null; }
            skipStep = false;
        }

        // Waits until the player did it; repeats a hint now and then, and moves on after a while anyway.
        private IEnumerator WaitFor(Func<bool> done, string hint, float giveUp = -1f)
        {
            if (giveUp < 0f) giveUp = giveUpAfter;
            float t = 0f, sinceHint = 0f;
            skipStep = false;
            while (!done() && !skipStep && t < giveUp)
            {
                t += Time.deltaTime; sinceHint += Time.deltaTime;
                if (sinceHint > hintAfter) { yield return Say(hint); sinceHint = 0f; }
                yield return null;
            }
            skipStep = false;
        }

        private void EnsureBalloon()
        {
            if (balloon != null) return;
            balloon = new GameObject("Speech Balloon").transform;

            var back = GameObject.CreatePrimitive(PrimitiveType.Quad);
            back.name = "Back";
            Destroy(back.GetComponent<Collider>());
            back.transform.SetParent(balloon, false);
            back.GetComponent<MeshRenderer>().sharedMaterial = balloonMaterial;
            back.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            balloonBack = back.transform;

            var tail = GameObject.CreatePrimitive(PrimitiveType.Quad);
            tail.name = "Tail";
            Destroy(tail.GetComponent<Collider>());
            tail.transform.SetParent(balloon, false);
            tail.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
            tail.transform.localScale = Vector3.one * 0.12f;
            tail.GetComponent<MeshRenderer>().sharedMaterial = balloonMaterial;
            tail.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            balloonTail = tail.transform;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(balloon, false);
            balloonText = textGo.AddComponent<TextMeshPro>();
            balloonText.fontSize = fontSize;
            balloonText.color = new Color(0.12f, 0.12f, 0.16f);
            balloonText.alignment = TextAlignmentOptions.Center;
            balloonText.textWrappingMode = TextWrappingModes.Normal;
            balloonText.rectTransform.sizeDelta = new Vector2(1.25f, 0.4f);
            // The text faces the camera: the balloon looks along camera->balloon, so flip the text.
            textGo.transform.localRotation = Quaternion.identity;

            var nameGo = new GameObject("Name");
            nameGo.transform.SetParent(balloon, false);
            balloonName = nameGo.AddComponent<TextMeshPro>();
            balloonName.text = $"<b>{guideName}</b>";
            balloonName.fontSize = fontSize * 0.8f;
            balloonName.color = new Color(0.95f, 0.55f, 0.25f);
            balloonName.alignment = TextAlignmentOptions.Left;
            balloonName.rectTransform.pivot = new Vector2(0f, 0.5f);
            balloonName.rectTransform.sizeDelta = new Vector2(0.8f, 0.15f);
            balloon.gameObject.SetActive(false);
        }

        private void HideBalloon()
        {
            if (balloon != null) balloon.gameObject.SetActive(false);
        }

        // ---------------- Moving the guide ----------------

        private void SpawnGuide()
        {
            // Somewhere in front of the player, a bit to the side, with a clear line to walk in.
            Vector3 feet = PlayerFeet;
            Vector3 fwd = Flatten(cam.transform.forward);
            Vector3 spawn = feet + fwd * 3f;
            foreach (float dist in new[] { 9f, 7f, 5f })
            foreach (float ang in new[] { 30f, -30f, 15f, -15f, 45f, -45f })
            {
                Vector3 p = feet + Quaternion.Euler(0f, ang, 0f) * fwd * dist;
                if (Physics.Linecast(feet + Vector3.up, p + Vector3.up, ~0, QueryTriggerInteraction.Ignore)) continue;
                spawn = p; goto found;
            }
            found:
            SpawnGuideAt(Ground(spawn), Quaternion.LookRotation(Flatten(feet - spawn)));
        }

        private void SpawnGuideAt(Vector3 position, Quaternion rotation)
        {
            var go = Instantiate(guidePrefab, position, rotation);
            go.name = guideName;
            // She walks under our control, not as a wandering bot.
            foreach (var mb in go.GetComponents<MonoBehaviour>())
                if (mb.GetType().Name == "BotWanderer") Destroy(mb);
            var agent = go.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null) Destroy(agent);
            guide = go.transform;
            BubbleFeature.Bots.BotWanderer.RegisterObstacle(guide);   // bots walk around her
            // Her own jacket colour (warm orange), so she doesn't look like one of the bots.
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                if (r.name.Contains("Jacket"))
                {
                    var block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block, 0);
                    block.SetColor("_BaseColor", new Color(0.95f, 0.55f, 0.25f));
                    r.SetPropertyBlock(block, 0);
                }
        }

        private Coroutine walking;

        // Starts walking (and stops any walk still going, so two walks never fight each other).
        private Coroutine Walk(Vector3 target)
        {
            if (walking != null) StopCoroutine(walking);
            walking = StartCoroutine(WalkTo(target));
            return walking;
        }

        private IEnumerator WalkTo(Vector3 target)
        {
            float limit = 5f + (guide != null ? Flat(target - guide.position) / walkSpeed * 1.5f : 0f);
            float time = 0f;
            while (guide != null && (time += Time.deltaTime) < limit)
            {
                Vector3 to = target - guide.position; to.y = 0f;
                if (to.magnitude < 0.15f) break;
                Vector3 step = to.normalized * Mathf.Min(walkSpeed * Time.deltaTime, to.magnitude);
                Vector3 p = guide.position + step;
                guide.position = Ground(p);
                guide.rotation = Quaternion.RotateTowards(guide.rotation, Quaternion.LookRotation(to.normalized), 360f * Time.deltaTime);
                yield return null;
            }
            walking = null;
        }

        private void FacePlayer()
        {
            if (guide == null) return;
            Vector3 to = Flatten(PlayerFeet - guide.position);
            if (to.sqrMagnitude > 0.01f) guide.rotation = Quaternion.LookRotation(to);
        }

        private Vector3 PointInFrontOfPlayer(float distance)
        {
            Vector3 fwd = Flatten(cam.transform.forward);
            return Ground(PlayerFeet + fwd * distance);
        }

        // A free spot around the player, preferring the given angle from where they look.
        private Vector3 PointNearPlayer(float distance, float preferredAngle)
        {
            Vector3 feet = PlayerFeet, fwd = Flatten(cam.transform.forward);
            foreach (float d in new[] { distance, distance * 0.7f, distance * 0.5f })
            foreach (float a in new[] { preferredAngle, -preferredAngle, preferredAngle * 0.5f, -preferredAngle * 0.5f, 0f, 120f, -120f })
            {
                Vector3 p = feet + Quaternion.Euler(0f, a, 0f) * fwd * d;
                if (Physics.Linecast(feet + Vector3.up, p + Vector3.up, ~0, QueryTriggerInteraction.Ignore)) continue;
                Vector3 g = Ground(p);
                if (Mathf.Abs(g.y - feet.y) > 1.2f) continue;   // not down a quay or onto a roof
                return g;
            }
            return Ground(feet + fwd * 2f);
        }

        private void ShowMarker(Vector3 at)
        {
            if (marker == null)
            {
                var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                m.name = "Teleport Target";
                Destroy(m.GetComponent<Collider>());
                m.GetComponent<MeshRenderer>().sharedMaterial = markerMaterial;
                m.transform.localScale = new Vector3(1.4f, 0.02f, 1.4f);
                marker = m.transform;
            }
            marker.position = at + Vector3.up * 0.03f;
            marker.gameObject.SetActive(true);
        }

        private void HideMarker()
        {
            if (marker != null) marker.gameObject.SetActive(false);
        }

        // ---------------- Helpers ----------------

        private Vector3 PlayerFeet
        {
            get
            {
                if (cam == null) cam = Camera.main;
                return origin != null ? new Vector3(cam.transform.position.x, origin.transform.position.y, cam.transform.position.z) : cam.transform.position;
            }
        }

        // Ground height at a point: the highest walkable surface (not the guide herself, not bubbles).
        private Vector3 Ground(Vector3 p)
        {
            float best = float.MinValue;
            foreach (var hit in Physics.RaycastAll(p + Vector3.up * 3f, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (guide != null && hit.transform.IsChildOf(guide)) continue;
                var part = hit.collider.GetComponentInParent<CityPart>();
                if (part == null || !part.isGround) continue;
                best = Mathf.Max(best, hit.point.y);
            }
            return best > float.MinValue ? new Vector3(p.x, best, p.z) : p;
        }

        private static Vector3 Flatten(Vector3 v) { v.y = 0f; return v.sqrMagnitude > 0.0001f ? v.normalized : Vector3.forward; }
        private static float Flat(Vector3 v) { v.y = 0f; return v.magnitude; }
    }
}
