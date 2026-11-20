using System;
using UnityEngine;

namespace EntityPainter.Editor
{
    /// <summary>One palette slot of an <see cref="EntityBrush"/>. Disabled slots are neither painted nor erased.</summary>
    [Serializable]
    public sealed class EntityBrushPrefab
    {
        public GameObject Prefab;
        public bool Enabled = true;
    }
}
