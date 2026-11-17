using System.Collections;
using BubbleFeature.Bots;
using BubbleFeature.Bubbles;
using UnityEngine;

namespace CityFeature
{
    /// <summary>
    /// Brings a neighbourhood to life when you arrive there:
    /// bots spawn around you and wander (behind the loading screen), the ambient bubbles restart around you,
    /// and once the loading screen is gone your personal gift bubble flies in.
    /// </summary>
    public class CityLife : MonoBehaviour
    {
        [SerializeField] private NeighbourhoodTeleporter teleporter;
        [SerializeField] private BotSpawner bots;
        [SerializeField] private AmbientBubbleSpawner ambientBubbles;
        [SerializeField] private GiftBubbleSpawner giftBubbles;
        [Tooltip("Seconds after the screen fades in before your gift bubble starts flying in.")]
        [SerializeField] private float giftBubbleDelay = 1.5f;
        [Tooltip("Off = only the first neighbourhood you arrive in gets a gift bubble.")]
        [SerializeField] private bool giftBubbleEveryArrival = true;

        private bool gaveFirstGift;

        private void OnEnable()
        {
            if (teleporter == null) return;
            teleporter.Arrived += OnArrived;
            teleporter.Revealed += OnRevealed;
        }

        private void OnDisable()
        {
            if (teleporter == null) return;
            teleporter.Arrived -= OnArrived;
            teleporter.Revealed -= OnRevealed;
        }

        private void OnArrived(string neighbourhood)
        {
            StopAllCoroutines(); // a pending gift bubble from the previous place
            if (bots != null) bots.SpawnAround(teleporter.PlayerFeet);
            if (ambientBubbles != null) ambientBubbles.Restart();
        }

        private void OnRevealed(string neighbourhood)
        {
            if (giftBubbles == null) return;
            if (!giftBubbleEveryArrival && gaveFirstGift) return;
            gaveFirstGift = true;
            StartCoroutine(SpawnGiftLater());
        }

        private IEnumerator SpawnGiftLater()
        {
            yield return new WaitForSeconds(giftBubbleDelay);
            giftBubbles.Spawn();
        }
    }
}
