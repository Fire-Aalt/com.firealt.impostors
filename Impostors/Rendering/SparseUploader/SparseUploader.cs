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
    /// Provides utility methods that you can use to upload data into GPU memory.
    /// </summary>
    /// <remarks>
    /// To add uploads from jobs, use a ThreadedSparseUploader which you can create using SparseUploader.Begin.
    /// If you add uploads from jobs, the ThreadedSparseUploader submits them to the GPU in a series of compute shader dispatches when you call SparseUploader.EndAndCommit.
    /// </remarks>
    public unsafe struct SparseUploader : IDisposable
    {
        const int k_MaxThreadGroupsPerDispatch = 65535;

        int m_BufferChunkSize;

        GraphicsBuffer m_DestinationBuffer;

        BufferPool m_UploadBufferPool;

        NativeArray<MappedBuffer> m_MappedBuffers;

        private long m_CurrentFrameUploadSize;
        private long m_MaxUploadSize;

        class FrameData
        {
            public Stack<int> m_Buffers;

            public FrameData()
            {
                m_Buffers = new Stack<int>();
            }
        }

        Stack<FrameData> m_FreeFrameData;
        List<FrameData> m_FrameData;

        ThreadedSparseUploaderData* m_ThreadData;

        ComputeShader m_SparseUploaderShader;
        int m_CopyKernelIndex;
        int m_ReplaceKernelIndex;

        int m_SrcBufferID;
        int m_DstBufferID;
        int m_OperationsBaseID;
        int m_ReplaceOperationSize;

        int m_RequestedUploadBufferPoolMaxSizeBytes;
        bool m_PruneUploadBufferPool;

        /// <summary>
        /// Constructs a new sparse uploader with the specified buffer as the target.
        /// </summary>
        /// <param name="destinationBuffer">The target buffer to write uploads into.</param>
        /// <param name="bufferChunkSize">The upload buffer chunk size.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="destinationBuffer"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="destinationBuffer"/> is invalid or has an incompatible target type.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bufferChunkSize"/> is less than or equal to zero.</exception>
        public SparseUploader(GraphicsBuffer destinationBuffer, int bufferChunkSize = 16 * 1024 * 1024)
        {
            if (destinationBuffer == null)
                throw new ArgumentNullException(nameof(destinationBuffer), "Destination buffer cannot be null.");

            if (!destinationBuffer.IsValid())
                throw new ArgumentException("Destination buffer is not in a valid state. The buffer may have been disposed or not properly initialized.", nameof(destinationBuffer));

            if ((destinationBuffer.target & GraphicsBuffer.Target.Raw) != GraphicsBuffer.Target.Raw)
                throw new ArgumentException("Destination buffer must have Raw target type for sparse uploading.", nameof(destinationBuffer));

            if (bufferChunkSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferChunkSize), bufferChunkSize, "Buffer chunk size must be greater than zero.");

            m_BufferChunkSize = bufferChunkSize;

            m_DestinationBuffer = destinationBuffer;

            m_UploadBufferPool = new BufferPool(m_BufferChunkSize / 4, 4, GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.LockBufferForWrite);
            m_MappedBuffers = new NativeArray<MappedBuffer>();
            m_FreeFrameData = new Stack<FrameData>();
            m_FrameData = new List<FrameData>();

            m_ThreadData = (ThreadedSparseUploaderData*)UnsafeUtility.Malloc(sizeof(ThreadedSparseUploaderData),
                UnsafeUtility.AlignOf<ThreadedSparseUploaderData>(), Allocator.Persistent);
            m_ThreadData->m_Buffers = null;
            m_ThreadData->m_NumBuffers = 0;
            m_ThreadData->m_CurrBuffer = 0;

            m_SparseUploaderShader = Resources.Load<ComputeShader>("SparseUploader");
            m_CopyKernelIndex = m_SparseUploaderShader.FindKernel("CopyKernel");
            m_ReplaceKernelIndex = m_SparseUploaderShader.FindKernel("ReplaceKernel");

            m_SrcBufferID = Shader.PropertyToID("srcBuffer");
            m_DstBufferID = Shader.PropertyToID("dstBuffer");
            m_OperationsBaseID = Shader.PropertyToID("operationsBase");
            m_ReplaceOperationSize = Shader.PropertyToID("replaceOperationSize");

            m_CurrentFrameUploadSize = 0;
            m_MaxUploadSize = 0;

            m_RequestedUploadBufferPoolMaxSizeBytes = 0;
            m_PruneUploadBufferPool = false;
        }

        /// <summary>
        /// Disposes of the SparseUploader.
        /// </summary>
        public void Dispose()
        {
            m_UploadBufferPool.Dispose();
            UnsafeUtility.Free(m_ThreadData, Allocator.Persistent);
        }

        /// <summary>
        /// Replaces the destination GPU buffer with a new one.
        /// </summary>
        /// <remarks>
        /// If the existing buffer is non-null and copyFromPrevious is true, this method
        /// dispatches a copy operation that copies data from the previous buffer to the new one.
        ///
        /// This is useful when the persistent storage buffer needs to grow.
        /// </remarks>
        /// <param name="buffer">The new buffer to replace the old one with.</param>
        /// <param name="copyFromPrevious">Indicates whether to copy the contents of the old buffer to the new buffer.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="buffer"/> is invalid or has an incompatible target type.</exception>
        public void ReplaceBuffer(GraphicsBuffer buffer, bool copyFromPrevious = false)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer), "Destination buffer cannot be null.");

            if (!buffer.IsValid())
                throw new ArgumentException("Destination buffer is not in a valid state. The buffer may have been disposed or not properly initialized.", nameof(buffer));

            if ((buffer.target & GraphicsBuffer.Target.Raw) != GraphicsBuffer.Target.Raw)
                throw new ArgumentException("Destination buffer must have Raw target type for sparse uploading.", nameof(buffer));

            if (copyFromPrevious && m_DestinationBuffer != null)
            {
                // Since we have no code such as Graphics.CopyBuffer(dst, src) currently
                // we have to do this ourselves in a compute shader
                var srcSize = m_DestinationBuffer.count * m_DestinationBuffer.stride;
                m_SparseUploaderShader.SetBuffer(m_ReplaceKernelIndex, m_SrcBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetBuffer(m_ReplaceKernelIndex, m_DstBufferID, buffer);
                m_SparseUploaderShader.SetInt(m_ReplaceOperationSize, srcSize);

                m_SparseUploaderShader.Dispatch(m_ReplaceKernelIndex, 1, 1, 1);
            }

            m_DestinationBuffer = buffer;
        }

        internal static int NumFramesInFlight
        {
            get
            {
                // The number of frames in flight at the same time
                // depends on the Graphics device that we are using.
                // This number tells how long we need to keep the buffers
                // for a given frame alive. For example, if this is 4,
                // we can reclaim the buffers for a frame after 4 frames have passed.
                int numFrames = 0;

                switch (SystemInfo.graphicsDeviceType)
                {
                    case GraphicsDeviceType.Vulkan:
                    case GraphicsDeviceType.Direct3D11:
                    case GraphicsDeviceType.Direct3D12:
                    case GraphicsDeviceType.PlayStation4:
                    case GraphicsDeviceType.PlayStation5:
                    case GraphicsDeviceType.XboxOne:
                    case GraphicsDeviceType.GameCoreXboxOne:
                    case GraphicsDeviceType.GameCoreXboxSeries:
                    case GraphicsDeviceType.OpenGLCore:

                    // OpenGL ES 2.0 is no longer supported in Unity 2023.1 and later
#if !UNITY_2023_1_OR_NEWER
                    case GraphicsDeviceType.OpenGLES2:
#endif

                    case GraphicsDeviceType.OpenGLES3:
                    case GraphicsDeviceType.PlayStation5NGGC:
                        numFrames = 3;
                        break;
                    case GraphicsDeviceType.Switch:
                    case GraphicsDeviceType.Metal:
                    default:
                        numFrames = 4;
                        break;
                }

                // Use at least as many frames as the quality settings have, but use a platform
                // specific lower limit in any case.
                numFrames = math.max(numFrames, QualitySettings.maxQueuedFrames);

                return numFrames;
            }
        }

        private void RecoverBuffers()
        {
            int numFree = 0;

            // Count frames instead of using async readback to determine completion, because
            // using async readback prevents Unity from letting the device idle, which is really
            // bad for power usage.
            // Add 1 to the device frame count to account for two frames overlapping on
            // CPU side before reaching the GPU.
            int maxBufferedFrames = NumFramesInFlight + 1;

            // If we have more buffered frames than the maximum, free all the excess
            if (m_FrameData.Count > maxBufferedFrames)
                numFree = m_FrameData.Count - maxBufferedFrames;

            for (int i = 0; i < numFree; ++i)
            {
                while (m_FrameData[i].m_Buffers.Count > 0)
                {
                    var buffer = m_FrameData[i].m_Buffers.Pop();
                    m_UploadBufferPool.PutBufferId(buffer);
                }
                m_FreeFrameData.Push(m_FrameData[i]);
            }

            if (numFree > 0)
            {
                m_FrameData.RemoveRange(0, numFree);
            }
        }

        /// <summary>
        /// Begins a new upload frame and returns a new ThreadedSparseUploader that is valid until the next call to
        /// SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// You must follow this method with a call to SparseUploader.EndAndCommit later in the frame. You must also pass
        /// the returned value from a Begin method to the next SparseUploader.EndAndCommit.
        /// </remarks>
        /// <param name="maxDataSizeInBytes">An upper bound of total data size that you want to upload this frame.</param>
        /// <param name="biggestDataUpload">The size of the largest upload operation that will occur.</param>
        /// <param name="maxOperationCount">An upper bound of the total number of upload operations that will occur this frame.</param>
        /// <returns>Returns a new ThreadedSparseUploader that must be passed to SparseUploader.EndAndCommit later.</returns>
        public ThreadedSparseUploader Begin(int maxDataSizeInBytes, int biggestDataUpload, int maxOperationCount)
        {
            // First: recover all buffers from the previous frames (if any)
            RecoverBuffers();

            // Second: calculate total size needed this frame, allocate buffers and map what is needed
            var operationSize = UnsafeUtility.SizeOf<Operation>();
            var maxOperationSizeInBytes = maxOperationCount * operationSize;
            var sizeNeeded = maxOperationSizeInBytes + maxDataSizeInBytes;
            var bufferSizeWithMaxPaddingRemoved = m_BufferChunkSize - operationSize - biggestDataUpload;
            var numBuffersNeeded = (sizeNeeded + bufferSizeWithMaxPaddingRemoved - 1) / bufferSizeWithMaxPaddingRemoved;

            if (numBuffersNeeded < 0)
                numBuffersNeeded = 0;

            m_CurrentFrameUploadSize = sizeNeeded;
            if (m_CurrentFrameUploadSize > m_MaxUploadSize)
                m_MaxUploadSize = m_CurrentFrameUploadSize;

            m_MappedBuffers = new NativeArray<MappedBuffer>(numBuffersNeeded, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

            for(int i = 0; i < numBuffersNeeded; ++i)
            {
                var id = m_UploadBufferPool.GetBufferId();
                var cb = m_UploadBufferPool.GetBufferFromId(id);
                var data = cb.LockBufferForWrite<byte>(0, m_BufferChunkSize);
                var marker = MappedBuffer.PackMarker(0, m_BufferChunkSize);
                m_MappedBuffers[i] = new MappedBuffer
                {
                    m_Data = (byte*)data.GetUnsafePtr(),
                    m_Marker = marker,
                    m_BufferID = id,
                };
            }

            m_ThreadData->m_Buffers = (MappedBuffer*)m_MappedBuffers.GetUnsafePtr();
            m_ThreadData->m_NumBuffers = numBuffersNeeded;

            // TODO: set safety handle on thread data
            return new ThreadedSparseUploader
            {
                m_Data = m_ThreadData
            };
        }

        private void DispatchUploads(int numOps, GraphicsBuffer graphicsBuffer)
        {
            for (int iOp = 0; iOp < numOps; iOp += k_MaxThreadGroupsPerDispatch)
            {
                int opsBegin = iOp;
                int opsEnd = math.min(opsBegin + k_MaxThreadGroupsPerDispatch, numOps);
                int numThreadGroups = opsEnd - opsBegin;

                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_SrcBufferID, graphicsBuffer);
                m_SparseUploaderShader.SetBuffer(m_CopyKernelIndex, m_DstBufferID, m_DestinationBuffer);
                m_SparseUploaderShader.SetInt(m_OperationsBaseID, opsBegin);

                m_SparseUploaderShader.Dispatch(m_CopyKernelIndex, numThreadGroups, 1, 1);
            }
        }

        private void StepFrame()
        {
            // TODO: release safety handle of thread data
            m_ThreadData->m_Buffers = null;
            m_ThreadData->m_NumBuffers = 0;
            m_ThreadData->m_CurrBuffer = 0;
        }

        /// <summary>
        /// Ends an upload frame and dispatches any upload operations added to the passed in ThreadedSparseUploader.
        /// </summary>
        /// <param name="tsu">The ThreadedSparseUploader to consume and process upload dispatches for. You must have created this with a call to SparseUploader.Begin.</param>
        public void EndAndCommit(ThreadedSparseUploader tsu)
        {
            // Enforce that EndAndCommit is only called with a valid ThreadedSparseUploader
            if (!tsu.IsValid)
            {
                Debug.LogError("Invalid ThreadedSparseUploader passed to EndAndCommit");
                return;
            }

            int numBuffers = m_ThreadData->m_NumBuffers;

            // If there is no work for us to do, early out so we don't add empty entries into m_FrameData
            if (numBuffers == 0 && !m_MappedBuffers.IsCreated)
                return;

            var frameData = m_FreeFrameData.Count > 0 ? m_FreeFrameData.Pop() : new FrameData();
            for (int iBuf = 0; iBuf < numBuffers; ++iBuf)
            {
                var mappedBuffer = m_MappedBuffers[iBuf];
                MappedBuffer.UnpackMarker(mappedBuffer.m_Marker, out var operationOffset, out var dataOffset);
                var numOps = (int) (operationOffset / UnsafeUtility.SizeOf<Operation>());
                var graphicsBufferID = mappedBuffer.m_BufferID;
                var graphicsBuffer = m_UploadBufferPool.GetBufferFromId(graphicsBufferID);

                if (numOps > 0)
                {
                    graphicsBuffer.UnlockBufferAfterWrite<byte>(m_BufferChunkSize);

                    DispatchUploads(numOps, graphicsBuffer);

                    frameData.m_Buffers.Push(graphicsBufferID);
                }
                else
                {
                    graphicsBuffer.UnlockBufferAfterWrite<byte>(0);
                    m_UploadBufferPool.PutBufferId(graphicsBufferID);
                }
            }

            m_FrameData.Add(frameData);

            if (m_MappedBuffers.IsCreated)
                m_MappedBuffers.Dispose();

            StepFrame();
        }

        /// <summary>
        /// Requests pruning of upload buffers. The actual release will happen in FrameCleanup.
        /// </summary>
        /// <param name="requestedMaxSizeRetainedInBytes">Maximum memory target to keep alive in upload buffer pool. Only buffers marked as free will be pruned, so the memory retained might be more than requested.</param>
        public void PruneUploadBufferPoolOnFrameCleanup(int requestedMaxSizeRetainedInBytes)
        {
            m_RequestedUploadBufferPoolMaxSizeBytes = requestedMaxSizeRetainedInBytes;
            m_PruneUploadBufferPool = true;
        }

        /// <summary>
        /// Cleans up internal data and recovers buffers into the free buffer pool.
        /// </summary>
        /// <remarks>
        /// It's best practice to call this once at the end of every frame.
        /// </remarks>
        public void FrameCleanup()
        {
            var numBuffers = m_ThreadData->m_NumBuffers;

            if (numBuffers > 0)
            {
                // These buffers were never used, so they get returned to the pool at once
                for (int iBuf = 0; iBuf < numBuffers; ++iBuf)
                {
                    var mappedBuffer = m_MappedBuffers[iBuf];
                    MappedBuffer.UnpackMarker(mappedBuffer.m_Marker, out var operationOffset, out var dataOffset);
                    var graphicsBufferID = mappedBuffer.m_BufferID;
                    var graphicsBuffer = m_UploadBufferPool.GetBufferFromId(graphicsBufferID);

                    graphicsBuffer.UnlockBufferAfterWrite<byte>(0);
                    m_UploadBufferPool.PutBufferId(graphicsBufferID);
                }

                m_MappedBuffers.Dispose();
            }

            if (m_PruneUploadBufferPool)
            {
                m_UploadBufferPool.PruneFreeBuffers(m_RequestedUploadBufferPoolMaxSizeBytes);
                m_PruneUploadBufferPool = false;
            }

            StepFrame();
        }

        /// <summary>
        /// Calculates statistics about the current and previous frame uploads.
        /// </summary>
        /// <returns>Returns a new statistics struct that contains information about the frame uploads.</returns>
        public SparseUploaderStats ComputeStats()
        {
            var stats = default(SparseUploaderStats);

            var totalUploadMemory = m_UploadBufferPool.TotalBufferSize;
            stats.BytesGPUMemoryUsed = totalUploadMemory;
            stats.BytesGPUMemoryUploadedCurr = m_CurrentFrameUploadSize;
            stats.BytesGPUMemoryUploadedMax = m_MaxUploadSize;

            return stats;
        }
    }
}
