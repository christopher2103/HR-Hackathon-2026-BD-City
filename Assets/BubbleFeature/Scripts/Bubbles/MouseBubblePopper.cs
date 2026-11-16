using UnityEngine;
using UnityEngine.InputSystem;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Desktop popping: left-click a bubble in the Game view.
    /// Put this on any one object in the scene (e.g. Bubble Spawner).
    /// Does nothing on Quest (no mouse), so it can stay in the build.
    /// </summary>
    public class MouseBubblePopper : MonoBehaviour
    {
        [SerializeField] private float maxDistance = 50f;

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, ~0, QueryTriggerInteraction.Collide))
            {
                GiftBubble bubble = hit.collider.GetComponentInParent<GiftBubble>();
                if (bubble != null) bubble.Pop();
            }
        }
    }
}
