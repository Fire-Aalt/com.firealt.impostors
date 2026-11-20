using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    /// <remarks>
    /// Direct BRG draws split every 256 instances, the size of the visible-instance constant buffer.
    /// Indirect draws read that list from a buffer of any length. Culling jobs send it through
    /// <see cref="SparseUploader"/>, so the main thread only begins and commits the upload.
    /// </remarks>
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        private const string UNSUPPORTED_DEVICE = "Impostors cannot render on {0} ({1}). The device does not support: {2}.";
        private const string MISSING_COMPUTE = "compute shaders";
        private const string MISSING_INDIRECT_ARGUMENTS = "indirect draw arguments";
        private const string MISSING_RAW_BUFFERS = "raw graphics buffers";
        private const int MINIMUM_VISIBLE_CAPACITY = 16 * 1024;
        private const int MINIMUM_ARGUMENT_CAPACITY = 256;
        private const int MINIMUM_OPERATION_CAPACITY = 256;
        private const int VISIBLE_CHUNK_BYTES = 1024 * 1024;
        private const int ARGUMENT_CHUNK_BYTES = 64 * 1024;
        private const int ARGUMENT_WORDS = 5;
        private const int CAMERA_SLOT = 0;
        private const int LIGHT_SLOT = 1;

        // Expected counts decay by 1/128 per frame, so a brief spike does not pin large buffers.
        private const int EXPECTATION_DECAY_SHIFT = 7;

        // A pass's draws are submitted before the next pass culls. One extra pass also covers a camera rendered
        // from inside another camera's rendering, whose outer draws are still pending when the inner pass finishes.
        private const int RECYCLE_AFTER_PASSES = 2;

        /// <summary>Draws camera and shadow views indirectly. Editor picking and selection stay direct.</summary>
        public static bool IndirectDraws = true;

        // Marks Unity's own culling calls, which are the only ones followed by a finished-culling callback.
        private static readonly IntPtr EngineCullingContext = new(1);
        private static readonly ProfilerMarker FinishMarker = new("Impostors.FinishIndirect");

        private readonly int[] _expectedVisible = new int[2];
        private readonly int[] _expectedCommands = new int[2];
        private readonly int[] _expectedOperations = new int[2];

        // Unity's finished-culling callbacks so far. Edit mode culls many passes without advancing the frame count.
        private int _finishedPasses;

        private static void ThrowIfDeviceUnsupported()
        {
            // A headless player has no device to draw with, so it never reaches the indirect path.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return;
            }

            var missing = new List<string>();
            if (!SystemInfo.supportsComputeShaders)
            {
                missing.Add(MISSING_COMPUTE);
            }

            if (!SystemInfo.supportsIndirectArgumentsBuffer)
            {
                missing.Add(MISSING_INDIRECT_ARGUMENTS);
            }

            if (BatchRendererGroup.BufferTarget != BatchBufferTarget.RawBuffer)
            {
                missing.Add(MISSING_RAW_BUFFERS);
            }

            if (missing.Count > 0)
            {
                throw new NotSupportedException(string.Format(UNSUPPORTED_DEVICE, SystemInfo.graphicsDeviceName,
                    SystemInfo.graphicsDeviceType, string.Join(", ", missing)));
            }
        }

        private static void Expect(int[] expected, int slot, int actual)
        {
            expected[slot] = math.max(actual, expected[slot] - (expected[slot] >> EXPECTATION_DECAY_SHIFT));
        }

        private void BeginIndirectView(ViewState view, IndirectDrawTarget* target, BatchCullingViewType viewType,
            IntPtr user)
        {
            *target = default;
            int slot = viewType switch
            {
                BatchCullingViewType.Camera => CAMERA_SLOT,
                BatchCullingViewType.Light => LIGHT_SLOT,
                _ => -1,
            };
            if (slot < 0 || !IndirectDraws || user != EngineCullingContext)
            {
                return;
            }

            int visible = math.max(MINIMUM_VISIBLE_CAPACITY, _expectedVisible[slot] + _expectedVisible[slot] / 4);
            int commands = math.max(MINIMUM_ARGUMENT_CAPACITY, _expectedCommands[slot] * 2);
            int operations = math.max(MINIMUM_OPERATION_CAPACITY, _expectedOperations[slot] * 2);
            view.Indirect ??= new IndirectViewBuffers(visible, commands);
            IndirectViewBuffers buffers = view.Indirect;
            buffers.EnsureCapacity(visible, commands);
            buffers.Slot = slot;
            buffers.LastFrame = Time.renderedFrameCount;
            buffers.LastPass = _finishedPasses;

            int biggestUpload = CullingViewScratch.MASKS_PER_WORK_ITEM * ChunkVisibilityMask.CAPACITY * sizeof(int);
            int argumentBytes = GraphicsBuffer.IndirectDrawIndexedArgs.size;
            buffers.VisibleWriter = buffers.VisibleUploader.Begin(visible * sizeof(int), biggestUpload, operations);
            buffers.ArgumentWriter = buffers.ArgumentUploader.Begin(commands * argumentBytes, argumentBytes, commands);
            buffers.Uploading = true;

            target->Available = true;
            target->VisibleUploader = buffers.VisibleWriter;
            target->ArgumentUploader = buffers.ArgumentWriter;
            target->VisibleBuffer = buffers.Visible.bufferHandle;
            target->ArgumentBuffer = buffers.Arguments.bufferHandle;
            target->VisibleCapacity = visible;
            target->ArgumentCapacity = commands;
            target->OperationCapacity = operations;
        }

        private void OnFinishedCulling(IntPtr customCullingResult)
        {
            FinishIndirectViews();
            _finishedPasses++;
        }

        /// <summary>
        /// Whether Unity has drawn everything that reads these buffers: a later frame has started, or enough culling
        /// passes have finished since they were filled.
        /// </summary>
        private bool CanRecycle(IndirectViewBuffers buffers)
        {
            return buffers.LastFrame != Time.renderedFrameCount || _finishedPasses - buffers.LastPass >= RECYCLE_AFTER_PASSES;
        }

        /// <summary>Waits for each view's jobs and dispatches its uploads. Must run before Unity draws those views.</summary>
        private void FinishIndirectViews()
        {
            using ProfilerMarker.AutoScope scope = FinishMarker.Auto();
            foreach (ViewState view in _views)
            {
                if (view?.Indirect is { Uploading: true })
                {
                    view.Handle.Complete();
                    CommitIndirectView(view);
                }
            }
        }

        private void CommitIndirectView(ViewState view)
        {
            IndirectViewBuffers buffers = view.Indirect;
            if (buffers is not { Uploading: true })
            {
                return;
            }

            buffers.Uploading = false;
            buffers.VisibleUploader.EndAndCommit(buffers.VisibleWriter);
            buffers.ArgumentUploader.EndAndCommit(buffers.ArgumentWriter);
            buffers.VisibleUploader.FrameCleanup();
            buffers.ArgumentUploader.FrameCleanup();

            IndirectDrawTarget target = view.Scratch.Indirect[0];
            if (target.Indirect && target.VisibleOutput != null)
            {
                UnsafeUtility.Free(target.VisibleOutput, Allocator.TempJob);
            }

            int slot = buffers.Slot;
            Expect(_expectedVisible, slot, target.VisibleCount);
            Expect(_expectedCommands, slot, target.CommandCount);
            Expect(_expectedOperations, slot, view.Scratch.WorkItems.Length);
            int retainedFrames = SparseUploader.NumFramesInFlight + 2;
            int retained = (_expectedVisible[slot] + _expectedVisible[slot] / 4) * sizeof(int) * retainedFrames;
            buffers.VisibleUploader.PruneUploadBufferPoolOnFrameCleanup(retained);
        }

        /// <summary>GPU buffers and uploaders that one view's indirect draws read from.</summary>
        private sealed class IndirectViewBuffers : IDisposable
        {
            public GraphicsBuffer Visible;
            public GraphicsBuffer Arguments;
            public SparseUploader VisibleUploader;
            public SparseUploader ArgumentUploader;
            public ThreadedSparseUploader VisibleWriter;
            public ThreadedSparseUploader ArgumentWriter;
            public bool Uploading;
            public int LastFrame = -1;
            public int LastPass;
            public int Slot;

            public IndirectViewBuffers(int visible, int commands)
            {
                Visible = CreateVisible(visible);
                Arguments = CreateArguments(commands);
                VisibleUploader = new SparseUploader(Visible, VISIBLE_CHUNK_BYTES);
                ArgumentUploader = new SparseUploader(Arguments, ARGUMENT_CHUNK_BYTES);
            }

            private int ArgumentCapacity => Arguments.count / ARGUMENT_WORDS;

            public void EnsureCapacity(int visible, int commands)
            {
                if (Visible.count < visible)
                {
                    Visible.Dispose();
                    Visible = CreateVisible(visible);
                    VisibleUploader.ReplaceBuffer(Visible);
                }

                if (ArgumentCapacity < commands)
                {
                    Arguments.Dispose();
                    Arguments = CreateArguments(commands);
                    ArgumentUploader.ReplaceBuffer(Arguments);
                }
            }

            public void Dispose()
            {
                VisibleUploader.Dispose();
                ArgumentUploader.Dispose();
                Visible.Dispose();
                Arguments.Dispose();
            }

            private static GraphicsBuffer CreateVisible(int count)
            {
                return new GraphicsBuffer(GraphicsBuffer.Target.Raw, count, sizeof(int));
            }

            // Raw is what SparseUploader's compute copy writes through.
            private static GraphicsBuffer CreateArguments(int commands)
            {
                const GraphicsBuffer.Target TARGET = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments;
                return new GraphicsBuffer(TARGET, commands * ARGUMENT_WORDS, sizeof(int));
            }
        }
    }
}
