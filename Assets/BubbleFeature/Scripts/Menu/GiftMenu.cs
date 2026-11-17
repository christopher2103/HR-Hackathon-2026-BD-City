using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using BubbleFeature.Bots;
using BubbleFeature.Gifts;

namespace BubbleFeature.Menu
{
    /// <summary>One rebindable button in the Controls screen.</summary>
    [Serializable]
    public class RebindRow
    {
        public string label;
        public InputActionReference action;
        public int bindingIndex;
        [Tooltip("Strip the hand, so the new button works on both controllers (used for Pop).")]
        public bool anyHand;
    }

    /// <summary>
    /// Small world-space menu: Send gift (pick bot -> type -> item -> message -> send) and
    /// Change outfit (coming soon). Opens in front of the player with the OpenMenu action
    /// (left controller menu button / M key). Buttons are clicked with the controller ray + trigger,
    /// or with the mouse on desktop. Each screen is built from one hidden button template.
    /// </summary>
    public class GiftMenu : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Input (from BDCityControls)")]
        [SerializeField] private InputActionReference toggleAction;

        [Header("UI parts")]
        [SerializeField] private Canvas canvas;
        [SerializeField] private TMP_Text title;
        [SerializeField] private Transform buttonContainer;
        [SerializeField] private Button buttonTemplate;   // hidden; cloned for every option
        [SerializeField] private Button backButton;

        [Header("Placement")]
        [SerializeField] private float distance = 0.6f;
        [SerializeField] private float heightBelowEyes = 0.15f;

        [Header("Gift options")]
        [SerializeField] private BotSpawner bots;
        [SerializeField] private GiftSender sender;
        [SerializeField] private GiftData[] accessoryOptions;  // their item fields are used
        [SerializeField] private GiftData[] clothingOptions;
        [SerializeField] private string[] presetMessages =
        {
            "Enjoy!", "Welkom in B&D City!", "Looking good!", "This made me think of you"
        };

        [Header("Controls screen (rebinding)")]
        [SerializeField] private RebindRow[] rebindRows;
        [SerializeField] private float rebindTimeout = 8f;
        private const string PrefsKey = "BDCity.Controls";

        [Header("Selected-bot marker")]
        [SerializeField] private Material markerMaterial;
        [SerializeField] private Color markerColor = new Color(1f, 0.85f, 0.2f);
        [SerializeField] private float markerHeight = 2.4f;

        // Current choices
        private BotWanderer chosenBot;
        private GiftItemType chosenType;
        private GiftData chosenItem;
        private string chosenMessage;

        private Action onBack;
        private readonly List<GameObject> optionButtons = new List<GameObject>();
        private Transform marker;
        private InputActionRebindingExtensions.RebindingOperation rebinding;
        private float lastToggleTime = -10f;

        public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;

        private void Awake()
        {
            LoadControls(); // apply saved custom buttons before anything uses the actions
            canvas.gameObject.SetActive(false);
            buttonTemplate.gameObject.SetActive(false);
            backButton.onClick.AddListener(() => onBack?.Invoke());
        }

        private void OnEnable()
        {
            InputSystem.onDeviceChange += OnDeviceChange;
            if (toggleAction == null) return;
            toggleAction.action.performed += OnToggle;
            toggleAction.action.Enable();
        }

        private void OnDisable()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
            if (toggleAction != null) toggleAction.action.performed -= OnToggle;
        }

        private void OnToggle(InputAction.CallbackContext context)
        {
            // Ignore a second press in the same moment (e.g. the same button reported by two devices).
            if (Time.unscaledTime - lastToggleTime < 0.3f) return;
            lastToggleTime = Time.unscaledTime;

            if (IsOpen) Close(); else Open();
            Debug.Log($"[Menu] {(IsOpen ? "Opened" : "Closed")} by {context.control.path}", this);
        }

        // Debug help: when a controller connects, log which controls the actions are listening to.
        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change != InputDeviceChange.Added || !(device is UnityEngine.InputSystem.XR.XRController)) return;
            if (toggleAction == null || toggleAction.asset == null) return;
            foreach (InputAction action in toggleAction.asset)
            {
                var paths = new List<string>();
                foreach (InputControl c in action.controls) paths.Add(c.path);
                Debug.Log($"[Controls] {action.name} listens to: {(paths.Count > 0 ? string.Join(", ", paths) : "NOTHING")}", this);
            }
        }

        public void Open()
        {
            PlaceInFrontOfPlayer();
            canvas.gameObject.SetActive(true);
            ShowMain();
        }

        public void Close()
        {
            if (rebinding != null) rebinding.Cancel();
            canvas.gameObject.SetActive(false);
            SetMarker(null);
            chosenBot = null; chosenItem = null; chosenMessage = null;
        }

        private void Update()
        {
            // Keep the marker floating above the chosen bot.
            if (marker != null && marker.gameObject.activeSelf && chosenBot != null)
            {
                marker.position = chosenBot.transform.position + Vector3.up * markerHeight;
                marker.Rotate(0f, 90f * Time.deltaTime, 0f, Space.World);
            }
        }

        // ---------- Screens ----------

        private void ShowMain()
        {
            ShowScreen("B&D City", null);
            AddOption("Send gift", ShowRecipients);
            AddOption("Change outfit (soon)", null);
            AddOption("Controls", ShowControls);
            AddOption("Close", Close);
        }

        private void ShowRecipients()
        {
            SetMarker(null);
            ShowScreen("Send a gift to...", ShowMain);
            if (bots == null || bots.Bots.Count == 0) { AddOption("(no bots in this scene)", null); return; }
            foreach (BotWanderer bot in bots.Bots)
            {
                BotWanderer b = bot;
                AddOption(b.DisplayName, () => { chosenBot = b; SetMarker(b); ShowType(); }, b.Color);
            }
        }

        private void ShowType()
        {
            ShowScreen($"Gift for {chosenBot.DisplayName}", ShowRecipients);
            AddOption("Message only", () => { chosenType = GiftItemType.None; chosenItem = null; ShowMessages(ShowType); });
            AddOption("Accessory", () => ShowItems(GiftItemType.Accessory, accessoryOptions));
            AddOption("Clothing", () => ShowItems(GiftItemType.Clothing, clothingOptions));
        }

        private void ShowItems(GiftItemType type, GiftData[] options)
        {
            chosenType = type;
            ShowScreen(type == GiftItemType.Accessory ? "Pick an accessory" : "Pick a clothing piece", ShowType);
            if (options == null || options.Length == 0) { AddOption("(nothing in the catalogue yet)", null); return; }
            foreach (GiftData option in options)
            {
                if (option == null) continue;
                GiftData o = option;
                AddOption(Label(o), () => { chosenItem = o; ShowMessages(() => ShowItems(type, options)); });
            }
        }

        private void ShowMessages(Action back)
        {
            ShowScreen("Add a message", back);
            foreach (string message in presetMessages)
            {
                string m = message;
                AddOption($"\"{m}\"", () => { chosenMessage = m; ShowConfirm(() => ShowMessages(back)); });
            }
        }

        private void ShowConfirm(Action back)
        {
            string item = chosenItem != null ? Label(chosenItem) : "message only";
            ShowScreen($"To: {chosenBot.DisplayName}\n{item}\n\"{chosenMessage}\"", back);
            AddOption("Send!", Send);
        }

        private void Send()
        {
            GiftData gift = ScriptableObject.CreateInstance<GiftData>();
            gift.name = $"Sent_to_{chosenBot.DisplayName}";
            gift.sender = "You";
            gift.recipient = chosenBot.DisplayName;
            gift.message = chosenMessage;
            gift.itemType = chosenType;
            if (chosenItem != null)
            {
                gift.itemPrefab = chosenItem.itemPrefab;
                gift.itemColor = chosenItem.itemColor;
            }

            // The bubble starts at the menu, so it looks like it leaves your hands.
            if (sender != null) sender.Send(gift, chosenBot.transform, canvas.transform.position);

            string to = chosenBot.DisplayName;
            SetMarker(null);
            ShowScreen($"Sent to {to}!", null);
            AddOption("Send another", ShowRecipients);
            AddOption("Close", Close);
        }

        // ---------- Controls (rebinding) ----------

        private void ShowControls()
        {
            ShowScreen("Controls\n<size=65%>Click an action, then press the new button</size>", ShowMain);
            if (rebindRows == null) return;
            foreach (RebindRow row in rebindRows)
            {
                if (row == null || row.action == null) continue;
                RebindRow r = row;
                string current = r.action.action.GetBindingDisplayString(r.bindingIndex);
                AddOption($"{r.label}:  {current}", () => StartCoroutine(Rebind(r)));
            }
            AddOption("Reset to defaults", ResetControls);
        }

        private IEnumerator Rebind(RebindRow row)
        {
            ShowScreen($"Press the new button for\n\"{row.label}\"\n<size=65%>Esc or wait {rebindTimeout:0} s to cancel</size>", null);
            yield return new WaitForSeconds(0.5f); // time to let go of the button you clicked with

            InputAction action = row.action.action;
            bool wasEnabled = action.enabled;
            action.Disable(); // an action must be off while it is being rebound

            bool done = false;
            rebinding = action.PerformInteractiveRebinding(row.bindingIndex)
                .WithControlsExcluding("<Pointer>/position")
                .WithControlsExcluding("<Pointer>/delta")
                .WithControlsExcluding("<Mouse>/scroll")
                .WithControlsExcluding("*/isTracked")
                .WithControlsExcluding("*/userPresence")
                .WithCancelingThrough("<Keyboard>/escape")
                .WithTimeout(rebindTimeout)
                .OnMatchWaitForAnother(0.1f)
                .OnComplete(_ => done = true)
                .OnCancel(_ => done = true)
                .Start();

            while (!done) yield return null;
            rebinding.Dispose();
            rebinding = null;

            if (row.anyHand) RemoveHand(action, row.bindingIndex);
            if (wasEnabled) action.Enable();
            SaveControls();
            Debug.Log($"[Controls] {row.label} -> {action.GetBindingDisplayString(row.bindingIndex)}", this);
            if (IsOpen) ShowControls();
        }

        // "<...>{RightHand}/triggerPressed" -> "<...>/triggerPressed", so both hands can use it.
        private static void RemoveHand(InputAction action, int index)
        {
            string path = action.bindings[index].effectivePath;
            string anyHand = path.Replace("{LeftHand}", "").Replace("{RightHand}", "");
            if (anyHand != path) action.ApplyBindingOverride(index, anyHand);
        }

        private void ResetControls()
        {
            InputActionAsset asset = toggleAction != null ? toggleAction.asset : null;
            if (asset != null) asset.RemoveAllBindingOverrides();
            PlayerPrefs.DeleteKey(PrefsKey);
            ShowControls();
        }

        private void SaveControls()
        {
            InputActionAsset asset = toggleAction != null ? toggleAction.asset : null;
            if (asset == null) return;
            PlayerPrefs.SetString(PrefsKey, asset.SaveBindingOverridesAsJson());
            PlayerPrefs.Save();
        }

        private void LoadControls()
        {
            InputActionAsset asset = toggleAction != null ? toggleAction.asset : null;
            string json = PlayerPrefs.GetString(PrefsKey, "");
            if (asset != null && !string.IsNullOrEmpty(json)) asset.LoadBindingOverridesFromJson(json);
        }

        // ---------- Helpers ----------

        // Clear the old options, set the title, and show Back only when there is somewhere to go.
        private void ShowScreen(string text, Action back)
        {
            foreach (GameObject b in optionButtons) Destroy(b);
            optionButtons.Clear();
            title.text = text;
            onBack = back;
            backButton.gameObject.SetActive(back != null);
        }

        private void AddOption(string label, Action onClick, Color? tint = null)
        {
            Button button = Instantiate(buttonTemplate, buttonContainer);
            button.gameObject.SetActive(true);
            button.GetComponentInChildren<TMP_Text>().text = label;
            button.interactable = onClick != null;
            if (onClick != null) button.onClick.AddListener(() => onClick());
            if (tint.HasValue)
            {
                ColorBlock colors = button.colors;
                colors.normalColor = tint.Value;
                colors.highlightedColor = Color.Lerp(tint.Value, Color.white, 0.3f);
                colors.selectedColor = tint.Value;
                button.colors = colors;
                button.GetComponentInChildren<TMP_Text>().color = new Color(0.08f, 0.08f, 0.1f); // dark text on light colours
            }
            optionButtons.Add(button.gameObject);
        }

        private static string Label(GiftData item) =>
            item.name.Replace("Item_", "").Replace("Gift_", "").Replace("_", " ");

        private void PlaceInFrontOfPlayer()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform head = cam.transform;
            Vector3 forward = head.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            Vector3 position = head.position + forward.normalized * distance + Vector3.down * heightBelowEyes;

            canvas.transform.position = position;
            canvas.transform.rotation = Quaternion.LookRotation(position - head.position); // tilted toward your eyes
            canvas.worldCamera = cam; // needed for mouse clicks on desktop
        }

        private void SetMarker(BotWanderer bot)
        {
            if (bot == null) { if (marker != null) marker.gameObject.SetActive(false); return; }
            if (marker == null)
            {
                GameObject m = GameObject.CreatePrimitive(PrimitiveType.Cube);
                m.name = "Selected Bot Marker";
                Destroy(m.GetComponent<Collider>());
                m.transform.localScale = Vector3.one * 0.18f;
                m.transform.rotation = Quaternion.Euler(45f, 0f, 45f); // diamond shape
                var r = m.GetComponent<MeshRenderer>();
                if (markerMaterial != null) r.sharedMaterial = markerMaterial;
                var block = new MaterialPropertyBlock();
                block.SetColor(BaseColorId, markerColor);
                r.SetPropertyBlock(block);
                marker = m.transform;
            }
            marker.gameObject.SetActive(true);
        }
    }
}
