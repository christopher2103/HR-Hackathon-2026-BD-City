using System.Collections;
using BubbleFeature.Bubbles;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace CityFeature
{
    /// <summary>
    /// Running the demo for many visitors.
    /// Reset for the next visitor (tutorial again, back to Wijnhaven 107, menu closed, bubbles cleared):
    ///   - keyboard F1 (Quest Link / editor), or
    ///   - on the headset: hold BOTH grips + BOTH triggers for 2 seconds (nobody does that by accident).
    /// Other keyboard keys: F2 send a gift bubble, F3 skip the tutorial, 1-4 travel (on the teleporter), G bubble.
    /// </summary>
    public class DemoOperator : MonoBehaviour
    {
        [SerializeField] private NeighbourhoodTeleporter teleporter;
        [SerializeField] private TutorialGuide tutorial;
        [SerializeField] private GiftBubbleSpawner giftBubbles;
        [SerializeField] private BubbleFeature.Menu.GiftMenu menu;
        [SerializeField] private ArrivalTitle title;
        [SerializeField] private int startArea = 0;
        [SerializeField] private float holdSeconds = 2f;

        private float held;

        private void Update()
        {
            Keyboard k = Keyboard.current;
            if (k != null)
            {
                if (k.f1Key.wasPressedThisFrame) ResetForNextVisitor();
                if (k.f2Key.wasPressedThisFrame && giftBubbles != null) giftBubbles.Spawn();
                if (k.f3Key.wasPressedThisFrame && tutorial != null && tutorial.IsRunning) tutorial.Skip();
            }

            // Both grips + both triggers held.
            bool all = Squeezed(XRController.leftHand) && Squeezed(XRController.rightHand);
            held = all ? held + Time.unscaledDeltaTime : 0f;
            if (held >= holdSeconds) { held = float.NegativeInfinity; ResetForNextVisitor(); }
            if (!all && float.IsNegativeInfinity(held)) held = 0f;
        }

        private static bool Squeezed(XRController c)
        {
            if (c == null) return false;
            float grip = (c.TryGetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("grip")?.ReadValue()) ?? 0f;
            float trig = (c.TryGetChildControl<UnityEngine.InputSystem.Controls.AxisControl>("trigger")?.ReadValue()) ?? 0f;
            return grip > 0.8f && trig > 0.8f;
        }

        [ContextMenu("Reset for next visitor")]
        public void ResetForNextVisitor()
        {
            if (menu != null && menu.IsOpen) menu.Close();
            foreach (var b in FindObjectsByType<GiftBubbleFlight>(FindObjectsInactive.Exclude)) Destroy(b.gameObject);
            foreach (var r in FindObjectsByType<BubbleFeature.Gifts.GiftReveal>(FindObjectsInactive.Exclude)) Destroy(r.gameObject);
            if (tutorial != null) tutorial.ResetProgress();
            Debug.Log("[Demo] Reset for the next visitor.", this);
            StartCoroutine(Restart());
        }

        private IEnumerator Restart()
        {
            if (title != null) title.Toast("Ready for the next visitor");
            yield return new WaitForSecondsRealtime(1.2f);
            if (teleporter != null) teleporter.GoTo(startArea);   // the tutorial starts by itself on arrival
        }
    }
}
