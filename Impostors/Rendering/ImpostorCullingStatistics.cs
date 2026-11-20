using System;

namespace Impostors
{
    [Serializable]
    public struct ImpostorCullingStatistics
    {
        public int VisibleInstances;
        public int VisibleEntries;
        public int DrawCommands;
        public int DrawBins;
        public int RejectedChunks;
        public int InsideChunks;
        public int PartialChunks;
        public int UniformChunks;
        public int MixedChunks;
        public int FadingInstances;
        public int ScratchGrowths;
        public int ViewType;
    }
}
