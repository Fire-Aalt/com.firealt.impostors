using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace EntityPainter.Editor
{
    /// <summary>
    /// Finds paint surfaces on the brush layers: the closest hit among colliders and rendered meshes, so ground without
    /// a collider can be painted too. Must be called from Scene view GUI code.
    /// </summary>
    internal static class EntityPainterSurface
    {
        private const float MAX_DISTANCE = 100000f;
        private const string INTERSECT_RAY_MESH = "IntersectRayMesh";

        // Unity's own Scene view mesh raycast. It is internal, so it is reached through reflection.
        private static readonly MethodInfo IntersectRayMesh = typeof(HandleUtility).GetMethod(INTERSECT_RAY_MESH,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly List<MeshFilter> Meshes = new();
        private static readonly object[] IntersectArguments = new object[4];
        private static bool _meshesDirty = true;

        static EntityPainterSurface()
        {
            EditorApplication.hierarchyChanged += MarkMeshesDirty;
        }

        /// <summary>Hits the surface under a Scene view GUI point.</summary>
        public static bool TryPick(Vector2 guiPoint, int layers, out Vector3 point, out Vector3 normal)
        {
            return Raycast(HandleUtility.GUIPointToWorldRay(guiPoint), MAX_DISTANCE, layers, out point, out normal);
        }

        /// <summary>
        /// Drops a point scattered on the brush plane onto the surface along <paramref name="normal"/>, within
        /// <paramref name="reach"/> on either side. Falls back to picking through the Scene view camera.
        /// </summary>
        public static bool TryProject(Vector3 origin, Vector3 normal, float reach, int layers, out Vector3 point,
            out Vector3 surfaceNormal)
        {
            var ray = new Ray(origin + normal * reach, -normal);
            return Raycast(ray, reach * 2f, layers, out point, out surfaceNormal)
                || TryPick(HandleUtility.WorldToGUIPoint(origin), layers, out point, out surfaceNormal);
        }

        private static bool Raycast(Ray ray, float maxDistance, int layers, out Vector3 point, out Vector3 normal)
        {
            point = default;
            normal = Vector3.up;
            float closest = maxDistance;
            bool found = false;

            if (Physics.Raycast(ray, out RaycastHit hit, maxDistance, layers, QueryTriggerInteraction.Ignore))
            {
                closest = hit.distance;
                point = hit.point;
                normal = hit.normal;
                found = true;
            }

            if (IntersectRayMesh == null)
            {
                return found || HandleUtility.PlaceObject(HandleUtility.WorldToGUIPoint(ray.origin), out point, out normal);
            }

            if (_meshesDirty)
            {
                RefreshMeshes();
            }

            foreach (MeshFilter filter in Meshes)
            {
                if (!filter || !filter.gameObject.activeInHierarchy || (layers & (1 << filter.gameObject.layer)) == 0)
                {
                    continue;
                }

                Mesh mesh = filter.sharedMesh;
                if (!mesh || !filter.TryGetComponent(out MeshRenderer renderer) || !renderer.enabled
                    || !renderer.bounds.IntersectRay(ray, out float boundsDistance) || boundsDistance > closest)
                {
                    continue;
                }

                IntersectArguments[0] = ray;
                IntersectArguments[1] = mesh;
                IntersectArguments[2] = filter.transform.localToWorldMatrix;
                IntersectArguments[3] = null;
                if ((bool)IntersectRayMesh.Invoke(null, IntersectArguments))
                {
                    var meshHit = (RaycastHit)IntersectArguments[3];
                    if (meshHit.distance < closest)
                    {
                        closest = meshHit.distance;
                        point = meshHit.point;
                        normal = meshHit.normal;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static void RefreshMeshes()
        {
            Meshes.Clear();
            Meshes.AddRange(Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Exclude));
            _meshesDirty = false;
        }

        private static void MarkMeshesDirty()
        {
            _meshesDirty = true;
        }
    }
}
