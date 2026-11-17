using UnityEngine;

namespace CityFeature
{
    /// <summary>
    /// Marks a frozen (saved) piece of the city, so the teleporter and bots know what they stand on.
    /// Ground = walkable street level (aerial photo); otherwise a building.
    /// </summary>
    public class CityPart : MonoBehaviour
    {
        public bool isGround;
    }
}
