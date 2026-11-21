using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    /// <summary>
    /// Holds the most recent coarse farthest-depth map of the occlusion camera and the matrix it was rendered with.
    /// Culling tests instance bounds against it, so the data is one to three frames old.
    /// </summary>
    internal static class ImpostorOcclusion
    {
        public const int TILE = 16;
        private const float MAXIMUM_CAMERA_DRIFT = 4;

        private static NativeArray<float> _depth;
        private static NativeArray<float> _empty;
        private static int2 _size;
        private static float4x4 _viewProjection;
        private static float3 _cameraPosition;
        private static bool _valid;

        public static bool Enabled { get; set; }

        /// <summary>Readback rows run bottom-up, so APIs whose clip space starts at the top see the map flipped.</summary>
        public static bool FlipY { get; set; } = SystemInfo.graphicsUVStartsAtTop;

        public static void Store(AsyncGPUReadbackRequest request, float4x4 viewProjection, float3 cameraPosition)
        {
            if (request.hasError || !Enabled)
            {
                return;
            }

            NativeArray<float> data = request.GetData<float>();
            var size = new int2(request.width, request.height);
            if (!_depth.IsCreated || _depth.Length != data.Length)
            {
                Dispose();
                _depth = new NativeArray<float>(data.Length, Allocator.Persistent);
            }

            _depth.CopyFrom(data);
            _size = size;
            _viewProjection = viewProjection;
            _cameraPosition = cameraPosition;
            _valid = true;
        }

        /// <summary>Returns the map when it was captured near <paramref name="cameraPosition"/>, otherwise an empty one.</summary>
        public static bool TryGet(float3 cameraPosition, out NativeArray<float> depth, out int2 size, out float4x4 viewProjection)
        {
            if (!_empty.IsCreated)
            {
                _empty = new NativeArray<float>(1, Allocator.Persistent);
            }

            bool usable = Enabled && _valid && math.distance(cameraPosition, _cameraPosition) < MAXIMUM_CAMERA_DRIFT;
            depth = usable ? _depth : _empty;
            size = usable ? _size : int2.zero;
            viewProjection = _viewProjection;
            return usable;
        }

        public static void Invalidate()
        {
            _valid = false;
        }

        public static void Dispose()
        {
            _valid = false;
            if (_depth.IsCreated)
            {
                _depth.Dispose();
            }
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterCleanup()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                Dispose();
                if (_empty.IsCreated)
                {
                    _empty.Dispose();
                }
            };
        }
#endif
    }
}
