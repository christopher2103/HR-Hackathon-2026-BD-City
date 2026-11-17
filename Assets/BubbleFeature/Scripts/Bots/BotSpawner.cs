using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;
using BubbleFeature.Bubbles;

namespace BubbleFeature.Bots
{
    /// <summary>
    /// Spawns a few placeholder bots, each with its own name and colour,
    /// and hands them to the ambient bubbles as targets.
    /// Test scene: on the NavMesh (built at start if the surface has none baked).
    /// City: no NavMesh; call SpawnAround(point) after arriving somewhere, the bots then walk "simple".
    /// </summary>
    public class BotSpawner : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [Header("Bots")]
        [SerializeField] private BotWanderer botPrefab;
        [SerializeField] private string[] botNames = { "Sanne", "Daan", "Noor", "Milan", "Fatima" };
        [SerializeField] private float spawnRadius = 8f;
        [SerializeField] private float minDistanceFromPlayer = 2.5f;
        [SerializeField] private float wanderRadius = 6f;
        [Tooltip("Human bots: one shirt material per bot (same order/colours as the names). Empty = tint the capsule bot.")]
        [SerializeField] private Material[] shirtMaterials;
        [Tooltip("Renderer that wears the shirt (its name contains this).")]
        [SerializeField] private string shirtRendererName = "casualsuit";

        [Header("Female bots (optional)")]
        [Tooltip("Used for the names below instead of Bot Prefab.")]
        [SerializeField] private BotWanderer femaleBotPrefab;
        [SerializeField] private string[] femaleNames = { "Sanne", "Noor", "Fatima" };
        [Tooltip("Renderer whose first material gets the bot colour (her jacket).")]
        [SerializeField] private string femaleTopRendererName = "Jacket";
        [Tooltip("Off in the city: SpawnAround() is called when you arrive in a neighbourhood.")]
        [SerializeField] private bool spawnOnStart = true;

        [Header("NavMesh")]
        [Tooltip("Off = simple walking that follows the ground with raycasts (for the streamed city).")]
        [SerializeField] private bool useNavMesh = true;
        [Tooltip("Ground with a NavMeshSurface. Built at start when it has no baked data.")]
        [SerializeField] private NavMeshSurface surface;

        [Header("Keep bots around the player")]
        [Tooltip("Bots that end up far away are moved near the player again, but only while nobody can see them (out of view or behind a building), at a spot that is out of view too.")]
        [SerializeField] private bool keepNearPlayer = true;
        [SerializeField] private float maxDistanceFromPlayer = 18f;
        [SerializeField] private Vector2 relocateDistance = new Vector2(6f, 12f);
        [Tooltip("Where the player stands (e.g. the XR Origin). Empty = the camera, 1.6 m lower.")]
        [SerializeField] private Transform playerFeet;
        [SerializeField] private float checkInterval = 0.5f;

        [Header("Hook-ups")]
        [Tooltip("Ambient bubbles will also fly to these bots.")]
        [SerializeField] private AmbientBubbleSpawner ambientBubbles;

        private readonly List<BotWanderer> bots = new List<BotWanderer>();
        private float nextCheck;

        /// <summary>The bots in the scene (used by the gift menu to pick a recipient).</summary>
        public IReadOnlyList<BotWanderer> Bots => bots;

        private void Awake()
        {
            // Build the NavMesh before the bots (and AmbientBubbleSpawner.Start) need it.
            if (useNavMesh && surface != null && surface.navMeshData == null) surface.BuildNavMesh();
        }

        private void Start()
        {
            if (spawnOnStart) SpawnAround(transform.position);
        }

        private void Update()
        {
            if (!keepNearPlayer || bots.Count == 0 || Time.time < nextCheck) return;
            nextCheck = Time.time + checkInterval;

            if (!TryGetFeet(out Vector3 feet)) return;
            foreach (BotWanderer bot in bots)
            {
                if (bot == null) continue;
                Vector3 p = bot.transform.position;
                float flatDistance = new Vector2(p.x - feet.x, p.z - feet.z).magnitude;
                bool offLevel = Mathf.Abs(p.y - feet.y) > 2f; // e.g. ended up down in the harbour after the ground got more detailed
                if (flatDistance <= maxDistanceFromPlayer && !offLevel) continue;
                if (PlayerView.CanSee(p + Vector3.up * 1.7f) || PlayerView.CanSee(p + Vector3.up * 0.3f)) continue; // never jump in view

                if (TryFindHiddenSpot(feet, out Vector3 spot))
                    bot.Relocate(spot);
            }
        }

        private bool NearOtherBot(Vector3 spot)
        {
            foreach (BotWanderer other in bots)
                if (other != null && (other.transform.position - spot).sqrMagnitude < 2f * 2f) return true;
            return false;
        }

        private bool TryGetFeet(out Vector3 feet)
        {
            if (playerFeet != null) { feet = playerFeet.position; return true; }
            Camera cam = Camera.main;
            feet = cam != null ? cam.transform.position + Vector3.down * 1.6f : Vector3.zero;
            return cam != null;
        }

        // A street spot near the player that the player cannot see right now.
        private bool TryFindHiddenSpot(Vector3 feet, out Vector3 spot)
        {
            for (int i = 0; i < 20; i++)
            {
                Vector3 candidate = feet + PlayerView.HiddenDirection() * Random.Range(relocateDistance.x, relocateDistance.y);
                bool onGround;
                if (useNavMesh)
                {
                    onGround = NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas);
                    spot = onGround ? hit.position : default;
                }
                else
                {
                    onGround = StreetAt(candidate, feet.y, out spot);
                }
                if (onGround && !PlayerView.CanSee(spot + Vector3.up * 1.7f) && !NearOtherBot(spot)) return true;
            }
            spot = default;
            return false;
        }

        /// <summary>Removes the current bots and spawns new ones around a point (e.g. where the player landed).</summary>
        public void SpawnAround(Vector3 center)
        {
            if (botPrefab == null) { Debug.LogWarning("[Bots] No bot prefab assigned.", this); return; }

            foreach (BotWanderer old in bots) if (old != null) Destroy(old.gameObject);
            bots.Clear();

            var spawned = new List<Transform>();
            for (int i = 0; i < botNames.Length; i++)
            {
                if (!TryFindSpot(center, out Vector3 position)) continue;
                bool female = femaleBotPrefab != null && System.Array.IndexOf(femaleNames, botNames[i]) >= 0;
                BotWanderer bot = Instantiate(female ? femaleBotPrefab : botPrefab, position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), transform);
                bot.SetWalkMode(useNavMesh, wanderRadius);
                bot.SetName(botNames[i]);
                Color color = Color.HSVToRGB((float)i / botNames.Length, 0.55f, 0.9f);
                bot.SetColor(color);
                if (female) ColourTop(bot.gameObject, color);
                else if (shirtMaterials != null && shirtMaterials.Length > 0) Dress(bot.gameObject, shirtMaterials[i % shirtMaterials.Length]);
                else Tint(bot.gameObject, color);
                bots.Add(bot);
                spawned.Add(bot.transform);
            }

            if (ambientBubbles != null) ambientBubbles.SetTargets(spawned.ToArray());
            Debug.Log($"[Bots] Spawned {spawned.Count} bots.", this);
        }

        private bool TryFindSpot(Vector3 center, out Vector3 spot)
        {
            Vector3 player = Camera.main != null ? Camera.main.transform.position : center;
            for (int i = 0; i < 40; i++)
            {
                Vector2 offset = Random.insideUnitCircle * spawnRadius;
                Vector3 candidate = center + new Vector3(offset.x, 0f, offset.y);
                Vector2 flat = new Vector2(candidate.x - player.x, candidate.z - player.z);
                if (flat.magnitude < minDistanceFromPlayer) continue;

                if (useNavMesh)
                {
                    if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 2f, NavMesh.AllAreas)) { spot = hit.position; return true; }
                }
                else if (StreetAt(candidate, center.y, out spot))
                {
                    return true;
                }
            }
            spot = center + Vector3.forward * minDistanceFromPlayer;
            return useNavMesh; // NavMesh test scene: fall back to a point in front; city: skip this bot
        }

        // Open street near the player's height: the top surface there must be close to street level
        // (so not a roof, not inside a building, not down in the water).
        private static bool StreetAt(Vector3 p, float streetY, out Vector3 ground)
        {
            ground = default;
            if (!Physics.Raycast(new Vector3(p.x, streetY + 60f, p.z), Vector3.down, out RaycastHit hit, 120f, ~0, QueryTriggerInteraction.Ignore))
                return false;
            if (Mathf.Abs(hit.point.y - streetY) > 1.0f) return false;
            ground = hit.point;
            return true;
        }

        // Female model: the jacket is a plain colour, so tint its main material with the bot colour.
        private void ColourTop(GameObject bot, Color color)
        {
            var block = new MaterialPropertyBlock();
            foreach (Renderer r in bot.GetComponentsInChildren<Renderer>())
            {
                if (!r.name.Contains(femaleTopRendererName)) continue;
                r.GetPropertyBlock(block, 0);
                block.SetColor(BaseColorId, color);
                r.SetPropertyBlock(block, 0);
            }
        }

        private void Dress(GameObject bot, Material shirt)
        {
            foreach (Renderer r in bot.GetComponentsInChildren<Renderer>())
                if (r.name.Contains(shirtRendererName)) r.sharedMaterial = shirt;
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
