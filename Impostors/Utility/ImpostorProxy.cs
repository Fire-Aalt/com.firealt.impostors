using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using UnityEngine;

namespace Impostors
{
    /// <summary>Builds the convex hulls that bound an impostor's reconstructed surface: the drawn proxy and the baked 14-DOP.</summary>
    public static class ImpostorProxy
    {
        private const int DIRECTION_COUNT = 14;
        private const int AXIS_DIRECTION_COUNT = 6;
        private const float PARALLEL_EPSILON = 0.00001f;
        private const float OUTSIDE_EPSILON = 0.0001f;
        private const float DUPLICATE_EPSILON_SQUARED = 0.000001f;
        private const float FACE_EPSILON = 0.0002f;
        private const string DOP_NAME = "Impostor 14-DOP";
        private const string HULL_NAME = "Impostor hull";
        private static readonly float[] HULL_LOW = { -30, 12 };
        private static readonly float[] HULL_MEDIUM = { -30, 10, 25 };
        private static readonly float[] HULL_HIGH = { -60, -30, 0, 6, 12, 20, 32, 50, 70 };

        /// <summary>
        /// Builds the drawn proxy of a prototype: a hull of every mesh LOD vertex, padded like the bake, so the captured
        /// surface always lies inside it. Falls back to a copy of the baked 14-DOP when no mesh LOD can be read.
        /// </summary>
        public static Mesh BuildPrototypeHull(ImpostorDefinition definition, ImpostorHullDetail detail)
        {
            var points = new List<Vector3>();
            if (definition.LodData is { MeshLevels: not null })
            {
                foreach (ImpostorMeshLod level in definition.LodData.MeshLevels)
                {
                    foreach (ImpostorLodPart part in level.Parts)
                    {
                        if (part.Mesh && (part.Mesh.isReadable || Application.isEditor))
                        {
                            points.AddRange(part.Mesh.vertices);
                        }
                    }
                }
            }

            if (points.Count < 4)
            {
                return Object.Instantiate(definition.Proxy);
            }

            HullPlanes(detail, out float[] elevations, out int azimuths);
            return BuildHull(points.ToArray(), definition.Radius * 2 / Mathf.Max(definition.Resolution, 1), elevations, azimuths);
        }

        /// <summary>Projects a point onto the four diagonal axes of the 14-DOP.</summary>
        public static Vector4 Diagonals(Vector3 value)
        {
            return new Vector4(value.x + value.y + value.z, value.x + value.y - value.z, value.x - value.y + value.z,
                -value.x + value.y + value.z);
        }

        /// <summary>Gives the shader the axis and diagonal slabs of <paramref name="source"/>, which clip view rays.</summary>
        public static void ConfigureProxyVolume(Material material, Mesh source)
        {
            var minimum = Vector4.one * float.PositiveInfinity;
            var maximum = Vector4.one * float.NegativeInfinity;
            foreach (Vector3 vertex in source.vertices)
            {
                Vector4 diagonal = Diagonals(vertex);
                minimum = Vector4.Min(minimum, diagonal);
                maximum = Vector4.Max(maximum, diagonal);
            }

            material.SetVector(ImpostorShaderProperties.ProxyBoundsMin, source.bounds.min);
            material.SetVector(ImpostorShaderProperties.ProxyBoundsMax, source.bounds.max);
            material.SetVector(ImpostorShaderProperties.ProxyDiagonalMin, minimum);
            material.SetVector(ImpostorShaderProperties.ProxyDiagonalMax, maximum);
        }

        /// <summary>
        /// Builds a convex hull of <paramref name="points"/> from axis planes plus rings of slanted planes, which follow
        /// tapered shapes such as conifers far more tightly than the 14-DOP's bounding box silhouette.
        /// </summary>
        public static Mesh BuildHull(Vector3[] points, float padding, float[] elevations, int azimuths)
        {
            var directions = new List<Vector3>(Directions().Take(AXIS_DIRECTION_COUNT));
            for (int ring = 0; ring < elevations.Length; ring++)
            {
                float elevation = elevations[ring];
                for (int i = 0; i < azimuths; i++)
                {
                    // Stagger neighbouring rings. The horizontal ring stays off the axis planes it would duplicate.
                    bool staggered = elevation == 0 || ring % 2 == 1;
                    float azimuth = (i + (staggered ? .5f : 0)) * 360f / azimuths;
                    directions.Add(Quaternion.Euler(-elevation, azimuth, 0) * Vector3.forward);
                }
            }

            Vector3[] normals = directions.ToArray();
            float[] supports = normals.Select(d => points.Max(p => Vector3.Dot(d, p)) + padding).ToArray();
            List<Vector3> vertices = FindVertices(normals, supports);
            List<int> triangles = Triangulate(normals, supports, vertices);
            var mesh = new Mesh { name = HULL_NAME };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Elevation rings and azimuth count for each hull detail level, densest near the horizon.</summary>
        public static void HullPlanes(ImpostorHullDetail detail, out float[] elevations, out int azimuths)
        {
            switch (detail)
            {
                case ImpostorHullDetail.Low:
                    elevations = HULL_LOW;
                    azimuths = 4;
                    break;
                case ImpostorHullDetail.Medium:
                    elevations = HULL_MEDIUM;
                    azimuths = 6;
                    break;
                default:
                    elevations = HULL_HIGH;
                    azimuths = 8;
                    break;
            }
        }

        /// <summary>Returns the six axis and eight diagonal face normals of a 14-DOP.</summary>
        public static Vector3[] Directions()
        {
            var directions = new Vector3[DIRECTION_COUNT];
            directions[0] = Vector3.right;
            directions[1] = Vector3.left;
            directions[2] = Vector3.up;
            directions[3] = Vector3.down;
            directions[4] = Vector3.forward;
            directions[5] = Vector3.back;
            for (int i = 0; i < DIRECTION_COUNT - AXIS_DIRECTION_COUNT; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
                directions[i + AXIS_DIRECTION_COUNT] = corner.normalized;
            }

            return directions;
        }

        /// <summary>Builds the 14-DOP that encloses <paramref name="points"/>, expanded by <paramref name="padding"/>.</summary>
        public static Mesh Build(Vector3[] points, float padding)
        {
            Vector3[] directions = Directions();
            float[] supports = directions.Select(d => points.Max(p => Vector3.Dot(d, p)) + padding).ToArray();
            List<Vector3> vertices = FindVertices(directions, supports);
            List<int> triangles = Triangulate(directions, supports, vertices);
            var mesh = new Mesh { name = DOP_NAME };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Intersects every plane triple and keeps the unique points inside all planes.</summary>
        private static List<Vector3> FindVertices(Vector3[] directions, float[] supports)
        {
            var vertices = new List<Vector3>();
            int count = directions.Length;
            for (int a = 0; a < count; a++)
            {
                for (int b = a + 1; b < count; b++)
                {
                    for (int c = b + 1; c < count; c++)
                    {
                        if (TryIntersect(directions, supports, a, b, c, out Vector3 point)
                            && IsInside(directions, supports, point) && !ContainsPoint(vertices, point))
                        {
                            vertices.Add(point);
                        }
                    }
                }
            }

            return vertices;
        }

        private static bool TryIntersect(Vector3[] directions, float[] supports, int a, int b, int c, out Vector3 point)
        {
            Vector3 cross = Vector3.Cross(directions[b], directions[c]);
            float denominator = Vector3.Dot(directions[a], cross);
            if (Mathf.Abs(denominator) < PARALLEL_EPSILON)
            {
                point = default;
                return false;
            }

            point = (supports[a] * cross + supports[b] * Vector3.Cross(directions[c], directions[a])
                + supports[c] * Vector3.Cross(directions[a], directions[b])) / denominator;
            return true;
        }

        private static bool IsInside(Vector3[] directions, float[] supports, Vector3 point)
        {
            for (int i = 0; i < directions.Length; i++)
            {
                if (Vector3.Dot(directions[i], point) > supports[i] + OUTSIDE_EPSILON)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ContainsPoint(List<Vector3> vertices, Vector3 point)
        {
            return vertices.Any(vertex => (vertex - point).sqrMagnitude < DUPLICATE_EPSILON_SQUARED);
        }

        /// <summary>Fans each face polygon into triangles wound outward.</summary>
        private static List<int> Triangulate(Vector3[] directions, float[] supports, List<Vector3> vertices)
        {
            var triangles = new List<int>();
            for (int plane = 0; plane < directions.Length; plane++)
            {
                int[] face = Enumerable.Range(0, vertices.Count)
                    .Where(i => Mathf.Abs(Vector3.Dot(directions[plane], vertices[i]) - supports[plane]) < FACE_EPSILON)
                    .ToArray();
                if (face.Length < 3)
                {
                    continue;
                }

                face = SortAroundCenter(face, vertices, directions[plane]);
                for (int i = 1; i < face.Length - 1; i++)
                {
                    triangles.Add(face[0]);
                    triangles.Add(face[i]);
                    triangles.Add(face[i + 1]);
                }
            }

            return triangles;
        }

        private static int[] SortAroundCenter(int[] face, List<Vector3> vertices, Vector3 normal)
        {
            Vector3 center = face.Select(i => vertices[i]).Aggregate(Vector3.zero, (sum, p) => sum + p) / face.Length;
            ImpostorMath.Basis(normal, out Vector3 right, out Vector3 up);
            // Capture frames use camera handedness. Polygon winding needs an outward basis.
            right = -right;
            return face.OrderBy(i => Mathf.Atan2(Vector3.Dot(vertices[i] - center, up), Vector3.Dot(vertices[i] - center, right)))
                .ToArray();
        }
    }
}
