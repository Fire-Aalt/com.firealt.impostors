using System;
using UnityEngine;

namespace Impostors.Data
{
    [Serializable]
    public struct ImpostorLodPart
    {
        public Mesh Mesh;
        public Material Material;
        public int SubmeshIndex;
    }
}
