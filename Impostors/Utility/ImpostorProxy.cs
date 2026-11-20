using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using UnityEngine;

namespace Impostors
{
    /// <summary>Builds the camera-facing quad and the 14-DOP hull that bound an impostor's reconstructed surface.</summary>
    public static class ImpostorProxy
    {
        public const int CAMERA_QUAD_VERTEX_COUNT = 4;
        public const int CAMERA_QUAD_TRIANGLE_COUNT = 2;
        private const int DIRECTION_COUNT = 14;
        private const int AXIS_DIRECTION_COUNT = 6;
        private const float PARALLEL_EPSILON = 0.00001f;
        private const float OUTSIDE_EPSILON = 0.0001f;
        private const float DUPLICATE_EPSILON_SQUARED = 0.000001f;
        private const float FACE_EPSILON = 0.0002f;
        private const string CAMERA_QUAD_NAME = "Impostor camera quad";
        private const string DOP_NAME = "Impostor 14-DOP";

        public static Mesh BuildCameraQuad(Bounds bounds)
        {
            return new Mesh
            {
                name = CAMERA_QUAD_NAME,
                vertices = new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 },
                bounds = bounds,
            };
        }

        /// <summary>Projects a point onto the four diagonal axes of the 14-DOP.</summary>
        public static Vector4 Diagonals(Vector3 value)
        {
            return new Vector4(value.x + value.y + value.z, value.x + value.y - value.z, value.x - value.y + value.z,
                -value.x + value.y + value.z);
        }

        public static void ConfigureCameraProxy(Material material, Mesh source)
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
            for (int a = 0; a < DIRECTION_COUNT; a++)
            {
                for (int b = a + 1; b < DIRECTION_COUNT; b++)
                {
                    for (int c = b + 1; c < DIRECTION_COUNT; c++)
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
