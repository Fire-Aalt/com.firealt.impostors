using System;
using Impostors.Data;
using UnityEngine;

namespace Impostors.Samples.Benchmark
{
    public static class ImpostorBenchmarkGenerator
    {
        public const float Spacing = 8;
        public const int MaximumInstanceCount = (int.MaxValue - 64) / 128;
        private const int PlacementSeed = 24101987;

        public static ImpostorInstance[] CreateInstances(int dimensionX, int dimensionY, int prototypeCount)
        {
            long count = (long)dimensionX * dimensionY;
            if (dimensionX < 1 || dimensionY < 1 || count > MaximumInstanceCount || prototypeCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(dimensionX), "Use positive dimensions, at most 16,777,215 instances, valid prototypes.");
            }
            var instances = new ImpostorInstance[(int)count];
            int cursor = 0;
            foreach (var instance in EnumerateInstances(dimensionX, dimensionY, prototypeCount)) instances[cursor++] = instance;
            return instances;
        }

        public static System.Collections.Generic.IEnumerable<ImpostorInstance> EnumerateInstances(int dimensionX, int dimensionY, int prototypeCount)
        {
            long count = (long)dimensionX * dimensionY;
            if (dimensionX < 1 || dimensionY < 1 || count > MaximumInstanceCount || prototypeCount < 1)
                throw new ArgumentOutOfRangeException(nameof(dimensionX), "Use positive dimensions within the supported population limit and at least one prototype.");
            var random = new System.Random(PlacementSeed);
            for (int y = 0; y < dimensionY; y++)
            {
                for (int x = 0; x < dimensionX; x++)
                {
                    int index = y * dimensionX + x;
                    float px = (x - (dimensionX - 1) * 0.5f) * Spacing + (float)random.NextDouble() * 2 - 1;
                    float pz = (y - (dimensionY - 1) * 0.5f) * Spacing + (float)random.NextDouble() * 2 - 1;
                    // Preserve the established transform sequence after removing tint and identity draws.
                    random.NextDouble();
                    random.Next();
                    yield return new ImpostorInstance
                    {
                        Prototype = index % prototypeCount,
                        Position = new Vector3(px, Height(px, pz), pz),
                        Yaw = (float)random.NextDouble() * 360,
                        Scale = 1,
                    };
                }
            }
        }

        public static float Height(float x, float z) => 16 * Mathf.Pow(Mathf.Abs(x) / 1700, 1.5f) + z * 0.004f + Mathf.Sin(z * 0.006f) * 2;
    }
}
