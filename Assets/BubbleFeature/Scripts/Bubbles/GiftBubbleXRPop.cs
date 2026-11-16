using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// VR popping: point a controller at the bubble and press the trigger OR the grip.
    /// - Grip  = XRI "select"   -> handled by the selectEntered event.
    /// - Trigger = XRI "activate" -> XRI only sends activate to selected objects,
    ///   so we read the trigger of the controller that is pointing at (hovering) the bubble.
    /// Adding this component also adds an XR Simple Interactable (which uses the bubble's collider).
    /// </summary>
    [RequireComponent(typeof(GiftBubble), typeof(XRSimpleInteractable))]
    public class GiftBubbleXRPop : MonoBehaviour
    {
        private GiftBubble bubble;
        private XRSimpleInteractable interactable;

        private void Awake()
        {
            bubble = GetComponent<GiftBubble>();
            interactable = GetComponent<XRSimpleInteractable>();
        }

        private void OnEnable()  => interactable.selectEntered.AddListener(OnSelectEntered);
        private void OnDisable() => interactable.selectEntered.RemoveListener(OnSelectEntered);

        // Grip while pointing at the bubble.
        private void OnSelectEntered(SelectEnterEventArgs args) => bubble.Pop();

        // Trigger while pointing at the bubble.
        private void Update()
        {
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
