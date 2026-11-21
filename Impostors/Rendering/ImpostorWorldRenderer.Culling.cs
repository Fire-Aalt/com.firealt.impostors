using System;
using Impostors.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        private const int MAXIMUM_SPLITS = 16;
        private const int PLANES_PER_PACKET = 4;
        private const int WORKER_JOB_BATCH = 16;

        // Beyond this, depth bins order a chunk's rows closely enough at a fraction of the cost of an exact sort.
        private const float EXACT_ROW_SORT_DISTANCE = 512;
        private const string SPLIT_LIMIT = "BRG split visibility masks support at most 16 culling splits.";
        private static readonly ProfilerMarker SubmitMarker = new("Impostors.Submit");

        private JobHandle Cull(BatchRendererGroup group, BatchCullingContext context, BatchCullingOutput output, IntPtr user)
        {
            using ProfilerMarker.AutoScope scope = SubmitMarker.Auto();
            var result = (BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            *result = default;
#if UNITY_EDITOR
            if (!CountEditorCallback(context.viewType))
            {
                return default;
            }
#endif
            if (!ShouldCull(context))
            {
                return default;
            }

            float shadowDistance = float.PositiveInfinity;
            if (context.viewType == BatchCullingViewType.Light)
            {
                LightCallbacks++;
                shadowDistance = ImpostorsRendererFeature.ShadowCullingDistance(context.lodParameters.cameraPosition);
                if (shadowDistance <= 0)
                {
                    return default;
                }
            }
            else if (context.viewType == BatchCullingViewType.Camera)
            {
                CameraCallbacks++;
            }

            int splitCount = Mathf.Max(1, context.cullingSplits.Length);
            if (splitCount > MAXIMUM_SPLITS)
            {
                throw new InvalidOperationException(SPLIT_LIMIT);
            }

#if UNITY_EDITOR
            if (!TryCreatePickingFilter(context.viewType, out PickingFilter picking, out NativeArray<EntityId> include,
                out NativeArray<EntityId> exclude))
            {
                return default;
            }

            // Outlining a few selected trees must not scan a million-instance forest.
            NativeArray<ArchetypeChunk> chunks = picking.Enabled && picking.RequireInclude
                ? GatherPickingChunks(include)
                : GatherInstanceChunks(_entityQuery, Allocator.TempJob);
            if (chunks.Length == 0)
            {
                chunks.Dispose();
                DisposeIfCreated(include);
                DisposeIfCreated(exclude);
                return default;
            }
#else
            NativeArray<ArchetypeChunk> chunks = GatherInstanceChunks(_entityQuery, Allocator.TempJob);
#endif
            ViewState view = AcquireViewState(context.viewType == BatchCullingViewType.Camera);
            CullingViewScratch scratch = view.Scratch ??= new CullingViewScratch(chunks.Length);
            scratch.Prepare(chunks.Length * ChunkVisibilityMask.CAPACITY, _lods.Length, _hasFades, splitCount,
                CountPlanePackets(context, splitCount));
            PreparePlanes(context, scratch, splitCount);
            var target = (IndirectDrawTarget*)scratch.Indirect.GetUnsafePtr();
            BeginIndirectView(view, target, context.viewType, user);
            var job = new CullingJobContext
            {
                Context = context,
                Scratch = scratch,
                Chunks = chunks,
                Output = result,
                Target = target,
                ShadowDistance = shadowDistance,
                SplitCount = splitCount,
                Origin = CullingOrigin(context),
#if UNITY_EDITOR
                Picking = picking,
#endif
            };
            view.Handle = ScheduleCulling(job);
#if UNITY_EDITOR
            if (include.IsCreated)
            {
                view.Handle = include.Dispose(view.Handle);
            }

            if (exclude.IsCreated)
            {
                view.Handle = exclude.Dispose(view.Handle);
            }
#endif
            _entityQuery.AddDependency(view.Handle);
            view.Handle = chunks.Dispose(view.Handle);
            _fence = JobHandle.CombineDependencies(_fence, view.Handle);
            return view.Handle;
        }

        private static float3 CullingOrigin(BatchCullingContext context)
        {
            Vector3 position = context.lodParameters.cameraPosition;
            return float.IsFinite(position.sqrMagnitude) ? (float3)position : float3.zero;
        }

        private static int CountPlanePackets(BatchCullingContext context, int splitCount)
        {
            int packets = 0;
            for (int split = 0; split < splitCount; split++)
            {
                packets += (PlaneCount(context, split) + PLANES_PER_PACKET - 1) / PLANES_PER_PACKET;
            }

            return packets;
        }

        private static int PlaneCount(BatchCullingContext context, int split)
        {
            return context.cullingSplits.Length == 0
                ? context.cullingPlanes.Length
                : context.cullingSplits[split].cullingPlaneCount;
        }

        private static void PreparePlanes(BatchCullingContext context, CullingViewScratch scratch, int splitCount)
        {
            int destination = 0;
            for (int split = 0; split < splitCount; split++)
            {
                int start = context.cullingSplits.Length == 0 ? 0 : context.cullingSplits[split].cullingPlaneOffset;
                int count = PlaneCount(context, split);
                int packets = (count + PLANES_PER_PACKET - 1) / PLANES_PER_PACKET;
                scratch.Splits[split] = new RenderSplit { FirstPacket = destination, PacketCount = packets };
                for (int p = 0; p < packets; p++)
                {
                    scratch.Planes[destination++] = PackPlanes(context.cullingPlanes, start + p * PLANES_PER_PACKET,
                        math.min(PLANES_PER_PACKET, count - p * PLANES_PER_PACKET));
                }
            }
        }

        /// <summary>Packs up to four planes. Unused lanes never reject, because their distance is the float maximum.</summary>
        private static PlanePacket4 PackPlanes(NativeArray<Plane> planes, int first, int count)
        {
            var packet = new PlanePacket4 { Distance = new float4(float.MaxValue) };
            for (int lane = 0; lane < count; lane++)
            {
                Plane plane = planes[first + lane];
                packet.X[lane] = plane.normal.x;
                packet.Y[lane] = plane.normal.y;
                packet.Z[lane] = plane.normal.z;
                packet.Distance[lane] = plane.distance;
            }

            packet.AbsX = math.abs(packet.X);
            packet.AbsY = math.abs(packet.Y);
            packet.AbsZ = math.abs(packet.Z);
            return packet;
        }

#if UNITY_EDITOR
        /// <summary>Counts picking and outline callbacks. Returns false while editor rendering is suspended.</summary>
        private bool CountEditorCallback(BatchCullingViewType viewType)
        {
            if (viewType == BatchCullingViewType.Picking)
            {
                PickingCallbacks++;
            }
            else if (viewType == BatchCullingViewType.SelectionOutline)
            {
                SelectionOutlineCallbacks++;
            }

            return !EditorRenderingSuspended || Application.isPlaying;
        }
#endif

        private bool ShouldCull(BatchCullingContext context)
        {
            return _entityWorld is { IsCreated: true } && _chunks.IsCreated && !_changeTracker.Changed && InstanceCount > 0
                && (context.sceneCullingMask & SceneMask) != 0 && (context.cullingLayerMask & (1u << Layer)) != 0
                && (LiveShadows || context.viewType != BatchCullingViewType.Light);
        }

        private JobHandle ScheduleCulling(in CullingJobContext job)
        {
            BatchCullingContext context = job.Context;
            CullingViewScratch scratch = job.Scratch;
            EntityManager entityManager = _entityWorld.EntityManager;
            bool camera = context.viewType == BatchCullingViewType.Camera;
            JobHandle reset = new ResetCullingWorkersJob { Workers = scratch.Workers }
                .Schedule(scratch.Workers.Length, WORKER_JOB_BATCH);
            JobHandle chunksReady = _entityQuery.GetDependency();
            // Shadow casters and editor selection views have no use for draw order.
            if (camera)
            {
                chunksReady = new SortChunksFrontToBackJob
                {
                    Chunks = job.Chunks,
                    ChunkType = entityManager.GetComponentTypeHandle<RenderChunk>(true),
                    Origin = job.Origin,
                }.Schedule(chunksReady);
            }

            JobHandle cull = ScheduleCullAndEmit(job, JobHandle.CombineDependencies(reset, chunksReady));
            JobHandle flush = new FlushCullingWorkersJob
            {
                Workers = scratch.Workers,
                WorkItems = scratch.WorkItems.AsParallelWriter(),
            }.Schedule(scratch.Workers.Length, WORKER_JOB_BATCH, cull);
            JobHandle sortedKeys = new SortDrawKeysJob { Keys = scratch.Keys }.Schedule(flush);
            JobHandle collect = new CollectDrawBinsJob
            {
                Keys = scratch.Keys.AsDeferredJobArray(),
                Workers = scratch.Workers,
                Output = scratch.Bins,
            }.Schedule(scratch.Keys, 1, sortedKeys);
            JobHandle prefix = SchedulePrefix(job, collect);
#if UNITY_EDITOR
            if (context.viewType == BatchCullingViewType.Picking)
            {
                return new ExpandPickingMasksJob
                {
                    WorkItems = scratch.WorkItems.AsDeferredJobArray(),
                    Workers = scratch.Workers,
                    Fades = scratch.Fades,
                    Chunks = job.Chunks,
                    SlotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(true),
                    EntityType = entityManager.GetEntityTypeHandle(),
                    Bins = scratch.Bins,
                    Lods = _lods,
                    Parts = _parts,
                    Output = job.Output,
                }.Schedule(scratch.WorkItems, 1, prefix);
            }
#endif
            JobHandle expand = new ExpandDrawMasksJob
            {
                WorkItems = scratch.WorkItems.AsDeferredJobArray(),
                Workers = scratch.Workers,
                Fades = scratch.Fades,
                Chunks = job.Chunks,
                SlotType = entityManager.GetComponentTypeHandle<ImpostorGpuSlot>(true),
                MetricsType = entityManager.GetComponentTypeHandle<ImpostorLodMetrics>(true),
                ChunkType = entityManager.GetComponentTypeHandle<RenderChunk>(true),
                Bins = scratch.Bins,
                Origin = job.Origin,
                SortRows = camera,
                ExactRowSortWithinSquared = EXACT_ROW_SORT_DISTANCE * EXACT_ROW_SORT_DISTANCE,
                Target = job.Target,
            }.Schedule(scratch.WorkItems, 1, prefix);
            JobHandle commands = new EmitDrawCommandsJob
            {
                Keys = scratch.Keys.AsDeferredJobArray(),
                Bins = scratch.Bins,
                Lods = _lods,
                Parts = _parts,
                Output = job.Output,
                Target = job.Target,
            }.Schedule(scratch.Keys, 1, prefix);
            return JobHandle.CombineDependencies(expand, commands);
        }

        private JobHandle ScheduleCullAndEmit(in CullingJobContext job, JobHandle dependency)
        {
            BatchCullingContext context = job.Context;
            CullingViewScratch scratch = job.Scratch;
            EntityManager entityManager = _entityWorld.EntityManager;
            bool validCamera = float.IsFinite(context.lodParameters.cameraPosition.sqrMagnitude);
            bool occlusion = context.viewType == BatchCullingViewType.Camera && validCamera
                && ImpostorOcclusion.TryGet(context.lodParameters.cameraPosition, out _, out _, out _);
            ImpostorOcclusion.TryGet(context.lodParameters.cameraPosition, out NativeArray<float> occlusionDepth,
                out int2 occlusionSize, out float4x4 occlusionMatrix);
            return new CullAndEmitChunksJob
            {
                OcclusionDepth = occlusionDepth,
                OcclusionSize = occlusionSize,
                OcclusionViewProjection = occlusionMatrix,
                Occlusion = occlusion,
                OcclusionReversedZ = SystemInfo.usesReversedZBuffer,
                OcclusionFlipY = ImpostorOcclusion.FlipY,
                Chunks = job.Chunks,
                BoundsType = entityManager.GetComponentTypeHandle<RenderBounds>(true),
                MetricsType = entityManager.GetComponentTypeHandle<ImpostorLodMetrics>(true),
                ChunkType = entityManager.GetComponentTypeHandle<RenderChunk>(true),
                PrototypeType = entityManager.GetSharedComponentTypeHandle<ImpostorPrototype>(),
                Lods = _lods,
                Planes = scratch.Planes,
                Splits = scratch.Splits,
                Workers = scratch.Workers,
                Fades = scratch.Fades,
                KeySet = scratch.KeySet.AsParallelWriter(),
                Keys = scratch.Keys.AsParallelWriter(),
                WorkItems = scratch.WorkItems.AsParallelWriter(),
                Origin = job.Origin,
                UseDepthBuckets = context.viewType == BatchCullingViewType.Camera,
                CameraFactor = validCamera ? ImpostorLodSelection.CameraFactor(context.lodParameters, QualitySettings.lodBias) : 0,
                Orthographic = context.lodParameters.isOrthographic,
                CullByShadowDistance = float.IsFinite(job.ShadowDistance),
                ShadowDistanceSquared = job.ShadowDistance * job.ShadowDistance,
                SplitCount = job.SplitCount,
#if UNITY_EDITOR
                EntityType = entityManager.GetEntityTypeHandle(),
                Picking = job.Picking,
#endif
            }.Schedule(scratch.Workers.Length, 1, dependency);
        }

        private JobHandle SchedulePrefix(in CullingJobContext job, JobHandle dependency)
        {
            CullingViewScratch scratch = job.Scratch;
            return new PrefixDrawBinsJob
            {
                Keys = scratch.Keys,
                Lods = _lods,
                Workers = scratch.Workers,
                Bins = scratch.Bins,
                Statistics = scratch.Statistics,
                Output = job.Output,
                Target = job.Target,
                WorkItems = scratch.WorkItems,
                MainThreadGrowths = scratch.Growths,
                BandedRanges = job.Context.viewType == BatchCullingViewType.Camera,
                ViewType = (int)job.Context.viewType,
#if UNITY_EDITOR
                // Outline views use Unity's batched selection-mask path. Supplying
                // per-command picking IDs here suppresses native entity outlines.
                Picking = job.Context.viewType == BatchCullingViewType.Picking,
#endif
                Filter = new BatchFilterSettings
                {
                    layer = (byte)Layer,
                    sceneCullingMask = SceneMask,
                    renderingLayerMask = uint.MaxValue,
                    shadowCastingMode = LiveShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                    motionMode = MotionVectorGenerationMode.Camera,
                },
            }.Schedule(dependency);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Builds the include and exclude filter of a picking or selection-outline view. Returns false when an outline
        /// view has nothing selected.
        /// </summary>
        private static bool TryCreatePickingFilter(BatchCullingViewType viewType, out PickingFilter filter,
            out NativeArray<EntityId> include, out NativeArray<EntityId> exclude)
        {
            filter = default;
            include = default;
            exclude = default;
            bool picking = viewType == BatchCullingViewType.Picking;
            bool outline = viewType == BatchCullingViewType.SelectionOutline;
            if (!picking && !outline)
            {
                return true;
            }

            using var list = picking
                ? UnityEditor.HandleUtility.GetPickingIncludeExcludeEntityIdList(Allocator.Temp)
                : UnityEditor.HandleUtility.GetSelectionOutlineIncludeExcludeEntityIdList(Allocator.Temp);
            if (outline && list.IncludeEntities.Length == 0)
            {
                return false;
            }

            if (list.IncludeEntities.IsCreated)
            {
                include = new NativeArray<EntityId>(list.IncludeEntities, Allocator.TempJob);
            }

            if (list.ExcludeEntities.IsCreated)
            {
                exclude = new NativeArray<EntityId>(list.ExcludeEntities, Allocator.TempJob);
            }

            filter = new PickingFilter
            {
                Enabled = true,
                RequireInclude = outline || include.Length > 0,
                Include = include.IsCreated ? (EntityId*)include.GetUnsafeReadOnlyPtr() : null,
                Exclude = exclude.IsCreated ? (EntityId*)exclude.GetUnsafeReadOnlyPtr() : null,
                IncludeCount = include.Length,
                ExcludeCount = exclude.Length,
            };
            return true;
        }

        private static void DisposeIfCreated(NativeArray<EntityId> array)
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }
        }

        private NativeArray<ArchetypeChunk> GatherPickingChunks(NativeArray<EntityId> includes)
        {
            EntityManager entityManager = _entityWorld.EntityManager;
            var selected = new NativeList<ArchetypeChunk>(includes.Length, Allocator.Temp);
            foreach (EntityId id in includes)
            {
                // Entities defines the same bit-preserving conversion to EntityId.
                EntityId value = id;
                Entity entity = UnsafeUtility.As<EntityId, Entity>(ref value);
                if (!IsOwnedInstance(entityManager, entity))
                {
                    continue;
                }

                ArchetypeChunk chunk = entityManager.GetChunk(entity);
                if (!selected.Contains(chunk))
                {
                    selected.Add(chunk);
                }
            }

            return new NativeArray<ArchetypeChunk>(selected.AsArray(), Allocator.TempJob);
        }

        private bool IsOwnedInstance(EntityManager entityManager, Entity entity)
        {
            return entityManager.Exists(entity) && entityManager.HasComponent<ImpostorGpuSlot>(entity)
                && entityManager.HasComponent<LocalToWorld>(entity) && entityManager.HasComponent<ImpostorPrototype>(entity)
                && _prototypeBlobs.Contains(entityManager.GetSharedComponent<ImpostorPrototype>(entity).Data);
        }
#endif

        /// <summary>Everything the culling jobs of one view are scheduled from.</summary>
        private struct CullingJobContext
        {
            public BatchCullingContext Context;
            public CullingViewScratch Scratch;
            public NativeArray<ArchetypeChunk> Chunks;
            public BatchCullingOutputDrawCommands* Output;
            public IndirectDrawTarget* Target;
            public float3 Origin;
            public float ShadowDistance;
            public int SplitCount;
#if UNITY_EDITOR
            public PickingFilter Picking;
#endif
        }
    }
}
