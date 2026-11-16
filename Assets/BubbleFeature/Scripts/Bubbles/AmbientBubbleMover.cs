using System;
using System.Collections;
using UnityEngine;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// An ambient bubble (someone else's gift): flies from A to B with a gentle sway,
    /// swells briefly on arrival and disappears. Not poppable by the player.
    /// The target can be a point or a Transform (later: a bot), so it follows a moving bot.
    /// Pure math in Update: no physics, no colliders (cheap on Quest).
    /// </summary>
    public class AmbientBubbleMover : MonoBehaviour
    {
        [Header("Flight")]
        [SerializeField] private float speed = 1.2f;          // metres per second
        [SerializeField] private float swayAmount = 0.3f;     // metres of side-to-side drift
        [SerializeField] private float swaySpeed = 1.2f;
        [SerializeField] private float arriveDistance = 0.1f;

        [Header("Arrival pop")]
        [SerializeField] private float popSwellScale = 1.3f;
        [SerializeField] private float popDuration = 0.15f;

        private Vector3 basePosition;   // position along the straight path (sway is added on top)
        private Vector3 targetPoint;
        private Transform targetTransform;
        private Vector3 targetOffset;
        private float phase;
        private bool arrived;

        /// <summary>Fired just before the bubble is removed, so the spawner can make a new one.</summary>
        public event Action<AmbientBubbleMover> Finished;

        private Vector3 Target => targetTransform != null ? targetTransform.position + targetOffset : targetPoint;

        /// <summary>Fly to a fixed point.</summary>
        public void Launch(Vector3 from, Vector3 to)
        {
            basePosition = from;
            transform.position = from;
            targetPoint = to;
            targetTransform = null;
            phase = UnityEngine.Random.Range(0f, 10f);
        }

        /// <summary>Fly to a (moving) Transform, e.g. a bot's position plus a head-height offset.</summary>
        public void Launch(Vector3 from, Transform target, Vector3 offset)
        {
            Launch(from, target.position + offset);
            targetTransform = target;
            targetOffset = offset;
        }

        private void Update()
        {
            if (arrived) return;

            Vector3 target = Target;
            basePosition = Vector3.MoveTowards(basePosition, target, speed * Time.deltaTime);
            float distance = Vector3.Distance(basePosition, target);

            // Sway sideways and a little up/down; fade it out close to the target so it lands exactly.
            Vector3 direction = (target - basePosition).normalized;
            Vector3 side = Vector3.Cross(direction, Vector3.up);
            if (side.sqrMagnitude < 0.001f) side = Vector3.right;
            float t = Time.time * swaySpeed + phase;
            float fade = Mathf.Clamp01(distance / 1.5f);
            Vector3 sway = (side.normalized * Mathf.Sin(t) + Vector3.up * Mathf.Cos(t * 0.7f) * 0.5f) * swayAmount * fade;

            transform.position = basePosition + sway;

            if (distance <= arriveDistance)
            {
                arrived = true;
                StartCoroutine(PopRoutine());
            }
        }

        private IEnumerator PopRoutine()
        {
            Vector3 startScale = transform.localScale;
            float time = 0f;
            while (time < popDuration)
            {
                time += Time.deltaTime;
                transform.localScale = startScale * Mathf.Lerp(1f, popSwellScale, time / popDuration);
                yield return null;
            }

            Finished?.Invoke(this);
            Destroy(gameObject);
        }
    }
}
