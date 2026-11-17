using UnityEngine;

namespace BubbleFeature.Gifts
{
    /// <summary>
    /// Rocks a revealed item back and forth around one axis (default Z), on top of the
    /// GiftReveal's Y spin, so the top of e.g. a ring comes into view. Put it on the item prefab.
    /// </summary>
    public class ItemTumble : MonoBehaviour
    {
        [SerializeField] private Vector3 axis = Vector3.forward; // local Z
        [SerializeField] private float maxAngle = 60f;            // degrees each way
        [SerializeField] private float secondsPerRock = 4f;       // one full back-and-forth

        private Quaternion baseRotation;
        private float time;

        // Start runs after GiftReveal has placed and scaled the item.
        private void Start() => baseRotation = transform.localRotation;

        private void Update()
        {
            time += Time.deltaTime;
            float angle = Mathf.Sin(time * Mathf.PI * 2f / Mathf.Max(0.1f, secondsPerRock)) * maxAngle;
            transform.localRotation = baseRotation * Quaternion.AngleAxis(angle, axis);
        }
    }
}
