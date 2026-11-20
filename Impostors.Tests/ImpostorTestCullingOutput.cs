using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Rendering;

namespace Impostors.Tests
{
    internal static unsafe class ImpostorTestCullingOutput
    {
        internal static int[] CountSplitInstances(BatchCullingOutputDrawCommands commands, int splitCount)
        {
            var counts = new int[splitCount];
            for (int i = 0; i < commands.drawCommandCount; i++)
            {
                var draw = commands.drawCommands[i];
                for (int split = 0; split < splitCount; split++)
                    if ((draw.splitVisibilityMask & (1 << split)) != 0) counts[split] += (int)draw.visibleCount;
            }
            return counts;
        }

        internal static void Release(BatchCullingOutputDrawCommands commands)
        {
            UnsafeUtility.Free(commands.drawCommands, Allocator.TempJob);
            UnsafeUtility.Free(commands.indirectDrawCommands, Allocator.TempJob);
            UnsafeUtility.Free(commands.drawRanges, Allocator.TempJob);
            UnsafeUtility.Free(commands.visibleInstances, Allocator.TempJob);
            UnsafeUtility.Free(commands.drawCommandPickingEntityIds, Allocator.TempJob);
        }
    }
}
