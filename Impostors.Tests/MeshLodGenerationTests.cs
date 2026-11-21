using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    public sealed class MeshLodGenerationTests
    {
        private const int GRID = 48;

        private string _folder;

        [TearDown]
        public void TearDown()
        {
            if (_folder != null)
            {
                AssetDatabase.DeleteAsset(_folder);
            }
        }

        [Test]
        public void TargetRatiosHalveByDefaultAndHonourOverrides()
        {
            var overrides = new[]
            {
                new MeshLodOverride { Enabled = true, Keep = .1f },
                default,
                new MeshLodOverride { Enabled = true, Keep = .1f },
            };

            Assert.That(MeshLodGeneration.TargetRatios(3, .5f, null), Is.EqualTo(new[] { 1, .5f, .25f }));
            Assert.That(MeshLodGeneration.TargetRatios(3, .5f, overrides), Is.EqualTo(new[] { 1, .5f, .1f }));
        }

        [Test]
        public void PickLevelSnapsToNearestLevelInLogSpace()
        {
            int[] levels = { 1000, 500, 250, 120 };

            Assert.That(MeshLodGeneration.PickLevel(levels, 1), Is.Zero);
            Assert.That(MeshLodGeneration.PickLevel(levels, .3f), Is.EqualTo(2));
            Assert.That(MeshLodGeneration.PickLevel(levels, 0), Is.EqualTo(3));
        }

        [Test]
        public void AutoTransitionsFollowSquareRootOfTriangleRatio()
        {
            float[] ratios = { 1, .5f, .25f, .125f };

            float[] heights = MeshLodGeneration.AutoTransitions(.6f, .1f, ratios, out bool clamped);

            Assert.That(clamped, Is.False);
            Assert.That(heights[0], Is.EqualTo(.6f));
            Assert.That(heights[1], Is.EqualTo(.6f * Mathf.Sqrt(.5f)).Within(1e-5f));
            Assert.That(heights[2], Is.EqualTo(.3f).Within(1e-5f));
            Assert.That(heights[3], Is.EqualTo(.1f));
        }

        [Test]
        public void AutoTransitionsStayStrictlyDecreasingWhenClamped()
        {
            float[] heights = MeshLodGeneration.AutoTransitions(.6f, .5f, new[] { 1, .5f, .25f, .125f }, out bool clamped);

            Assert.That(clamped, Is.True);
            Assert.That(heights, Is.Ordered.Descending);
            Assert.That(heights.Distinct().Count(), Is.EqualTo(heights.Length));
        }

        [Test]
        public void ExtractLevelKeepsAttributesSubmeshesAndOnlyUsedVertices()
        {
            Mesh source = CreateGrid();
            Mesh chain = MeshLodGeneration.GenerateChain(source);
            try
            {
                int[] levels = MeshLodGeneration.LevelTriangleCounts(chain);
                Assert.That(levels.Length, Is.GreaterThan(2));
                Assert.That(levels, Is.Ordered.Descending);

                Mesh extracted = MeshLodGeneration.ExtractLevel(chain, 2);
                try
                {
                    AssertExtracted(source, extracted, levels[2]);
                }
                finally
                {
                    Object.DestroyImmediate(extracted);
                }
            }
            finally
            {
                Object.DestroyImmediate(chain);
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void GenerateBuildsLodGroupAndStripsModelComponents()
        {
            GameObject prefab = CreateGeneratorPrefab(out Mesh sourceMesh);

            MeshLodGeneratorAuthoring.Generate(prefab);

            LODGroup group = prefab.GetComponent<LODGroup>();
            LOD[] lods = group.GetLODs();
            Assert.That(lods.Length, Is.EqualTo(3));
            Assert.That(lods[0].renderers.Single().GetComponent<MeshFilter>().sharedMesh, Is.EqualTo(sourceMesh));
            Mesh[] generated = GeneratedLodMeshes(prefab);
            float density = Mathf.Sqrt((float)generated[1].triangles.Length / generated[0].triangles.Length);
            Assert.That(lods[0].screenRelativeTransitionHeight, Is.EqualTo(MeshLodGeneratorAuthoring.DEFAULT_FIRST_TRANSITION));
            Assert.That(lods[1].screenRelativeTransitionHeight,
                Is.EqualTo(MeshLodGeneratorAuthoring.DEFAULT_FIRST_TRANSITION * density).Within(1e-5f));
            Assert.That(lods[2].screenRelativeTransitionHeight, Is.EqualTo(MeshLodGeneratorAuthoring.DEFAULT_IMPOSTOR_TRANSITION));
            Assert.That(generated.Select(AssetDatabase.GetAssetPath), Is.All.EqualTo(AssetDatabase.GetAssetPath(prefab)));
            Assert.That(generated[0].triangles.Length, Is.LessThan(sourceMesh.triangles.Length));
            Assert.That(prefab.GetComponentsInChildren<BoxCollider>(true), Is.Empty);
            Assert.That(MeshLodGeneratorAuthoring.IsOutdated(prefab.GetComponent<MeshLodGenerator>()), Is.False);
        }

        [Test]
        public void RegenerateKeepsMeshIdentityAndRemovesUnusedLevels()
        {
            GameObject prefab = CreateGeneratorPrefab(out _);
            MeshLodGeneratorAuthoring.Generate(prefab);
            Mesh[] first = GeneratedLodMeshes(prefab);

            MeshLodGeneratorAuthoring.Generate(prefab);
            Assert.That(GeneratedLodMeshes(prefab), Is.EqualTo(first));

            SetLodCount(prefab, 2);
            MeshLodGeneratorAuthoring.Generate(prefab);
            Assert.That(GeneratedLodMeshes(prefab), Is.EqualTo(new[] { first[0] }));
            Assert.That((bool)first[1], Is.False);
        }

        [Test]
        public void RegenerateKeepsMeshesTheBakeStillUsesUntilItIsPruned()
        {
            GameObject prefab = CreateGeneratorPrefab(out _);
            MeshLodGeneratorAuthoring.Generate(prefab);
            Mesh baked = GeneratedLodMeshes(prefab)[1];
            ImpostorDefinition definition = AttachDefinition(prefab, baked);

            SetLodCount(prefab, 2);
            MeshLodGeneratorAuthoring.Generate(prefab);
            Assert.That((bool)baked, Is.True);

            // A rebake replaces the LOD data and prunes the previous bake's meshes that nothing uses anymore.
            definition.LodData = null;
            ImpostorPrefabArtifacts.PruneUnused(definition, new Object[] { baked });
            Assert.That((bool)baked, Is.False);
        }

        [Test]
        public void ChangedSettingsOutdateGeneratorAndImpostorBake()
        {
            GameObject prefab = CreateGeneratorPrefab(out _);
            var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
            try
            {
                MeshLodGeneratorAuthoring.Generate(prefab);
                string baked = ImpostorBakeFingerprint.Compute(prefab, settings);

                SetLodCount(prefab, 2);
                Assert.That(MeshLodGeneratorAuthoring.IsOutdated(prefab.GetComponent<MeshLodGenerator>()), Is.True);
                MeshLodGeneratorAuthoring.Generate(prefab);
                Assert.That(ImpostorBakeFingerprint.Compute(prefab, settings), Is.Not.EqualTo(baked));
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static void AssertExtracted(Mesh source, Mesh extracted, int expectedTriangles)
        {
            Assert.That(extracted.subMeshCount, Is.EqualTo(source.subMeshCount));
            Assert.That(extracted.GetVertexAttributes(), Is.EqualTo(source.GetVertexAttributes()));
            Assert.That(extracted.vertexCount, Is.LessThan(source.vertexCount));
            int[] indices = Enumerable.Range(0, extracted.subMeshCount).SelectMany(extracted.GetIndices).ToArray();
            Assert.That(indices.Length / 3, Is.EqualTo(expectedTriangles));
            Assert.That(indices.Distinct().Count(), Is.EqualTo(extracted.vertexCount));
            var positions = new HashSet<Vector3>(source.vertices);
            Assert.That(extracted.vertices.All(positions.Contains), Is.True);
        }

        /// <summary>A wavy grid with normals, UVs and two submeshes split at its middle column.</summary>
        private static Mesh CreateGrid()
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            for (int y = 0; y <= GRID; y++)
            {
                for (int x = 0; x <= GRID; x++)
                {
                    vertices.Add(new Vector3(x, Mathf.Sin(x * .4f) * Mathf.Cos(y * .3f) * 2, y));
                    uvs.Add(new Vector2((float)x / GRID, (float)y / GRID));
                }
            }

            var halves = new[] { new List<int>(), new List<int>() };
            for (int y = 0; y < GRID; y++)
            {
                for (int x = 0; x < GRID; x++)
                {
                    int corner = y * (GRID + 1) + x;
                    halves[x < GRID / 2 ? 0 : 1].AddRange(new[]
                    {
                        corner, corner + GRID + 1, corner + 1, corner + 1, corner + GRID + 1, corner + GRID + 2,
                    });
                }
            }

            var mesh = new Mesh { name = "Grid", indexFormat = IndexFormat.UInt16 };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(halves[0], 0);
            mesh.SetTriangles(halves[1], 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A model prefab with a nested renderer and a collider, and an impostor prefab that generates from it.</summary>
        private GameObject CreateGeneratorPrefab(out Mesh sourceMesh)
        {
            _folder = ImpostorTestAssets.NewFolder("MeshLod");
            sourceMesh = CreateGrid();
            AssetDatabase.CreateAsset(sourceMesh, _folder + "/Grid.asset");
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(material, _folder + "/Grid.mat");
            var model = new GameObject("Model");
            var child = new GameObject("Mesh", typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider));
            child.transform.SetParent(model.transform, false);
            child.transform.localPosition = Vector3.up;
            child.GetComponent<MeshFilter>().sharedMesh = sourceMesh;
            child.GetComponent<MeshRenderer>().sharedMaterials = new[] { material, material };
            var root = new GameObject("Impostor", typeof(Impostor), typeof(MeshLodGenerator));
            try
            {
                root.GetComponent<MeshLodGenerator>().SourceModel = PrefabUtility.SaveAsPrefabAsset(model, _folder + "/Model.prefab");
                return PrefabUtility.SaveAsPrefabAsset(root, _folder + "/Impostor.prefab");
            }
            finally
            {
                Object.DestroyImmediate(model);
                Object.DestroyImmediate(root);
            }
        }

        private static Mesh[] GeneratedLodMeshes(GameObject prefab)
        {
            return prefab.GetComponent<LODGroup>().GetLODs().Skip(1)
                .Select(lod => lod.renderers.Single().GetComponent<MeshFilter>().sharedMesh)
                .ToArray();
        }

        private static void SetLodCount(GameObject prefab, int count)
        {
            prefab.GetComponent<MeshLodGenerator>().LodCount = count;
            PrefabUtility.SavePrefabAsset(prefab);
        }

        private static ImpostorDefinition AttachDefinition(GameObject prefab, Mesh baked)
        {
            var definition = ScriptableObject.CreateInstance<ImpostorDefinition>();
            definition.name = "Impostor Definition";
            AssetDatabase.AddObjectToAsset(definition, prefab);
            definition.SourcePrefab = prefab;
            definition.BakeData = new ImpostorBakeData();
            definition.LodData = new ImpostorLodData
            {
                MeshLevels = new[] { new ImpostorMeshLod { Parts = new[] { new ImpostorLodPart { Mesh = baked } } } },
            };
            ImpostorAuthoring.Attach(prefab, null, definition);
            return definition;
        }
    }
}
