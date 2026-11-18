using UnityEngine;

namespace CharacterFeature
{
    /// <summary>
    /// The player's own avatar: which body they picked and what they wear (both saved on the device).
    /// The player has no visible body in VR yet, so the outfit menu shows a "mirror" copy of the avatar
    /// standing in front of them while they change clothes. Later (multiplayer) other players can see it.
    /// </summary>
    public class PlayerAvatar : MonoBehaviour
    {
        private const string BodyKey = "BDCity.Avatar.Body";

        [SerializeField] private GameObject[] bodies;          // avatar prefabs with an AvatarWardrobe (not the bot variants)
        [SerializeField] private string[] bodyLabels;
        [Tooltip("How far in front of the player the mirror copy stands.")]
        [SerializeField] private float previewDistance = 2.2f;
        [Tooltip("Sideways, so the menu doesn't cover it.")]
        [SerializeField] private float previewSide = 0.8f;

        private int body;
        private GameObject preview;
        private Vector3 previewSpot;
        private Quaternion previewRotation;

        public AvatarWardrobe Wardrobe => preview != null ? preview.GetComponent<AvatarWardrobe>() : null;
        public string BodyLabel => bodyLabels != null && body < bodyLabels.Length ? bodyLabels[body] : bodies[body].name;
        public int BodyCount => bodies != null ? bodies.Length : 0;
        public bool PreviewVisible => preview != null;

        private void Awake()
        {
            body = Mathf.Clamp(PlayerPrefs.GetInt(BodyKey, 0), 0, Mathf.Max(0, BodyCount - 1));
        }

        /// <summary>Shows the mirror copy in front of the player (head = the headset).</summary>
        public void ShowPreview(Transform head)
        {
            if (BodyCount == 0 || head == null) return;
            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 spot = head.position + fwd * previewDistance + right * previewSide;
            // Stand on the ground under that spot.
            float y = head.position.y - 1.6f;
            if (Physics.Raycast(spot + Vector3.up * 1f, Vector3.down, out RaycastHit hit, 4f, ~0, QueryTriggerInteraction.Ignore)) y = hit.point.y;
            previewSpot = new Vector3(spot.x, y, spot.z);
            Vector3 toPlayer = head.position - previewSpot; toPlayer.y = 0f;
            previewRotation = Quaternion.LookRotation(toPlayer.normalized);
            Spawn();
        }

        public void HidePreview()
        {
            if (preview != null) Destroy(preview);
            preview = null;
        }

        public void NextBody(int step = 1)
        {
            if (BodyCount == 0) return;
            body = (body + step + BodyCount) % BodyCount;
            PlayerPrefs.SetInt(BodyKey, body);
            PlayerPrefs.Save();
            if (preview != null) Spawn();
        }

        private void Spawn()
        {
            if (preview != null) Destroy(preview);
            preview = Instantiate(bodies[body], previewSpot, previewRotation);
            preview.name = "Outfit Mirror (" + BodyLabel + ")";
            var walk = preview.GetComponent<BubbleFeature.Bots.ProceduralWalk>();
            if (walk != null) walk.enabled = true;   // keeps the arms down from the T-pose (standing still = no swing)
            var w = preview.GetComponent<AvatarWardrobe>();
            if (w != null) w.UseSaveKey("BDCity.Avatar.Outfit." + bodies[body].name);
        }
    }
}
