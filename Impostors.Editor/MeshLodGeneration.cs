using System.Collections.Generic;
using Impostors.Data;
using Unity.Assertions;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Snaps triangle targets to Unity's generated Mesh LOD chain and extracts standalone level meshes.</summary>
    public static class MeshLodGeneration
    {
        private const int TRIANGLE = 3;
        private const int UNTIL_SMALLEST = -1;
        private const float MIN_RATIO = 1e-4f;

        // Successive LODGroup transitions must strictly decrease.
        private const float MIN_TRANSITION_STEP = 1.05f;

        /// <summary>Fraction of LOD0's triangles each LOD targets. LOD0 always keeps every triangle.</summary>
        public static float[] TargetRatios(int lodCount, float reduction, IReadOnlyList<MeshLodOverride> overrides)
        {
            var ratios = new float[lodCount];
            for (int lod = 0; lod < lodCount; lod++)
            {
                bool overridden = lod > 0 && overrides != null && lod < overrides.Count && overrides[lod].Enabled;
                ratios[lod] = overridden ? overrides[lod].Keep : Mathf.Pow(1 - reduction, lod);
            }

            return ratios;
        }

        /// <summary>Returns a temporary copy of <paramref name="source"/> with Unity's full Mesh LOD chain. The caller destroys it.</summary>
        public static Mesh GenerateChain(Mesh source)
        {
            Mesh chain = Object.Instantiate(source);
            chain.hideFlags = HideFlags.HideAndDontSave;
            MeshLodUtility.GenerateMeshLods(chain, UNTIL_SMALLEST);
            return chain;
        }

        /// <summary>Triangles per chain level, ending before the first level that leaves a submesh empty.</summary>
        public static int[] LevelTriangleCounts(Mesh chain)
        {
            var counts = new List<int>();
            for (int level = 0; level < chain.lodCount; level++)
            {
                int indices = 0;
                for (int submesh = 0; submesh < chain.subMeshCount; submesh++)
                {
                    int count = (int)chain.GetLod(submesh, level).indexCount;
                    if (count < TRIANGLE)
                    {
                        return counts.ToArray();
                    }

                    indices += count;
                }

                counts.Add(indices / TRIANGLE);
            }

            return counts.ToArray();
        }

        /// <summary>The chain level whose triangle count is nearest to <paramref name="ratio"/> of level 0, in log space.</summary>
        public static int PickLevel(IReadOnlyList<int> levelTriangles, float ratio)
        {
            Assert.IsTrue(levelTriangles.Count > 0, "A chain has at least one level.");
            float target = Mathf.Log(Mathf.Max(ratio, MIN_RATIO) * levelTriangles[0]);
            int best = 0;
            for (int level = 1; level < levelTriangles.Count; level++)
            {
                if (Mathf.Abs(Mathf.Log(levelTriangles[level]) - target) < Mathf.Abs(Mathf.Log(levelTriangles[best]) - target))
                {
                    best = level;
                }
            }

            return best;
        }

        /// <summary>Total triangles of each LOD when every renderer snaps its own chain to the LOD's target ratio.</summary>
        public static int[] LodTriangles(IEnumerable<int[]> rendererLevelTriangles, IReadOnlyList<float> ratios)
        {
            var totals = new int[ratios.Count];
            foreach (int[] levels in rendererLevelTriangles)
            {
                for (int lod = 0; lod < totals.Length; lod++)
                {
                    totals[lod] += levels[PickLevel(levels, ratios[lod])];
                }
            }

            return totals;
        }

        /// <summary>
        /// Screen-relative transition heights that keep on-screen triangle density roughly constant. LOD0's transition and
        /// the impostor start (the last mesh LOD's transition) stay fixed. <paramref name="clamped"/> reports adjusted levels.
        /// </summary>
        public static float[] AutoTransitions(float first, float impostorStart, IReadOnlyList<float> ratios, out bool clamped)
        {
            Assert.IsTrue(first > impostorStart, "LOD0 must end above the impostor start.");
            int last = ratios.Count - 1;
            var heights = new float[ratios.Count];
            heights[0] = first;
            heights[last] = impostorStart;
            clamped = false;
            for (int lod = last - 1; lod > 0; lod--)
            {
                float height = first * Mathf.Sqrt(ratios[lod + 1] / ratios[1]);
                heights[lod] = Mathf.Max(height, heights[lod + 1] * MIN_TRANSITION_STEP);
                clamped |= heights[lod] > height;
            }

            if (last > 1 && heights[1] >= first)
            {
                clamped = true;
                SpaceGeometrically(heights);
            }

            return heights;
        }

        /// <summary>Extracts one chain level as a standalone mesh that holds only the vertices the level uses.</summary>
        public static Mesh ExtractLevel(Mesh chain, int level)
        {
            using Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(chain);
            int[][] indices = ReadLevelIndices(chain, data[0], level);
            int[] order = CompactVertices(indices, chain.vertexCount);
            return BuildMesh(data[0], chain.GetVertexAttributes(), order, indices);
        }

        private static void SpaceGeometrically(float[] heights)
        {
            int last = heights.Length - 1;
            for (int lod = 1; lod < last; lod++)
            {
                heights[lod] = heights[0] * Mathf.Pow(heights[last] / heights[0], (float)lod / last);
            }
        }

        /// <summary>Absolute vertex indices of every submesh at <paramref name="level"/>.</summary>
        private static int[][] ReadLevelIndices(Mesh chain, Mesh.MeshData data, int level)
        {
            var result = new int[chain.subMeshCount][];
            for (int submesh = 0; submesh < result.Length; submesh++)
            {
                SubMeshDescriptor descriptor = data.GetSubMesh(submesh);
                MeshLodRange range = chain.GetLod(submesh, level);
                int start = descriptor.indexStart + (int)range.indexStart;
                result[submesh] = ReadIndices(data, start, (int)range.indexCount, descriptor.baseVertex);
            }

            return result;
        }

        private static int[] ReadIndices(Mesh.MeshData data, int start, int count, int baseVertex)
        {
            var result = new int[count];
            if (data.indexFormat == IndexFormat.UInt16)
            {
                NativeArray<ushort> source = data.GetIndexData<ushort>();
                for (int i = 0; i < count; i++)
                {
                    result[i] = source[start + i] + baseVertex;
                }
            }
            else
            {
                NativeArray<uint> source = data.GetIndexData<uint>();
                for (int i = 0; i < count; i++)
                {
                    result[i] = (int)source[start + i] + baseVertex;
                }
            }

            return result;
        }

        /// <summary>Renumbers <paramref name="indices"/> in first-use order and returns the source vertex of each new vertex.</summary>
        private static int[] CompactVertices(int[][] indices, int vertexCount)
        {
            var remap = new int[vertexCount];
            System.Array.Fill(remap, -1);
            var order = new List<int>();
            foreach (int[] submesh in indices)
            {
                for (int i = 0; i < submesh.Length; i++)
                {
                    int vertex = submesh[i];
                    if (remap[vertex] < 0)
                    {
                        remap[vertex] = order.Count;
                        order.Add(vertex);
                    }

                    submesh[i] = remap[vertex];
                }
            }

            return order.ToArray();
        }

        private static Mesh BuildMesh(Mesh.MeshData source, VertexAttributeDescriptor[] attributes, int[] order, int[][] indices)
        {
            Mesh.MeshDataArray output = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData target = output[0];
            target.SetVertexBufferParams(order.Length, attributes);
            for (int stream = 0; stream < source.vertexBufferCount; stream++)
            {
                CopyVertices(source.GetVertexData<byte>(stream), target.GetVertexData<byte>(stream),
                    source.GetVertexBufferStride(stream), order);
            }

            WriteIndices(target, order.Length, indices);
            var mesh = new Mesh();
            Mesh.ApplyAndDisposeWritableMeshData(output, mesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Copies whole vertices at the stream's byte stride, which keeps every attribute of the stream.</summary>
        private static void CopyVertices(NativeArray<byte> source, NativeArray<byte> target, int stride, int[] order)
        {
            for (int vertex = 0; vertex < order.Length; vertex++)
            {
                NativeArray<byte>.Copy(source, order[vertex] * stride, target, vertex * stride, stride);
            }
        }

        private static void WriteIndices(Mesh.MeshData target, int vertexCount, int[][] indices)
        {
            int total = 0;
            foreach (int[] submesh in indices)
            {
                total += submesh.Length;
            }

            bool wide = vertexCount > ushort.MaxValue + 1;
            target.SetIndexBufferParams(total, wide ? IndexFormat.UInt32 : IndexFormat.UInt16);
            NativeArray<ushort> narrow = wide ? default : target.GetIndexData<ushort>();
            NativeArray<uint> large = wide ? target.GetIndexData<uint>() : default;
            target.subMeshCount = indices.Length;
            int start = 0;
            for (int submesh = 0; submesh < indices.Length; submesh++)
            {
                int[] values = indices[submesh];
                for (int i = 0; i < values.Length; i++)
                {
                    if (wide)
                    {
                        large[start + i] = (uint)values[i];
                    }
                    else
                    {
                        narrow[start + i] = (ushort)values[i];
                    }
                }

                target.SetSubMesh(submesh, new SubMeshDescriptor(start, values.Length), MeshUpdateFlags.DontRecalculateBounds);
                start += values.Length;
            }
        }
    }
}
