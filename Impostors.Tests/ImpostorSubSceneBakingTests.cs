using System;
using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using Impostors.Tests.Authoring;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Hash128 = Unity.Entities.Hash128;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class ImpostorSubSceneBakingTests
    {
        private const float Tolerance = 0.001f;
        // Internal: SceneSection streaming adds it to every entity it loads in the Editor.
        private const string EditorRenderDataType = "Unity.Entities.EditorRenderData";
        private const string PublicEntityRefType = "Unity.Scenes.PublicEntityRef";
        private static readonly Type[] s_required =
        {
            typeof(LocalToWorld), typeof(RenderBounds), typeof(ImpostorLodMetrics), typeof(ImpostorSpatialCell),
            typeof(ImpostorBakedPrototype), typeof(ImpostorPrototype), typeof(ImpostorGpuSlot), typeof(ImpostorCleanupPrototype)
        };
        // Engine bookkeeping that may legitimately appear on a loaded entity.
        private static readonly Type[] s_bookkeeping =
        {
            typeof(SceneSection), typeof(SceneTag), typeof(Simulate), typeof(EntityGuid), typeof(Prefab), typeof(Disabled)
        };

        private string _folder;
        private GameObject _prefab;
        private World _world;

        [OneTimeSetUp]
        public void CreatePrefabs()
        {
            _folder = ImpostorTestAssets.NewFolder("SubSceneBaking");
            ImpostorDefinition definition = ImpostorTestAssets.Definition(1);
            Mesh mesh = definition.LodData.MeshLevels[0].Parts[0].Mesh;
            Material material = definition.LodData.MeshLevels[0].Parts[0].Material;

            // Every source of extra entities and components that baking must discard.
            var root = new GameObject("Baked impostor", typeof(Impostor), typeof(LODGroup), typeof(Light), typeof(ImpostorBakingProbe));
            var visible = CreateRendererChild(root, "Mesh", mesh, material);
            // Multi-material renderers bake one extra entity per material that points back at the LOD group.
            var split = Object.Instantiate(mesh);
            split.subMeshCount = 2;
            split.SetTriangles(mesh.triangles, 0);
            split.SetTriangles(mesh.triangles, 1);
            AssetDatabase.CreateAsset(split, _folder + "/Split mesh.asset");
            var multi = CreateRendererChild(root, "Multi material", split, material);
            multi.GetComponent<MeshRenderer>().sharedMaterials = new[] { material, material };
            multi.AddComponent<CapsuleCollider>();
            CreateRendererChild(root, "Inactive", mesh, material).SetActive(false);
            var nested = new GameObject("Probe", typeof(ImpostorBakingProbe));
            nested.transform.SetParent(visible.transform, false);
            root.GetComponent<Impostor>().Definition = definition;
            root.GetComponent<LODGroup>().SetLODs(new[]
            {
                new LOD(.3f, new[] { visible.GetComponent<Renderer>() }),
                new LOD(.1f, new[] { multi.GetComponent<Renderer>() })
            });
            _prefab = PrefabUtility.SaveAsPrefabAsset(root, _folder + "/Baked.prefab");
            Object.DestroyImmediate(root);

        }

        [OneTimeTearDown]
        public void DeleteAssets()
        {
            if (!string.IsNullOrEmpty(_folder)) AssetDatabase.DeleteAsset(_folder);
        }

        [TearDown]
        public void DisposeWorld()
        {
            if (_world != null && _world.IsCreated) _world.Dispose();
            _world = null;
        }

        [Test]
        public void PlacedPrefabBakesToOneImpostorEntity()
        {
            Matrix4x4 expected = default;
            Hash128 scene = CreateSection("Placed", () =>
            {
                var group = new GameObject("Group");
                group.transform.SetPositionAndRotation(new Vector3(250, 3, -40), Quaternion.Euler(0, 30, 0));
                var placement = Place(_prefab);
                placement.transform.SetParent(group.transform, false);
                placement.transform.localPosition = new Vector3(20, 0, 5);
                placement.transform.localRotation = Quaternion.Euler(0, 45, 0);
                placement.transform.localScale = Vector3.one * 2;
                expected = placement.transform.localToWorldMatrix;
                new GameObject("Probe only", typeof(ImpostorBakingProbe));
            });
            Entity section = Load(scene);
            var em = _world.EntityManager;

            Entity impostor = Single(em, typeof(ImpostorBakedPrototype));
            AssertImpostorArchetype(em, impostor);
            float4x4 matrix = em.GetComponentData<LocalToWorld>(impostor).Value;
            AssertMatrix(matrix, expected);
            Assert.That(em.GetSharedComponent<ImpostorSpatialCell>(impostor).Value,
                Is.EqualTo(ImpostorSpatialCell.FromPosition(matrix.c3.xyz).Value));

            // The probe on its own proves the third-party baker ran; it is the only other scene entity.
            Entity probe = Single(em, typeof(ImpostorBakingJunk));
            Assert.That(em.HasComponent<ImpostorBakedPrototype>(probe), Is.False);
            var others = SceneEntities(em).Where(entity => entity != impostor && entity != probe).ToArray();
            Assert.That(others, Is.Empty, "Children and LOD entities must not be baked: " + string.Join("; ", others.Select(entity => Describe(em, entity))));

            ImpostorWorldRenderer renderer = Renderers().Single();
            Assert.That(renderer.InstanceCount, Is.EqualTo(1));
            Assert.That(renderer.LiveShadows, Is.False);
            Assert.That(renderer.Layer, Is.EqualTo(_prefab.layer));
            SceneSystem.UnloadScene(_world.Unmanaged, section);
        }

        [Test]
        public void GetEntityOnPrefabBakesOneInstantiablePrefabEntity()
        {
            Hash128 scene = CreateSection("Spawner", () =>
            {
                var spawner = new GameObject("Spawner", typeof(ImpostorBakingSpawner));
                spawner.GetComponent<ImpostorBakingSpawner>().Prefab = _prefab;
            });
            Load(scene);
            var em = _world.EntityManager;

            Entity spawnerEntity = Single(em, typeof(ImpostorBakingSpawnerData));
            Entity prefab = em.GetComponentData<ImpostorBakingSpawnerData>(spawnerEntity).Prefab;
            Assert.That(em.HasComponent<Prefab>(prefab), Is.True);
            Assert.That(em.HasComponent<LinkedEntityGroup>(prefab), Is.False);
            AssertNoForeignTypes(em, prefab);
            Assert.That(em.HasComponent<ImpostorPrototype>(prefab), Is.True, "Baked prefabs are adopted so instances land in the final archetype.");

            using var instances = em.Instantiate(prefab, 3, Allocator.Temp);
            for (int i = 0; i < instances.Length; i++)
                em.SetComponentData(instances[i], new LocalToWorld { Value = float4x4.Translate(new float3(i * 10, 0, 0)) });
            _world.Update();

            Assert.That(Renderers().Single().InstanceCount, Is.EqualTo(3));
            foreach (Entity instance in instances)
                AssertImpostorArchetype(em, instance);
        }

        [Test]
        public void SectionUnloadFreesSlotsAndReloadAdoptsAgain()
        {
            Hash128 scene = CreateSection("Reload", () => Place(_prefab));
            Entity section = Load(scene);
            ImpostorWorldRenderer renderer = Renderers().Single();
            Assert.That(renderer.InstanceCount, Is.EqualTo(1));

            SceneSystem.UnloadScene(_world.Unmanaged, section);
            _world.Update();
            Assert.That(renderer.InstanceCount, Is.Zero);
            using (var cleanup = _world.EntityManager.CreateEntityQuery(typeof(ImpostorGpuSlot)))
                Assert.That(cleanup.CalculateEntityCount(), Is.Zero, "Unloaded instances must release their GPU slots.");

            Load(scene);
            Assert.That(Renderers().Single(), Is.SameAs(renderer));
            Assert.That(renderer.InstanceCount, Is.EqualTo(1));
        }

        private static GameObject CreateRendererChild(GameObject parent, string name, Mesh mesh, Material material)
        {
            var child = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            child.transform.SetParent(parent.transform, false);
            child.GetComponent<MeshFilter>().sharedMesh = mesh;
            child.GetComponent<MeshRenderer>().sharedMaterial = material;
            return child;
        }

        private static GameObject Place(GameObject prefab) => (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());

        private Hash128 CreateSection(string name, Action populate)
        {
            // The test runner restores its own scene afterwards. An additive scene is refused beside its untitled scene.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string path = _folder + "/" + name + ".unity";
            populate();
            Assert.That(EditorSceneManager.SaveScene(scene, path), Is.True);
            // Leave the section closed so it is imported from disk rather than live baked.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return new Hash128(AssetDatabase.AssetPathToGUID(path));
        }

        private Entity Load(Hash128 scene)
        {
            if (_world == null)
            {
                _world = new World("Impostor SubScene baking test");
                var systems = DefaultWorldInitialization.GetAllSystemTypeIndices(WorldSystemFilterFlags.Default);
                DefaultWorldInitialization.AddSystemsToRootLevelSystemGroups(_world, systems);
            }

            Entity section = SceneSystem.LoadSceneAsync(_world.Unmanaged, scene, new SceneSystem.LoadParameters
            {
                Flags = SceneLoadFlags.BlockOnImport | SceneLoadFlags.BlockOnStreamIn
            });
            _world.Update();
            Assert.That(SceneSystem.IsSceneLoaded(_world.Unmanaged, section), Is.True);
            return section;
        }

        private IReadOnlyCollection<ImpostorWorldRenderer> Renderers()
        {
            return _world.GetExistingSystemManaged<ImpostorRenderingSystem>().Renderers.ToArray();
        }

        private static List<Entity> SceneEntities(EntityManager em)
        {
            using var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { typeof(SceneSection) },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            // Each section also loads Unity's own public-reference bookkeeping entity.
            return entities.ToArray().Where(entity => !IsSectionBookkeeping(em, entity)).ToList();
        }

        private static bool IsSectionBookkeeping(EntityManager em, Entity entity)
        {
            using var types = em.GetComponentTypes(entity, Allocator.Temp);
            return types.ToArray().Any(type => type.GetManagedType().FullName == PublicEntityRefType);
        }

        private static string Describe(EntityManager em, Entity entity)
        {
            using var types = em.GetComponentTypes(entity, Allocator.Temp);
            return em.GetName(entity) + " [" + string.Join(", ", types.ToArray().Select(type => type.ToString())) + "]";
        }

        private static Entity Single(EntityManager em, Type type)
        {
            using var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[] { type },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities
            });
            using var entities = query.ToEntityArray(Allocator.Temp);
            Assert.That(entities.Length, Is.EqualTo(1), type.Name);
            return entities[0];
        }

        private static void AssertImpostorArchetype(EntityManager em, Entity entity)
        {
            using var types = em.GetComponentTypes(entity, Allocator.Temp);
            var present = types.ToArray().Where(type => !type.IsChunkComponent).Select(type => type.GetManagedType()).ToList();
            foreach (Type required in s_required)
                Assert.That(present, Does.Contain(required), required.Name);
            var unexpected = present.Except(s_required).Except(s_bookkeeping)
                .Where(type => type.FullName != EditorRenderDataType).Select(type => type.Name).ToArray();
            Assert.That(unexpected, Is.Empty, "Unexpected components: " + string.Join(", ", unexpected));
            Assert.That(types.ToArray().Where(type => type.IsChunkComponent).Select(type => type.GetManagedType()),
                Is.EquivalentTo(new[] { typeof(RenderChunk) }));
        }

        private static void AssertNoForeignTypes(EntityManager em, Entity entity)
        {
            foreach (Type type in new[] { typeof(LocalTransform), typeof(Parent), typeof(PostTransformMatrix), typeof(Static),
                         typeof(LinkedEntityGroup), typeof(ImpostorBakingJunk) })
                Assert.That(em.HasComponent(entity, type), Is.False, type.Name);
        }

        private static void AssertMatrix(float4x4 actual, Matrix4x4 expected)
        {
            for (int column = 0; column < 4; column++)
            for (int row = 0; row < 4; row++)
                Assert.That(actual[column][row], Is.EqualTo(expected[row, column]).Within(Tolerance), $"m{row}{column}");
        }
    }
}
