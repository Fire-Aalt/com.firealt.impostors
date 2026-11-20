using System;
using System.Collections.Generic;
using Impostors.Data;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Destroys every transient object a bake created and did not save as an asset.</summary>
    internal sealed class ImpostorBakeWorkspace : IDisposable
    {
        private readonly HashSet<Object> _owned = new();

        public void Track(Object value)
        {
            if (value && !EditorUtility.IsPersistent(value))
            {
                _owned.Add(value);
            }
        }

        public void Capture(ImpostorDefinition definition)
        {
            if (!definition)
            {
                return;
            }

            Track(definition);
            Track(definition.Proxy);
            Track(definition.Material);
            Track(definition.BaseColor);
            Track(definition.Surface);
            Track(definition.Depth);
            Track(definition.Reflectance);
            Track(definition.MaterialData);
            Track(definition.Emission);
            Track(definition.FrameRectangles);
            Track(definition.SelfShadowVolume);
            if (definition.LodData?.MeshLevels == null)
            {
                return;
            }

            foreach (ImpostorMeshLod level in definition.LodData.MeshLevels)
            {
                foreach (ImpostorLodPart part in level.Parts)
                {
                    Track(part.Mesh);
                    Track(part.Material);
                }
            }
        }

        public void Dispose()
        {
            foreach (Object value in _owned)
            {
                if (value && !EditorUtility.IsPersistent(value))
                {
                    Object.DestroyImmediate(value);
                }
            }
        }
    }
}
