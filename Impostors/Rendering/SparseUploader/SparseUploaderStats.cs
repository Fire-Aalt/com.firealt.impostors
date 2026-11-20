using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    /// <summary>
    /// Represents SparseUploader statistics.
    /// </summary>
    public struct SparseUploaderStats
    {
        /// <summary>
        /// The amount of GPU memory the SparseUploader uses internally.
        /// </summary>
        /// <remarks>
        /// This value doesn't include memory in the managed GPU buffer that you pass into the SparseUploader on construction,
        /// or when you use SparseUploader.ReplaceBuffer.
        /// </remarks>
        public long BytesGPUMemoryUsed;

        /// <summary>
        /// The amount of memory the SparseUploader used to upload during the current frame.
        /// </summary>
        public long BytesGPUMemoryUploadedCurr;

        /// <summary>
        /// The highest amount of memory the SparseUploader used for upload during a previous frame.
        /// </summary>
        public long BytesGPUMemoryUploadedMax;
    }
}
