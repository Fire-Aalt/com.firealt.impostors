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
    internal enum OperationType : int
    {
        Upload = 0,
        Matrix_4x4 = 1,
        Matrix_Inverse_4x4 = 2,
        Matrix_3x4 = 3,
        Matrix_Inverse_3x4 = 4,
        StridedUpload = 5,
    }
}
