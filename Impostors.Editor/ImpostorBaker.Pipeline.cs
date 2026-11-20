using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    public static partial class ImpostorBaker
    {
        // Seven float4 targets/depth attachments, readbacks, managed snapshots,
        // packing and geometric-normal mip buffers stay within this budget.
        private const long CAPTURE_BYTES_PER_PIXEL = 608;
        private const long FINAL_BYTES_PER_PIXEL = 320;
        private const long PIPELINE_MEMORY_BUDGET = 192L * 1024 * 1024;
        private const int MAXIMUM_PIPELINE_SLOTS = 4;
        private const int RESERVED_PROCESSORS = 2;
        private const int MAIN_THREAD_BUDGET_MILLISECONDS = 8;
        private const int CAPTURE_CHANNELS = 7;
        private const int CAPTURE_DEPTH_BITS = 24;
        private const string PROGRESS_TITLE = "Baking ";
        private const string READBACK_FAILED = "An impostor capture GPU readback failed.";

        // Capture target order, as written by the capture shaders.
        private const int COLOR_CHANNEL = 0;
        private const int SURFACE_CHANNEL = 1;
        private const int SCALAR_CHANNEL = 2;
        private const int REFLECTANCE_CHANNEL = 3;
        private const int EMISSION_CHANNEL = 4;
        private const int DEPTH_CHANNEL = 5;
        private const int MATERIAL_CHANNEL = 6;

        public static int LastPipelineCapacity { get; private set; }

        /// <summary>
        /// Captures every frame through a small pool of GPU targets. Readbacks and CPU processing overlap with the next
        /// frames' rendering, and finished slices are written into the arrays on the main thread. Returns whether any
        /// covered texel emits.
        /// </summary>
        private static async Task<bool> CaptureFrames(string name, int frames, int captureSize, int size,
            BakedTextures textures, Action<int, RenderTexture[]> submit)
        {
            // Limit both CPU workers and GPU/CPU staging memory. Never enqueue the entire capture in RAM.
            long bytesPerSlot = (long)captureSize * captureSize * CAPTURE_BYTES_PER_PIXEL
                + (long)size * size * FINAL_BYTES_PER_PIXEL;
            int workers = Math.Min(MAXIMUM_PIPELINE_SLOTS, Math.Max(1, Environment.ProcessorCount - RESERVED_PROCESSORS));
            int capacity = (int)Math.Max(1, Math.Min(workers, PIPELINE_MEMORY_BUDGET / bytesPerSlot));
            LastPipelineCapacity = capacity;
            var pipeline = new CapturePipeline(frames, captureSize, size, textures, submit);
            try
            {
                pipeline.CreateSlots(capacity);
                var budget = Stopwatch.StartNew();
                while (pipeline.Completed < frames)
                {
                    CurrentCapture = $"{name} · completed {pipeline.Completed}/{frames} · queued {pipeline.Submitted}";
                    float progress = pipeline.Completed / (float)frames;
                    if (_cancellationRequested
                        || EditorUtility.DisplayCancelableProgressBar(PROGRESS_TITLE + name, CurrentCapture, progress))
                    {
                        throw new OperationCanceledException();
                    }

                    bool progressed = pipeline.SubmitFrames();
                    progressed |= pipeline.StartProcessing();
                    progressed |= await pipeline.WriteFinished(budget);
                    if (!progressed || budget.ElapsedMilliseconds >= MAIN_THREAD_BUDGET_MILLISECONDS)
                    {
                        EditorApplication.QueuePlayerLoopUpdate();
                        await Task.Yield();
                        budget.Restart();
                    }
                }
            }
            finally
            {
                await pipeline.DisposeAsync();
            }

            return pipeline.HasEmission;
        }

        // Only managed arrays and thread-safe math below: no textures, Editor APIs or asset writes on workers.
        private static PreparedSlice PrepareSlice(SliceChannels channels, int captureSize, int size, bool linearColors)
        {
            Downsample(channels, captureSize, size);
            var result = new PreparedSlice { HasEmission = HasEmission(channels) };
            float coverage = AlphaTestCoverage(channels.Colors);
            while (true)
            {
                if (_cancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                Dilate(channels, size);
                var stored = (Color[])channels.Colors.Clone();
                if (result.Colors.Count > 0)
                {
                    PreserveCoverage(stored, coverage);
                }

                if (linearColors)
                {
                    ToGamma(stored);
                }

                result.Add(stored, channels);
                if (size == 1)
                {
                    return result;
                }

                int nextSize = Math.Max(1, size / 2);
                Downsample(channels, size, nextSize);
                size = nextSize;
            }
        }

        private static bool HasEmission(SliceChannels channels)
        {
            for (int i = 0; i < channels.Colors.Length; i++)
            {
                Color emission = channels.Emission[i];
                if (channels.Colors[i].a > 0 && (emission.r != 0 || emission.g != 0 || emission.b != 0))
                {
                    return true;
                }
            }

            return false;
        }

        private static float AlphaTestCoverage(Color[] colors)
        {
            int covered = 0;
            foreach (Color color in colors)
            {
                if (color.a >= ALPHA_TEST_THRESHOLD)
                {
                    covered++;
                }
            }

            return covered / (float)colors.Length;
        }

        private static void ToGamma(Color[] colors)
        {
            for (int i = 0; i < colors.Length; i++)
            {
                Color c = colors[i];
                colors[i] = new Color(Mathf.LinearToGammaSpace(c.r), Mathf.LinearToGammaSpace(c.g),
                    Mathf.LinearToGammaSpace(c.b), c.a);
            }
        }

        /// <summary>Folds the scalar target into the surface and reflectance alphas and splits the workflow sign.</summary>
        private static SliceChannels PackCapture(Color[][] captured)
        {
            Color[] surface = captured[SURFACE_CHANNEL];
            Color[] scalars = captured[SCALAR_CHANNEL];
            Color[] reflectance = captured[REFLECTANCE_CHANNEL];
            Color[] materials = captured[MATERIAL_CHANNEL];
            for (int i = 0; i < surface.Length; i++)
            {
                surface[i].b = scalars[i].r;
                surface[i].a = scalars[i].g;
                reflectance[i].a = scalars[i].b;
                float sourceWorkflow = materials[i].b;
                materials[i].b = Math.Max(0, sourceWorkflow);
                materials[i].a = sourceWorkflow < 0 ? 1 : 0;
            }

            return new SliceChannels(captured[COLOR_CHANNEL], surface, captured[DEPTH_CHANNEL], reflectance,
                captured[EMISSION_CHANNEL], materials);
        }

        private static void WritePreparedSlice(BakedTextures textures, int frame, PreparedSlice prepared)
        {
            WriteChannel(textures.BaseColor, frame, prepared.Colors);
            WriteChannel(textures.Surface, frame, prepared.Surfaces);
            WriteChannel(textures.Depth, frame, prepared.Depths);
            WriteChannel(textures.Reflectance, frame, prepared.Reflectance);
            WriteChannel(textures.Emission, frame, prepared.Emission);
            if (textures.MaterialData)
            {
                WriteChannel(textures.MaterialData, frame, prepared.Materials);
            }
        }

        private static void WriteChannel(Texture2DArray array, int frame, List<Color[]> mips)
        {
            bool compressed = array.format is TextureFormat.BC7 or TextureFormat.BC4;
            TextureFormat source = array.format switch
            {
                TextureFormat.BC7 => TextureFormat.RGBA32,
                TextureFormat.BC4 => TextureFormat.R8,
                _ => array.format,
            };
            var texture = new Texture2D(array.width, array.height, source, true, !array.isDataSRGB);
            try
            {
                // A half-resolution array stores the capture's mip chain from its second level.
                int first = mips.Count - array.mipmapCount;
                for (int mip = 0; mip < array.mipmapCount; mip++)
                {
                    texture.SetPixels(mips[first + mip], mip);
                }

                // Compression reads CPU texture storage. Uploading this temporary texture first is wasted work.
                if (compressed)
                {
                    EditorUtility.CompressTexture(texture, array.format, TextureCompressionQuality.Best);
                }

                for (int mip = 0; mip < array.mipmapCount; mip++)
                {
                    array.SetPixelData(texture.GetPixelData<byte>(mip), mip, frame);
                }
            }
            finally
            {
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>The mip chain of every channel of one captured frame, ready to write into the arrays.</summary>
        private sealed class PreparedSlice
        {
            public readonly List<Color[]> Colors = new();
            public readonly List<Color[]> Surfaces = new();
            public readonly List<Color[]> Depths = new();
            public readonly List<Color[]> Reflectance = new();
            public readonly List<Color[]> Emission = new();
            public readonly List<Color[]> Materials = new();
            public bool HasEmission;

            public void Add(Color[] colors, SliceChannels channels)
            {
                Colors.Add(colors);
                Surfaces.Add(channels.Surfaces);
                Depths.Add(channels.Depths);
                Reflectance.Add(channels.Reflectance);
                Emission.Add(channels.Emission);
                Materials.Add(channels.Materials);
            }
        }

        /// <summary>The capture slots of one bake and the frames moving through them.</summary>
        private sealed class CapturePipeline
        {
            private readonly List<CaptureSlot> _slots = new();
            private readonly int _frames;
            private readonly int _captureSize;
            private readonly int _size;
            private readonly BakedTextures _textures;
            private readonly Action<int, RenderTexture[]> _submit;
            private readonly bool _asynchronous = SystemInfo.supportsAsyncGPUReadback;
            private readonly bool _linearColors = QualitySettings.activeColorSpace == ColorSpace.Linear;

            public CapturePipeline(int frames, int captureSize, int size, BakedTextures textures,
                Action<int, RenderTexture[]> submit)
            {
                _frames = frames;
                _captureSize = captureSize;
                _size = size;
                _textures = textures;
                _submit = submit;
            }

            public int Submitted { get; private set; }
            public int Completed { get; private set; }
            public bool HasEmission { get; private set; }

            public void CreateSlots(int capacity)
            {
                for (int i = 0; i < capacity; i++)
                {
                    _slots.Add(new CaptureSlot(_captureSize));
                }
            }

            /// <summary>Fills the GPU queue before spending time on compression/asset work on the main thread.</summary>
            public bool SubmitFrames()
            {
                bool progressed = false;
                foreach (CaptureSlot slot in _slots)
                {
                    if (slot.Frame >= 0 || Submitted >= _frames)
                    {
                        continue;
                    }

                    slot.Frame = Submitted++;
                    _submit(slot.Frame, slot.Targets);
                    slot.Readback(_asynchronous);
                    progressed = true;
                }

                return progressed;
            }

            /// <summary>Starts all ready CPU jobs before committing any of their results.</summary>
            public bool StartProcessing()
            {
                bool progressed = false;
                foreach (CaptureSlot slot in _slots)
                {
                    if (slot.Frame < 0 || slot.Processing != null || !slot.Ready)
                    {
                        continue;
                    }

                    if (slot.Pending && slot.ReadbackError)
                    {
                        throw new InvalidOperationException(READBACK_FAILED);
                    }

                    slot.Pending = false;
                    slot.Processing = Task.Run(() => PrepareSlice(PackCapture(slot.Snapshot()), _captureSize, _size,
                        _linearColors));
                    progressed = true;
                }

                return progressed;
            }

            /// <summary>Writes finished slices into the arrays until the main-thread budget runs out.</summary>
            public async Task<bool> WriteFinished(Stopwatch budget)
            {
                bool progressed = false;
                foreach (CaptureSlot slot in _slots)
                {
                    if (slot.Processing is not { IsCompleted: true })
                    {
                        continue;
                    }

                    PreparedSlice prepared = await slot.Processing;
                    WritePreparedSlice(_textures, slot.Frame, prepared);
                    HasEmission |= prepared.HasEmission;
                    slot.Processing = null;
                    slot.Frame = -1;
                    Completed++;
                    progressed = true;
                    if (budget.ElapsedMilliseconds >= MAIN_THREAD_BUDGET_MILLISECONDS)
                    {
                        break;
                    }
                }

                return progressed;
            }

            public async Task DisposeAsync()
            {
                foreach (CaptureSlot slot in _slots)
                {
                    if (slot.Processing == null)
                    {
                        continue;
                    }

                    try
                    {
                        await slot.Processing;
                    }
                    catch
                    {
                        // Preserve the original failure or cancellation.
                    }
                }

                foreach (CaptureSlot slot in _slots)
                {
                    slot.Dispose();
                }
            }
        }

        /// <summary>One set of capture targets and their readback buffers.</summary>
        private sealed class CaptureSlot : IDisposable
        {
            public readonly RenderTexture[] Targets = new RenderTexture[CAPTURE_CHANNELS];
            public readonly NativeArray<Color>[] Pixels = new NativeArray<Color>[CAPTURE_CHANNELS];
            public Task<PreparedSlice> Processing;
            public int Frame = -1;
            public bool Pending;
            public bool ReadbackError;

            private readonly AsyncGPUReadbackRequest[] _requests = new AsyncGPUReadbackRequest[CAPTURE_CHANNELS];
            private int _finished;
            private int _started;

            public CaptureSlot(int size)
            {
                try
                {
                    for (int i = 0; i < Targets.Length; i++)
                    {
                        Targets[i] = Target(size, RenderTextureFormat.ARGBFloat, CAPTURE_DEPTH_BITS);
                        Pixels[i] = new NativeArray<Color>(checked(size * size), Allocator.Persistent,
                            NativeArrayOptions.UninitializedMemory);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public bool Ready => !Pending || Volatile.Read(ref _finished) == Targets.Length;

            public void Readback(bool asynchronous)
            {
                _finished = 0;
                _started = 0;
                ReadbackError = false;
                for (int i = 0; i < Targets.Length; i++)
                {
                    if (!asynchronous)
                    {
                        Pixels[i].CopyFrom(Read(Targets[i]));
                        continue;
                    }

                    _requests[i] = AsyncGPUReadback.RequestIntoNativeArray(ref Pixels[i], Targets[i], 0,
                        TextureFormat.RGBAFloat, Finished);
                    _started++;
                    Pending = true;
                }
            }

            /// <summary>Copies the readback buffers into managed arrays that worker threads own.</summary>
            public Color[][] Snapshot()
            {
                var captured = new Color[CAPTURE_CHANNELS][];
                for (int channel = 0; channel < captured.Length; channel++)
                {
                    captured[channel] = Pixels[channel].ToArray();
                }

                return captured;
            }

            public void Dispose()
            {
                for (int i = 0; i < Targets.Length; i++)
                {
                    if (Pending && i < _started && !_requests[i].done)
                    {
                        _requests[i].WaitForCompletion();
                    }

                    if (Pixels[i].IsCreated)
                    {
                        Pixels[i].Dispose();
                    }

                    if (Targets[i])
                    {
                        Targets[i].Release();
                        Object.DestroyImmediate(Targets[i]);
                    }
                }
            }

            private void Finished(AsyncGPUReadbackRequest request)
            {
                if (request.hasError)
                {
                    ReadbackError = true;
                }

                Interlocked.Increment(ref _finished);
            }
        }
    }
}
