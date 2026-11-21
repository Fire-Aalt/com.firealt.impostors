using System;
using System.Collections.Generic;
using Impostors.Data;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        // The instance buffer layout must agree with the DOTS instancing metadata in the impostor shaders.
        private const int RESERVED_INSTANCE_BYTES = 64;
        private const int PACKED_MATRIX_BYTES = 48;
        private const int PACKED_MATRIX_WORDS = PACKED_MATRIX_BYTES / sizeof(uint);
        private const int ENTITY_ID_OFFSET_BYTES = PACKED_MATRIX_BYTES * 2;
        private const int ENTITY_ID_WORDS = 2;
        private const int INSTANCE_RECORD_BYTES = ENTITY_ID_OFFSET_BYTES + ENTITY_ID_WORDS * sizeof(uint);
        private const int INSTANCE_METADATA_COUNT = 3;
        private const uint PER_INSTANCE_METADATA_FLAG = 0x80000000u;
        private const int MAXIMUM_BATCH_INSTANCES = 1 << 24;
        private const int INSTANTIATE_BATCH = 128;
        private const string INVALID_PLACEMENT = "Instances require a valid prototype, finite transforms and positive scale.";
        private const string INVALID_DEFINITION = "Incomplete impostor definition ";
        private const string NULL_DEFINITION_NAME = "(none)";
        private const string BATCH_LIMIT =
            "One prototype exceeds the 24-bit BRG fade index limit. Split it into multiple prototypes.";
        private const string QUAD_TOPOLOGY = "Impostor mesh LODs must use triangles, but mesh '{0}' submesh {1} uses quads. "
            + "Turn off Keep Quads in its model import settings.";
        private const string RUNTIME_PROXY_SUFFIX = " Runtime proxy";
        private const string RUNTIME_MESH_SUFFIX = " Runtime LOD";

        /// <summary>Replaces the renderer's population with CPU-side placements.</summary>
        public void SetData(ImpostorRenderData data)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ImpostorWorldRenderer));
            }

            CompleteOutstandingViews();
            Clear();
            if (data == null)
            {
                return;
            }

            try
            {
                BuildData(data);
            }
            catch
            {
                Clear();
                throw;
            }
        }

        private void BuildData(ImpostorRenderData data)
        {
            List<ImpostorInstance>[] placements = GroupPlacements(data);
            var batch = new NativeArray<ImpostorInstance>(INSTANTIATE_BATCH, Allocator.Temp);
            for (int p = 0; p < placements.Length; p++)
            {
                ImpostorDefinition definition = data.Definitions[p];
                if (!definition || !definition.IsValid)
                {
                    throw new ArgumentException(INVALID_DEFINITION + p);
                }

                RegisterAdditionalPrototype(definition);
                ResizePrototypeBuffer(p, Math.Max(1, placements[p].Count));
                placements[p].Sort(ComparePlacements);
                for (int start = 0; start < placements[p].Count; start += INSTANTIATE_BATCH)
                {
                    int count = Math.Min(INSTANTIATE_BATCH, placements[p].Count - start);
                    for (int i = 0; i < count; i++)
                    {
                        batch[i] = placements[p][start + i];
                    }

                    InstantiateBatch(p, batch, count);
                }
            }

            SynchronizeEntities(true);
        }

        private static List<ImpostorInstance>[] GroupPlacements(ImpostorRenderData data)
        {
            var placements = new List<ImpostorInstance>[data.Definitions.Length];
            for (int p = 0; p < placements.Length; p++)
            {
                placements[p] = new List<ImpostorInstance>();
            }

            foreach (ImpostorInstance placement in data.Instances)
            {
                if (!IsValidPlacement(placement, placements.Length))
                {
                    throw new ArgumentException(INVALID_PLACEMENT);
                }

                placements[placement.Prototype].Add(placement);
            }

            return placements;
        }

        private static bool IsValidPlacement(ImpostorInstance placement, int prototypeCount)
        {
            return placement.Prototype >= 0 && placement.Prototype < prototypeCount && placement.Scale > 0
                && float.IsFinite(placement.Scale) && float.IsFinite(placement.Yaw)
                && float.IsFinite(placement.Position.sqrMagnitude);
        }

        /// <summary>
        /// Registers the meshes, materials and LOD levels of a definition. A <paramref name="refreshIndex"/> rebuilds an
        /// existing prototype in place instead of adding one.
        /// </summary>
        private void RegisterAdditionalPrototype(ImpostorDefinition definition, int refreshIndex = -1)
        {
            CompleteOutstandingViews();
            ImpostorLodData profile = definition.LodData;
            if (profile != null && !profile.TryValidate(out string error))
            {
                throw new ArgumentException(error);
            }

            int prototype = refreshIndex >= 0 ? refreshIndex : _templates.Count;
            var lods = new List<RenderLod>(_lods.IsCreated ? _lods.ToArray() : Array.Empty<RenderLod>());
            BatchID batch = CreateInstanceBuffer();
            int firstLod = lods.Count;
            Bounds localBounds = definition.Bounds;
            Vector3 reference = definition.Bounds.center;
            float size = Mathf.Max(localBounds.size.x, Mathf.Max(localBounds.size.y, localBounds.size.z));
            if (profile != null)
            {
                reference = profile.ReferencePoint;
                size = profile.Size;
                RegisterMeshLevels(profile, prototype, batch, lods, ref localBounds);
            }

            RegisterImpostorLevel(definition, profile, batch, lods);
            int lodCount = lods.Count - firstLod;
            if (refreshIndex < 0)
            {
                AddEntityPrototype(definition, prototype, firstLod, lodCount, localBounds, reference, size);
            }
            else
            {
                // CompleteOutstandingViews above fences every reader of these private runtime blobs.
                // Keep the allocation stable so instance and cleanup shared-component values do not change.
                _prototypeBlobs[prototype].Value = CreatePrototypeData(definition, prototype, firstLod, lodCount,
                    localBounds, reference, size);
            }

            if (_lods.IsCreated)
            {
                _lods.Dispose();
            }

            if (_parts.IsCreated)
            {
                _parts.Dispose();
            }

            _lods = new NativeArray<RenderLod>(lods.ToArray(), Allocator.Persistent);
            _parts = new NativeArray<RenderPart>(_registrations.ToArray(), Allocator.Persistent);
        }

        private void RegisterMeshLevels(ImpostorLodData profile, int prototype, BatchID batch, List<RenderLod> lods,
            ref Bounds localBounds)
        {
            var ownedMeshes = new Dictionary<Mesh, Mesh>();
            var ownedMaterials = new Dictionary<Material, Material>();
            for (int level = 0; level < profile.MeshLevels.Length; level++)
            {
                ImpostorMeshLod meshLevel = profile.MeshLevels[level];
                int firstPart = _registrations.Count;
                foreach (ImpostorLodPart part in meshLevel.Parts)
                {
                    localBounds.Encapsulate(part.Mesh.bounds);
                    Mesh mesh = GetOwnedMesh(part.Mesh, ownedMeshes);
                    Material material = GetOwnedMaterial(part.Material, ownedMaterials);
                    RegisterPart(mesh, material, part.SubmeshIndex);
                }

                float fadeStart = FadeStart(profile, level);
                lods.Add(new RenderLod
                {
                    Batch = batch,
                    FirstPart = firstPart,
                    PartCount = _registrations.Count - firstPart,
                    TransitionScreenHeight = fadeStart,
                    FadeWidth = 1 - meshLevel.TransitionScreenHeight / fadeStart,
                });
                _hasFades |= meshLevel.FadeWidth > 0;
            }
        }

        private void RegisterImpostorLevel(ImpostorDefinition definition, ImpostorLodData profile, BatchID batch,
            List<RenderLod> lods)
        {
            Material impostorMaterial = CreateImpostorMaterial(definition);
            Mesh proxy = ImpostorProxy.BuildPrototypeHull(definition, HullDetail);
            proxy.name = definition.name + RUNTIME_PROXY_SUFFIX;
            proxy.hideFlags = HideFlags.HideAndDontSave;
            _meshes.Add(proxy);
            lods.Add(new RenderLod
            {
                Batch = batch,
                FirstPart = _registrations.Count,
                PartCount = 1,
                TransitionScreenHeight = 0,
            });

            bool impostorFades = profile is { MeshLevels: { Length: > 0 } } && profile.MeshLevels[^1].FadeWidth > 0;
            Material fadeMaterial = impostorFades ? CreateImpostorMaterial(definition, true) : null;
            RegisterPart(proxy, impostorMaterial, 0, fadeMaterial);
        }

        private Mesh GetOwnedMesh(Mesh source, Dictionary<Mesh, Mesh> owned)
        {
            if (owned.TryGetValue(source, out Mesh mesh))
            {
                return mesh;
            }

            mesh = UnityEngine.Object.Instantiate(source);
            mesh.name = source.name + RUNTIME_MESH_SUFFIX;
            mesh.hideFlags = HideFlags.HideAndDontSave;
            owned.Add(source, mesh);
            _meshes.Add(mesh);
            return mesh;
        }

        /// <summary>Copies a source material once per prototype, so property refreshes never leak across prototypes.</summary>
        private Material GetOwnedMaterial(Material source, Dictionary<Material, Material> owned)
        {
            if (owned.TryGetValue(source, out Material material))
            {
                return material;
            }

            material = new Material(source) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
            owned.Add(source, material);
            _materials.Add(material);
            TrackMaterial(source, material);
            return material;
        }

        private static float FadeStart(ImpostorLodData profile, int level)
        {
            float previous = level == 0 ? 1 : profile.MeshLevels[level - 1].TransitionScreenHeight;
            ImpostorMeshLod current = profile.MeshLevels[level];
            return Mathf.Lerp(current.TransitionScreenHeight, previous, current.FadeWidth);
        }

        private void RegisterPart(Mesh mesh, Material material, int submesh, Material fadeMaterial = null)
        {
            // Unity converts quads for direct draws only, so an indirect draw would render them wrongly.
            if (mesh.GetTopology(submesh) == MeshTopology.Quads)
            {
                string sourceName = mesh.name.Replace(RUNTIME_MESH_SUFFIX, string.Empty);
                throw new ArgumentException(string.Format(QUAD_TOPOLOGY, sourceName, submesh));
            }

            BatchMeshID meshId = _brg.RegisterMesh(mesh);
            try
            {
                BatchMaterialID materialId = _brg.RegisterMaterial(material);
                BatchMaterialID fadeId = fadeMaterial ? _brg.RegisterMaterial(fadeMaterial) : materialId;
                _registrations.Add(new RenderPart
                {
                    Mesh = meshId,
                    Material = materialId,
                    FadeMaterial = fadeId,
                    Submesh = (ushort)submesh,
                    IndexCount = mesh.GetIndexCount(submesh),
                    IndexStart = mesh.GetIndexStart(submesh),
                    BaseVertex = mesh.GetBaseVertex(submesh),
                    Topology = mesh.GetTopology(submesh),
                });
            }
            catch
            {
                _brg.UnregisterMesh(meshId);
                throw;
            }
        }

        private BatchID CreateInstanceBuffer()
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, RESERVED_INSTANCE_BYTES / sizeof(uint), sizeof(uint));
            buffer.SetData(new uint[RESERVED_INSTANCE_BYTES / sizeof(uint)]);
            _buffers.Add(buffer);
            InstanceBufferBytes += RESERVED_INSTANCE_BYTES;

            // The first population reservation replaces these zero-capacity offsets before rendering.
            NativeArray<MetadataValue> metadata = CreateInstanceMetadata(0);
            BatchID batch = _brg.AddBatch(metadata, buffer.bufferHandle);
            _batches.Add(batch);
            return batch;
        }

        /// <summary>Describes where the shaders read each per-instance property in a buffer of <paramref name="capacity"/> instances.</summary>
        private static NativeArray<MetadataValue> CreateInstanceMetadata(int capacity)
        {
            var metadata = new NativeArray<MetadataValue>(INSTANCE_METADATA_COUNT, Allocator.Temp);
            metadata[0] = Metadata(ImpostorShaderProperties.ObjectToWorld, RESERVED_INSTANCE_BYTES);
            metadata[1] = Metadata(ImpostorShaderProperties.WorldToObject,
                RESERVED_INSTANCE_BYTES + capacity * PACKED_MATRIX_BYTES);
            metadata[2] = Metadata(ImpostorShaderProperties.EntityId,
                RESERVED_INSTANCE_BYTES + capacity * ENTITY_ID_OFFSET_BYTES);
            return metadata;
        }

        private static MetadataValue Metadata(int property, int offset)
        {
            return new MetadataValue { NameID = property, Value = PER_INSTANCE_METADATA_FLAG | (uint)offset };
        }

        private Material CreateImpostorMaterial(ImpostorDefinition definition, bool nativeFade = false)
        {
            var material = new Material(definition.Material) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
            _materials.Add(material);
            ConfigureImpostorMaterial(material, definition);
            TrackMaterial(definition.Material, material, definition, nativeFade);
            return material;
        }

        private static void ConfigureImpostorMaterial(Material material, ImpostorDefinition definition)
        {
            ImpostorMaterialControls.ApplyKeywords(material);
            material.SetFloat(ImpostorShaderProperties.Cutoff, definition.Cutoff);
            if (definition.HasValidSelfShadowVolume)
            {
                definition.SelfShadowLayout.Configure(material);
                material.SetTexture(ImpostorShaderProperties.SelfShadowVolume, definition.SelfShadowVolume);
                material.EnableKeyword(ImpostorShaderKeywords.SELF_SHADOW_VOLUME);
            }
            else
            {
                material.DisableKeyword(ImpostorShaderKeywords.SELF_SHADOW_VOLUME);
            }

            ImpostorProxy.ConfigureProxyVolume(material, definition.Proxy);
        }

        /// <summary>Orders placements by spatial tile, then by position, so instantiated chunks stay spatially compact.</summary>
        private static int ComparePlacements(ImpostorInstance a, ImpostorInstance b)
        {
            Vector2Int tileA = Tile(a.Position);
            Vector2Int tileB = Tile(b.Position);
            int compare = tileA.y.CompareTo(tileB.y);
            if (compare == 0)
            {
                compare = tileA.x.CompareTo(tileB.x);
            }

            if (compare != 0)
            {
                return compare;
            }

            compare = a.Position.z.CompareTo(b.Position.z);
            return compare != 0 ? compare : a.Position.x.CompareTo(b.Position.x);
        }

        private static Vector2Int Tile(Vector3 position)
        {
            float SIZE = ImpostorSpatialCell.TileSize;
            return new Vector2Int(Mathf.FloorToInt(position.x / SIZE), Mathf.FloorToInt(position.z / SIZE));
        }

        private static RenderBounds ConvertBounds(Bounds bounds)
        {
            return new RenderBounds { Center = bounds.center, Extents = bounds.extents };
        }
    }
}
