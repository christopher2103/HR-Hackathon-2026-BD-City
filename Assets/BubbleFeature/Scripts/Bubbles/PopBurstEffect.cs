using System;
using UnityEngine;

namespace BubbleFeature.Bubbles
{
    /// <summary>
    /// Pop effect for the personal bubble:
    /// 1. tiny bubbles burst out in all directions (and drift up a little),
    /// 2. they swirl together into one point,
    /// 3. at that moment Formed fires (the gift message appears there) and a few
    ///    leftover bubbles float up and fade. The effect removes itself afterwards.
    /// Uses one particle system; steps 2 and 3 move the particles from code.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class PopBurstEffect : MonoBehaviour
    {
        [SerializeField] private float burstSeconds = 0.6f;   // fly outward
        [SerializeField] private float gatherSeconds = 0.8f;  // swirl together
        [SerializeField] private float swirlDegrees = 200f;   // how far they spiral while gathering
        [SerializeField] private int leftoverBubbles = 12;    // float up after the message appears
        [SerializeField] private float cleanupSeconds = 3.5f; // remove the effect after the message appears

        /// <summary>Fired once, when the tiny bubbles have come together.</summary>
        public event Action Formed;

        private ParticleSystem system;
        private ParticleSystem.Particle[] particles;
        private Vector3[] gatherStart;
        private float[] startSizes;
        private Vector3 target;
        private float time;
        private bool gathering, formed;

        private void Awake()
        {
            system = GetComponent<ParticleSystem>();
            particles = new ParticleSystem.Particle[system.main.maxParticles];
            gatherStart = new Vector3[particles.Length];
            startSizes = new float[particles.Length];
        }

        /// <summary>Start the effect. The bubbles gather at gatherPoint.</summary>
        public void Play(Vector3 gatherPoint, Color tint)
        {
            target = gatherPoint;
            ParticleSystem.MainModule main = system.main;
            main.startColor = tint;
            system.Play();
        }

        private void LateUpdate()
        {
            if (formed) return;
            time += Time.deltaTime;
            if (time < burstSeconds) return; // step 1: normal particle simulation

            int count = system.GetParticles(particles);
            if (!gathering)
            {
                // Remember where each bubble was when gathering starts.
                gathering = true;
                for (int i = 0; i < count; i++)
                {
                    gatherStart[i] = particles[i].position;
                    startSizes[i] = particles[i].startSize;
                }
            }

            // Step 2: spiral every bubble from its start point into the target.
            float k = Mathf.Clamp01((time - burstSeconds) / gatherSeconds);
            float eased = k * k * (3f - 2f * k); // smoothstep
            Quaternion swirl = Quaternion.Euler(0f, swirlDegrees * eased, 0f);
            for (int i = 0; i < count; i++)
            {
                Vector3 offset = gatherStart[i] - target;
                particles[i].position = target + swirl * offset * (1f - eased);
                particles[i].velocity = Vector3.zero;
                particles[i].startSize = startSizes[i] * Mathf.Lerp(1f, 0.4f, eased);
            }
            system.SetParticles(particles, count);

            if (k >= 1f)
            {
                // Step 3: the message appears here; a few bubbles float up from it.
                formed = true;
                system.Clear();
                var emit = new ParticleSystem.EmitParams { position = target, applyShapeToPosition = true };
                system.Emit(emit, leftoverBubbles);
                Formed?.Invoke();
                Destroy(gameObject, cleanupSeconds);
            }
        }
    }
}
