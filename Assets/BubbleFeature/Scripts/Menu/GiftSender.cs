using UnityEngine;
using BubbleFeature.Bubbles;
using BubbleFeature.Gifts;

namespace BubbleFeature.Menu
{
    /// <summary>
    /// Sends a gift from the player to someone (a bot): a bubble flies from a start point
    /// to the recipient's head, pops there, and the gift reveal appears above them.
    /// Reuses the ambient bubble movement and the normal GiftReveal.
    /// </summary>
    public class GiftSender : MonoBehaviour
    {
        [SerializeField] private AmbientBubbleMover bubblePrefab;
        [SerializeField] private GiftReveal revealPrefab;
        [SerializeField] private float targetHeadHeight = 2.0f;   // pop just above the head
        [SerializeField] private float revealHeightOffset = 0.1f;
        [SerializeField] private bool showGlow = true;            // sent bubbles glow like personal ones
        [SerializeField] private float speed = 2.5f;              // faster than ambient bubbles, so it catches a walking bot

        public void Send(GiftData gift, Transform recipient, Vector3 from)
        {
            if (bubblePrefab == null || recipient == null) { Debug.LogWarning("[Send] Missing bubble prefab or recipient.", this); return; }

            AmbientBubbleMover bubble = Instantiate(bubblePrefab, from, Quaternion.identity);
            Transform glow = bubble.transform.Find("Core/CoreGlow");
            if (glow != null) glow.gameObject.SetActive(showGlow);

            bubble.Speed = speed;
            bubble.Launch(from, recipient, Vector3.up * targetHeadHeight);
            bubble.Finished += b => ShowReveal(gift, b.transform.position);

            Debug.Log($"[Send] {gift.sender} -> {gift.recipient}: {gift.itemType} \"{gift.message}\"", this);
        }

        private void ShowReveal(GiftData gift, Vector3 at)
        {
            if (revealPrefab == null) return;
            GiftReveal reveal = Instantiate(revealPrefab, at + Vector3.up * revealHeightOffset, Quaternion.identity);
            reveal.Show(gift, Camera.main != null ? Camera.main.transform : null);
        }
    }
}
