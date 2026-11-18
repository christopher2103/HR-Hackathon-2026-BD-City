using UnityEngine;

namespace CharacterFeature
{
    /// <summary>
    /// For every triangle of the body: which clothing parts cover it (bit i = coverParts[i] on the wardrobe).
    /// Baked in the Editor (B&D City > Avatar > Bake body cover). At runtime the wardrobe hides body triangles
    /// under the clothes that are worn, so skin never pokes through tight clothes.
    /// </summary>
    public class BodyCoverData : ScriptableObject
    {
        public int[] triangles;   // original body triangles (index buffer, submesh 0)
        public int[] cover;       // one mask per triangle
    }
}
