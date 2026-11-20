using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    /// <summary>One registered mesh, submesh and material draw of a LOD level.</summary>
    public struct RenderPart
    {
        public BatchMeshID Mesh;
        public BatchMaterialID Material;

        /// <summary>Used by draws inside a LOD fade band. Differs from <see cref="Material"/> only on impostor parts.</summary>
        public BatchMaterialID FadeMaterial;

        public ushort Submesh;

        // Indirect arguments cannot query the managed Mesh from a job.
        public uint IndexCount;
        public uint IndexStart;
        public uint BaseVertex;
        public MeshTopology Topology;
    }
}
