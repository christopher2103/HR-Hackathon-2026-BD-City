using UnityEngine;

namespace BubbleFeature
{
    /// <summary>
    /// Small helpers to move things around the player without them seeing it:
    /// is a point in view (inside the view cone and not hidden behind a building)?
    /// and a random direction that is out of view (beside or behind the player).
    /// Uses Camera.main (the headset).
    /// </summary>
    public static class PlayerView
    {
        /// <summary>True when the player could see this point: inside the view cone and nothing in between.</summary>
        /// <param name="halfAngle">Half the view cone in degrees. Wider than the headset's view on purpose (head turns).</param>
        public static bool CanSee(Vector3 point, float halfAngle = 70f)
        {
            Camera cam = Camera.main;
            if (cam == null) return false;

            Vector3 eye = cam.transform.position;
            Vector3 toPoint = point - eye;
            float distance = toPoint.magnitude;
            if (distance < 0.01f) return true;
            if (Vector3.Angle(cam.transform.forward, toPoint) > halfAngle) return false;

            // Behind a wall or building counts as not visible.
            return !Physics.Raycast(eye, toPoint / distance, distance - 0.3f, ~0, QueryTriggerInteraction.Ignore);
        }

        /// <summary>A flat direction at least minAngle degrees away from where the player looks.</summary>
        public static Vector3 HiddenDirection(float minAngle = 110f)
        {
            Camera cam = Camera.main;
            Vector3 forward = cam != null ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up) : Vector3.forward;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            float yaw = Random.Range(minAngle, 360f - minAngle);
            return Quaternion.Euler(0f, yaw, 0f) * forward.normalized;
        }
    }
}
