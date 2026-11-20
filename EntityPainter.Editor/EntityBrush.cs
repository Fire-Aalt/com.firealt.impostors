using System.Collections.Generic;
using UnityEngine;

namespace EntityPainter.Editor
{
    /// <summary>A reusable Entity Painter brush: the prefab palette and every placement setting.</summary>
    [CreateAssetMenu(menuName = MENU_NAME, fileName = FILE_NAME)]
    public sealed class EntityBrush : ScriptableObject
    {
        public const string FILE_NAME = "New Entity Brush";

        private const string MENU_NAME = "Entity Painter/Brush";

        public List<EntityBrushPrefab> Prefabs = new();

        [Tooltip("Layers the brush places instances on. Ground without a collider is hit through Scene view picking.")]
        public LayerMask SurfaceLayers = ~0;

        [Tooltip("Brush diameter in world units.")]
        [Min(0.1f)]
        public float Size = 20f;

        [Tooltip("Minimum distance the brush moves before it places or erases again.")]
        [Min(0f)]
        public float DeltaDistance = 5f;

        [Tooltip("Minimum distance between instance origins, checked against every instance of the target. 0 disables it.")]
        [Min(0f)]
        public float MinDistance;

        [Tooltip("Instances scattered inside the brush per dab. A single instance is placed at the brush centre.")]
        [Min(1)]
        public int PrefabsPerDab = 1;

        [Tooltip("Tilts each instance's up axis to the surface normal before the random rotation is applied.")]
        public bool AlignToNormal;

        [Tooltip("Minimum random Euler rotation in degrees.")]
        public Vector3 MinRotation = Vector3.zero;

        [Tooltip("Maximum random Euler rotation in degrees.")]
        public Vector3 MaxRotation = new(0f, 360f, 0f);

        [Tooltip("Minimum uniform scale.")]
        [Min(0.001f)]
        public float MinScale = 1f;

        [Tooltip("Maximum uniform scale.")]
        [Min(0.001f)]
        public float MaxScale = 1f;
    }
}
