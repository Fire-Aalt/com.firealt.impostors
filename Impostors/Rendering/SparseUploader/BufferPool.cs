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
    internal class BufferPool : IDisposable
    {
        private List<GraphicsBuffer> m_Buffers;
        private Stack<int> m_FreeBufferIds;
        private Stack<int> m_BuffersReleased;

        private int m_Count;
        private int m_Stride;
        private GraphicsBuffer.Target m_Target;
        private GraphicsBuffer.UsageFlags m_UsageFlags;


        public BufferPool(int count, int stride, GraphicsBuffer.Target target, GraphicsBuffer.UsageFlags usageFlags)
        {
            m_Buffers = new List<GraphicsBuffer>();
            m_FreeBufferIds = new Stack<int>();
            m_BuffersReleased = new Stack<int>();

            m_Count = count;
            m_Stride = stride;
            m_Target = target;
            m_UsageFlags = usageFlags;
        }

        public void Dispose()
        {
            for (int i = 0; i < m_Buffers.Count; ++i)
            {
                if (m_Buffers[i].IsValid())
                {
                    m_Buffers[i].Dispose();
                }
            }
        }

        private int AllocateBuffer()
        {
            var cb = new GraphicsBuffer(m_Target, m_UsageFlags, m_Count, m_Stride);
            cb.name = "SparseUploaderBuffer";
            if (m_BuffersReleased.Count > 0)
            {
                var id = m_BuffersReleased.Pop();
                m_Buffers[id] = cb;
                return id;
            }
            else
            {
                var id = m_Buffers.Count;
                m_Buffers.Add(cb);
                return id;
            }
        }

        public int GetBufferId()
        {
            if (m_FreeBufferIds.Count == 0)
                return AllocateBuffer();

            return m_FreeBufferIds.Pop();
        }

        public GraphicsBuffer GetBufferFromId(int id)
        {
            return m_Buffers[id];
        }

        public void PutBufferId(int id)
        {
            m_FreeBufferIds.Push(id);
        }

        /*
         * Prune free buffers to allow up to maxMemoryToRetainInBytes to remain.
         * Note that this will only release buffers that are marked free, so the actual memory retained might be higher than requested
         */
        public void PruneFreeBuffers(int maxMemoryToRetainInBytes)
        {
            int memoryToFree = TotalBufferSize - maxMemoryToRetainInBytes;
            if (memoryToFree <= 0) return;

            while (memoryToFree > 0 && m_FreeBufferIds.Count > 0)
            {
                var id = m_FreeBufferIds.Pop();
                var buffer = GetBufferFromId(id);
                buffer.Dispose();
                m_BuffersReleased.Push(id);
                memoryToFree -= m_Count * m_Stride;
            }
        }

        public int TotalBufferCount => m_Buffers.Count - m_BuffersReleased.Count;
        public int TotalBufferSize => TotalBufferCount * m_Count * m_Stride;
    }
}
