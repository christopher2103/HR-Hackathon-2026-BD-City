using UnityEngine;

namespace BubbleFeature.Gifts
{
    /// <summary>What kind of item a gift contains. None = message-only gift.</summary>
    public enum GiftItemType
    {
        None,
        Accessory,
        Clothing
    }

    /// <summary>
    /// One gift, stored as an asset so it can be created in the Editor
    /// (right-click > Create > B&D City > Gift) and reused later by the gift menu.
    /// Placeholder demo data only: no backend, no accounts.
    /// </summary>
    [CreateAssetMenu(fileName = "Gift_New", menuName = "B&D City/Gift")]
    public class GiftData : ScriptableObject
    {
        [Header("Who")]
        public string sender = "Anonymous";
        public string recipient = "You";

        [Header("Message (shown when the bubble pops)")]
        [TextArea(2, 5)]
        public string message = "Enjoy your gift!";

        [Header("Optional item (None = message-only gift)")]
        public GiftItemType itemType = GiftItemType.None;

        [Tooltip("Placeholder model shown on pop. Leave empty to use a default shape for the item type (stage 4).")]
        public GameObject itemPrefab;

        [Tooltip("Colour for the placeholder item.")]
        public Color itemColor = Color.white;

        /// <summary>True when the gift contains an accessory or clothing piece.</summary>
        public bool HasItem => itemType != GiftItemType.None;
    }
}
