using System;
using UnityEngine;

namespace EntityPainter
{
    /// <summary>
    /// Authoring container for painted entity instances. Place it in a SubScene; baking turns it into one spawner entity
    /// and <see cref="PaintedEntitySpawnSystem"/> instantiates the prototypes' baked entity prefabs at runtime and in the
    /// Editor world. Instances are grouped by prototype: the first <c>Counts[0]</c> records belong to
    /// <c>Prototypes[0]</c>, the next <c>Counts[1]</c> to <c>Prototypes[1]</c>, and so on.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu(MENU_PATH)]
    public sealed class PaintedEntities : MonoBehaviour
    {
        public const int FORMAT_VERSION = 1;

        private const string MENU_PATH = "Entity Painter/Painted Entities";

        [HideInInspector]
        public GameObject[] Prototypes = Array.Empty<GameObject>();

        [HideInInspector]
        public int[] Counts = Array.Empty<int>();

        /// <summary>Back-to-back <see cref="PaintedRecord"/> values, <see cref="PaintedRecord.SIZE"/> bytes each.</summary>
        [HideInInspector]
        public byte[] Records = Array.Empty<byte>();

        [HideInInspector]
        public int FormatVersion = FORMAT_VERSION;

        public int InstanceCount => Records.Length / PaintedRecord.SIZE;

        /// <summary>True when the prototype, count and record arrays agree with each other and the format version.</summary>
        public bool IsConsistent
        {
            get
            {
                if (FormatVersion != FORMAT_VERSION || Prototypes == null || Counts == null || Records == null
                    || Prototypes.Length != Counts.Length)
                {
                    return false;
                }

                long total = 0;
                foreach (int count in Counts)
                {
                    if (count < 0)
                    {
                        return false;
                    }

                    total += count;
                }

                return total * PaintedRecord.SIZE == Records.Length;
            }
        }

        /// <summary>Index of the first record of <paramref name="prototype"/>.</summary>
        public int GetStart(int prototype)
        {
            int start = 0;
            for (int i = 0; i < prototype; i++)
            {
                start += Counts[i];
            }

            return start;
        }
    }
}
