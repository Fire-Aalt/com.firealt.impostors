using System.Collections.Generic;
using Impostors.Data;
using UnityEngine;

namespace Impostors
{
    public sealed unsafe partial class ImpostorWorldRenderer
    {
        private static bool _offscreenShading;

        private readonly List<MaterialBinding> _materialBindings = new();

        /// <summary>Refreshes material properties on every renderer that draws a definition.</summary>
        public static void RefreshMaterialProperties(ImpostorDefinition definition)
        {
            foreach (ImpostorWorldRenderer renderer in LiveRenderers)
            {
                if (renderer.UsesDefinition(definition))
                {
                    renderer.RefreshMaterialProperties();
                }
            }
        }

        /// <summary>Copy changed source properties in place, without rebuilding entities, BRG registrations or buffers.</summary>
        public bool RefreshMaterialProperties()
        {
            if (_disposed)
            {
                return false;
            }

            bool changed = false;
            foreach (MaterialBinding binding in _materialBindings)
            {
                changed |= binding.Refresh();
            }

            return changed;
        }

        /// <summary>Moves impostor forward shading out of URP's opaque pass and into the renderer feature's buffer.</summary>
        internal static void SetOffscreenShading(bool enabled)
        {
            if (_offscreenShading == enabled)
            {
                return;
            }

            _offscreenShading = enabled;
            foreach (ImpostorWorldRenderer renderer in LiveRenderers)
            {
                foreach (MaterialBinding binding in renderer._materialBindings)
                {
                    if (binding.Definition && binding.Target)
                    {
                        binding.ApplyForwardPassState();
                    }
                }
            }
        }

#if UNITY_EDITOR
        private static void RefreshEditorMaterials()
        {
            bool changed = false;
            foreach (ImpostorWorldRenderer renderer in LiveRenderers)
            {
                changed |= renderer.RefreshMaterialProperties();
            }

            if (!changed)
            {
                return;
            }

            UnityEditor.SceneView.RepaintAll();
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif

        private void TrackMaterial(Material source, Material target, ImpostorDefinition definition = null,
            bool nativeFade = false)
        {
            var binding = new MaterialBinding
            {
                Owner = this,
                Source = source,
                Target = target,
                Definition = definition,
                Checksum = source.ComputeCRC(),
                Cutoff = definition ? definition.Cutoff : 0,
                NativeFade = nativeFade,
            };
            _materialBindings.Add(binding);
            if (definition)
            {
                binding.ApplyForwardPassState();
                binding.ApplyLiveShadows();
            }
        }

        /// <summary>Links an owned runtime material to the asset material it copies.</summary>
        private sealed class MaterialBinding
        {
            public ImpostorWorldRenderer Owner;
            public Material Source;
            public Material Target;
            public ImpostorDefinition Definition;
            public int Checksum;
            public float Cutoff;

            /// <summary>Draws crossfading impostors, which stay in URP's opaque pass at every Render Scale.</summary>
            public bool NativeFade;

            /// <summary>Copies the source material when it changed since the last copy. Returns whether it did.</summary>
            public bool Refresh()
            {
                if (!Source || !Target)
                {
                    return false;
                }

                int checksum = Source.ComputeCRC();
                float cutoff = Definition ? Definition.Cutoff : 0;
                if (checksum == Checksum && cutoff == Cutoff && Target.shader == Source.shader)
                {
                    return false;
                }

                Target.shader = Source.shader;
                Target.CopyPropertiesFromMaterial(Source);
                Target.enableInstancing = true;
                if (Definition)
                {
                    ConfigureImpostorMaterial(Target, Definition);
                    ApplyForwardPassState();
                    ApplyLiveShadows();
                }

                Checksum = checksum;
                Cutoff = cutoff;
                return true;
            }

            /// <summary>Copying the source material resets the flag, so it is reapplied after every copy.</summary>
            public void ApplyLiveShadows()
            {
                if (Definition && Target)
                {
                    Target.SetFloat(ImpostorShaderProperties.LiveShadows, Owner._liveShadows ? 1 : 0);
                }
            }

            // Crossfade dither must land on the same pixel grid as the mesh LOD it complements, which
            // is always native, so fading impostors never move into the renderer feature's smaller buffer.
            public void ApplyForwardPassState()
            {
                Target.SetShaderPassEnabled(ImpostorShaderPasses.FORWARD_LIGHT_MODE, NativeFade || !_offscreenShading);
                Target.SetShaderPassEnabled(ImpostorShaderPasses.OFFSCREEN_LIGHT_MODE, !NativeFade);
            }
        }
    }
}
