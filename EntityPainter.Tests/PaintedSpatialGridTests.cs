using System.Collections.Generic;
using EntityPainter.Editor;
using NUnit.Framework;
using Unity.Mathematics;

namespace EntityPainter.Tests
{
    public sealed class PaintedSpatialGridTests
    {
        private const float MIN_DISTANCE = 3f;
        private const int POINTS = 3000;
        private const int QUERIES = 3000;
        private const uint SEED = 99;

        [Test]
        public void NeighbourCheckMatchesBruteForce()
        {
            var random = new Random(SEED);
            var grid = new PaintedSpatialGrid(MIN_DISTANCE);
            var points = new List<float3>();
            for (int i = 0; i < POINTS; i++)
            {
                // Includes negative coordinates and points on cell borders.
                float3 point = math.round(random.NextFloat3(-60f, 60f) * 2f) * 0.5f;
                grid.Add(point);
                points.Add(point);
            }

            int hits = 0;
            for (int i = 0; i < QUERIES; i++)
            {
                float3 query = random.NextFloat3(-70f, 70f);
                bool expected = false;
                foreach (float3 point in points)
                {
                    if (math.distancesq(point, query) < MIN_DISTANCE * MIN_DISTANCE)
                    {
                        expected = true;
                        break;
                    }
                }

                Assert.AreEqual(expected, grid.HasNeighbour(query, MIN_DISTANCE), $"query {query}");
                hits += expected ? 1 : 0;
            }

            // Both outcomes must be exercised for the comparison to mean anything.
            Assert.That(hits, Is.InRange(1, QUERIES - 1));
        }
    }
}
