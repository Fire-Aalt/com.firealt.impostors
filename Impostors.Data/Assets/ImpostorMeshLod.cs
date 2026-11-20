using System;
using UnityEngine;

namespace Impostors.Data
{
    [Serializable]
    public sealed class ImpostorMeshLod
    {
        public ImpostorLodPart[] Parts = Array.Empty<ImpostorLodPart>();

        [Range(0.0001f, 1)]
        public float TransitionScreenHeight = 0.025f;

        [Range(0, 1)]
        public float FadeWidth = 0.1f;

        public ImpostorMeshLod Copy()
        {
            return new ImpostorMeshLod
            {
                Parts = (ImpostorLodPart[])Parts.Clone(),
                TransitionScreenHeight = TransitionScreenHeight,
                FadeWidth = FadeWidth,
            };
        }
    }
}
