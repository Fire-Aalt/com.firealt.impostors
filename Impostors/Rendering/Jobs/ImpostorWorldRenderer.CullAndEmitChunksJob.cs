using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Scripting.LifecycleManagement;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        /// <summary>
        /// Culls one partition of chunks against the view's splits and shadow range, selects LODs and routes the visible
        /// rows into the partition's draw bins.
        /// </summary>
        [BurstCompile]
        [NoAutoStaticsCleanup]
        private struct CullAndEmitChunksJob : IJobParallelFor
        {
            private const int ROUTE_CAPACITY = 256;
            private const int ROUTE_HASH_CAPACITY = 512;
            private const int INITIAL_BIN_CAPACITY = 8;
            private const int OPAQUE = 0;
            private const int FADE_HIGH_DETAIL = 1;
            private const int FADE_LOW_DETAIL = 2;
            private static readonly ProfilerMarker Marker = new("Impostors.CullCells");

            [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
            [ReadOnly] public ComponentTypeHandle<RenderBounds> BoundsType;
            [ReadOnly] public ComponentTypeHandle<ImpostorLodMetrics> MetricsType;
            [ReadOnly] public ComponentTypeHandle<RenderChunk> ChunkType;
            [ReadOnly] public SharedComponentTypeHandle<ImpostorPrototype> PrototypeType;
            [ReadOnly] public NativeArray<RenderLod> Lods;
            [ReadOnly] public NativeArray<PlanePacket4> Planes;
            [ReadOnly] public NativeArray<RenderSplit> Splits;
#if UNITY_EDITOR
            [ReadOnly] public EntityTypeHandle EntityType;
#endif

            [NativeDisableContainerSafetyRestriction] public NativeArray<CullingWorker> Workers;
            [NativeDisableParallelForRestriction] public NativeArray<byte> Fades;
            public NativeParallelHashMap<DrawBinKey, byte>.ParallelWriter KeySet;
            public NativeList<DrawBinKey>.ParallelWriter Keys;
            public NativeList<DrawWorkItem>.ParallelWriter WorkItems;
            public float3 Origin;
            public float CameraFactor;
            public bool Orthographic;
            public bool UseDepthBuckets;
            public bool CullByShadowDistance;
            public float ShadowDistanceSquared;
            public int SplitCount;
            [ReadOnly] public NativeArray<float> OcclusionDepth;
            public float4x4 OcclusionViewProjection;
            public int2 OcclusionSize;
            public bool Occlusion;
            public bool OcclusionReversedZ;
            public bool OcclusionFlipY;
#if UNITY_EDITOR
            public PickingFilter Picking;
#endif

            public void Execute(int partitionIndex)
            {
                using ProfilerMarker.AutoScope scope = Marker.Auto();
                ref CullingWorker worker = ref ((CullingWorker*)Workers.GetUnsafePtr())[partitionIndex];
                int first = (int)((long)Chunks.Length * partitionIndex / Workers.Length);
                int end = (int)((long)Chunks.Length * (partitionIndex + 1) / Workers.Length);
                for (int i = first; i < end; i++)
                {
                    CullChunk(ref worker, i, partitionIndex);
                }
            }

            private void CullChunk(ref CullingWorker worker, int chunkIndex, int partitionIndex)
            {
                ArchetypeChunk ecsChunk = Chunks[chunkIndex];
                RenderChunk chunk = ecsChunk.GetChunkComponentData(ref ChunkType);
                int shadowRange = ClassifyShadowRange(chunk.Bounds);
                if (shadowRange < 0)
                {
                    worker.RejectedChunks++;
                    return;
                }

                ClassifySplits(chunk.Bounds, out ushort possible, out ushort inside);
                if (possible == 0 || Occluded(chunk.Bounds))
                {
                    worker.RejectedChunks++;
                    return;
                }

                bool fullyInside = possible == inside && shadowRange > 0;
                if (fullyInside)
                {
                    worker.InsideChunks++;
                }
                else
                {
                    worker.PartialChunks++;
                }

                // Depth order only needs a chunk-level key, so a chunk never splits across bands.
                int bucket = UseDepthBuckets ? ImpostorLodSelection.DepthBucket(math.distancesq(chunk.Bounds.Center, Origin)) : 0;
                ImpostorPrototypeData prototype = ecsChunk.GetSharedComponent(PrototypeType).Data.Value;
                SelectChunkLodRange(chunk, prototype, out LodSelection near, out LodSelection far);
                bool uniformLod = near.Level == far.Level && !near.Crossfading && !far.Crossfading;
                bool fullChunk = uniformLod && fullyInside && !Occlusion;
#if UNITY_EDITOR
                fullChunk &= !Picking.Enabled;
#endif
                int count = ecsChunk.Count;
                if (fullChunk)
                {
                    worker.Visible += count;
                    worker.UniformChunks++;
                    var mask = new DrawMask { Visible = ChunkVisibilityMask.Full(count), ChunkIndex = chunkIndex };
                    Emit(ref worker, DrawBinKey.Create(near.Level, bucket, possible, OPAQUE), mask, partitionIndex);
                    return;
                }

                worker.MixedChunks++;
                var rows = new ChunkRows
                {
                    Bounds = ecsChunk.GetNativeArray(ref BoundsType),
                    Metrics = ecsChunk.GetNativeArray(ref MetricsType),
                    Prototype = prototype,
                    ChunkIndex = chunkIndex,
                    Bucket = bucket,
                    TestShadowRange = shadowRange == 0,
                    Possible = possible,
                    Inside = inside,
                    UniformLod = uniformLod ? near : default,
                    HasUniformLod = uniformLod,
                };
                CullRows(ref worker, rows, count, partitionIndex);
            }

            /// <summary>
            /// True when the bounds lie behind the farthest depth of every coarse texel they cover in the occlusion
            /// camera's last depth. Bounds crossing the near plane or covering many texels are kept.
            /// </summary>
            private bool Occluded(RenderBounds bounds)
            {
                if (!Occlusion)
                {
                    return false;
                }

                float2 minimum = float.MaxValue;
                float2 maximum = float.MinValue;
                float nearest = OcclusionReversedZ ? 0 : 1;
                for (int corner = 0; corner < 8; corner++)
                {
                    float3 sign = new float3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1);
                    float4 clip = math.mul(OcclusionViewProjection, new float4(bounds.Center + bounds.Extents * sign, 1));
                    if (clip.w <= 0.01f)
                    {
                        return false;
                    }

                    float3 ndc = clip.xyz / clip.w;
                    minimum = math.min(minimum, ndc.xy);
                    maximum = math.max(maximum, ndc.xy);
                    nearest = OcclusionReversedZ ? math.max(nearest, ndc.z) : math.min(nearest, ndc.z);
                }

                if (OcclusionFlipY)
                {
                    float top = -minimum.y;
                    minimum.y = -maximum.y;
                    maximum.y = top;
                }

                int2 low = math.max((int2)math.floor((minimum * 0.5f + 0.5f) * OcclusionSize), 0);
                int2 high = math.min((int2)math.floor((maximum * 0.5f + 0.5f) * OcclusionSize), OcclusionSize - 1);
                if (math.any(high < low))
                {
                    return false;
                }

                int2 extent = high - low + 1;
                if (extent.x * extent.y > 256)
                {
                    return false;
                }

                for (int y = low.y; y <= high.y; y++)
                {
                    for (int x = low.x; x <= high.x; x++)
                    {
                        float farthest = OcclusionDepth[y * OcclusionSize.x + x];
                        bool behind = OcclusionReversedZ ? nearest < farthest : nearest > farthest;
                        if (!behind)
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            private void SelectChunkLodRange(RenderChunk chunk, ImpostorPrototypeData prototype, out LodSelection near,
                out LodSelection far)
            {
                near = new LodSelection { Level = prototype.FirstLod, HighDetailOpacity = 1 };
                far = near;
                if (prototype.LodCount <= 1)
                {
                    return;
                }

                ImpostorLodSelection.DistanceRange(chunk.ReferenceBounds, Origin, out float minimum, out float maximum);
                near = ImpostorLodSelection.Select(Lods, prototype.FirstLod, prototype.LodCount, chunk.MaximumHeight,
                    minimum, CameraFactor, Orthographic);
                far = ImpostorLodSelection.Select(Lods, prototype.FirstLod, prototype.LodCount, chunk.MinimumHeight,
                    maximum, CameraFactor, Orthographic);
            }

            /// <summary>Culls and routes each row of a chunk that is not drawn whole with one LOD.</summary>
            private void CullRows(ref CullingWorker worker, in ChunkRows rows, int count, int partitionIndex)
            {
                ChunkRoute* routes = stackalloc ChunkRoute[ROUTE_CAPACITY];
                int* lookup = stackalloc int[ROUTE_HASH_CAPACITY];
                UnsafeUtility.MemSet(lookup, 255, sizeof(int) * ROUTE_HASH_CAPACITY);
                int routeCount = 0;
                for (int bit = 0; bit < count; bit++)
                {
#if UNITY_EDITOR
                    if (Picking.Enabled && !Picking.Accepts(Chunks[rows.ChunkIndex].GetNativeArray(EntityType)[bit]))
                    {
                        continue;
                    }
#endif
                    if (rows.TestShadowRange && ClassifyShadowRange(rows.Bounds[bit]) < 0)
                    {
                        continue;
                    }

                    ushort instanceSplits = ClassifyRowSplits(rows.Bounds[bit], rows.Possible, rows.Inside);
                    if (instanceSplits == 0 || Occluded(rows.Bounds[bit]))
                    {
                        continue;
                    }

                    LodSelection selected = rows.HasUniformLod ? rows.UniformLod : SelectRowLod(rows, bit);
                    worker.Visible++;
                    if (!selected.Crossfading)
                    {
                        DrawBinKey key = DrawBinKey.Create(selected.Level, rows.Bucket, instanceSplits, OPAQUE);
                        Accumulate(routes, lookup, ref routeCount, key, bit);
                        continue;
                    }

                    worker.FadingInstances++;
                    Fades[rows.ChunkIndex * ChunkVisibilityMask.CAPACITY + bit] =
                        ImpostorLodSelection.QuantizeOpacity(selected.HighDetailOpacity);
                    DrawBinKey high = DrawBinKey.Create(selected.Level, rows.Bucket, instanceSplits, FADE_HIGH_DETAIL);
                    DrawBinKey low = DrawBinKey.Create(selected.Level + 1, rows.Bucket, instanceSplits, FADE_LOW_DETAIL);
                    Accumulate(routes, lookup, ref routeCount, high, bit);
                    Accumulate(routes, lookup, ref routeCount, low, bit);
                }

                for (int route = 0; route < routeCount; route++)
                {
                    var mask = new DrawMask { Visible = routes[route].Mask, ChunkIndex = rows.ChunkIndex };
                    Emit(ref worker, routes[route].Key, mask, partitionIndex);
                }
            }

            private LodSelection SelectRowLod(in ChunkRows rows, int bit)
            {
                ImpostorLodMetrics metrics = rows.Metrics[bit];
                return ImpostorLodSelection.Select(Lods, rows.Prototype.FirstLod, rows.Prototype.LodCount, metrics.Height,
                    math.distancesq(metrics.ReferencePoint, Origin), CameraFactor, Orthographic);
            }

            /// <summary>Adds the partially covered splits that this row's bounds touch to the fully covered ones.</summary>
            private ushort ClassifyRowSplits(RenderBounds bounds, ushort possible, ushort inside)
            {
                ushort splits = inside;
                ushort partial = (ushort)(possible & ~inside);
                for (int split = 0; split < SplitCount; split++)
                {
                    ushort flag = (ushort)(1 << split);
                    if ((partial & flag) != 0 && Classify(bounds, Splits[split]) >= 0)
                    {
                        splits |= flag;
                    }
                }

                return splits;
            }

            private static void Accumulate(ChunkRoute* routes, int* lookup, ref int count, DrawBinKey key, int bit)
            {
                int slot = key.GetHashCode() & (ROUTE_HASH_CAPACITY - 1);
                while (lookup[slot] >= 0 && !routes[lookup[slot]].Key.Equals(key))
                {
                    slot = (slot + 1) & (ROUTE_HASH_CAPACITY - 1);
                }

                int route = lookup[slot];
                if (route < 0)
                {
                    route = count++;
                    lookup[slot] = route;
                    routes[route] = new ChunkRoute { Key = key };
                }

                routes[route].Mask.Set(bit);
            }

            private void Emit(ref CullingWorker worker, DrawBinKey key, DrawMask mask, int partitionIndex)
            {
                ref WorkerDrawBin bin = ref FindOrAddBin(ref worker, key, out int index);
                if (bin.Count == 0 && KeySet.TryAdd(key, 0))
                {
                    Keys.AddNoResize(key);
                }

                int capacity = bin.Masks.Capacity;
                bin.Masks.Add(mask);
                bin.Count += mask.Visible.Count;
                if (capacity != bin.Masks.Capacity)
                {
                    worker.Growths++;
                }

                if (bin.Masks.Length - bin.PublishedMasks == CullingViewScratch.MASKS_PER_WORK_ITEM)
                {
                    Publish(ref bin, index, partitionIndex);
                }
            }

            private static ref WorkerDrawBin FindOrAddBin(ref CullingWorker worker, DrawBinKey key, out int index)
            {
                if (!worker.Bins.IsCreated)
                {
                    worker.Bins = new UnsafeList<WorkerDrawBin>(INITIAL_BIN_CAPACITY, Allocator.Persistent);
                    worker.BinLookup = new UnsafeHashMap<DrawBinKey, int>(INITIAL_BIN_CAPACITY, Allocator.Persistent);
                    worker.Growths++;
                }

                if (!worker.BinLookup.TryGetValue(key, out index))
                {
                    index = worker.Bins.Length;
                    var masks = new UnsafeList<DrawMask>(CullingViewScratch.MASKS_PER_WORK_ITEM, Allocator.Persistent);
                    worker.Bins.Add(new WorkerDrawBin { Key = key, Masks = masks });
                    worker.BinLookup.Add(key, index);
                    worker.Growths++;
                }

                return ref worker.Bins.ElementAt(index);
            }

            private void Publish(ref WorkerDrawBin bin, int index, int partitionIndex)
            {
                WorkItems.AddNoResize(new DrawWorkItem
                {
                    Worker = partitionIndex,
                    Bin = index,
                    FirstMask = bin.PublishedMasks,
                    MaskCount = bin.Masks.Length - bin.PublishedMasks,
                    LocalVisibleOffset = bin.PublishedCount,
                });
                bin.PublishedMasks = bin.Masks.Length;
                bin.PublishedCount = bin.Count;
            }

            private void ClassifySplits(RenderBounds bounds, out ushort possible, out ushort inside)
            {
                possible = 0;
                inside = 0;
                for (int split = 0; split < SplitCount; split++)
                {
                    ushort flag = (ushort)(1 << split);
                    int classification = Classify(bounds, Splits[split]);
                    if (classification >= 0)
                    {
                        possible |= flag;
                    }

                    if (classification > 0)
                    {
                        inside |= flag;
                    }
                }
            }

            /// <summary>Returns -1 outside the shadow range, 0 across its boundary and 1 fully inside it.</summary>
            private int ClassifyShadowRange(RenderBounds bounds)
            {
                if (!CullByShadowDistance)
                {
                    return 1;
                }

                // URP fades realtime shadows by radial camera distance. Directional
                // caster planes alone can span kilometres for a wide orthographic view.
                // Test the entire AABB so large/rotated trees survive the range boundary.
                float3 offset = math.abs(bounds.Center - Origin);
                float3 nearest = math.max(offset - bounds.Extents, 0);
                if (math.lengthsq(nearest) > ShadowDistanceSquared)
                {
                    return -1;
                }

                return math.lengthsq(offset + bounds.Extents) <= ShadowDistanceSquared ? 1 : 0;
            }

            /// <summary>Returns -1 outside the split, 0 across one of its planes and 1 fully inside it.</summary>
            private int Classify(RenderBounds bounds, RenderSplit split)
            {
                bool inside = true;
                for (int i = split.FirstPacket; i < split.FirstPacket + split.PacketCount; i++)
                {
                    PlanePacket4 plane = Planes[i];
                    float4 radius = plane.AbsX * bounds.Extents.x + plane.AbsY * bounds.Extents.y
                        + plane.AbsZ * bounds.Extents.z;
                    float4 distance = plane.X * bounds.Center.x + plane.Y * bounds.Center.y + plane.Z * bounds.Center.z
                        + plane.Distance;
                    if (math.any(distance + radius < 0))
                    {
                        return -1;
                    }

                    inside &= math.all(distance - radius >= 0);
                }

                return inside ? 1 : 0;
            }

            private struct ChunkRoute
            {
                public DrawBinKey Key;
                public ChunkVisibilityMask Mask;
            }

            /// <summary>Chunk-wide inputs of per-row culling.</summary>
            private struct ChunkRows
            {
                public NativeArray<RenderBounds> Bounds;
                public NativeArray<ImpostorLodMetrics> Metrics;
                public ImpostorPrototypeData Prototype;
                public int ChunkIndex;
                public int Bucket;
                public bool TestShadowRange;
                public ushort Possible;
                public ushort Inside;
                public LodSelection UniformLod;
                public bool HasUniformLod;
            }
        }

#if UNITY_EDITOR
        /// <summary>Editor picking and selection-outline include and exclude lists, read by the culling job.</summary>
        private struct PickingFilter
        {
            [NativeDisableUnsafePtrRestriction] public UnityEngine.EntityId* Include;
            [NativeDisableUnsafePtrRestriction] public UnityEngine.EntityId* Exclude;
            public int IncludeCount;
            public int ExcludeCount;
            public bool Enabled;
            public bool RequireInclude;

            public readonly bool Accepts(UnityEngine.EntityId entity)
            {
                return (!RequireInclude || Contains(Include, IncludeCount, entity)) && !Contains(Exclude, ExcludeCount, entity);
            }

            private static bool Contains(UnityEngine.EntityId* values, int count, UnityEngine.EntityId entity)
            {
                for (int i = 0; i < count; i++)
                {
                    if (values[i] == entity)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
#endif
    }
}
