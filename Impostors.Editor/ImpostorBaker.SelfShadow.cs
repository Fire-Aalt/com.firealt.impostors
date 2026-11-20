using System;
using System.IO;
using System.Threading.Tasks;
using Impostors.Data;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    public static partial class ImpostorBaker
    {
        private const string SELF_SHADOW_COMPUTE = "Impostors.Editor/Shaders/BakeSelfShadowVolume.compute";
        private const string BASE_MIP_PROPERTY = "_BaseMip";
        private const string INTERIOR_PADDING_PROPERTY = "_InteriorPadding";
        private const int THREAD_GROUP_SIZE = 8;
        private const int BC4_BLOCK_BYTES = 8;
        private const int BLOCK_SIZE = 4;
        private const int COMPRESSION_YIELD_MASK = 15;
        private const float DISPATCH_PROGRESS = 0.9f;
        private const float READBACK_PROGRESS = 0.92f;
        private const float COMPRESSION_PROGRESS = 0.93f;
        private const float COMPRESSION_PROGRESS_RANGE = 0.05f;
        private const float SAVE_PROGRESS = 0.99f;
        private const string INVALID_DEFINITION = "A valid definition with finite, nonempty bounds is required.";
        private const string UNUSED_OUTPUT = "Choose an unused .asset output path.";
        private const string NO_COMPUTE = "Self shadow derivation requires compute, 3D textures and GPU readback.";
        private const string NO_BC4 = "Compact self-shadow baking requires BC4-compressed 3D texture support.";
        private const string NO_R8_SAMPLING = "The GPU cannot linearly sample R8 self shadow volumes.";
        private const string NO_VOLUME_PROPERTY = "The definition's shader has no _SelfShadowVolume property.";
        private const string MISMATCHED_ARRAYS =
            "Base coverage and depth must have matching dimensions and every frame slice.";
        private const string ATLAS_TOO_LARGE_3D = "The self-shadow atlas {0} exceeds the GPU's maximum 3D texture size "
            + "({1}). Reduce self-shadow quality or the angle matrix.";
        private const string NO_RANDOM_WRITE = "The GPU cannot write either R8 or R16 float self shadow volumes.";
        private const string MISSING_COMPUTE = "The self shadow derivation compute shader is missing.";
        private const string ALLOCATION_FAILED = "Could not allocate the self shadow derivation texture.";
        private const string READBACK_FAILED_SHADOW = "Self shadow GPU readback failed.";
        private const string UNEXPECTED_BC4 = "Self-shadow BC4 compression returned an unexpected texture layout.";
        private const string CAPTURE_TARGET_SUFFIX = " derived self shadow capture";
        private const string DIRECTION_PROGRESS = " self shadow direction ";
        private const string READBACK_PROGRESS_LABEL = " self shadow GPU readback";
        private const string VISIBILITY_SUFFIX = " Self Shadow Visibility";
        private const string SAVING_PROGRESS = " saving self shadow volume";
        private const string COMPRESSING_PROGRESS = "Compressing self-shadow volume: ";
        private const string SELF_SHADOW_TITLE = "Deriving impostor self shadow";

        // Filtering/bias describes the lighting footprint, independently of the cache's storage grid.
        // Reducing cache resolution must not also weaken canopy shadows by enlarging the comparison bias.
        private static readonly Vector3Int SelfShadowFilterResolution = new(64, 96, 64);

        public static async Task<Texture3D> BakeDerivedSelfShadow(ImpostorDefinition definition, string outputPath,
            ImpostorSelfShadowQuality quality)
        {
            outputPath = ValidateSelfShadowBake(definition, outputPath);
            int frameCount = checked(definition.Frames * definition.Frames);
            int baseMip = CoverageMip(definition, frameCount);
            ImpostorSelfShadowLayout layout = ImpostorSelfShadowLayout.ForBounds(definition.Bounds, quality);
            Vector3Int atlasSize = layout.AtlasSize(definition.Frames);
            int maximum = SystemInfo.maxTexture3DSize;
            if (atlasSize.x > maximum || atlasSize.y > maximum || atlasSize.z > maximum)
            {
                throw new NotSupportedException(string.Format(ATLAS_TOO_LARGE_3D, atlasSize, maximum));
            }

            RenderTextureFormat format = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8)
                ? RenderTextureFormat.R8
                : RenderTextureFormat.RHalf;
            if (!SystemInfo.SupportsRandomWriteOnRenderTextureFormat(format))
            {
                throw new NotSupportedException(NO_RANDOM_WRITE);
            }

            var compute = ImpostorPackageAssets.Load<ComputeShader>(SELF_SHADOW_COMPUTE);
            if (!compute)
            {
                throw new InvalidOperationException(MISSING_COMPUTE);
            }

            _isBaking = true;
            _cancellationRequested = false;
            var previous = new SelfShadowState(definition);
            var readback = new VolumeReadback();
            RenderTexture target = null;
            Texture3D texture = null;
            bool createdAsset = false;
            bool assigned = false;
            try
            {
                target = CreateVolumeTarget(definition, atlasSize, format);
                DispatchFrames(compute, definition, target, layout, baseMip, frameCount);
                CurrentCapture = definition.name + READBACK_PROGRESS_LABEL;
                NativeArray<byte> pixels = await readback.Read(target, atlasSize, format == RenderTextureFormat.R8);
                byte[] compressed = await CompressSelfShadow(pixels, atlasSize);
                texture = new Texture3D(atlasSize.x, atlasSize.y, atlasSize.z, TextureFormat.BC4, false, true)
                {
                    name = definition.name + VISIBILITY_SUFFIX,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = 0,
                };
                texture.SetPixelData(compressed, 0);
                texture.Apply(false, true);
                CheckSelfShadowCancellation(definition.name + SAVING_PROGRESS, SAVE_PROGRESS);
                if (outputPath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                    AssetDatabase.CreateAsset(texture, outputPath);
                    createdAsset = true;
                    AssetDatabase.SaveAssetIfDirty(texture);
                }

                // Publish only a complete saved volume. Existing captures and their
                // content hash are unchanged by this derived lighting representation.
                assigned = true;
                new SelfShadowState(texture, layout, definition.ContentHash, texture, true).Apply(definition, outputPath != null);
                return texture;
            }
            catch
            {
                if (assigned)
                {
                    assigned = false;
                    previous.Apply(definition, outputPath != null);
                }

                if (createdAsset)
                {
                    AssetDatabase.DeleteAsset(outputPath);
                    texture = null;
                }

                throw;
            }
            finally
            {
                // A cancelled or failed poll still owns a GPU request that refers
                // to the UAV. Finish it before releasing its underlying resource.
                readback.Dispose();
                if (target)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }

                if (texture && !assigned && !AssetDatabase.Contains(texture))
                {
                    Object.DestroyImmediate(texture);
                }

                EditorUtility.ClearProgressBar();
                CurrentCapture = IDLE;
                _isBaking = false;
            }
        }

        /// <summary>Validates the inputs and device support. Returns the writable output asset path, if any.</summary>
        private static string ValidateSelfShadowBake(ImpostorDefinition definition, string outputPath)
        {
            if (!IsValidSelfShadowSource(definition))
            {
                throw new ArgumentException(INVALID_DEFINITION);
            }

            if (_isBaking)
            {
                throw new InvalidOperationException(BAKE_RUNNING);
            }

            if (EditorUtility.IsPersistent(definition))
            {
                ImpostorPackageAssets.RequireWritableAssetPath(AssetDatabase.GetAssetPath(definition));
            }

            if (EditorUtility.IsPersistent(definition.Material))
            {
                ImpostorPackageAssets.RequireWritableAssetPath(AssetDatabase.GetAssetPath(definition.Material));
            }

            if (outputPath != null)
            {
                outputPath = ImpostorPackageAssets.RequireWritableAssetPath(outputPath, ".asset");
                if (File.Exists(outputPath))
                {
                    throw new ArgumentException(UNUSED_OUTPUT, nameof(outputPath));
                }
            }

            RequireSelfShadowSupport();
            if (!definition.Material.HasProperty(ImpostorShaderProperties.SelfShadowVolume))
            {
                throw new InvalidOperationException(NO_VOLUME_PROPERTY);
            }

            return outputPath;
        }

        private static bool IsValidSelfShadowSource(ImpostorDefinition definition)
        {
            if (!definition || !definition.IsValid || definition.Frames < 2 || string.IsNullOrEmpty(definition.ContentHash))
            {
                return false;
            }

            Bounds bounds = definition.Bounds;
            return float.IsFinite(definition.Radius) && float.IsFinite(bounds.center.sqrMagnitude)
                && float.IsFinite(bounds.size.sqrMagnitude) && bounds.size is { x: > 0, y: > 0, z: > 0 };
        }

        private static void RequireSelfShadowSupport()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supports3DTextures || !SystemInfo.supportsAsyncGPUReadback)
            {
                throw new NotSupportedException(NO_COMPUTE);
            }

            if (!SystemInfo.supportsCompressed3DTextures || !SystemInfo.SupportsTextureFormat(TextureFormat.BC4))
            {
                throw new NotSupportedException(NO_BC4);
            }

            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, GraphicsFormatUsage.Sample | GraphicsFormatUsage.Linear))
            {
                throw new NotSupportedException(NO_R8_SAMPLING);
            }
        }

        /// <summary>Compact bakes store depth below the base colour resolution. Coverage is read from the matching mip.</summary>
        private static int CoverageMip(ImpostorDefinition definition, int frameCount)
        {
            Texture2DArray baseColor = definition.BaseColor;
            Texture2DArray depth = definition.Depth;
            int baseMip = 0;
            while (baseColor.width >> baseMip > depth.width)
            {
                baseMip++;
            }

            if (depth.depth < frameCount || baseColor.depth < frameCount || baseColor.width >> baseMip != depth.width
                || baseColor.height >> baseMip != depth.height)
            {
                throw new ArgumentException(MISMATCHED_ARRAYS);
            }

            return baseMip;
        }

        private static RenderTexture CreateVolumeTarget(ImpostorDefinition definition, Vector3Int atlasSize,
            RenderTextureFormat format)
        {
            var target = new RenderTexture(atlasSize.x, atlasSize.y, 0, format, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = atlasSize.z,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = definition.name + CAPTURE_TARGET_SUFFIX,
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (!target.Create())
            {
                Object.DestroyImmediate(target);
                throw new InvalidOperationException(ALLOCATION_FAILED);
            }

            return target;
        }

        /// <summary>Queues one dispatch per frame before yielding, so CPU asset imports can overlap the GPU work.</summary>
        private static void DispatchFrames(ComputeShader compute, ImpostorDefinition definition, RenderTexture target,
            ImpostorSelfShadowLayout layout, int baseMip, int frameCount)
        {
            int kernel = compute.FindKernel(ImpostorComputeKernels.BAKE_SELF_SHADOW_FRAME);
            ConfigureCompute(compute, kernel, definition, target, layout, baseMip);
            Vector3Int brickSize = layout.BrickSize;
            for (int frame = 0; frame < frameCount; frame++)
            {
                CurrentCapture = definition.name + DIRECTION_PROGRESS + (frame + 1) + "/" + frameCount;
                CheckSelfShadowCancellation(CurrentCapture, frame / (float)frameCount * DISPATCH_PROGRESS);
                compute.SetInt(ImpostorShaderProperties.Frame, frame);
                compute.Dispatch(kernel, ThreadGroups(brickSize.x), ThreadGroups(brickSize.y), ThreadGroups(brickSize.z));
            }
        }

        private static int ThreadGroups(int size) => (size + THREAD_GROUP_SIZE - 1) / THREAD_GROUP_SIZE;

        private static void ConfigureCompute(ComputeShader compute, int kernel, ImpostorDefinition definition,
            RenderTexture target, ImpostorSelfShadowLayout layout, int baseMip)
        {
            Bounds bounds = definition.Bounds;
            Texture frameRectangles = definition.FrameRectangles ? definition.FrameRectangles : Texture2D.whiteTexture;
            compute.SetTexture(kernel, ImpostorShaderProperties.BaseArray, definition.BaseColor);
            compute.SetTexture(kernel, ImpostorShaderProperties.DepthArray, definition.Depth);
            compute.SetTexture(kernel, ImpostorShaderProperties.FrameRectangles, frameRectangles);
            compute.SetTexture(kernel, ImpostorShaderProperties.VisibilityVolume, target);
            compute.SetVector(ImpostorShaderProperties.BoundsMin, bounds.min);
            compute.SetVector(ImpostorShaderProperties.BoundsSize, bounds.size);
            compute.SetVector(ImpostorShaderProperties.CaptureCenter, definition.Center);
            compute.SetVector(ImpostorShaderProperties.VoxelWorldSize, new Vector3(
                bounds.size.x / (SelfShadowFilterResolution.x - 1),
                bounds.size.y / (SelfShadowFilterResolution.y - 1),
                bounds.size.z / (SelfShadowFilterResolution.z - 1)));
            compute.SetFloat(ImpostorShaderProperties.CaptureRadius, definition.Radius);
            compute.SetFloat(ImpostorShaderProperties.Border, definition.Border / (float)definition.Resolution);
            compute.SetInt(ImpostorShaderProperties.Frames, definition.Frames);
            compute.SetInt(ImpostorShaderProperties.HasFrameRectangles, definition.FrameRectangles ? 1 : 0);
            compute.SetInts(ImpostorShaderProperties.CaptureResolution, definition.Depth.width, definition.Depth.height);
            compute.SetInt(BASE_MIP_PROPERTY, baseMip);
            Vector3Int interior = layout.Interior;
            Vector3Int padding = layout.Padding;
            Vector3Int brickSize = layout.BrickSize;
            compute.SetInts(ImpostorShaderProperties.InteriorSize, interior.x, interior.y, interior.z);
            compute.SetInts(INTERIOR_PADDING_PROPERTY, padding.x, padding.y, padding.z);
            compute.SetInts(ImpostorShaderProperties.BrickSize, brickSize.x, brickSize.y, brickSize.z);
        }

        private static async Task<byte[]> CompressSelfShadow(NativeArray<byte> pixels, Vector3Int size)
        {
            int pixelsPerSlice = checked(size.x * size.y);
            int bytesPerSlice = checked(size.x / BLOCK_SIZE * (size.y / BLOCK_SIZE) * BC4_BLOCK_BYTES);
            var result = new byte[checked(bytesPerSlice * size.z)];
            for (int z = 0; z < size.z; z++)
            {
                CurrentCapture = COMPRESSING_PROGRESS + (z + 1) + "/" + size.z;
                CheckSelfShadowCancellation(CurrentCapture, COMPRESSION_PROGRESS + COMPRESSION_PROGRESS_RANGE * z / size.z);
                CompressSlice(pixels.GetSubArray(z * pixelsPerSlice, pixelsPerSlice), size, result, z * bytesPerSlice,
                    bytesPerSlice);
                // Keep cancellation and the Editor responsive without uploading temporary slices to the GPU.
                if ((z & COMPRESSION_YIELD_MASK) == COMPRESSION_YIELD_MASK)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    await Task.Yield();
                }
            }

            return result;
        }

        private static void CompressSlice(NativeArray<byte> pixels, Vector3Int size, byte[] output, int offset,
            int bytesPerSlice)
        {
            var slice = new Texture2D(size.x, size.y, TextureFormat.R8, false, true);
            try
            {
                slice.SetPixelData(pixels, 0);
                EditorUtility.CompressTexture(slice, TextureFormat.BC4, TextureCompressionQuality.Best);
                NativeArray<byte> blocks = slice.GetPixelData<byte>(0);
                if (slice.format != TextureFormat.BC4 || blocks.Length != bytesPerSlice)
                {
                    throw new InvalidOperationException(UNEXPECTED_BC4);
                }

                NativeArray<byte>.Copy(blocks, 0, output, offset, bytesPerSlice);
            }
            finally
            {
                Object.DestroyImmediate(slice);
            }
        }

        private static void CheckSelfShadowCancellation(string message, float progress)
        {
            if (_cancellationRequested || EditorUtility.DisplayCancelableProgressBar(SELF_SHADOW_TITLE, message, progress))
            {
                throw new OperationCanceledException();
            }
        }

        /// <summary>The self-shadow fields of a definition and its material, captured so a failed publish can restore them.</summary>
        private sealed class SelfShadowState
        {
            private readonly Texture3D _volume;
            private readonly ImpostorSelfShadowLayout _layout;
            private readonly string _sourceHash;
            private readonly Texture _materialVolume;
            private readonly bool _keyword;

            public SelfShadowState(ImpostorDefinition definition)
                : this(definition.SelfShadowVolume, definition.SelfShadowLayout, definition.SelfShadowSourceHash,
                    definition.Material.GetTexture(ImpostorShaderProperties.SelfShadowVolume),
                    definition.Material.IsKeywordEnabled(ImpostorShaderKeywords.SELF_SHADOW_VOLUME))
            {
            }

            public SelfShadowState(Texture3D volume, ImpostorSelfShadowLayout layout, string sourceHash, Texture materialVolume,
                bool keyword)
            {
                _volume = volume;
                _layout = layout;
                _sourceHash = sourceHash;
                _materialVolume = materialVolume;
                _keyword = keyword;
            }

            public void Apply(ImpostorDefinition definition, bool save)
            {
                definition.SelfShadowVolume = _volume;
                definition.SelfShadowLayout = _layout;
                definition.SelfShadowSourceHash = _sourceHash;
                definition.Material.SetTexture(ImpostorShaderProperties.SelfShadowVolume, _materialVolume);
                _layout.Configure(definition.Material);
                if (_keyword)
                {
                    definition.Material.EnableKeyword(ImpostorShaderKeywords.SELF_SHADOW_VOLUME);
                }
                else
                {
                    definition.Material.DisableKeyword(ImpostorShaderKeywords.SELF_SHADOW_VOLUME);
                }

                EditorUtility.SetDirty(definition);
                EditorUtility.SetDirty(definition.Material);
                if (save && EditorUtility.IsPersistent(definition))
                {
                    AssetDatabase.SaveAssetIfDirty(definition);
                }

                if (save && EditorUtility.IsPersistent(definition.Material))
                {
                    AssetDatabase.SaveAssetIfDirty(definition.Material);
                }
            }
        }

        /// <summary>Reads the visibility volume back as 8-bit voxels, owning the request and its buffers until disposed.</summary>
        private sealed class VolumeReadback : IDisposable
        {
            private AsyncGPUReadbackRequest _request;
            private bool _pending;
            private NativeArray<byte> _bytes;
            private NativeArray<float> _floats;

            public async Task<NativeArray<byte>> Read(RenderTexture target, Vector3Int size, bool bytes)
            {
                int expected = checked(size.x * size.y * size.z);
                bool complete = false;
                bool failed = false;
                Action<AsyncGPUReadbackRequest> finished = request =>
                {
                    failed = request.hasError;
                    complete = true;
                };
                if (bytes)
                {
                    _bytes = new NativeArray<byte>(expected, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    _request = AsyncGPUReadback.RequestIntoNativeArray(ref _bytes, target, 0, TextureFormat.R8, finished);
                }
                else
                {
                    _floats = new NativeArray<float>(expected, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    _request = AsyncGPUReadback.RequestIntoNativeArray(ref _floats, target, 0, TextureFormat.RFloat, finished);
                }

                _pending = true;
                while (!complete)
                {
                    CheckSelfShadowCancellation(CurrentCapture, READBACK_PROGRESS);
                    EditorApplication.QueuePlayerLoopUpdate();
                    await Task.Yield();
                }

                _pending = false;
                if (failed)
                {
                    throw new InvalidOperationException(READBACK_FAILED_SHADOW);
                }

                if (!_bytes.IsCreated)
                {
                    NativeArray<float> floats = _floats;
                    byte[] quantized = await Task.Run(() => Quantize(floats));
                    _bytes = new NativeArray<byte>(quantized, Allocator.Persistent);
                }

                return _bytes;
            }

            public void Dispose()
            {
                if (_pending && !_request.done)
                {
                    _request.WaitForCompletion();
                }

                if (_bytes.IsCreated)
                {
                    _bytes.Dispose();
                }

                if (_floats.IsCreated)
                {
                    _floats.Dispose();
                }
            }

            private static byte[] Quantize(NativeArray<float> values)
            {
                var result = new byte[values.Length];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(values[i]) * byte.MaxValue);
                }

                return result;
            }
        }
    }
}
