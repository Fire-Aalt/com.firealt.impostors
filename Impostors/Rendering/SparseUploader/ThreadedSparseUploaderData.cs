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
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ThreadedSparseUploaderData
    {
        [NativeDisableUnsafePtrRestriction] public MappedBuffer* m_Buffers;
        public int m_NumBuffers;
        public int m_CurrBuffer;
    }
}
