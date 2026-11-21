using System;
using System.Collections.Generic;
using System.IO;
using Impostors.Data;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Scripting.APIUpdating;

namespace Impostors.Samples.Benchmark
{
    [MovedFrom(true, "Impostors", "Impostors.Runtime", "ImpostorBenchmark")]
    [ExecuteAlways, DefaultExecutionOrder(-100)]
    public sealed class ImpostorBenchmark : MonoBehaviour
    {
        [Serializable]
        public sealed class Report
        {
            public string Hardware;
            public string GraphicsApi;
            public string GraphicsThreading;
            public bool DevelopmentBuild;
            public string UnityVersion;
            public string Timestamp;
            public int Run;
            public int RequestedRuns;
            public int Frames;
            public int Width;
            public int Height;
            public int Msaa;
            public float RenderScale;
            public int VSyncCount;
            public int TargetFrameRate;
            public int PrototypeCount;
            public int DimensionX;
            public int DimensionY;
            public float RequestedCulledPercent;
            public int[] InstancesPerPrototype;
            public int SubmittedMinimum;
            public int SubmittedMaximum;
            public int DrawCommands;
            public int ChunkCount;
            public int CameraCallbacks;
            public int LightCallbacks;
            public ImpostorCullingStatistics LastCulling;
            public long NativeDrawCallsMinimum;
            public long NativeDrawCallsMaximum;
            public double NativeDrawCallsP95;
            public double MeasurementDurationSeconds;
            public double FrameP50Ms;
            public double FrameP95Ms;
            public double CpuP95Ms;
            public double CpuP50Ms;
            public double GpuP95Ms;
            public double GpuP50Ms;
            public double MainThreadP95Ms;
            public double RenderThreadP95Ms;
            public double PresentWaitP95Ms;
            public uint LastSyncInterval;
            public int UniqueTimingTimestampCount;
            public int RawTimingSamples;
            public double FrameHistogramBinWidthMs;
            public int[] FrameHistogram;
            public string TimingTraceFile;
            public double CullingP95Ms;
            public double CommandPrefixP95Ms;
            public double SubmitP95Ms;
            public double CullWorkersP95Ms;
            public double PackWorkersP95Ms;
            public string CullingTimingMeaning;
            public bool CullingWallTimingAvailable;
            public long ManagedAllocationMaximum;
            public long ImpostorGpuBytes;
            public int GpuTimingSamples;
            public int CullingTimingSamples;
            public bool FrameTimingAvailable;
            public bool CullingRecorderAvailable;
            public bool AllocationRecorderAvailable;
            public bool NativeDrawCounterAvailable;
            public bool AllInstanceBoundsInsideFrustum;
            public bool SampleBufferExhausted;
            public bool Passed;
            public int RenderTargetWidth;
            public int RenderTargetHeight;
            public int ProxyVertices;
            public int ProxyTriangles;
            public int CaptureGridAxis;
            public int PixelsPerCapturedView;
            public int EquivalentAtlasResolution;
            public bool SelfShadowsEnabled;
            public bool HullProxy;
        }

        [Serializable]
        public struct TimingSample
        {
            public int MeasurementFrame;
            public double WallFrameMs;
            public double WallMeasurementSeconds;
            public double CpuFrameMs;
            public double MainThreadMs;
            public double RenderThreadMs;
            public double PresentWaitMs;
            public double GpuMs;
            public ulong FrameStartTimestamp;
            public ulong FirstSubmitTimestamp;
            public ulong PresentCalledTimestamp;
            public ulong FrameCompleteTimestamp;
            public uint SyncInterval;
        }

        [Serializable]
        private sealed class TimingTrace
        {
            public string Notes;
            public ulong CpuTimerFrequency;
            public TimingSample[] Samples;
        }

        private const int SampleCapacity = 120000;
        private const int TraceCapacity = 256;
        private const int HistogramRegularBins = 400;
        private const double HistogramBinWidthMs = 0.25;
        private const int BenchmarkWidth = 3840;
        private const int BenchmarkHeight = 2160;
        private const int BenchmarkMsaaSamples = 4;
        private const int RequiredTreeCount = 100000;
        private const int RequiredPrototypeCount = 2;
        private const int RequiredCaptureGridAxis = 16;
        private const int RequiredPixelsPerView = 256;
        private const int RequiredInstancesPerPrototype = 50000;
        private const int RequiredRuns = 3;
        private const int MinimumDiagnosticRuns = 1;
        private const int MaximumNativeDrawCalls = 1024;
        private const int UncappedFrameRate = -1;
        private const int CompressedBlockExtent = 4;
        private const int ColorSurfaceBytesPerTexel = 4;
        private const int DepthBytesPerTexel = 2;
        private const int FrameRectangleBytesPerTexel = 16;
        private const long MaximumImpostorGpuBytes = 1610612736L;
        private const float RequiredWarmupSeconds = 20;
        private const float RequiredMeasurementSeconds = 60;
        private const float ScreenshotLeadSeconds = 3;
        private const float BenchmarkRenderScale = 1;
        private const double RequiredFramesPerSecond = 144;
        private const double RequiredCullingCpuMs = 1;
        private const double TimingAvailabilityFraction = 0.95;
        private const double MedianPercentile = 0.50;
        private const double UpperPercentile = 0.95;
        private const double MillisecondsPerSecond = 1000;
        private const double NanosecondsPerMillisecond = 1000000;
        private const string AllocationMarker = "GC Allocated In Frame";
        private const string CullCellsMarker = "Impostors.CullCells";
        private const string BuildCommandsMarker = "Impostors.BuildCommands";
        private const string PackVisibleMarker = "Impostors.PackVisible";
        private const string SubmitMarker = "Impostors.Submit";
        private const string ResetWorkersMarker = "Impostors.ResetWorkers";
        private const string FlushWorkersMarker = "Impostors.FlushWorkers";
        private const string CollectBinsMarker = "Impostors.CollectBins";
        private const string EmitCommandsMarker = "Impostors.EmitCommands";
        private const string DrawCallsMarker = "Draw Calls Count";
        private const string Direct3D12Name = "Direct3D12";
        private const string HardwareSeparator = " / ";
        private const string UtcTimestampFormat = "O";
        private const string ScreenshotNameFormat = "run-{0}.png";
        private const string ReportNameFormat = "run-{0}.json";
        private const string TraceNameFormat = "run-{0}-timing.json";
        private const string MissingSetupMessage = "The benchmark needs rendered impostors and a camera.";
        private const int SpawnBatch = 4096;
        private const string InvalidRunsMessage = "Diagnostic run count must be between one and three.";
        private const string CullingTimingDescription = "Summed CPU time across parallel culling/packing workers, command prefix, and main-thread submission. This is not elapsed culling critical-path time.";
        private const string TimingTraceNotes = "FrameTiming data is delayed relative to the current Update wall sample. Timings are recorded once per increasing nonzero frameStartTimestamp. WallFrameMs and WallMeasurementSeconds identify when the delayed sample was observed. The final frame histogram bin contains values at or above its lower bound.";
        private const string ResultLogFormat = "Impostor benchmark run {0}: p95 {1:F3} ms, submitted {2}, native draws {3}, passed {4}";
        [Tooltip("Baked tree definitions. Placements are generated in memory on load; no instance asset is needed.")]
        public GameObject[] Prefabs = Array.Empty<GameObject>();
        [NonSerialized] private ImpostorDefinition[] _definitionOverrides;
        public ImpostorDefinition[] Definitions {
            get => _definitionOverrides ?? Array.ConvertAll(Prefabs, p => p ? p.GetComponent<Impostor>()?.Definition : null);
            set => _definitionOverrides = value;
        }
        public Material GroundMaterial;
        public Camera Camera;
        [Min(1)] public int DimensionX = 400;
        [Min(1)] public int DimensionY = 250;
        [Range(0, 100)] public float CulledPercent;
        public MeshFilter Ground;
        [Min(0)] public float WarmupSeconds = RequiredWarmupSeconds;
        [Min(0.1f)] public float MeasurementSeconds = RequiredMeasurementSeconds;
        [Range(1, RequiredRuns)] public int Runs = RequiredRuns;
        private readonly double[] _frames = new double[SampleCapacity];
        private readonly double[] _cpu = new double[SampleCapacity];
        private readonly double[] _gpu = new double[SampleCapacity];
        private readonly double[] _mainThread = new double[SampleCapacity];
        private readonly double[] _renderThread = new double[SampleCapacity];
        private readonly double[] _presentWait = new double[SampleCapacity];
        private readonly double[] _culling = new double[SampleCapacity];
        private readonly double[] _prefixTimes = new double[SampleCapacity];
        private readonly double[] _submitTimes = new double[SampleCapacity];
        private readonly double[] _cullTimes = new double[SampleCapacity];
        private readonly double[] _packTimes = new double[SampleCapacity];
        private readonly double[] _nativeDrawCalls = new double[SampleCapacity];
        private readonly FrameTiming[] _timing = new FrameTiming[1];
        private readonly TimingSample[] _trace = new TimingSample[TraceCapacity];
        private readonly int[] _frameHistogram = new int[HistogramRegularBins + 1];
        private ProfilerRecorder _allocations;
        private ProfilerRecorder _cullCellsRecorder;
        private ProfilerRecorder _buildCommandsRecorder;
        private ProfilerRecorder _packVisibleRecorder;
        private ProfilerRecorder _submitRecorder;
        private ProfilerRecorder _resetWorkersRecorder;
        private ProfilerRecorder _flushWorkersRecorder;
        private ProfilerRecorder _collectBinsRecorder;
        private ProfilerRecorder _emitCommandsRecorder;
        private ProfilerRecorder _drawCallsRecorder;
        private double _phaseStart;
        private double _measurementStart;
        private double _previousFrame;
        private int _count;
        private int _run;
        private int _submittedMinimum;
        private int _submittedMaximum;
        private int _gpuTimingSamples;
        private int _cullingTimingSamples;
        private int _uniqueTimingTimestampCount;
        private int _traceCount;
        private ulong _lastTimingTimestamp;
        private uint _lastSyncInterval;
        private long _maximumAllocation;
        private long _minimumDrawCalls;
        private long _maximumDrawCalls;
        private bool _screenshotRequested;
        private bool _allBoundsInsideFrustum;
        private int[] _instancesPerPrototype;
        private string _directory;
        private World _world;
        private NativeList<Entity> _instances;
        private ImpostorWorldRenderer[] _renderers = Array.Empty<ImpostorWorldRenderer>();
        private bool _spawnPending;

        /// <summary>Entities this benchmark spawned into the default world.</summary>
        public int InstanceCount => _instances.IsCreated ? _instances.Length : 0;

        private void OnEnable()
        {
            if (Definitions != null && Definitions.Length > 0)
                RebuildInstances();
        }

        /// <summary>Replaces this benchmark's entities with a fresh deterministic population in the default world.</summary>
        public void RebuildInstances()
        {
            DestroyInstances();
            if (Definitions == null || Definitions.Length == 0)
                return;
            foreach (var definition in Definitions)
                if (!definition || !definition.IsValid)
                    return;
            long count = (long)DimensionX * DimensionY;
            if (DimensionX < 1 || DimensionY < 1 || count > ImpostorBenchmarkGenerator.MaximumInstanceCount)
                throw new ArgumentOutOfRangeException(nameof(DimensionX));
            // The Editor world can appear after scene objects are enabled; retry from Update.
            World world = World.DefaultGameObjectInjectionWorld;
            var system = world != null && world.IsCreated ? world.GetExistingSystemManaged<ImpostorRenderingSystem>() : null;
            _spawnPending = system == null;
            if (system == null)
                return;

            ImpostorDefinition[] definitions = Definitions;
            bool usePrefabs = _definitionOverrides == null && Prefabs.Length > 0;
            var prefabs = new Entity[definitions.Length];
            for (int p = 0; p < prefabs.Length; p++)
                prefabs[p] = usePrefabs ? system.CreatePrefab(Prefabs[p]) : system.CreatePrefab(definitions[p], gameObject.layer);

            _world = world;
            _instances = new NativeList<Entity>((int)count, Allocator.Persistent);
            var pending = new NativeArray<float4x4>[prefabs.Length];
            var counts = new int[prefabs.Length];
            try
            {
                for (int p = 0; p < pending.Length; p++)
                    pending[p] = new NativeArray<float4x4>(SpawnBatch, Allocator.Temp);
                foreach (ImpostorInstance placement in ImpostorBenchmarkGenerator.EnumerateInstances(DimensionX, DimensionY, prefabs.Length))
                {
                    int p = placement.Prototype;
                    var batch = pending[p];
                    batch[counts[p]++] = placement.Matrix;
                    if (counts[p] == SpawnBatch)
                    {
                        Spawn(system, prefabs[p], batch, counts[p]);
                        counts[p] = 0;
                    }
                }

                for (int p = 0; p < counts.Length; p++)
                    if (counts[p] > 0)
                        Spawn(system, prefabs[p], pending[p], counts[p]);
            }
            finally
            {
                foreach (var batch in pending)
                    if (batch.IsCreated)
                        batch.Dispose();
            }
        }

        private void Spawn(ImpostorRenderingSystem system, Entity prefab, NativeArray<float4x4> transforms, int count)
        {
            int start = _instances.Length;
            _instances.ResizeUninitialized(start + count);
            system.Instantiate(prefab, transforms.GetSubArray(0, count), _instances.AsArray().GetSubArray(start, count));
        }

        private void DestroyInstances()
        {
            _renderers = Array.Empty<ImpostorWorldRenderer>();
            if (!_instances.IsCreated)
                return;
            if (_world != null && _world.IsCreated)
            {
                EntityManager entities = _world.EntityManager;
                using var alive = new NativeList<Entity>(_instances.Length, Allocator.Temp);
                foreach (Entity entity in _instances)
                    if (entities.Exists(entity))
                        alive.Add(entity);
                entities.DestroyEntity(alive.AsArray());
            }

            _instances.Dispose();
            _world = null;
        }

        /// <summary>CPU-side copy of the generated placements, for layout tools.</summary>
        public ImpostorRenderData CreateRenderData()
        {
            return new ImpostorRenderData(Definitions, ImpostorBenchmarkGenerator.CreateInstances(DimensionX, DimensionY, Definitions.Length));
        }

        private ImpostorWorldRenderer[] ResolveRenderers()
        {
            var system = _world != null && _world.IsCreated ? _world.GetExistingSystemManaged<ImpostorRenderingSystem>() : null;
            if (system == null)
                return Array.Empty<ImpostorWorldRenderer>();
            var renderers = new List<ImpostorWorldRenderer>();
            ImpostorDefinition[] definitions = Definitions;
            foreach (ImpostorWorldRenderer renderer in system.Renderers)
                if (Array.Exists(definitions, renderer.UsesDefinition))
                    renderers.Add(renderer);
            return renderers.ToArray();
        }

        private bool HasLiveRenderers => _renderers.Length > 0 && _world != null && _world.IsCreated;

        private int SubmittedCount()
        {
            int submitted = 0;
            for (int i = 0; i < _renderers.Length; i++)
                submitted += _renderers[i].SubmittedCount;
            return submitted;
        }

        private bool AllBoundsInsideFrustum()
        {
            foreach (ImpostorWorldRenderer renderer in _renderers)
                if (!renderer.AreAllBoundsInsideFrustum(Camera))
                    return false;
            return true;
        }

        private int[] CountInstancesPerPrototype()
        {
            ImpostorDefinition[] definitions = Definitions;
            var counts = new int[definitions.Length];
            foreach (ImpostorWorldRenderer renderer in _renderers)
            {
                ImpostorDefinition[] registered = renderer.GetDefinitions();
                int[] registeredCounts = renderer.GetPrototypeCounts();
                for (int d = 0; d < definitions.Length; d++)
                {
                    int index = Array.IndexOf(registered, definitions[d]);
                    if (index >= 0)
                        counts[d] += registeredCounts[index];
                }
            }

            return counts;
        }

        public bool IsCapturing => _directory != null;

        // Tool-invoked, instance-local capture. Never serialized or started by a lifecycle callback.
        public void BeginCapture(string outputDirectory)
        {
            if (!Application.IsPlaying(gameObject) || !isActiveAndEnabled)
                throw new InvalidOperationException("Benchmark capture requires an active instance in the current Play session.");
            if (IsCapturing)
                throw new InvalidOperationException("Benchmark capture is already running.");
            if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
                throw new ArgumentException("Supply an absolute output directory for this capture.", nameof(outputDirectory));
            if (Runs < MinimumDiagnosticRuns || Runs > RequiredRuns)
                throw new InvalidOperationException(InvalidRunsMessage);
            _renderers = ResolveRenderers();
            if (_renderers.Length == 0 || !Camera)
            {
                throw new InvalidOperationException(MissingSetupMessage);
            }
            Directory.CreateDirectory(outputDirectory);
            try
            {
                _allocations = ProfilerRecorder.StartNew(ProfilerCategory.Memory, AllocationMarker);
                _cullCellsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, CullCellsMarker);
                _buildCommandsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, BuildCommandsMarker);
                _packVisibleRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, PackVisibleMarker);
                _submitRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, SubmitMarker);
                _resetWorkersRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, ResetWorkersMarker);
                _flushWorkersRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, FlushWorkersMarker);
                _collectBinsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, CollectBinsMarker);
                _emitCommandsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, EmitCommandsMarker);
                _drawCallsRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, DrawCallsMarker);
                _instancesPerPrototype = CountInstancesPerPrototype();

                _allBoundsInsideFrustum = AllBoundsInsideFrustum();
                _phaseStart = Time.realtimeSinceStartupAsDouble;
                _previousFrame = _phaseStart;
                _run = 0;
                ResetRun();
                _directory = Path.GetFullPath(outputDirectory);
            }
            catch
            {
                StopCapture();
                throw;
            }
        }

        private void Update()
        {
            if (_spawnPending || (_instances.IsCreated && (_world == null || !_world.IsCreated)))
                RebuildInstances();
            if (_directory == null)
                return;
            if (!Application.IsPlaying(gameObject) || !HasLiveRenderers || !Camera)
            {
                StopCapture();
                return;
            }
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = now - _phaseStart;
            double frameMs = (now - _previousFrame) * MillisecondsPerSecond;
            _previousFrame = now;
            if (!_screenshotRequested && elapsed >= WarmupSeconds - ScreenshotLeadSeconds)
            {
                _allBoundsInsideFrustum = AllBoundsInsideFrustum();
                string path = Path.Combine(_directory, string.Format(ScreenshotNameFormat, _run + 1));
                ScreenCapture.CaptureScreenshot(path);
                _screenshotRequested = true;
            }

            if (elapsed < WarmupSeconds)
            {
                return;
            }

            if (_count == 0)
            {
                _measurementStart = now;
            }

            if (_count < SampleCapacity)
            {
                _frames[_count] = frameMs;
                int histogramBin = Math.Min(HistogramRegularBins, Math.Max(0, (int)(frameMs / HistogramBinWidthMs)));
                _frameHistogram[histogramBin]++;
                if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
                {
                    RecordTiming(_timing[0], now, frameMs);
                }

                _culling[_count] = (MarkerNanoseconds(_cullCellsRecorder) + MarkerNanoseconds(_buildCommandsRecorder)
                    + MarkerNanoseconds(_packVisibleRecorder) + MarkerNanoseconds(_submitRecorder)
                    + MarkerNanoseconds(_resetWorkersRecorder) + MarkerNanoseconds(_flushWorkersRecorder)
                    + MarkerNanoseconds(_collectBinsRecorder) + MarkerNanoseconds(_emitCommandsRecorder)) / NanosecondsPerMillisecond;
                _prefixTimes[_count] = MarkerNanoseconds(_buildCommandsRecorder) / NanosecondsPerMillisecond;
                _submitTimes[_count] = MarkerNanoseconds(_submitRecorder) / NanosecondsPerMillisecond;
                _cullTimes[_count] = MarkerNanoseconds(_cullCellsRecorder) / NanosecondsPerMillisecond;
                _packTimes[_count] = MarkerNanoseconds(_packVisibleRecorder) / NanosecondsPerMillisecond;
                if (HasMarkerSample(_cullCellsRecorder) && HasMarkerSample(_buildCommandsRecorder)
                    && HasMarkerSample(_packVisibleRecorder) && HasMarkerSample(_submitRecorder)
                    && HasMarkerSample(_resetWorkersRecorder) && HasMarkerSample(_flushWorkersRecorder)
                    && HasMarkerSample(_collectBinsRecorder) && HasMarkerSample(_emitCommandsRecorder))
                {
                    _cullingTimingSamples++;
                }

                _maximumAllocation = Math.Max(_maximumAllocation, _allocations.Valid ? _allocations.LastValue : 0);
                long drawCalls = _drawCallsRecorder.Valid ? _drawCallsRecorder.LastValue : 0;
                _nativeDrawCalls[_count] = drawCalls;
                _minimumDrawCalls = Math.Min(_minimumDrawCalls, drawCalls);
                _maximumDrawCalls = Math.Max(_maximumDrawCalls, drawCalls);
                int submitted = SubmittedCount();
                _submittedMinimum = Math.Min(_submittedMinimum, submitted);
                _submittedMaximum = Math.Max(_submittedMaximum, submitted);
                _count++;
            }

            if (now - _measurementStart >= MeasurementSeconds || _count == SampleCapacity)
            {
                SaveReport(now);
                _run++;
                if (_run == Runs)
                {
                    StopCapture();
                    return;
                }

                ResetRun();
                _phaseStart = Time.realtimeSinceStartupAsDouble;
                _previousFrame = _phaseStart;
            }
        }

        private void LateUpdate()
        {
            if (_directory != null)
                FrameTimingManager.CaptureFrameTimings();
        }

        private void RecordTiming(FrameTiming timing, double now, double frameMs)
        {
            _lastSyncInterval = timing.syncInterval;
            if (timing.frameStartTimestamp == 0 || timing.frameStartTimestamp <= _lastTimingTimestamp)
            {
                return;
            }
            int index = _uniqueTimingTimestampCount++;
            _lastTimingTimestamp = timing.frameStartTimestamp;
            _cpu[index] = timing.cpuFrameTime;
            _gpu[index] = timing.gpuFrameTime;
            _mainThread[index] = timing.cpuMainThreadFrameTime;
            _renderThread[index] = timing.cpuRenderThreadFrameTime;
            _presentWait[index] = timing.cpuMainThreadPresentWaitTime;
            if (timing.gpuFrameTime > 0)
            {
                _gpuTimingSamples++;
            }
            if (_traceCount < TraceCapacity)
            {
                _trace[_traceCount++] = new TimingSample
                {
                    MeasurementFrame = _count,
                    WallFrameMs = frameMs,
                    WallMeasurementSeconds = now - _measurementStart,
                    CpuFrameMs = timing.cpuFrameTime,
                    MainThreadMs = timing.cpuMainThreadFrameTime,
                    RenderThreadMs = timing.cpuRenderThreadFrameTime,
                    PresentWaitMs = timing.cpuMainThreadPresentWaitTime,
                    GpuMs = timing.gpuFrameTime,
                    FrameStartTimestamp = timing.frameStartTimestamp,
                    FirstSubmitTimestamp = timing.firstSubmitTimestamp,
                    PresentCalledTimestamp = timing.cpuTimePresentCalled,
                    FrameCompleteTimestamp = timing.cpuTimeFrameComplete,
                    SyncInterval = timing.syncInterval
                };
            }
        }

        private void ResetRun()
        {
            _count = 0;
            _screenshotRequested = false;
            _maximumAllocation = 0;
            _submittedMinimum = int.MaxValue;
            _submittedMaximum = 0;
            _minimumDrawCalls = long.MaxValue;
            _maximumDrawCalls = 0;
            _gpuTimingSamples = 0;
            _cullingTimingSamples = 0;
            _uniqueTimingTimestampCount = 0;
            _traceCount = 0;
            _lastTimingTimestamp = 0;
            _lastSyncInterval = 0;
            Array.Clear(_frameHistogram, 0, _frameHistogram.Length);
        }

        private void SaveReport(double endTime)
        {
            long bytes = 0;
            int drawCommands = 0, chunkCount = 0, cameraCallbacks = 0, lightCallbacks = 0;
            foreach (ImpostorWorldRenderer renderer in _renderers)
            {
                bytes += renderer.InstanceBufferBytes;
                drawCommands += renderer.DrawCommandCount;
                chunkCount += renderer.ChunkCount;
                cameraCallbacks += renderer.CameraCallbacks;
                lightCallbacks += renderer.LightCallbacks;
            }

            ImpostorDefinition[] definitions = Definitions;
            foreach (ImpostorDefinition definition in definitions)
            {
                int size = definition.Resolution;
                for (int mip = 0; mip < definition.BaseColor.mipmapCount; mip++)
                {
                    bytes += (long)Math.Max(CompressedBlockExtent, size) * Math.Max(CompressedBlockExtent, size)
                        * definition.Frames * definition.Frames * ColorSurfaceBytesPerTexel;
                    bytes += (long)size * size * definition.Frames * definition.Frames * DepthBytesPerTexel;
                    if (definition.Emission) bytes += (long)size * size * definition.Frames * definition.Frames * 8;
                    size = Math.Max(1, size / 2);
                }

                if (definition.FrameRectangles)
                {
                    bytes += (long)definition.FrameRectangles.width * definition.FrameRectangles.height * FrameRectangleBytesPerTexel;
                }
                if (definition.HasValidSelfShadowVolume)
                {
                    bytes += definition.SelfShadowLayout.Bytes(definition.Frames,
                        definition.SelfShadowVolume.format == TextureFormat.BC4);
                }
            }

            string traceFile = SaveTimingTrace();
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var report = new Report
            {
                Hardware = SystemInfo.graphicsDeviceName + HardwareSeparator + SystemInfo.processorType,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                GraphicsThreading = SystemInfo.renderingThreadingMode.ToString(),
                DevelopmentBuild = Debug.isDebugBuild,
                UnityVersion = Application.unityVersion,
                Timestamp = DateTime.UtcNow.ToString(UtcTimestampFormat),
                Run = _run + 1,
                RequestedRuns = Runs,
                Frames = _count,
                Width = Screen.width,
                Height = Screen.height,
                Msaa = pipeline ? pipeline.msaaSampleCount : QualitySettings.antiAliasing,
                RenderScale = pipeline ? pipeline.renderScale : 0,
                VSyncCount = QualitySettings.vSyncCount,
                TargetFrameRate = Application.targetFrameRate,
                PrototypeCount = _instancesPerPrototype.Length,
                DimensionX = DimensionX,
                DimensionY = DimensionY,
                RequestedCulledPercent = CulledPercent,
                InstancesPerPrototype = _instancesPerPrototype,
                SubmittedMinimum = _submittedMinimum,
                SubmittedMaximum = _submittedMaximum,
                DrawCommands = drawCommands,
                ChunkCount = chunkCount,
                CameraCallbacks = cameraCallbacks,
                LightCallbacks = lightCallbacks,
                LastCulling = _renderers.Length > 0 ? _renderers[0].Statistics : default,
                NativeDrawCallsMinimum = _minimumDrawCalls,
                NativeDrawCallsMaximum = _maximumDrawCalls,
                NativeDrawCallsP95 = Percentile(_nativeDrawCalls, _count, UpperPercentile),
                MeasurementDurationSeconds = endTime - _measurementStart,
                FrameP50Ms = Percentile(_frames, _count, MedianPercentile),
                FrameP95Ms = Percentile(_frames, _count, UpperPercentile),
                CpuP50Ms = Percentile(_cpu, _uniqueTimingTimestampCount, MedianPercentile),
                CpuP95Ms = Percentile(_cpu, _uniqueTimingTimestampCount, UpperPercentile),
                GpuP50Ms = Percentile(_gpu, _uniqueTimingTimestampCount, MedianPercentile),
                GpuP95Ms = Percentile(_gpu, _uniqueTimingTimestampCount, UpperPercentile),
                MainThreadP95Ms = Percentile(_mainThread, _uniqueTimingTimestampCount, UpperPercentile),
                RenderThreadP95Ms = Percentile(_renderThread, _uniqueTimingTimestampCount, UpperPercentile),
                PresentWaitP95Ms = Percentile(_presentWait, _uniqueTimingTimestampCount, UpperPercentile),
                LastSyncInterval = _lastSyncInterval,
                UniqueTimingTimestampCount = _uniqueTimingTimestampCount,
                RawTimingSamples = _traceCount,
                FrameHistogramBinWidthMs = HistogramBinWidthMs,
                FrameHistogram = _frameHistogram,
                TimingTraceFile = traceFile,
                CullingP95Ms = Percentile(_culling, _count, UpperPercentile),
                CommandPrefixP95Ms = Percentile(_prefixTimes, _count, UpperPercentile),
                SubmitP95Ms = Percentile(_submitTimes, _count, UpperPercentile),
                CullWorkersP95Ms = Percentile(_cullTimes, _count, UpperPercentile),
                PackWorkersP95Ms = Percentile(_packTimes, _count, UpperPercentile),
                CullingTimingMeaning = CullingTimingDescription,
                CullingWallTimingAvailable = false,
                ManagedAllocationMaximum = _maximumAllocation,
                ImpostorGpuBytes = bytes,
                GpuTimingSamples = _gpuTimingSamples,
                CullingTimingSamples = _cullingTimingSamples,
                FrameTimingAvailable = _gpuTimingSamples >= _count * TimingAvailabilityFraction
                    && _uniqueTimingTimestampCount >= _count * TimingAvailabilityFraction,
                CullingRecorderAvailable = _cullingTimingSamples >= _count * TimingAvailabilityFraction,
                AllocationRecorderAvailable = _allocations.Valid,
                NativeDrawCounterAvailable = _drawCallsRecorder.Valid,
                AllInstanceBoundsInsideFrustum = _allBoundsInsideFrustum,
                SampleBufferExhausted = _count == SampleCapacity && endTime - _measurementStart < MeasurementSeconds
            };
            report.RenderTargetWidth = Camera.targetTexture ? Camera.targetTexture.width : Screen.width;
            report.RenderTargetHeight = Camera.targetTexture ? Camera.targetTexture.height : Screen.height;
            report.HullProxy = true;
            report.SelfShadowsEnabled = Array.TrueForAll(definitions, definition => definition.HasValidSelfShadowVolume);
            report.CaptureGridAxis = definitions[0].Frames;
            report.PixelsPerCapturedView = definitions[0].Resolution;
            report.EquivalentAtlasResolution = report.CaptureGridAxis * report.PixelsPerCapturedView;
            Mesh hull = ImpostorProxy.BuildPrototypeHull(definitions[0], ImpostorWorldRenderer.HullDetail);
            report.ProxyVertices = hull.vertexCount;
            report.ProxyTriangles = (int)hull.GetIndexCount(0) / 3;
            UnityEngine.Object.DestroyImmediate(hull);
            report.Passed = report.FrameP95Ms <= MillisecondsPerSecond / RequiredFramesPerSecond
                && report.CullingP95Ms < RequiredCullingCpuMs && report.ManagedAllocationMaximum == 0
                && report.ImpostorGpuBytes < MaximumImpostorGpuBytes && report.Width == BenchmarkWidth
                && report.Height == BenchmarkHeight && report.Msaa == BenchmarkMsaaSamples
                && report.RenderScale == BenchmarkRenderScale && report.VSyncCount == 0
                && report.TargetFrameRate == UncappedFrameRate && report.SubmittedMinimum == RequiredTreeCount
                && report.PrototypeCount == RequiredPrototypeCount
                && report.CaptureGridAxis == RequiredCaptureGridAxis && report.PixelsPerCapturedView == RequiredPixelsPerView
                && report.InstancesPerPrototype[0] == RequiredInstancesPerPrototype
                && report.InstancesPerPrototype[1] == RequiredInstancesPerPrototype
                && report.GraphicsApi == Direct3D12Name && report.FrameTimingAvailable && report.CullingRecorderAvailable
                && report.AllocationRecorderAvailable && report.NativeDrawCounterAvailable
                && report.NativeDrawCallsMaximum <= MaximumNativeDrawCalls && report.AllInstanceBoundsInsideFrustum
                && report.SelfShadowsEnabled && !report.SampleBufferExhausted
                && report.MeasurementDurationSeconds >= RequiredMeasurementSeconds
                && WarmupSeconds >= RequiredWarmupSeconds && Runs == RequiredRuns;
            File.WriteAllText(Path.Combine(_directory, string.Format(ReportNameFormat, _run + 1)), JsonUtility.ToJson(report, true));
            Debug.Log(string.Format(ResultLogFormat, report.Run, report.FrameP95Ms,
                report.SubmittedMinimum, report.NativeDrawCallsMaximum, report.Passed));
        }

        private string SaveTimingTrace()
        {
            var samples = new TimingSample[_traceCount];
            Array.Copy(_trace, samples, _traceCount);
            var trace = new TimingTrace
            {
                Notes = TimingTraceNotes,
                CpuTimerFrequency = FrameTimingManager.GetCpuTimerFrequency(),
                Samples = samples
            };
            string name = string.Format(TraceNameFormat, _run + 1);
            File.WriteAllText(Path.Combine(_directory, name), JsonUtility.ToJson(trace, true));
            return name;
        }

        public static bool AreAllBoundsInsideFrustum(ImpostorRenderData data, Camera camera)
        {
            Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
            foreach (ImpostorInstance instance in data.Instances)
            {
                Bounds local = data.Definitions[instance.Prototype].Bounds;
                Matrix4x4 matrix = instance.Matrix;
                Vector3 x = matrix.MultiplyVector(new Vector3(local.extents.x, 0, 0));
                Vector3 y = matrix.MultiplyVector(new Vector3(0, local.extents.y, 0));
                Vector3 z = matrix.MultiplyVector(new Vector3(0, 0, local.extents.z));
                Vector3 center = matrix.MultiplyPoint3x4(local.center);
                Vector3 extent = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
                foreach (Plane plane in planes)
                {
                    Vector3 normal = plane.normal;
                    float radius = Mathf.Abs(normal.x) * extent.x + Mathf.Abs(normal.y) * extent.y + Mathf.Abs(normal.z) * extent.z;
                    if (Vector3.Dot(normal, center) + plane.distance - radius < 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static double Percentile(double[] values, int count, double percentile)
        {
            Array.Sort(values, 0, count);
            return count == 0 ? 0 : values[Math.Min(count - 1, (int)Math.Ceiling(count * percentile) - 1)];
        }

        private static bool HasMarkerSample(ProfilerRecorder recorder)
        {
            return recorder.Valid && recorder.Count > 0 && recorder.GetSample(recorder.Count - 1).Count > 0;
        }

        private static long MarkerNanoseconds(ProfilerRecorder recorder)
        {
            return recorder.Valid ? recorder.LastValue : 0;
        }

        private void OnDisable()
        {
            StopCapture();
            DestroyInstances();
            _spawnPending = false;
        }

        public void StopCapture()
        {
            _directory = null;
            _allocations.Dispose();
            _cullCellsRecorder.Dispose();
            _buildCommandsRecorder.Dispose();
            _packVisibleRecorder.Dispose();
            _submitRecorder.Dispose();
            _resetWorkersRecorder.Dispose();
            _flushWorkersRecorder.Dispose();
            _collectBinsRecorder.Dispose();
            _emitCommandsRecorder.Dispose();
            _drawCallsRecorder.Dispose();
        }
    }
}
