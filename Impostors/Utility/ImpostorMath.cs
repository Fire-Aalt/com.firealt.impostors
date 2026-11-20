using UnityEngine;

namespace Impostors
{
    public static class ImpostorMath
    {
        public static Vector2 Encode(Vector3 d)
        {
            d /= Mathf.Abs(d.x) + Mathf.Abs(d.y) + Mathf.Abs(d.z);
            return new Vector2(d.x + d.z, d.x - d.z) * .5f + Vector2.one * .5f;
        }

        public static Vector3 Decode(Vector2 uv)
        {
            var p = uv * 2 - Vector2.one;
            float x = (p.x + p.y) * .5f;
            float z = (p.x - p.y) * .5f;
            return new Vector3(x, 1 - Mathf.Abs(x) - Mathf.Abs(z), z).normalized;
        }

        public static void Basis(Vector3 d, out Vector3 right, out Vector3 up)
        {
            right = Vector3.Cross(d, Mathf.Abs(d.y) > .999f ? Vector3.forward : Vector3.up).normalized;
            up = Vector3.Cross(right, d);
        }

        public static void Triangle(Vector2 uv, int n, out Vector3Int frames, out Vector3 weights)
        {
            var p = uv * (n - 1);
            int x = Mathf.Clamp(Mathf.FloorToInt(p.x), 0, n - 2);
            int y = Mathf.Clamp(Mathf.FloorToInt(p.y), 0, n - 2);
            float fx = p.x - x;
            float fy = p.y - y;
            int a = y * n + x;
            if (fx >= fy)
            {
                frames = new Vector3Int(a, a + 1, a + n + 1);
                weights = new Vector3(1 - fx, fx - fy, fy);
            }
            else
            {
                frames = new Vector3Int(a, a + n + 1, a + n);
                weights = new Vector3(1 - fy, fx, fy - fx);
            }
        }

        public static Vector3 ProjectFrame(Vector3 position, Vector3 center, float radius, Vector3 direction)
        {
            Basis(direction, out Vector3 right, out Vector3 up);
            Vector3 relative = (position - center) / radius;
            return new Vector3(Vector3.Dot(relative, right) * 0.5f + 0.5f, Vector3.Dot(relative, up) * 0.5f + 0.5f, Vector3.Dot(relative, direction) * 0.5f + 0.5f);
        }

        public static Vector3 ReconstructFrame(Vector3 origin, Vector3 ray, Vector3 center, float radius, Vector3 direction, float depth)
        {
            float denominator = Vector3.Dot(ray, direction);
            float distance = (Vector3.Dot(center - origin, direction) + (depth * 2 - 1) * radius) / denominator;
            return origin + ray * distance;
        }

        public static Vector2 EncodeNormal(Vector3 n)
        {
            n /= Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);
            var p = new Vector2(n.x, n.y);
            if (n.z < 0)
            {
                p = new Vector2((1 - Mathf.Abs(p.y)) * Mathf.Sign(p.x), (1 - Mathf.Abs(p.x)) * Mathf.Sign(p.y));
            }

            return p * .5f + Vector2.one * .5f;
        }

        public static Vector3 DecodeNormal(Vector2 uv)
        {
            var p = uv * 2 - Vector2.one;
            var n = new Vector3(p.x, p.y, 1 - Mathf.Abs(p.x) - Mathf.Abs(p.y));
            float t = Mathf.Clamp01(-n.z);
            n.x += n.x >= 0 ? -t : t;
            n.y += n.y >= 0 ? -t : t;
            return n.normalized;
        }
    }
}
