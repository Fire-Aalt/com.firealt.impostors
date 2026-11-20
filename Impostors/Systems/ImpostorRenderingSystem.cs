using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Impostors.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    /// <summary>
    /// Owns impostor rendering for its world. Entities carrying <see cref="ImpostorBakedPrototype"/>, from SubScene
    /// baking, GetEntity() or <see cref="CreatePrefab(GameObject)"/>, are bound to a renderer that is created lazily for
    /// each Layer and Realtime Shadows pair and lives as long as this system.
    /// </summary>
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateBefore(typeof(ImpostorSynchronizationSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class ImpostorRenderingSystem : SystemBase
    {
        private const string REJECTED_DEFINITION =
            "Impostor definition '{0}' cannot be rendered. Its entities stay unbound until it is rebaked.";
        private const string UNSUPPORTED_DEVICE = "Impostors are not rendered on this device.";
        private const string MISSING_IMPOSTOR = "A prefab root with an Impostor component is required.";
        private const string INVALID_DEFINITION =
            "Impostor definition '{0}' is missing or incomplete. Bake it before creating entities.";
        private const string INVALID_PREFAB =
            "The entity is not an impostor prefab. Create it with CreatePrefab or bake an Impostor prefab.";
        private const string MISMATCHED_LENGTH = "One output entity is needed per transform.";
        private const int TRANSFORM_BATCH = 256;

        private readonly Dictionary<(int Layer, bool RealtimeShadows), ImpostorWorldRenderer> _renderers = new();
        private readonly Dictionary<(ImpostorDefinition Definition, int Layer, bool RealtimeShadows), Entity> _prefabs = new();
        private readonly HashSet<ImpostorDefinition> _rejected = new();

        private EntityQuery _pending;
        private EntityArchetype _prefabArchetype;
        private int _definitionVersion;
        private bool _unsupported;

        /// <summary>Renderers created so far, for diagnostics. A renderer exists once entities with its settings are bound.</summary>
        public IReadOnlyCollection<ImpostorWorldRenderer> Renderers => _renderers.Values;

        /// <summary>The renderer for one Layer and Realtime Shadows pair, or null before any entity with those settings is bound.</summary>
        public ImpostorWorldRenderer GetRenderer(int layer, bool realtimeShadows)
        {
            return _renderers.TryGetValue((layer, realtimeShadows), out ImpostorWorldRenderer renderer) && !renderer.IsDisposed
                ? renderer
                : null;
        }

        /// <summary>
        /// Converts an <see cref="Impostor"/> prefab at runtime into the single-entity prefab that baking produces.
        /// Layer comes from the prefab root and shadows from its Realtime Shadows toggle. Repeated calls return the same entity.
        /// </summary>
        public Entity CreatePrefab(GameObject prefab)
        {
            if (!prefab || !prefab.TryGetComponent(out Impostor authoring))
            {
                throw new ArgumentException(MISSING_IMPOSTOR, nameof(prefab));
            }

            return CreatePrefab(authoring.Definition, prefab.layer, authoring.RealtimeShadows);
        }

        /// <summary>Creates, or returns the existing, entity prefab for a baked definition.</summary>
        public Entity CreatePrefab(ImpostorDefinition definition, int layer = 0, bool realtimeShadows = false)
        {
            if (!definition || !definition.IsValid)
            {
                string name = definition ? definition.name : string.Empty;
                throw new ArgumentException(string.Format(INVALID_DEFINITION, name), nameof(definition));
            }

            var key = (definition, layer, realtimeShadows);
            if (_prefabs.TryGetValue(key, out Entity existing) && EntityManager.Exists(existing))
            {
                return existing;
            }

            if (!_prefabArchetype.Valid)
            {
                _prefabArchetype = EntityManager.CreateArchetype(typeof(Prefab), typeof(LocalToWorld), typeof(RenderBounds),
                    typeof(ImpostorLodMetrics), typeof(ImpostorSpatialCell), typeof(ImpostorBakedPrototype));
            }

            Entity prefab = EntityManager.CreateEntity(_prefabArchetype);
            EntityManager.SetComponentData(prefab, new LocalToWorld { Value = float4x4.identity });
            EntityManager.SetSharedComponent(prefab, new ImpostorBakedPrototype
            {
                Definition = definition,
                Layer = layer,
                RealtimeShadows = realtimeShadows,
            });
            // Entities are left unnamed: the Editor name table has a fixed capacity.
            _prefabs[key] = prefab;
            return prefab;
        }

        /// <summary>
        /// Instantiates one entity per transform in a single batch and writes the transforms in parallel.
        /// Transforms need positive uniform scale. Instances render after the next presentation update.
        /// </summary>
        public void Instantiate(Entity prefab, NativeArray<float4x4> localToWorld, NativeArray<Entity> output)
        {
            if (output.Length != localToWorld.Length)
            {
                throw new ArgumentException(MISMATCHED_LENGTH, nameof(output));
            }

            if (!IsImpostorPrefab(prefab))
            {
                throw new ArgumentException(INVALID_PREFAB, nameof(prefab));
            }

            if (output.Length == 0)
            {
                return;
            }

            EntityManager.Instantiate(prefab, output);
            // Callers may pass Temp arrays, which jobs cannot read. Job-readable copies keep the write parallel.
            new WriteTransformsJob
            {
                Entities = CopyForJobs(output),
                Transforms = CopyForJobs(localToWorld),
                LocalToWorld = GetComponentLookup<LocalToWorld>(),
            }.Schedule(output.Length, TRANSFORM_BATCH).Complete();
        }

        /// <summary>Instantiates one entity per transform and returns the new entities.</summary>
        public NativeArray<Entity> Instantiate(Entity prefab, NativeArray<float4x4> localToWorld, Allocator allocator)
        {
            var output = new NativeArray<Entity>(localToWorld.Length, allocator, NativeArrayOptions.UninitializedMemory);
            try
            {
                Instantiate(prefab, localToWorld, output);
            }
            catch
            {
                output.Dispose();
                throw;
            }

            return output;
        }

        protected override void OnCreate()
        {
            _pending = SystemAPI.QueryBuilder()
                .WithAll<LocalToWorld, ImpostorBakedPrototype>()
                .WithNone<ImpostorPrototype>()
                .WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities)
                .Build();
            RequireForUpdate(_pending);
        }

        protected override void OnUpdate()
        {
            // Headless and pipeline-less sessions keep the entities without rendering them.
            if (_unsupported || !GraphicsSettings.currentRenderPipeline)
            {
                return;
            }

            if (_definitionVersion != ImpostorWorldRenderer.AdoptedDefinitionVersion)
            {
                _definitionVersion = ImpostorWorldRenderer.AdoptedDefinitionVersion;
                _rejected.Clear();
            }

            EntityManager.GetAllUniqueSharedComponents(out NativeList<ImpostorBakedPrototype> values, Allocator.Temp);
            try
            {
                foreach (ImpostorBakedPrototype value in values)
                {
                    if (!TryAdopt(value))
                    {
                        return;
                    }
                }
            }
            finally
            {
                _pending.ResetFilter();
            }
        }

        [SuppressMessage("FireAlt.Analyzers", "FA1001", Justification = "Disposing a renderer unbinds the entities it "
            + "adopted so a later renderer can adopt them again.")]
        [SuppressMessage("FireAlt.Analyzers", "FA1002", Justification = "Disposing a renderer waits for its culling jobs "
            + "before it releases the buffers they read.")]
        protected override void OnDestroy()
        {
            foreach (ImpostorWorldRenderer renderer in _renderers.Values)
            {
                renderer.Dispose();
            }

            _renderers.Clear();
            _prefabs.Clear();
        }

        private NativeArray<T> CopyForJobs<T>(NativeArray<T> source) where T : unmanaged
        {
            NativeArray<T> copy = CollectionHelper.CreateNativeArray<T>(source.Length, WorldUpdateAllocator,
                NativeArrayOptions.UninitializedMemory);
            copy.CopyFrom(source);
            return copy;
        }

        private bool IsImpostorPrefab(Entity prefab)
        {
            return EntityManager.Exists(prefab) && EntityManager.HasComponent<Prefab>(prefab)
                && EntityManager.HasComponent<ImpostorBakedPrototype>(prefab);
        }

        /// <summary>
        /// Binds the pending entities of one baked prototype to its renderer. Returns false when no renderer can be
        /// created on this device, which stops adoption for the rest of the session.
        /// </summary>
        [SuppressMessage("FireAlt.Analyzers", "FA1001", Justification = "Adopting baked entities binds the renderer's "
            + "runtime prototype blob and chunk metadata, which baking cannot write. It runs once per pending prototype.")]
        [SuppressMessage("FireAlt.Analyzers", "FA1002", Justification = "Registering a prototype rebuilds BRG resources, "
            + "so in-flight culling jobs that read them must finish first.")]
        private bool TryAdopt(ImpostorBakedPrototype value)
        {
            // An unloaded or deleted definition stays pending until a scene patch replaces it.
            if (!value.Definition.IsValid())
            {
                return true;
            }

            ImpostorDefinition definition = value.Definition.Value;
            if (!definition || _rejected.Contains(definition))
            {
                return true;
            }

            _pending.SetSharedComponentFilter(value);
            if (_pending.IsEmpty)
            {
                return true;
            }

            ImpostorWorldRenderer renderer = GetOrCreateRenderer(value.Layer, value.RealtimeShadows);
            if (renderer == null)
            {
                return false;
            }

            BlobAssetReference<ImpostorPrototypeData> blob;
            try
            {
                blob = renderer.EnsurePrototype(definition);
            }
            catch (Exception exception)
            {
                _rejected.Add(definition);
                Debug.LogError(string.Format(REJECTED_DEFINITION, definition.name), definition);
                Debug.LogException(exception, definition);
                return true;
            }

            // The live-baking differ skips chunk components, so the chunk metadata is added here for every path.
            EntityManager.AddComponent(_pending, ComponentType.ChunkComponent<RenderChunk>());
            EntityManager.AddSharedComponent(_pending, new ImpostorPrototype { Data = blob });
            return true;
        }

        private ImpostorWorldRenderer GetOrCreateRenderer(int layer, bool realtimeShadows)
        {
            ImpostorWorldRenderer renderer = GetRenderer(layer, realtimeShadows);
            if (renderer != null)
            {
                return renderer;
            }

            try
            {
                renderer = new ImpostorWorldRenderer(World)
                {
                    Layer = layer,
                    LiveShadows = realtimeShadows,
                    AdoptsBakedEntities = true,
                };
            }
            catch (Exception exception)
            {
                _unsupported = true;
                Debug.LogWarning(UNSUPPORTED_DEVICE);
                Debug.LogException(exception);
                return null;
            }

#if UNITY_EDITOR
            // Matches Entities Graphics for chunks without editor render data.
            renderer.SceneMask = UnityEditor.SceneManagement.EditorSceneManager.DefaultSceneCullingMask;
#endif
            _renderers[(layer, realtimeShadows)] = renderer;
            return renderer;
        }

        [BurstCompile]
        private struct WriteTransformsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Entity> Entities;
            [ReadOnly] public NativeArray<float4x4> Transforms;

            [NativeDisableParallelForRestriction] public ComponentLookup<LocalToWorld> LocalToWorld;

            public void Execute(int index)
            {
                LocalToWorld[Entities[index]] = new LocalToWorld { Value = Transforms[index] };
            }
        }
    }
}
