using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Scripting.LifecycleManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    /// <summary>
    /// Owns one BatchRendererGroup and the ECS population it draws. Culling jobs and their scratch types are nested
    /// in the partial files of this class.
    /// </summary>
    [NoAutoStaticsCleanup]
    public sealed unsafe partial class ImpostorWorldRenderer : IDisposable
    {
        private const int INITIAL_VIEW_CAPACITY = 4;
        private const string MISSING_PIPELINE = "Impostors require an active Scriptable Render Pipeline.";
        private const string PICKING_SHADER = "Hidden/Universal Render Pipeline/BRGPicking";

        private static readonly HashSet<ImpostorWorldRenderer> LiveRenderers = new();

        private readonly List<GraphicsBuffer> _buffers = new();
        private readonly List<Material> _materials = new();
        private readonly List<Mesh> _meshes = new();
        private readonly List<BatchID> _batches = new();
        private readonly List<RenderPart> _registrations = new();

#if UNITY_EDITOR
        private readonly Material _pickingMaterial;
#endif

        public ulong SceneMask = ulong.MaxValue;
        public int Layer;

        /// <summary>
        /// Whether impostors render into the main light's shadow map. Their shading relies on those cascades for
        /// self-shadowing where they cover it, so the materials are told as well.
        /// </summary>
        public bool LiveShadows
        {
            get => _liveShadows;
            set
            {
                _liveShadows = value;
                foreach (MaterialBinding binding in _materialBindings)
                {
                    binding.ApplyLiveShadows();
                }
            }
        }
        private BatchRendererGroup _brg;
        private bool _liveShadows;
        private bool _disposed;
        private JobHandle _fence;
        private ViewState[] _views = new ViewState[INITIAL_VIEW_CAPACITY];
        private ulong _nextViewSequence;
        private ulong _publishedViewSequence;
        private ImpostorCullingStatistics _statistics;
        private NativeArray<ArchetypeChunk> _chunks;
        private NativeArray<RenderLod> _lods;
        private NativeArray<RenderPart> _parts;
        private bool _hasFades;

        public ImpostorWorldRenderer(World entityWorld = null)
        {
            if (!GraphicsSettings.currentRenderPipeline)
            {
                throw new InvalidOperationException(MISSING_PIPELINE);
            }

            ThrowIfDeviceUnsupported();
            InitializeEntities(entityWorld);
            _brg = new BatchRendererGroup(new BatchRendererGroupCreateInfo
            {
                cullingCallback = Cull,
                finishedCullingCallback = OnFinishedCulling,
                userContext = EngineCullingContext,
            });

#if UNITY_EDITOR
            // Imported LOD materials without picking passes use URP's fallback.
            // Our own passes retain their alpha clipping and reconstructed depth.
            _pickingMaterial = new Material(Shader.Find(PICKING_SHADER)) { hideFlags = HideFlags.HideAndDontSave };
            _brg.SetPickingMaterial(_pickingMaterial);
            _brg.SetEnabledViewTypes(new[]
            {
                BatchCullingViewType.Camera, BatchCullingViewType.Light, BatchCullingViewType.Picking,
                BatchCullingViewType.SelectionOutline,
            });
#else
            _brg.SetEnabledViewTypes(new[] { BatchCullingViewType.Camera, BatchCullingViewType.Light });
#endif
            LiveRenderers.Add(this);
        }

#if UNITY_EDITOR
        public static bool EditorRenderingSuspended { get; set; }
        public int PickingCallbacks { get; private set; }
        public int SelectionOutlineCallbacks { get; private set; }
#endif

        public int SubmittedCount => Statistics.VisibleInstances;
        public int DrawCommandCount => Statistics.DrawCommands;
        public int InstanceCount { get; private set; }
        public int ChunkCount => _ownedChunkCount;
        public long InstanceBufferBytes { get; private set; }
        public int CameraCallbacks { get; private set; }
        public int LightCallbacks { get; private set; }

        public ImpostorCullingStatistics Statistics
        {
            get
            {
                CollectCompletedViews();
                return _statistics;
            }
        }

        public bool HasValidRegistrations
        {
            get
            {
                if (_disposed || !_parts.IsCreated)
                {
                    return false;
                }

                foreach (RenderPart part in _parts)
                {
                    if (!_brg.GetRegisteredMesh(part.Mesh) || !_brg.GetRegisteredMaterial(part.Material))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CompleteOutstandingViews();
            Clear();
            _disposed = true;
            _brg.Dispose();
#if UNITY_EDITOR
            DestroyOwned(_pickingMaterial);
#endif
            _brg = null;
            LiveRenderers.Remove(this);
            DisposeEntities();
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterEditorCleanup()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeAll;
            UnityEditor.EditorApplication.quitting += DisposeAll;
            UnityEditor.EditorApplication.update += RefreshEditorMaterials;
        }
#endif

        private static void DisposeAll()
        {
            foreach (ImpostorWorldRenderer renderer in new List<ImpostorWorldRenderer>(LiveRenderers))
            {
                renderer.Dispose();
            }
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        private ViewState AcquireViewState(bool camera)
        {
            CollectCompletedViews();
            for (int i = 0; ; i++)
            {
                if (i == _views.Length)
                {
                    Array.Resize(ref _views, _views.Length * 2);
                }

                ViewState view = _views[i] ??= new ViewState();
                // A view's GPU buffers hold its visible list until Unity has drawn it.
                if (view.Pending || (view.Indirect != null && !CanRecycle(view.Indirect)))
                {
                    continue;
                }

                view.Pending = true;
                view.Camera = camera;
                view.Sequence = ++_nextViewSequence;
                return view;
            }
        }

        private void CollectCompletedViews()
        {
            bool pending = false;
            foreach (ViewState view in _views)
            {
                if (view is not { Pending: true })
                {
                    continue;
                }

                if (!view.Handle.IsCompleted)
                {
                    pending = true;
                    continue;
                }

                view.Handle.Complete();
                CommitIndirectView(view);
                if (view.Camera && view.Sequence > _publishedViewSequence)
                {
                    _statistics = view.Scratch.Statistics[0];
                    _publishedViewSequence = view.Sequence;
                }

                view.Pending = false;
            }

            if (!pending)
            {
                _fence = default;
            }
        }

        private void CompleteOutstandingViews()
        {
            FinishIndirectViews();
            _fence.Complete();
            CollectCompletedViews();
        }

        private void Clear()
        {
            FinishIndirectViews();
            Array.Clear(_expectedVisible, 0, _expectedVisible.Length);
            Array.Clear(_expectedCommands, 0, _expectedCommands.Length);
            Array.Clear(_expectedOperations, 0, _expectedOperations.Length);
            foreach (ViewState view in _views)
            {
                view?.Dispose();
            }

            Array.Clear(_views, 0, _views.Length);
            ClearEntities();
            ReleaseRenderResources();
            if (_chunks.IsCreated)
            {
                _chunks.Dispose();
            }

            InstanceCount = 0;
            CameraCallbacks = 0;
            LightCallbacks = 0;
#if UNITY_EDITOR
            PickingCallbacks = 0;
            SelectionOutlineCallbacks = 0;
#endif
            _statistics = default;
            _publishedViewSequence = _nextViewSequence;
        }

        /// <summary>Releases every BRG registration, GPU buffer and owned asset. Entities and prototypes are kept.</summary>
        private void ReleaseRenderResources()
        {
            foreach (BatchID batch in _batches)
            {
                _brg.RemoveBatch(batch);
            }

            foreach (RenderPart part in _registrations)
            {
                _brg.UnregisterMesh(part.Mesh);
                _brg.UnregisterMaterial(part.Material);
                if (part.FadeMaterial != part.Material)
                {
                    _brg.UnregisterMaterial(part.FadeMaterial);
                }
            }

            foreach (GraphicsBuffer buffer in _buffers)
            {
                buffer.Dispose();
            }

            foreach (Material material in _materials)
            {
                DestroyOwned(material);
            }

            foreach (Mesh mesh in _meshes)
            {
                DestroyOwned(mesh);
            }

            _batches.Clear();
            _registrations.Clear();
            _buffers.Clear();
            _materials.Clear();
            _meshes.Clear();
            _materialBindings.Clear();
            if (_lods.IsCreated)
            {
                _lods.Dispose();
            }

            if (_parts.IsCreated)
            {
                _parts.Dispose();
            }

            _lods = default;
            _parts = default;
            _hasFades = false;
            InstanceBufferBytes = 0;
        }

        /// <summary>Culling state of one BRG view, reused once Unity has drawn the frame it produced.</summary>
        private sealed class ViewState : IDisposable
        {
            public CullingViewScratch Scratch;
            public IndirectViewBuffers Indirect;
            public JobHandle Handle;
            public ulong Sequence;
            public bool Pending;
            public bool Camera;

            public void Dispose()
            {
                Indirect?.Dispose();
                Scratch?.Dispose();
            }
        }
    }
}
