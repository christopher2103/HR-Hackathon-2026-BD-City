using System;
using System.Collections;
using System.Collections.Generic;
using CesiumForUnity;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CityFeature
{
    /// <summary>
    /// Moves the player between a few neighbourhoods of the streamed city (Cesium 3D Tiles).
    /// Because the city streams in, the player first stands on an invisible safety floor;
    /// as soon as the ground and buildings have loaded, they are placed on a free spot on the street
    /// (it searches nearby if the exact point is inside a building).
    /// Desktop test keys: 1, 2, 3.
    /// </summary>
    public class NeighbourhoodTeleporter : MonoBehaviour
    {
        [Serializable]
        public class Neighbourhood
        {
            public string name;
            [Tooltip("A CesiumGlobeAnchor under the CesiumGeoreference, placed on the street.")]
            public Transform spot;
            [Tooltip("Saved (frozen) copy of this neighbourhood. Used instead of streaming when 'Use Frozen Areas' is on.")]
            public GameObject frozen;
            [Tooltip("Optional: the player arrives facing this (e.g. a landmark). Empty = face the most open direction.")]
            public Transform lookAt;
        }

        [SerializeField] private Neighbourhood[] neighbourhoods;

        [Header("City data")]
        [SerializeField] private Cesium3DTileset terrain;
        [SerializeField] private Cesium3DTileset buildings;

        [Header("Player")]
        [Tooltip("Empty = the XR Origin in the scene.")]
        [SerializeField] private XROrigin xrOrigin;
        [SerializeField] private bool teleportOnStart = true;
        [SerializeField] private int startIndex = 0;

        [Header("Finding a free spot")]
        [SerializeField] private float searchRadius = 40f;     // look this far around the spot for open street
        [SerializeField] private float freeHeight = 2.5f;      // nothing may be higher than this above the ground
        [SerializeField] private float buildingsLoadedPercent = 90f;
        [Tooltip("Coarse ground tiles get replaced by detailed ones: wait for this much terrain (or the max wait below).")]
        [SerializeField] private float terrainLoadedPercent = 95f;
        [Tooltip("After the ground is there, wait at most this long for buildings (so you don't land inside one).")]
        [SerializeField] private float maxBuildingWaitSeconds = 5f;
        [Tooltip("After this long a warning is logged; it keeps waiting on the safety floor until the city is there.")]
        [SerializeField] private float slowWarningSeconds = 25f;

        [Header("Ground sanity checks")]
        [Tooltip("Ground lower/higher than this (metres, relative to the CesiumGeoreference) is a coarse, not-yet-detailed tile: ignore it.")]
        [SerializeField] private float minGroundY = -20f;
        [SerializeField] private float maxGroundY = 40f;
        [Tooltip("The ground height must stay the same this long before we land (coarse tiles get replaced by detailed ones).")]
        [SerializeField] private float groundStableSeconds = 1f;
        [Tooltip("After landing, step out of buildings that stream in on top of the player for this long.")]
        [SerializeField] private float watchAfterLandingSeconds = 10f;

        [Header("Frozen neighbourhoods (offline)")]
        [Tooltip("Use the saved neighbourhoods and switch off streaming. Off = stream the city live (needs internet).")]
        [SerializeField] private bool useFrozenAreas = true;

        [Header("Loading screen (optional)")]
        [SerializeField] private LoadingScreen loadingScreen;
        [Tooltip("Stay dark a little after landing, so bots and the last tiles are in place.")]
        [SerializeField] private float extraDarkSeconds = 0.6f;

        private Transform safetyFloor;
        private float lastGroundY = float.NaN; // unknown until the first landing
        private int currentIndex = -1;

        public IReadOnlyList<Neighbourhood> Neighbourhoods => neighbourhoods;
        public string CurrentName => currentIndex >= 0 ? neighbourhoods[currentIndex].name : "";

        /// <summary>Fired when the player has been placed on the ground, still behind the loading screen (name of the neighbourhood).</summary>
        public event Action<string> Arrived;

        /// <summary>Fired when the loading screen has faded away and the player sees the neighbourhood.</summary>
        public event Action<string> Revealed;

        /// <summary>Where the player's feet are (the XR Origin).</summary>
        public Vector3 PlayerFeet => xrOrigin != null ? xrOrigin.transform.position : Vector3.zero;

        private void Awake()
        {
            if (xrOrigin == null) xrOrigin = FindAnyObjectByType<XROrigin>();

            // Invisible floor that holds the player while the city loads.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Safety Floor";
            Destroy(floor.GetComponent<MeshRenderer>());
            floor.transform.localScale = new Vector3(6f, 0.1f, 6f);
            safetyFloor = floor.transform;

            if (FrozenMode)
            {
                // Offline: no streaming at all, only the saved neighbourhood we are in is switched on.
                if (terrain != null) terrain.gameObject.SetActive(false);
                if (buildings != null) buildings.gameObject.SetActive(false);
                foreach (Neighbourhood n in neighbourhoods) if (n.frozen != null) n.frozen.SetActive(false);
            }
            else
            {
                // Streaming mode: the tilesets are kept switched off in the scene (keeps the Editor light), turn them on.
                if (terrain != null) terrain.gameObject.SetActive(true);
                if (buildings != null) buildings.gameObject.SetActive(true);
                foreach (Neighbourhood n in neighbourhoods) if (n.frozen != null) n.frozen.SetActive(false);
            }
        }

        private bool FrozenMode
        {
            get
            {
                if (!useFrozenAreas || neighbourhoods == null) return false;
                foreach (Neighbourhood n in neighbourhoods) if (n.frozen == null) return false;
                return true;
            }
        }

        private void Start()
        {
            if (teleportOnStart && neighbourhoods != null && neighbourhoods.Length > 0) GoTo(startIndex);
        }

        private void Update()
        {
            // Desktop test keys.
            Keyboard k = Keyboard.current;
            if (k == null) return;
            if (k.digit1Key.wasPressedThisFrame) GoTo(0);
            if (k.digit2Key.wasPressedThisFrame) GoTo(1);
            if (k.digit3Key.wasPressedThisFrame) GoTo(2);
            if (k.digit4Key.wasPressedThisFrame && neighbourhoods.Length > 3) GoTo(3);
        }

        public void GoTo(int index)
        {
            if (neighbourhoods == null || index < 0 || index >= neighbourhoods.Length || neighbourhoods[index].spot == null) return;
            StopAllCoroutines();
            StartCoroutine(Teleport(index));
        }

        private IEnumerator Teleport(int index)
        {
            currentIndex = index;
            Neighbourhood n = neighbourhoods[index];
            Vector3 spot = n.spot.position;
            Debug.Log($"[City] Going to {n.name}...", this);
            bool frozen = FrozenMode;

            // 0. Fade to black first, so nobody sees the jump or the tiles popping in.
            if (loadingScreen != null)
            {
                loadingScreen.Show($"Going to {n.name}");
                yield return loadingScreen.WaitUntilOpaque();
            }
            if (frozen)
            {
                // Areas can share a frozen prefab (Stadhuis reuses Lijnbaan): switch the others off first, then ours on.
                foreach (Neighbourhood other in neighbourhoods) if (other.frozen != n.frozen) other.frozen.SetActive(false);
                n.frozen.SetActive(true);
                Physics.SyncTransforms();
            }

            // 1. While tiles stream in, hover on the safety floor: at the last known ground height,
            //    or (first time) at the spot's height, which gives a nice view over the city as it loads.
            float holdY = float.IsNaN(lastGroundY) ? spot.y : lastGroundY;
            safetyFloor.position = new Vector3(spot.x, holdY - 0.05f, spot.z);
            MovePlayer(new Vector3(spot.x, holdY, spot.z));

            // 2. Wait for the ground, give buildings a moment, then put the player on a free spot.
            float waited = 0f;
            float groundSince = -1f;     // when plausible ground first appeared
            float stableSince = -1f;     // when the ground height last changed
            float stableY = float.NaN;
            bool warned = false;
            while (true)
            {
                if (GroundAt(spot, out Vector3 g0, out _))
                {
                    if (groundSince < 0f) groundSince = waited;
                    if (float.IsNaN(stableY) || Mathf.Abs(g0.y - stableY) > 0.3f) { stableY = g0.y; stableSince = waited; }
                }
                bool groundStable = stableSince >= 0f && waited - stableSince >= groundStableSeconds;
                if (loadingScreen != null)
                    loadingScreen.SetStatus(groundSince < 0f ? "Loading the streets..." : "Loading the buildings...", LoadProgress());
                bool terrainReady = frozen || terrain == null
                                    || terrain.ComputeLoadProgress() >= terrainLoadedPercent
                                    || (groundSince >= 0f && waited - groundSince >= maxBuildingWaitSeconds);
                bool buildingsReady = frozen || buildings == null
                                      || buildings.ComputeLoadProgress() >= buildingsLoadedPercent
                                      || (groundSince >= 0f && waited - groundSince >= maxBuildingWaitSeconds);
                if (groundStable && terrainReady && buildingsReady && TryFindFreeSpot(spot, out Vector3 ground))
                {
                    lastGroundY = ground.y;
                    safetyFloor.position = ground + Vector3.down * 0.05f; // stays as a small safe patch
                    MovePlayer(ground);
                    if (n.lookAt != null) FaceTowards(n.lookAt.position - ground);
                    else FaceOpenDirection(ground - spot);
                    Debug.Log($"[City] Arrived at {n.name} after {waited:F1} s (ground y={ground.y:F1}).", this);
                    StartCoroutine(WatchGround()); // keeps the player on the (still refining) ground
                    Arrived?.Invoke(n.name);

                    if (loadingScreen != null)
                    {
                        loadingScreen.SetStatus("Welcome!");
                        yield return new WaitForSeconds(extraDarkSeconds);
                        loadingScreen.Hide();
                        yield return loadingScreen.WaitUntilHidden();
                    }
                    Revealed?.Invoke(n.name);
                    yield break;
                }
                if (!warned && waited > slowWarningSeconds)
                {
                    warned = true;
                    Debug.LogWarning($"[City] {n.name}: still loading after {slowWarningSeconds} s (slow internet?). Waiting on the safety floor.", this);
                }
                waited += Time.deltaTime;
                yield return null;
            }
        }

        // Turn the player towards the longest free view (down the street instead of into a wall).
        // awayFromSpot: from the spot to where we landed (points out of the building when the spot was inside one).
        private void FaceTowards(Vector3 direction)
        {
            Camera cam = xrOrigin != null ? xrOrigin.Camera : null;
            direction.y = 0f;
            if (cam == null || direction.sqrMagnitude < 0.01f) return;
            Vector3 look = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (look.sqrMagnitude < 0.001f) look = xrOrigin.transform.forward;
            xrOrigin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(look, direction, Vector3.up));
        }

        private void FaceOpenDirection(Vector3 awayFromSpot)
        {
            Camera cam = xrOrigin != null ? xrOrigin.Camera : null;
            if (cam == null) return;
            Vector3 eye = xrOrigin.transform.position + Vector3.up * 1.6f;
            awayFromSpot.y = 0f;
            Vector3 preferred = awayFromSpot.sqrMagnitude > 0.01f ? awayFromSpot.normalized : xrOrigin.transform.forward;
            Vector3 best = preferred;
            float bestScore = float.MinValue;
            for (int i = 0; i < 16; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, i * 22.5f, 0f) * Vector3.forward;
                float d = Physics.Raycast(eye, dir, out RaycastHit hit, 150f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 150f;
                float score = d + Vector3.Dot(dir, preferred); // ties (e.g. buildings not loaded yet) go to the preferred side
                if (score > bestScore) { bestScore = score; best = dir; }
            }
            Vector3 look = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (look.sqrMagnitude < 0.001f) look = xrOrigin.transform.forward;
            xrOrigin.RotateAroundCameraUsingOriginUp(Vector3.SignedAngle(look, best, Vector3.up));
        }

        // Tiles keep refining while you are there: if the ground under the player rises (or they fell through),
        // lift them back up. Runs until the next GoTo. Buildings are only checked shortly after landing.
        private IEnumerator WatchGround()
        {
            float t = 0f;
            float nextBuildingCheck = 0f;
            var wait = new WaitForSeconds(0.2f);
            while (true)
            {
                Vector3 feet = xrOrigin.transform.position;
                bool hasGround = GroundAt(feet, out Vector3 g, out float roof);
                bool checkBuildings = t < watchAfterLandingSeconds && t >= nextBuildingCheck;
                if (checkBuildings) nextBuildingCheck = t + 0.5f;
                if (checkBuildings && hasGround && roof - g.y >= freeHeight && roof > feet.y + 0.5f
                    && TryFindFreeSpot(feet, out Vector3 outside))
                {
                    // A building streamed in on top of us: step out of it.
                    safetyFloor.position = outside + Vector3.down * 0.05f;
                    MovePlayer(outside);
                    FaceOpenDirection(outside - feet);
                    lastGroundY = outside.y;
                    Debug.Log("[City] A building loaded where you stood; moved you onto the street.", this);
                }
                else if (hasGround && g.y - feet.y > 0.05f)
                {
                    safetyFloor.position = g + Vector3.down * 0.05f;
                    MovePlayer(g);
                    lastGroundY = g.y;
                }
                else if (!float.IsNaN(lastGroundY) && feet.y < lastGroundY - 10f)
                {
                    MovePlayer(new Vector3(feet.x, lastGroundY, feet.z)); // fell through a hole while tiles swapped
                }
                t += 0.2f;
                yield return wait;
            }
        }

        // 0..1, average of terrain and buildings (as Cesium reports it for the current view).
        private float LoadProgress()
        {
            if (FrozenMode) return 1f;
            float sum = 0f; int count = 0;
            if (terrain != null) { sum += terrain.ComputeLoadProgress(); count++; }
            if (buildings != null) { sum += buildings.ComputeLoadProgress(); count++; }
            return count == 0 ? 1f : Mathf.Clamp01(sum / count / 100f);
        }

        // Search the spot, then rings around it, for open ground (no building on top).
        private bool TryFindFreeSpot(Vector3 spot, out Vector3 ground)
        {
            ground = default;
            if (!GroundAt(spot, out _, out _)) return false; // terrain not loaded here yet

            for (float r = 0f; r <= searchRadius; r += 4f)
            {
                int steps = r == 0f ? 1 : 12;
                for (int i = 0; i < steps; i++)
                {
                    float a = i * Mathf.PI * 2f / steps;
                    Vector3 p = spot + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (GroundAt(p, out Vector3 g, out float highest) && highest - g.y < freeHeight)
                    {
                        ground = g;
                        return true;
                    }
                }
            }
            return false;
        }

        // Terrain point below p, plus the highest city surface (e.g. a roof) above it.
        private bool GroundAt(Vector3 p, out Vector3 terrainPoint, out float highestY)
        {
            terrainPoint = default;
            highestY = float.MinValue;
            bool foundTerrain = false;
            Vector3 from = new Vector3(p.x, p.y + 400f, p.z);
            foreach (RaycastHit hit in Physics.RaycastAll(from, Vector3.down, 800f, ~0, QueryTriggerInteraction.Ignore))
            {
                CityPart part = hit.collider.GetComponentInParent<CityPart>();
                Cesium3DTileset owner = part == null ? hit.collider.GetComponentInParent<Cesium3DTileset>() : null;
                if (part == null && owner == null) continue; // ignore our own objects (bots, trees, ...)
                bool isGround = part != null ? part.isGround : owner == terrain;
                highestY = Mathf.Max(highestY, hit.point.y);
                bool plausible = hit.point.y >= minGroundY && hit.point.y <= maxGroundY;
                if (isGround && plausible && (!foundTerrain || hit.point.y > terrainPoint.y))
                {
                    terrainPoint = hit.point;
                    foundTerrain = true;
                }
            }
            return foundTerrain;
        }

        private void MovePlayer(Vector3 floorPosition)
        {
            if (xrOrigin == null) return;
            var controller = xrOrigin.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false; // a CharacterController ignores direct moves
            xrOrigin.transform.position = floorPosition;
            if (controller != null)
            {
                // Right at start (headset not tracked yet) XRI can have shrunk the capsule to ~0 height;
                // Unity then refuses to enable it ("Step Offset must be less or equal to...").
                // Despite the message, in practice the height itself must be >= Step Offset.
                float minHeight = controller.stepOffset + 0.01f;
                if (controller.height < minHeight) controller.height = minHeight;
                controller.enabled = true;
            }
        }
    }
}
