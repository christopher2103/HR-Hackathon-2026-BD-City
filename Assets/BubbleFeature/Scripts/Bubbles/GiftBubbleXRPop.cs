using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// VR popping: point a controller at the bubble and press the Pop button.
    /// Pop comes from BDCityControls (trigger or grip by default, rebindable in the in-game Controls menu).
    /// Only the hand that is pointing at the bubble can pop it.
    /// Counts as "pointing at it" when the hand's ray hovers the bubble (or did so a moment ago), OR when the
    /// controller simply points close enough at it (aim assist): so a tree, lamp post or stand that happens to be
    /// in front of the ray, or a fast-moving bubble, doesn't stop the pop.
    /// Adding this component also adds an XR Simple Interactable (which uses the bubble's collider for the ray).
    /// </summary>
    [RequireComponent(typeof(GiftBubble), typeof(XRSimpleInteractable))]
    public class GiftBubbleXRPop : MonoBehaviour
    {
        [Tooltip("Pop action from BDCityControls. Empty = old behaviour (trigger = activate, grip = select).")]
        [SerializeField] private InputActionReference popAction;

        [Tooltip("Still counts as 'pointing at it' this long after the ray slipped off (a moving bubble is hard to keep aimed at).")]
        [SerializeField] private float aimGraceSeconds = 0.35f;

        [Tooltip("Aim assist: the controller points within this many degrees of the bubble centre.")]
        [SerializeField] private float aimAssistDegrees = 9f;
        [SerializeField] private float aimAssistMaxDistance = 6f;

        private GiftBubble bubble;
        private XRSimpleInteractable interactable;
        private float lastHoverLeft = -10f, lastHoverRight = -10f;
        private static NearFarInteractor[] hands;

        private void Awake()
        {
            bubble = GetComponent<GiftBubble>();
            interactable = GetComponent<XRSimpleInteractable>();
        }

        private void OnEnable()
        {
            if (popAction != null)
            {
                popAction.action.performed += OnPop;
                popAction.action.Enable();
            }
            else
            {
                interactable.selectEntered.AddListener(OnSelectEntered);
            }
        }

        private void OnDisable()
        {
            if (popAction != null) popAction.action.performed -= OnPop;
            else interactable.selectEntered.RemoveListener(OnSelectEntered);
        }

        // Pop button pressed on some device: pop if that hand is (or just was) pointing at this bubble.
        private void OnPop(InputAction.CallbackContext context)
        {
            if (bubble == null || bubble.IsPopped) return;
            RememberHover();

            InteractorHandedness hand = HandOf(context.control.device);
            float sinceLeft = Time.time - lastHoverLeft;
            float sinceRight = Time.time - lastHoverRight;
            bool aimed = hand == InteractorHandedness.Left  ? sinceLeft  <= aimGraceSeconds
                       : hand == InteractorHandedness.Right ? sinceRight <= aimGraceSeconds
                       : Mathf.Min(sinceLeft, sinceRight) <= aimGraceSeconds; // key without a hand

            if (!aimed && PointsAt(hand)) { aimed = true; Debug.Log("[Pop] Aim assist: popped by pointing close enough.", this); }

            if (aimed) bubble.Pop();
            else Debug.Log($"[Pop] {context.control.path} pressed, but that hand wasn't pointing at the bubble.", this);
        }

        // Is the controller of this hand (either hand when unknown) pointing close enough at the bubble?
        private bool PointsAt(InteractorHandedness hand)
        {
            if (hands == null || hands.Length == 0 || hands[0] == null)
                hands = FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Exclude);
            foreach (var h in hands)
            {
                if (h == null || !h.isActiveAndEnabled) continue;
                if (hand != InteractorHandedness.None && h.handedness != hand) continue;
                Transform origin = h.curveOrigin != null ? h.curveOrigin : h.transform;
                Vector3 to = transform.position - origin.position;
                if (to.magnitude > aimAssistMaxDistance) continue;
                if (Vector3.Angle(origin.forward, to) <= aimAssistDegrees) return true;
            }
            return false;
        }

        // Track when each hand last pointed at the bubble.
        private void RememberHover()
        {
            if (!interactable.isHovered) return;
            foreach (var interactor in interactable.interactorsHovering)
            {
                // An interactor without a hand setting counts for both hands.
                if (interactor.handedness != InteractorHandedness.Right) lastHoverLeft = Time.time;
                if (interactor.handedness != InteractorHandedness.Left) lastHoverRight = Time.time;
            }
        }

        private static InteractorHandedness HandOf(InputDevice device)
        {
            foreach (var usage in device.usages)
            {
                if (usage == UnityEngine.InputSystem.CommonUsages.LeftHand) return InteractorHandedness.Left;
                if (usage == UnityEngine.InputSystem.CommonUsages.RightHand) return InteractorHandedness.Right;
            }
            return InteractorHandedness.None;
        }

        // ----- Fallback when no Pop action is assigned -----

        private void OnSelectEntered(SelectEnterEventArgs args) => bubble.Pop();

        private void Update()
        {
            if (popAction != null) { RememberHover(); return; }
            if (bubble.IsPopped || !interactable.isHovered) return;

            var hovering = interactable.interactorsHovering;
            for (int i = 0; i < hovering.Count; i++)
            {
                if (hovering[i] is XRBaseInputInteractor controller &&
                    controller.activateInput.ReadWasPerformedThisFrame())
                {
                    bubble.Pop();
                    return;
                }
            }
        }
    }
}
