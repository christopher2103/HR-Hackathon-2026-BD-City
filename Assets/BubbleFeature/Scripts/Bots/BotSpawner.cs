using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using BubbleFeature.Bubbles;

namespace BubbleFeature.Bots
{
    /// <summary>
    /// Spawns a few placeholder bots on the NavMesh, each with its own name and colour,
    /// and hands them to the ambient bubbles as targets.
    /// Builds the NavMesh at start if the surface has none baked (fine for the flat test scene).
    /// </summary>
    public class BotSpawner : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Bots")]
        [SerializeField] private BotWanderer botPrefab;
        [SerializeField] private string[] botNames = { "Sanne", "Daan", "Noor", "Milan", "Fatima" };
        [SerializeField] private float spawnRadius = 8f;
        [SerializeField] private float minDistanceFromPlayer = 2.5f;

        [Header("NavMesh")]
        [Tooltip("Ground with a NavMeshSurface. Built at start when it has no baked data.")]
        [SerializeField] private NavMeshSurface surface;

        [Header("Hook-ups")]
        [Tooltip("Ambient bubbles will also fly to these bots.")]
        [SerializeField] private AmbientBubbleSpawner ambientBubbles;

        private readonly List<BotWanderer> bots = new List<BotWanderer>();

        /// <summary>The bots in the scene (used by the gift menu to pick a recipient).</summary>
        public IReadOnlyList<BotWanderer> Bots => bots;

        private void Awake()
        {
            // Build the NavMesh before the bots (and AmbientBubbleSpawner.Start) need it.
            if (surface != null && surface.navMeshData == null) surface.BuildNavMesh();
        }

        private void Start()
        {
            if (botPrefab == null) { Debug.LogWarning("[Bots] No bot prefab assigned.", this); return; }

            var spawned = new Transform[botNames.Length];
            for (int i = 0; i < botNames.Length; i++)
            {
                Vector3 position = RandomPointOnNavMesh();
                BotWanderer bot = Instantiate(botPrefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform);
                bot.SetName(botNames[i]);
                Color color = Color.HSVToRGB((float)i / botNames.Length, 0.55f, 0.9f);
                bot.SetColor(color);
                Tint(bot.gameObject, color);
                bots.Add(bot);
                spawned[i] = bot.transform;
            }

            if (ambientBubbles != null) ambientBubbles.SetTargets(spawned);
            Debug.Log($"[Bots] Spawned {spawned.Length} bots.", this);
        }

        private Vector3 RandomPointOnNavMesh()
        {
            Vector3 player = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            for (int i = 0; i < 20; i++)
            {
                Vector2 offset = Random.insideUnitCircle * spawnRadius;
                Vector3 candidate = transform.position + new Vector3(offset.x, 0f, offset.y);
                Vector2 flat = new Vector2(candidate.x - player.x, candidate.z - player.z);
                if (flat.magnitude < minDistanceFromPlayer) continue;
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas)) return hit.position;
            }
            return transform.position + Vector3.forward * minDistanceFromPlayer;
        }

        // Colour only the body parts marked by name ("Body", "Head"); keeps the visor dark.
        private static void Tint(GameObject bot, Color color)
        {
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in bot.GetComponentsInChildren<Renderer>())
            {
                if (r.name != "Body" && r.name != "Head") continue;
                r.GetPropertyBlock(block);
                block.SetColor(BaseColorId, color);
                r.SetPropertyBlock(block);
            }
        }
    }
}
