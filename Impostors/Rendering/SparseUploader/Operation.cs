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
    internal struct Operation
    {
        public uint type;
        public uint srcOffset;
        public uint srcStride;
        public uint dstOffset;
        public uint dstOffsetExtra;
        public int dstStride;
        public uint size;
        public uint count;
    }
}
