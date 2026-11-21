using System;
using System.Collections.Generic;
using System.Linq;
using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;

namespace Impostors.Editor
{
    /// <summary>
    /// Predicts the self-shadow layout of a bake. Caches geometry only, never generated textures, so quality and
    /// resolution edits need no vertex reads.
    /// </summary>
    [NoAutoStaticsCleanup]
    public static class ImpostorSelfShadowPreview
    {
        private const float RADIUS_MARGIN = 1.001f;
        private const float PROXY_PADDING_PIXELS = 4;

        private static int? _geometryKey;
        private static Bounds _bounds;
        private static float _radius;

        static ImpostorSelfShadowPreview()
        {
            EditorApplication.projectChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
        }

        public static bool TryGetLayout(GameObject source, ImpostorSourceLod sourceLod, int resolution, ImpostorSelfShadowQuality quality,
            out ImpostorSelfShadowLayout layout)
        {
            layout = default;
            if (!source || resolution < 1)
            {
                return false;
            }

            try
            {
                Renderer[] renderers = ImpostorBaker.GetCaptureRenderers(source, sourceLod);
                int geometryKey = GeometryKey(source, sourceLod, renderers);
                if (_geometryKey != geometryKey)
                {
                    if (!TryMeasure(source, renderers))
                    {
                        return false;
                    }

                    _geometryKey = geometryKey;
                }

                // The bake's proxy adds this padding to each face of the geometry bounds.
                Bounds padded = _bounds;
                padded.Expand(PROXY_PADDING_PIXELS * _radius / resolution);
                layout = ImpostorSelfShadowLayout.ForBounds(padded, quality);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void Invalidate() => _geometryKey = null;

        private static int GeometryKey(GameObject source, ImpostorSourceLod sourceLod, Renderer[] renderers)
        {
            var key = new HashCode();
            key.Add(source.GetEntityId());
            key.Add(sourceLod);
            foreach (Renderer renderer in renderers)
            {
                Mesh mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                key.Add(mesh.GetEntityId());
                key.Add(mesh.vertexCount);
                key.Add(mesh.bounds);
                key.Add(source.transform.worldToLocalMatrix * renderer.localToWorldMatrix);
            }

            return key.ToHashCode();
        }

        private static bool TryMeasure(GameObject source, Renderer[] renderers)
        {
            var positions = new List<Vector3>();
            foreach (Renderer renderer in renderers)
            {
                Matrix4x4 matrix = source.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                foreach (Vector3 point in renderer.GetComponent<MeshFilter>().sharedMesh.vertices)
                {
                    positions.Add(matrix.MultiplyPoint3x4(point));
                }
            }

            if (positions.Count == 0)
            {
                return false;
            }

            _bounds = new Bounds(positions[0], Vector3.zero);
            foreach (Vector3 point in positions)
            {
                _bounds.Encapsulate(point);
            }

            Vector3 center = _bounds.center;
            _radius = positions.Max(point => Vector3.Distance(point, center)) * RADIUS_MARGIN;
            return true;
        }
    }
}
