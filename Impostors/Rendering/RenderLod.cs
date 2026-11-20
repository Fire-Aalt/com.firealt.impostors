using UnityEngine.Rendering;

namespace Impostors
{
    /// <summary>One LOD level of a registered prototype: its batch, its parts and its fade band.</summary>
    public struct RenderLod
    {
        public BatchID Batch;
        public int FirstPart;
        public int PartCount;
        public float TransitionScreenHeight;
        public float FadeWidth;
    }
}
