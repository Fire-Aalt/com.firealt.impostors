using System.Collections.Generic;
using UnityEngine;

namespace Impostors.Editor
{
    public static partial class ImpostorBaker
    {
        /// <summary>
        /// The camera of one hemi-octahedral view: its direction, and an orthographic frame fitted around the projected
        /// geometry that leaves the border empty.
        /// </summary>
        private readonly struct CaptureFrame
        {
            private const float NEAR_PLANE = 0.5f;
            private const float FAR_PLANE = 3.5f;
            private const float EYE_DISTANCE = 2;

            public readonly Vector3 Direction;
            public readonly Matrix4x4 View;
            public readonly Matrix4x4 Projection;
            public readonly Matrix4x4 CameraProjection;
            public readonly Vector2 Center;
            public readonly Vector2 HalfSize;

            private CaptureFrame(Vector3 direction, Matrix4x4 view, Matrix4x4 cameraProjection, Vector2 center, Vector2 halfSize)
            {
                Direction = direction;
                View = view;
                CameraProjection = cameraProjection;
                Projection = GL.GetGPUProjectionMatrix(cameraProjection, true);
                Center = center;
                HalfSize = halfSize;
            }

            /// <param name="interior">The fraction of the view's width left inside the border.</param>
            public static CaptureFrame Fit(int frame, int frames, List<Vector3> positions, Vector3 center, float radius,
                float interior)
            {
                var cell = new Vector2(frame % frames, frame / frames);
                Vector3 direction = ImpostorMath.Decode(cell / (frames - 1));
                ImpostorMath.Basis(direction, out Vector3 right, out Vector3 up);
                var minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                var maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                foreach (Vector3 position in positions)
                {
                    Vector3 relative = position - center;
                    var projected = new Vector2(Vector3.Dot(relative, right), Vector3.Dot(relative, up));
                    minimum = Vector2.Min(minimum, projected);
                    maximum = Vector2.Max(maximum, projected);
                }

                Vector2 halfSize = (maximum - minimum) * 0.5f / interior;
                Vector2 frameCenter = (minimum + maximum) * 0.5f;
                Vector3 eye = center + direction * radius * EYE_DISTANCE;
                Matrix4x4 view = Matrix4x4.Scale(new Vector3(1, 1, -1))
                    * Matrix4x4.TRS(eye, Quaternion.LookRotation(-direction, up), Vector3.one).inverse;
                Matrix4x4 ortho = Matrix4x4.Ortho(frameCenter.x - halfSize.x, frameCenter.x + halfSize.x,
                    frameCenter.y - halfSize.y, frameCenter.y + halfSize.y, radius * NEAR_PLANE, radius * FAR_PLANE);
                return new CaptureFrame(direction, view, ortho, frameCenter, halfSize);
            }

            /// <summary>Maps the radius-sized view square onto the fitted frame, as the shader reads it back.</summary>
            public Color Rectangle(float radius)
            {
                var scale = new Vector2(radius / HalfSize.x, radius / HalfSize.y);
                var offset = new Vector2(0.5f - (0.5f + Center.x / (2 * radius)) * scale.x,
                    0.5f - (0.5f + Center.y / (2 * radius)) * scale.y);
                return new Color(scale.x, scale.y, offset.x, offset.y);
            }
        }
    }
}
