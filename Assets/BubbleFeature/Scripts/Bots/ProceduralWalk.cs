using System.Collections.Generic;
using UnityEngine;

namespace BubbleFeature.Bots
{
    /// <summary>
    /// Simple walk animation in code for a rigged human without animation clips:
    /// swings the legs and arms, bends the knees and bobs the hips, driven by how fast the object actually moves.
    /// Also lowers the arms from the T-pose. Rotations are applied around the character's own right axis,
    /// so it works no matter how the bones are oriented. Cheap: a handful of bone rotations per frame.
    /// Bones come from a Humanoid Animator if the model has one, otherwise from the MakeHuman
    /// "Game_engine" names (thigh_l, calf_l, upperarm_l, ...).
    /// </summary>
    public class ProceduralWalk : MonoBehaviour
    {
        [Header("Walk")]
        [SerializeField] private float stepLength = 0.65f;     // metres per step
        [SerializeField] private float legSwing = 25f;         // degrees forward/back
        [SerializeField] private float kneeBend = 40f;
        [SerializeField] private float armSwing = 20f;
        [SerializeField] private float hipBob = 0.03f;          // metres
        [SerializeField] private float fullSpeed = 1.0f;        // m/s where the full swing is reached

        [Header("Arms down from T-pose")]
        [Tooltip("Degrees below horizontal the upper arms hang (90 = straight down).")]
        [SerializeField] private float armDownAngle = 75f;
        [SerializeField] private float elbowBend = 15f;

        private Transform pelvis, thighL, thighR, calfL, calfR, upperL, upperR, lowerL, lowerR;
        private readonly Dictionary<Transform, Quaternion> rest = new Dictionary<Transform, Quaternion>();
        private Quaternion armFixL, armFixR;   // in the character's own space
        private Vector3 pelvisRest;
        private Vector3 lastPosition;
        private float phase, speed;

        private void Awake()
        {
            Animator animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                pelvis = animator.GetBoneTransform(HumanBodyBones.Hips);
                thighL = animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg); thighR = animator.GetBoneTransform(HumanBodyBones.RightUpperLeg);
                calfL = animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg); calfR = animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                upperL = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm); upperR = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                lowerL = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm); lowerR = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                animator.enabled = false; // no animation clips: we drive the bones ourselves
            }
            else
            {
                pelvis = Find("pelvis");
                thighL = Find("thigh_l"); thighR = Find("thigh_r");
                calfL = Find("calf_l"); calfR = Find("calf_r");
                upperL = Find("upperarm_l"); upperR = Find("upperarm_r");
                lowerL = Find("lowerarm_l"); lowerR = Find("lowerarm_r");
            }

            foreach (Transform t in new[] { pelvis, thighL, thighR, calfL, calfR, upperL, upperR, lowerL, lowerR })
                if (t != null) rest[t] = t.localRotation;
            if (pelvis != null) pelvisRest = pelvis.localPosition;

            armFixL = ArmDownFix(upperL, lowerL);
            armFixR = ArmDownFix(upperR, lowerR);
            lastPosition = transform.position;
        }

        // Rotation (in character space) that turns the upper arm from its rest direction to hanging down.
        private Quaternion ArmDownFix(Transform upper, Transform lower)
        {
            if (upper == null || lower == null) return Quaternion.identity;
            Vector3 dir = transform.InverseTransformDirection(lower.position - upper.position).normalized;
            float side = Mathf.Sign(dir.x == 0f ? 1f : dir.x);
            float a = armDownAngle * Mathf.Deg2Rad;
            Vector3 target = new Vector3(side * Mathf.Cos(a), -Mathf.Sin(a), 0f);
            return Quaternion.FromToRotation(dir, target);
        }

        private void LateUpdate()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 delta = transform.position - lastPosition;
            delta.y = 0f;
            lastPosition = transform.position;
            speed = Mathf.Lerp(speed, delta.magnitude / dt, 1f - Mathf.Exp(-8f * dt));

            float amount = Mathf.Clamp01(speed / fullSpeed);
            phase += speed / stepLength * Mathf.PI * dt;
            float s = Mathf.Sin(phase);

            foreach (KeyValuePair<Transform, Quaternion> pair in rest) pair.Key.localRotation = pair.Value;

            Vector3 right = transform.right;
            Quaternion toWorld = transform.rotation;

            // Legs: negative angle around "right" swings a hanging leg forward.
            Rotate(thighL, -legSwing * s * amount, right);
            Rotate(thighR, legSwing * s * amount, right);
            Rotate(calfL, kneeBend * amount * Mathf.Max(0f, Mathf.Cos(phase)), right);
            Rotate(calfR, kneeBend * amount * Mathf.Max(0f, -Mathf.Cos(phase)), right);

            // Arms: first hang them down, then swing opposite to the legs.
            if (upperL != null) upperL.rotation = toWorld * armFixL * Quaternion.Inverse(toWorld) * upperL.rotation;
            if (upperR != null) upperR.rotation = toWorld * armFixR * Quaternion.Inverse(toWorld) * upperR.rotation;
            Rotate(upperL, armSwing * s * amount, right);
            Rotate(upperR, -armSwing * s * amount, right);
            Rotate(lowerL, -elbowBend, right);
            Rotate(lowerR, -elbowBend, right);

            if (pelvis != null)
            {
                pelvis.localPosition = pelvisRest;
                pelvis.position += Vector3.up * (hipBob * amount * (Mathf.Abs(Mathf.Cos(phase)) - 0.5f));
            }
        }

        private static void Rotate(Transform bone, float degrees, Vector3 axis)
        {
            if (bone != null) bone.rotation = Quaternion.AngleAxis(degrees, axis) * bone.rotation;
        }

        private Transform Find(string boneName)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
                if (t.name == boneName) return t;
            return null;
        }
    }
}
