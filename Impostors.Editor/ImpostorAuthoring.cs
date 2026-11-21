using System;
using System.Threading.Tasks;
using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Attaches bake settings to source prefabs and runs the complete rebake of one prefab.</summary>
    [NoAutoStaticsCleanup]
    public static class ImpostorAuthoring
    {
        private const string PREFAB_EXTENSION = ".prefab";
        private const string ASSET_EXTENSION = ".asset";
        private const string INVALID_SOURCE = "Choose a writable source prefab root (.prefab).";
        private const string READ_ONLY_SOURCE = "The source prefab is read-only: ";
        private const string BAKE_RUNNING = "An impostor bake is already running.";
        private const string MISSING_SETTINGS = "Bake settings are required.";

        // Artist-tuned appearance that a rebake keeps from the previous definition.
        private static readonly int[] PreservedColors =
        {
            ImpostorShaderProperties.HueVariation, ImpostorShaderProperties.SpecularTint, ImpostorShaderProperties.EmissionTint,
            ImpostorShaderProperties.SubsurfaceScatteringTint,
        };

        private static readonly int[] PreservedFloats =
        {
            ImpostorShaderProperties.BaseColorGain, ImpostorShaderProperties.OcclusionStrength,
            ImpostorShaderProperties.SmoothnessScale, ImpostorShaderProperties.SelfShadowStrength,
            ImpostorShaderProperties.SelfShadowNormalBias, ImpostorShaderProperties.SelfShadowPower,
            ImpostorShaderProperties.ShadowReceiverBias,
            ImpostorShaderProperties.DetailFullPixels, ImpostorShaderProperties.DetailReducedPixels,
            ImpostorShaderProperties.AlbedoContrast, ImpostorShaderProperties.AlbedoSaturation,
            ImpostorShaderProperties.AlbedoLift, ImpostorShaderProperties.SpecularStrength,
            ImpostorShaderProperties.SpecularHighlights, ImpostorShaderProperties.EnvironmentReflections,
            ImpostorShaderProperties.SmoothnessOffset, ImpostorShaderProperties.OcclusionContrast,
            ImpostorShaderProperties.OcclusionOffset, ImpostorShaderProperties.EmissionStrength,
            ImpostorShaderProperties.NormalStrength, ImpostorShaderProperties.MetallicInfluence,
            ImpostorShaderProperties.SubsurfaceScattering, ImpostorShaderProperties.SubsurfaceScatteringStrength,
            ImpostorShaderProperties.SubsurfaceScatteringRoughness, ImpostorShaderProperties.SubsurfaceScatteringAmbient,
        };

        public static bool IsBaking { get; private set; }

        public static bool IsOutdated(GameObject source, ImpostorBakeSettings settings, ImpostorDefinition definition)
        {
            return !source || !settings || !definition || !definition.IsValid || definition.SourcePrefab != source
                || definition.BakeData == null || string.IsNullOrEmpty(definition.BakeData.InputFingerprint)
                || definition.BakeData.InputFingerprint != ImpostorBakeFingerprint.Compute(source, settings);
        }

        public static Impostor Attach(GameObject source, ImpostorBakeSettings settings, ImpostorDefinition definition = null)
        {
            string path = RequireWritableSource(source);
            if (settings && EditorUtility.IsPersistent(settings))
            {
                ImpostorPackageAssets.RequireWritableAssetPath(AssetDatabase.GetAssetPath(settings), ASSET_EXTENSION);
            }

            if (!AssetDatabase.IsOpenForEdit(path))
            {
                throw new InvalidOperationException(READ_ONLY_SOURCE + path);
            }

            if (source.TryGetComponent(out Impostor component) && component.BakeSettings == settings
                && (!definition || component.Definition == definition))
            {
                return component;
            }

            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (!contents.TryGetComponent(out Impostor authoring))
                {
                    authoring = contents.AddComponent<Impostor>();
                }

                authoring.BakeSettings = settings;
                if (definition)
                {
                    authoring.Definition = definition;
                }

                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<Impostor>();
        }

        public static async Task<ImpostorDefinition> Rebake(GameObject source, ImpostorBakeSettings settings)
        {
            if (IsBaking || ImpostorBaker.IsBaking)
            {
                throw new InvalidOperationException(BAKE_RUNNING);
            }

            IsBaking = true;
            ImpostorBakeSettings captureSettings = null;
            using var workspace = new ImpostorBakeWorkspace();
            ImpostorDefinition baked = null;
            try
            {
                if (!settings)
                {
                    throw new ArgumentException(MISSING_SETTINGS);
                }

                ImpostorBaker.GetCaptureRenderers(source, settings.SourceLod);
                ImpostorDefinition previous = Attach(source, settings).Definition;
                // Freeze settings for the asynchronous capture. Subsequent edits must remain outdated.
                ImpostorBakeFingerprint.SaveInputs(source, settings);
                string fingerprint = ImpostorBakeFingerprint.Compute(source, settings);
                captureSettings = Object.Instantiate(settings);
                captureSettings.name = settings.name;
                baked = await ImpostorBaker.Bake(source, captureSettings, workspace);
                if (previous)
                {
                    CopyAppearance(previous, baked);
                }

                await BuildDerivedData(baked, previous, captureSettings, workspace);
                baked.BakeData.Settings = settings;
                baked.BakeData.InputFingerprint = fingerprint;
                EditorUtility.SetDirty(baked);
                ImpostorDefinition result = ImpostorPrefabArtifacts.Store(source, baked);
                if (previous && previous != result)
                {
                    ImpostorPrefabArtifacts.ReplaceReferences(previous, result);
                }

                ImpostorWorldRenderer.RefreshPrototypes(previous, result);
                SceneView.RepaintAll();
                return result;
            }
            finally
            {
                if (captureSettings)
                {
                    Object.DestroyImmediate(captureSettings);
                }

                workspace.Capture(baked);
                IsBaking = false;
            }
        }

        internal static string RequireWritableSource(GameObject source)
        {
            string path = AssetDatabase.GetAssetPath(source);
            if (!source || !PrefabUtility.IsPartOfPrefabAsset(source) || source.transform.parent
                || !path.EndsWith(PREFAB_EXTENSION, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(INVALID_SOURCE);
            }

            return ImpostorPackageAssets.RequireWritableAssetPath(path, PREFAB_EXTENSION);
        }

        /// <summary>
        /// Builds the LOD profile while the independent GPU self-shadow workload runs. The self-shadow bake is submitted
        /// first so it overlaps the main-thread asset imports.
        /// </summary>
        private static async Task BuildDerivedData(ImpostorDefinition baked, ImpostorDefinition previous,
            ImpostorBakeSettings settings, ImpostorBakeWorkspace workspace)
        {
            Task<Texture3D> shadow = settings.SelfShadowing
                ? ImpostorBaker.BakeDerivedSelfShadow(baked, null, settings.SelfShadowQuality)
                : null;
            try
            {
                ImpostorLodData previousLods = previous ? previous.LodData : null;
                baked.LodData = ImpostorLodProfileBuilder.BuildForBake(baked, null, previousLods, workspace.Track);
                if (shadow != null)
                {
                    await shadow;
                }
            }
            catch
            {
                if (shadow != null)
                {
                    ImpostorBaker.RequestCancellation();
                    await ObserveCancellation(shadow);
                }

                throw;
            }
        }

        private static async Task ObserveCancellation(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // The cancelled bake's own failure is superseded by the error that cancelled it.
            }
        }

        private static void CopyAppearance(ImpostorDefinition previous, ImpostorDefinition baked)
        {
            baked.Cutoff = previous.Cutoff;
            Material source = previous.Material;
            if (!source)
            {
                return;
            }

            if (source.HasColor(ImpostorShaderProperties.BaseColor))
            {
                baked.Material.SetColor(ImpostorShaderProperties.BaseColor, source.GetColor(ImpostorShaderProperties.BaseColor));
            }

            foreach (int property in PreservedColors)
            {
                if (source.HasColor(property))
                {
                    baked.Material.SetColor(property, source.GetColor(property));
                }
            }

            foreach (int property in PreservedFloats)
            {
                if (source.HasFloat(property))
                {
                    baked.Material.SetFloat(property, source.GetFloat(property));
                }
            }

            ImpostorMaterialControls.ApplyKeywords(baked.Material);
        }
    }
}
