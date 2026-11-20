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
    /// An unmanaged and Burst-compatible interface for SparseUploader.
    /// </summary>
    /// <remarks>
    /// This should be created each frame by a call to SparseUploader.Begin and is later returned by a call to SparseUploader.EndAndCommit.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ThreadedSparseUploader
    {
        // TODO: safety handle?
        [NativeDisableUnsafePtrRestriction] internal ThreadedSparseUploaderData* m_Data;

        /// <summary>
        /// Indicates whether the SparseUploader is valid and can be used.
        /// </summary>
        public bool IsValid => m_Data != null;

        private bool TryAlloc(int operationSize, int dataSize, out byte* ptr, out int operationOffset, out int dataOffset)
        {
            // Fetch current buffer and ensure we are not already out of GPU buffers to allocate from;
            var numBuffers = m_Data->m_NumBuffers;
            var buffer = m_Data->m_CurrBuffer;
            if (buffer < numBuffers)
            {
                do
                {
                    // Try to allocate from the current buffer
                    if (m_Data->m_Buffers[buffer].TryAlloc(operationSize, dataSize, out var p, out var op, out var d))
                    {
                        // Success, we can return true at onnce
                        ptr = p;
                        operationOffset = op;
                        dataOffset = d;
                        return true;
                    }

                    // Try to increment the buffer.
                    // If someone else has done this while we where trying to alloc we will use their
                    // value and du another iteration. Otherwise we will use our new value
                    buffer = Interlocked.CompareExchange(ref m_Data->m_CurrBuffer, buffer + 1, buffer);
                } while (buffer < m_Data->m_NumBuffers);
            }

            // We have run out of buffers, return false
            ptr = null;
            operationOffset = 0;
            dataOffset = 0;
            return false;
        }

        /// <summary>
        /// Adds a new pending upload operation to execute when you call SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// When this operation executes, the SparseUploader copies data from the source pointer.
        /// </remarks>
        /// <param name="src">The source pointer of data to upload.</param>
        /// <param name="size">The amount of data, in bytes, to read from the source pointer.</param>
        /// <param name="offsetInBytes">The destination offset of the data in the GPU buffer.</param>
        /// <param name="repeatCount">The number of times to repeat the source data in the destination buffer when uploading.</param>
        public void AddUpload(void* src, int size, int offsetInBytes, int repeatCount = 1)
        {
            var opsize = UnsafeUtility.SizeOf<Operation>();
            var allocSucceeded = TryAlloc(opsize, size, out var dst, out var operationOffset, out var dataOffset);

            if (!allocSucceeded)
            {
                Debug.Log("SparseUploader failed to allocate upload memory for AddUpload operation");
                return;
            }

            if (repeatCount <= 0)
                repeatCount = 1;

            // TODO: Vectorized memcpy
            UnsafeUtility.MemCpy(dst + dataOffset, src, size);
            var op = new Operation
            {
                type = (uint)OperationType.Upload,
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offsetInBytes,
                dstOffsetExtra = 0,
                size = (uint)size,
                count = (uint)repeatCount
            };
            UnsafeUtility.MemCpy(dst + operationOffset, &op, opsize);
        }

        /// <summary>
        /// Adds a new pending upload operation to execute when you call SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// When this operation executes, the SparseUploader copies data from the source value.
        /// </remarks>
        /// <param name="val">The source data to upload.</param>
        /// <param name="offsetInBytes">The destination offset of the data in the GPU buffer.</param>
        /// <param name="repeatCount">The number of times to repeat the source data in the destination buffer when uploading.</param>
        /// <typeparam name="T">Any unmanaged simple type.</typeparam>
        public void AddUpload<T>(T val, int offsetInBytes, int repeatCount = 1) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>();
            AddUpload(&val, size, offsetInBytes, repeatCount);
        }

        /// <summary>
        /// Adds a new pending upload operation to execute when you call SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// When this operation executes, the SparseUploader copies data from the source array.
        /// </remarks>
        /// <param name="array">The source array of data to upload.</param>
        /// <param name="offsetInBytes">The destination offset of the data in the GPU buffer.</param>
        /// <param name="repeatCount">The number of times to repeat the source data in the destination buffer when uploading.</param>
        /// <typeparam name="T">Any unmanaged simple type.</typeparam>
        public void AddUpload<T>(NativeArray<T> array, int offsetInBytes, int repeatCount = 1) where T : unmanaged
        {
            var size = UnsafeUtility.SizeOf<T>() * array.Length;
            AddUpload(array.GetUnsafeReadOnlyPtr(), size, offsetInBytes, repeatCount);
        }

        /// <summary>
        /// Options for the type of matrix to use in matrix uploads.
        /// </summary>
        public enum MatrixType
        {
            /// <summary>
            /// A float4x4 matrix.
            /// </summary>
            MatrixType4x4,

            /// <summary>
            /// A float3x4 matrix.
            /// </summary>
            MatrixType3x4,
        }

        private void MatrixUploadHelper(void* src, int numMatrices, int offset, int offsetInverse, MatrixType srcType, MatrixType dstType)
        {
            var size = numMatrices * sizeof(float3x4);
            var opsize = UnsafeUtility.SizeOf<Operation>();

            var allocSucceeded = TryAlloc(opsize, size, out var dst, out var operationOffset, out var dataOffset);

            if (!allocSucceeded)
            {
                Debug.Log("SparseUploader failed to allocate upload memory for AddMatrixUpload operation");
                return;
            }

            if (srcType == MatrixType.MatrixType4x4)
            {
                var srcLocal = (byte*)src;
                var dstLocal = dst + dataOffset;
                for (int i = 0; i < numMatrices; ++i)
                {
                    for (int j = 0; j < 4; ++j)
                    {
                        UnsafeUtility.MemCpy(dstLocal, srcLocal, 12);
                        dstLocal += 12;
                        srcLocal += 16;
                    }
                }
            }
            else
            {
                UnsafeUtility.MemCpy(dst + dataOffset, src, size);
            }

            var uploadType = (offsetInverse == -1) ? (uint)OperationType.Matrix_4x4 : (uint)OperationType.Matrix_Inverse_4x4;
            uploadType += (dstType == MatrixType.MatrixType3x4) ? 2u : 0u;

            var op = new Operation
            {
                type = uploadType,
                srcOffset = (uint)dataOffset,
                dstOffset = (uint)offset,
                dstOffsetExtra = (uint)offsetInverse,
                size = (uint)size,
                count = 1,
            };
            UnsafeUtility.MemCpy(dst + operationOffset, &op, opsize);
        }

        /// <summary>
        /// Adds a new pending matrix upload operation to execute when you call SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// When this operation executes, the SparseUploader copies data from the source pointer.
        /// </remarks>
        /// <param name="src">A pointer to a memory area that contains matrices of the type specified by srcType.</param>
        /// <param name="numMatrices">The number of matrices to upload.</param>
        /// <param name="offset">The destination offset of the copy part of the upload operation.</param>
        /// <param name="srcType">The source matrix format.</param>
        /// <param name="dstType">The destination matrix format.</param>
        public void AddMatrixUpload(void* src, int numMatrices, int offset, MatrixType srcType, MatrixType dstType)
        {
            MatrixUploadHelper(src, numMatrices, offset, -1, srcType, dstType);
        }

        /// <summary>
        /// Adds a new pending matrix upload operation to execute when you call SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// When this operation executes, the SparseUploader copies data from the source pointer.
        ///
        /// The upload operation automatically inverts matrices during the upload operation and it then stores the inverted matrices in a
        /// separate offset in the GPU buffer.
        /// </remarks>
        /// <param name="src">A pointer to a memory area that contains matrices of the type specified by srcType.</param>
        /// <param name="numMatrices">The number of matrices to upload.</param>
        /// <param name="offset">The destination offset of the copy part of the upload operation.</param>
        /// <param name="offsetInverse">The destination offset of the inverse part of the upload operation.</param>
        /// <param name="srcType">The source matrix format.</param>
        /// <param name="dstType">The destination matrix format.</param>
        public void AddMatrixUploadAndInverse(void* src, int numMatrices, int offset, int offsetInverse, MatrixType srcType, MatrixType dstType)
        {
            MatrixUploadHelper(src, numMatrices, offset, offsetInverse, srcType, dstType);
        }

        /// <summary>
        /// Adds a new pending upload operation to execute when you call SparseUploader.EndAndCommit.
        /// </summary>
        /// <remarks>
        /// When this operation executes, the SparseUploader copies data from the source pointer.
        ///
        /// The upload operations reads data with the specified source stride from the source pointer and then stores the data with the specified destination stride.
        /// </remarks>
        /// <param name="src">The source data pointer.</param>
        /// <param name="elemSize">The size of each data element to upload.</param>
        /// <param name="srcStride">The stride of each data element as stored in the source pointer.</param>
        /// <param name="count">The number of data elements to upload.</param>
        /// <param name="dstOffset">The destination offset</param>
        /// <param name="dstStride">The destination stride</param>
        public void AddStridedUpload(void* src, uint elemSize, uint srcStride, uint count, uint dstOffset, int dstStride)
        {
            int opSize = UnsafeUtility.SizeOf<Operation>();
            uint dataSize = count * srcStride;

            var allocSucceeded = TryAlloc(opSize, (int)dataSize, out var dst, out var operationOffset, out var dataOffset);

            if (!allocSucceeded)
            {
                Debug.Log("SparseUploader failed to allocate upload memory for AddStridedUpload operation");
                return;
            }

            UnsafeUtility.MemCpy(dst + dataOffset, src, dataSize);
            var op = new Operation
            {
                type = (uint)OperationType.StridedUpload,
                srcOffset = (uint)dataOffset,
                srcStride = srcStride,
                dstOffset = (uint)dstOffset,
                dstOffsetExtra = 0,
                dstStride = dstStride,
                size = elemSize,
                count = count,
            };
            UnsafeUtility.MemCpy(dst + operationOffset, &op, opSize);
        }
    }
}
