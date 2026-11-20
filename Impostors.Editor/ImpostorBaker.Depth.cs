using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    public static partial class ImpostorBaker
    {
        private const int DEPTH_CAPTURE_MODE = 5;
        private const int REFINED_DEPTH_FILTER_VERSION = 1;
        private const string INCOMPLETE_BAKE = "A complete bake with per-frame rectangles is required.";
        private const string DEPTH_BAKE_RUNNING = "A bake is already running.";
        private const string DEPTH_PROGRESS = " depth ";
        private const string REFINING_TITLE = "Refining ";
        private const string DEPTH_BUFFER_NAME = "Impostor depth refinement";
        private const string FOREGROUND_DEPTH_HASH = "/foreground-depth-1";

        /// <summary>Rebuilds only the inexpensive, uncompressed depth stream. BC7 attributes stay intact.</summary>
        public static async Task RefineDepth(GameObject source, ImpostorDefinition definition, ImpostorBakeSettings settings)
        {
            if (!source || !definition || !settings || !definition.FrameRectangles)
            {
                throw new ArgumentException(INCOMPLETE_BAKE);
            }

            ImpostorPackageAssets.RequireWritableAssetPath(AssetDatabase.GetAssetPath(definition));
            Renderer[] renderers = GetCaptureRenderers(source, settings.SourceLod);
            if (definition.DepthFilterVersion >= REFINED_DEPTH_FILTER_VERSION)
            {
                return;
            }

            if (_isBaking)
            {
                throw new InvalidOperationException(DEPTH_BAKE_RUNNING);
            }

            _isBaking = true;
            _cancellationRequested = false;
            var parts = new List<Part>();
            var vertices = new List<Vector3>();
            RenderTexture target = null;
            Texture2DArray array = null;
            bool asynchronousShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                CollectDepthParts(source, renderers, parts, vertices);
                int resolution = definition.Resolution;
                int captureSize = resolution * settings.Supersampling;
                int slices = definition.Frames * definition.Frames;
                array = new Texture2DArray(resolution, resolution, slices, TextureFormat.R16, true, true)
                {
                    name = definition.name + DEPTH_SUFFIX,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                };
                target = Target(captureSize, RenderTextureFormat.RFloat, CAPTURE_DEPTH_BITS);
                for (int frame = 0; frame < slices; frame++)
                {
                    CurrentCapture = definition.name + DEPTH_PROGRESS + (frame + 1) + "/" + slices;
                    float progress = frame / (float)slices;
                    if (_cancellationRequested
                        || EditorUtility.DisplayCancelableProgressBar(REFINING_TITLE + definition.name, CurrentCapture, progress))
                    {
                        throw new OperationCanceledException();
                    }

                    RenderDepth(definition, parts, vertices, frame, target);
                    float[] depths = Read(target).Select(color => color.r).ToArray();
                    if (captureSize != resolution)
                    {
                        depths = ReduceDepth(depths, captureSize, resolution);
                    }

                    WriteDepthMips(array, frame, depths, resolution);
                    await Task.Yield();
                }

                array.Apply(false, true);
                CommitRefinedDepth(definition, array);
                array = null;
            }
            finally
            {
                foreach (Part part in parts)
                {
                    Object.DestroyImmediate(part.Material);
                }

                if (target)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }

                if (array && !AssetDatabase.Contains(array))
                {
                    Object.DestroyImmediate(array);
                }

                _isBaking = false;
                ShaderUtil.allowAsyncCompilation = asynchronousShaders;
                CurrentCapture = IDLE;
                EditorUtility.ClearProgressBar();
            }
        }

        private static void CollectDepthParts(GameObject source, Renderer[] renderers, List<Part> parts,
            List<Vector3> vertices)
        {
            foreach (Renderer renderer in renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                Matrix4x4 matrix = source.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                vertices.AddRange(mesh.vertices.Select(matrix.MultiplyPoint3x4));
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    Material material = CaptureMaterial(renderer.sharedMaterials[submesh]);
                    parts.Add(new Part { Mesh = mesh, Matrix = matrix, Material = material, Pass = material.FindPass(ImpostorShaderPasses.FORWARD_LIT), Submesh = submesh });
                }
            }
        }

        private static void RenderDepth(ImpostorDefinition definition, List<Part> parts, List<Vector3> vertices, int frame,
            RenderTexture target)
        {
            float interior = 1 - 2 * definition.Border / (float)definition.Resolution;
            var view = CaptureFrame.Fit(frame, definition.Frames, vertices, definition.Center, definition.Radius, interior);
            using var commands = new CommandBuffer { name = DEPTH_BUFFER_NAME };
            commands.SetRenderTarget(target);
            commands.ClearRenderTarget(true, true, Color.clear);
            using (new ImpostorCaptureState(commands, view.View, view.Projection, view.CameraProjection, target.width))
            {
                var properties = new MaterialPropertyBlock();
                properties.SetInteger(ImpostorShaderProperties.ImpostorCaptureMode, DEPTH_CAPTURE_MODE);
                properties.SetVector(ImpostorShaderProperties.CaptureCenter, definition.Center);
                properties.SetVector(ImpostorShaderProperties.CaptureDirection, view.Direction);
                properties.SetFloat(ImpostorShaderProperties.CaptureRadius, definition.Radius);
                foreach (Part part in parts)
                {
                    commands.DrawMesh(part.Mesh, part.Matrix, part.Material, part.Submesh, part.Pass, properties);
                }
            }

            Graphics.ExecuteCommandBuffer(commands);
        }

        private static void WriteDepthMips(Texture2DArray array, int frame, float[] depths, int size)
        {
            for (int mip = 0; mip < array.mipmapCount; mip++)
            {
                var stored = (float[])depths.Clone();
                DilateDepth(stored, size);
                var encoded = new ushort[stored.Length];
                for (int i = 0; i < encoded.Length; i++)
                {
                    encoded[i] = (ushort)Mathf.RoundToInt(Mathf.Clamp01(stored[i]) * ushort.MaxValue);
                }

                array.SetPixelData(encoded, mip, frame);
                if (size > 1)
                {
                    depths = ReduceDepth(depths, size, size / 2);
                    size /= 2;
                }
            }
        }

        /// <summary>Swaps in the refined stream. Runs only after every slice and mip succeeded.</summary>
        private static void CommitRefinedDepth(ImpostorDefinition definition, Texture2DArray array)
        {
            Texture2DArray previous = definition.Depth;
            AssetDatabase.AddObjectToAsset(array, definition);
            definition.Depth = array;
            definition.DepthFilterVersion = REFINED_DEPTH_FILTER_VERSION;
            definition.ContentHash = Hash128.Compute(definition.ContentHash + FOREGROUND_DEPTH_HASH).ToString();
            definition.Material.SetTexture(ImpostorShaderProperties.DepthArray, array);
            EditorUtility.SetDirty(definition);
            EditorUtility.SetDirty(definition.Material);
            AssetDatabase.SaveAssetIfDirty(definition);
            // Cancelled work never removes the old stream.
            AssetDatabase.RemoveObjectFromAsset(previous);
            Object.DestroyImmediate(previous, true);
            AssetDatabase.SaveAssetIfDirty(definition);
        }

        /// <summary>Keeps the nearest depth of each block. Larger stored depth is nearer the camera.</summary>
        private static float[] ReduceDepth(float[] values, int oldSize, int size)
        {
            var output = new float[size * size];
            int step = oldSize / size;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nearest = 0;
                    for (int dy = 0; dy < step; dy++)
                    {
                        for (int dx = 0; dx < step; dx++)
                        {
                            nearest = Mathf.Max(nearest, values[(y * step + dy) * oldSize + x * step + dx]);
                        }
                    }

                    output[y * size + x] = nearest;
                }
            }

            return output;
        }

        private static void DilateDepth(float[] values, int size)
        {
            for (int iteration = 0; iteration < DILATION_ITERATIONS; iteration++)
            {
                var previous = (float[])values.Clone();
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int i = y * size + x;
                        if (!(previous[i] > 0))
                        {
                            values[i] = NearestNeighborDepth(previous, size, x, y);
                        }
                    }
                }
            }
        }

        private static float NearestNeighborDepth(float[] values, int size, int x, int y)
        {
            int i = y * size + x;
            float depth = 0;
            if (x > 0)
            {
                depth = Mathf.Max(depth, values[i - 1]);
            }

            if (x + 1 < size)
            {
                depth = Mathf.Max(depth, values[i + 1]);
            }

            if (y > 0)
            {
                depth = Mathf.Max(depth, values[i - size]);
            }

            if (y + 1 < size)
            {
                depth = Mathf.Max(depth, values[i + size]);
            }

            return depth;
        }
    }
}
