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
    internal unsafe struct MappedBuffer
    {
        public byte* m_Data;
        public long m_Marker;
        public int m_BufferID;

        public static long PackMarker(long operationOffset, long dataOffset)
        {
            return (dataOffset << 32) | (operationOffset & 0xFFFFFFFF);
        }

        public static void UnpackMarker(long marker, out long operationOffset, out long dataOffset)
        {
            operationOffset = marker & 0xFFFFFFFF;
            dataOffset = (marker >> 32) & 0xFFFFFFFF;
        }

        public bool TryAlloc(int operationSize, int dataSize, out byte* ptr, out int operationOffset, out int dataOffset)
        {
            long originalMarker;
            long newMarker;
            long currOperationOffset;
            long currDataOffset;
            do
            {
                // Read the marker as is right now
                originalMarker = Interlocked.Read(ref m_Marker);
                UnpackMarker(originalMarker, out currOperationOffset, out currDataOffset);

                // Calculate the new offsets for operation and data
                // Operations are stored in the beginning of the buffer
                // Data is stored at the end of the buffer
                var newOperationOffset = currOperationOffset + operationSize;
                var newDataOffset = currDataOffset - dataSize;

                // Check if there was enough space in the buffer for this allocation
                if (newDataOffset < newOperationOffset)
                {
                    // Not enough space, return false
                    ptr = null;
                    operationOffset = 0;
                    dataOffset = 0;
                    return false;
                }

                newMarker = PackMarker(newOperationOffset, newDataOffset);

                // Finally we try to CAS the new marker in.
                // If anyone has allocated from the buffer in the meantime this will fail and the loop will rerun
            } while (Interlocked.CompareExchange(ref m_Marker, newMarker, originalMarker) != originalMarker);

            // Now we have succeeded in getting a data slot out and can return true.
            ptr = m_Data;
            operationOffset = (int)currOperationOffset;
            dataOffset = (int)(currDataOffset - dataSize);
            return true;
        }
    }
}
