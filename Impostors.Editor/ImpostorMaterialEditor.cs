using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Impostors.Editor
{
    [NoAutoStaticsCleanup]
    public sealed class ImpostorMaterialEditor : ShaderGUI
    {
        private const float MAXIMUM_LABEL_WIDTH = 160;
        private const float LABEL_WIDTH_FRACTION = .45f;
        private const float NARROW_VIEW_WIDTH = 420;
        private const string BAKED_DATA_LABEL = "Baked data (read only)";
        private const string ADVANCED_LABEL = "Advanced";
        private const float TOGGLE_THRESHOLD = .5f;

        private static readonly Row SubsurfaceToggle =
            new(ImpostorShaderProperties.SUBSURFACE_SCATTERING_NAME, "Enabled",
                "Adds a view-dependent sun transmission lobe, matching source graphs that add subsurface lighting through "
                + "Emission. Capture omits that light-dependent term. Off by default; no rebake required.");

        private static readonly Row[] Surface =
        {
            new(ImpostorShaderProperties.BASE_COLOR_NAME, "Base Color tint",
                "Multiplies captured albedo before native URP Lit shading."),
            new(ImpostorShaderProperties.BASE_COLOR_GAIN_NAME, "Base Color gain",
                "Scales captured albedo. Does not change emission or shadow visibility."),
            new(ImpostorShaderProperties.ALBEDO_CONTRAST_NAME, "Albedo contrast",
                "Adjusts captured colour contrast around 18% linear grey. 1 is neutral."),
            new(ImpostorShaderProperties.ALBEDO_SATURATION_NAME, "Albedo saturation",
                "0 is greyscale, 1 preserves captured saturation, above 1 increases it."),
            new(ImpostorShaderProperties.ALBEDO_LIFT_NAME, "Albedo lift",
                "Adds or subtracts linear colour before tint/gain. Can deepen or lift dark regions."),
            new(ImpostorShaderProperties.HUE_VARIATION_NAME, "Per-instance hue variation",
                "RGB is a multiply tint. Alpha controls stable per-instance variation, with 0 disabled."),
        };

        private static readonly Row[] Reflectance =
        {
            new(ImpostorShaderProperties.SPECULAR_TINT_NAME, "Specular tint",
                "Multiplies captured F0 independently of diffuse albedo. Black removes the reflectance lobe."),
            new(ImpostorShaderProperties.SPECULAR_STRENGTH_NAME, "Specular strength",
                "Scales captured reflectance. 0 disables its direct, environment and grazing response."),
            new(ImpostorShaderProperties.METALLIC_INFLUENCE_NAME, "Metallic influence",
                "Uses baked original metalness and workflow. 1 preserves it. 0 makes metallic-workflow regions dielectric. "
                + "Specular-workflow regions remain unchanged. Requires Keep Adjustment Data in the bake settings.", true),
            new(ImpostorShaderProperties.SPECULAR_HIGHLIGHTS_NAME, "Direct highlights",
                "Native URP direct-specular toggle. Diffuse sunlight remains enabled."),
            new(ImpostorShaderProperties.ENVIRONMENT_REFLECTIONS_NAME, "Reflection probes",
                "Native URP environment-probe toggle. URP retains its constant ambient fallback."),
            new(ImpostorShaderProperties.SMOOTHNESS_SCALE_NAME, "Smoothness scale",
                "Scales captured smoothness, clamped to 0–1 after offset."),
            new(ImpostorShaderProperties.SMOOTHNESS_OFFSET_NAME, "Smoothness offset",
                "Offsets scaled captured smoothness. Negative values reduce gloss independently of albedo."),
            new(ImpostorShaderProperties.NORMAL_STRENGTH_NAME, "Normal strength",
                "0 uses the baked geometric normal, 1 uses the captured surface normal, and above 1 exaggerates its detail. "
                + "Requires a format 3 bake with Keep Adjustment Data.", true),
        };

        private static readonly Row[] Occlusion =
        {
            new(ImpostorShaderProperties.OCCLUSION_STRENGTH_NAME, "Captured AO strength",
                "1 keeps authored AO. 0 removes it. Independent of sunlight self-shadowing."),
            new(ImpostorShaderProperties.OCCLUSION_CONTRAST_NAME, "AO contrast",
                "Reshapes captured AO around 0.5. Does not modify direct-sun shadow visibility."),
            new(ImpostorShaderProperties.OCCLUSION_OFFSET_NAME, "AO offset",
                "Adds or subtracts captured ambient occlusion. Negative values deepen it."),
        };

        private static readonly Row[] Emission =
        {
            new(ImpostorShaderProperties.EMISSION_TINT_NAME, "Emission tint",
                "Multiplies captured HDR emission independently of albedo and shadows."),
            new(ImpostorShaderProperties.EMISSION_STRENGTH_NAME, "Emission strength",
                "Scales captured HDR emission. 0 removes it. A non-emissive bake remains non-emissive."),
        };

        private static readonly Row[] SelfShadow =
        {
            new(ImpostorShaderProperties.SHADOW_RECEIVER_BIAS_NAME, "Realtime receiver bias",
                "Moves the realtime shadow lookup towards the light by this fraction of the capture radius. Prevents "
                + "reconstructed impostor surfaces from shadowing themselves (acne). Higher values remove real contact "
                + "shadows. Independent of the light's bias, so mesh LOD shadows are unaffected."),
            new(ImpostorShaderProperties.SELF_SHADOW_STRENGTH_NAME, "Self-shadow strength",
                "0 disables baked sunlight attenuation. Realtime cascade shadows stay native."),
            new(ImpostorShaderProperties.SELF_SHADOW_NORMAL_BIAS_NAME, "Normal bias (voxels)",
                "Offsets the receiver along its captured normal to reduce false outer-shell occlusion. No rebake required."),
            new(ImpostorShaderProperties.SELF_SHADOW_POWER_NAME, "Shadow response",
                "Below 1 lifts intermediate visibility. Above 1 darkens it. Does not alter ambient lighting."),
        };

        private static readonly Row[] Subsurface =
        {
            new(ImpostorShaderProperties.SUBSURFACE_SCATTERING_TINT_NAME, "Tint",
                "Multiplies raw captured albedo to give the transmitted colour. Independent of the Surface controls."),
            new(ImpostorShaderProperties.SUBSURFACE_SCATTERING_STRENGTH_NAME, "Strength",
                "Scales direct sun transmission. Uses the same main-light colour, cookies and shadow visibility as "
                + "diffuse sunlight."),
            new(ImpostorShaderProperties.SUBSURFACE_SCATTERING_ROUGHNESS_NAME, "Spread",
                "GGX roughness of the lobe around the view-towards-sun direction. Lower values give a tighter, brighter "
                + "glow. Match the source graph's subsurface roughness (0.7 for the ForestLake foliage)."),
            new(ImpostorShaderProperties.SUBSURFACE_SCATTERING_AMBIENT_NAME, "Ambient",
                "Adds ambient SH light multiplied by the transmitted colour, like an indirect subsurface intensity. "
                + "0 disables it."),
        };

        private static readonly Row[] DistanceOptimization =
        {
            new(ImpostorShaderProperties.DETAIL_FULL_PIXELS_NAME, "Full detail above (px)",
                "Trees at least this tall on screen keep full parallax and three-view blending. Set both sliders to 0 to "
                + "always use full detail."),
            new(ImpostorShaderProperties.DETAIL_REDUCED_PIXELS_NAME, "Lowest detail below (px)",
                "Trees at most this tall on screen skip parallax and mostly read one view. Detail fades smoothly between "
                + "the two sizes."),
        };

        private bool _advanced;
        private bool _bakedData;

        public override void ValidateMaterial(Material material) => ImpostorMaterialControls.ApplyKeywords(material);

        public override void OnGUI(MaterialEditor editor, MaterialProperty[] properties)
        {
            editor.SetDefaultGUIWidths();
            EditorGUIUtility.labelWidth = Mathf.Min(MAXIMUM_LABEL_WIDTH, EditorGUIUtility.currentViewWidth * LABEL_WIDTH_FRACTION);
            bool hasAdjustmentData = (editor.target as Material)?.GetTexture(ImpostorShaderProperties.MaterialDataArray);
            DrawSection(editor, properties, "Surface", Surface, hasAdjustmentData);
            DrawSection(editor, properties, "Reflectance", Reflectance, hasAdjustmentData);
            DrawSection(editor, properties, "Captured occlusion", Occlusion, hasAdjustmentData);
            DrawSection(editor, properties, "Captured emission", Emission, hasAdjustmentData);
            _advanced = EditorGUILayout.Foldout(_advanced, ADVANCED_LABEL, true);
            if (_advanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    DrawSubsurface(editor, properties, hasAdjustmentData);
                    DrawSection(editor, properties, "Shadows", SelfShadow, hasAdjustmentData);
                    DrawSection(editor, properties, "Distance optimization", DistanceOptimization, hasAdjustmentData);
                }
            }

            _bakedData = EditorGUILayout.Foldout(_bakedData, BAKED_DATA_LABEL, true);
            if (_bakedData)
            {
                DrawTextures(editor, properties);
            }
        }

        private static void DrawSubsurface(MaterialEditor editor, MaterialProperty[] properties, bool hasAdjustmentData)
        {
            EditorGUILayout.LabelField("Subsurface scattering", EditorStyles.boldLabel);
            MaterialProperty toggle = FindProperty(SubsurfaceToggle.Name, properties);
            DrawProperty(editor, toggle, new GUIContent(SubsurfaceToggle.Label, SubsurfaceToggle.Tooltip));
            using (new EditorGUI.DisabledScope(toggle.hasMixedValue || toggle.floatValue <= TOGGLE_THRESHOLD))
            {
                DrawRows(editor, properties, Subsurface, hasAdjustmentData);
            }

            EditorGUILayout.Space();
        }

        private static void DrawSection(MaterialEditor editor, MaterialProperty[] properties, string title, Row[] rows,
            bool hasAdjustmentData)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            DrawRows(editor, properties, rows, hasAdjustmentData);
            EditorGUILayout.Space();
        }

        private static void DrawRows(MaterialEditor editor, MaterialProperty[] properties, Row[] rows, bool hasAdjustmentData)
        {
            foreach (Row row in rows)
            {
                using (new EditorGUI.DisabledScope(row.NeedsAdjustmentData && !hasAdjustmentData))
                {
                    DrawProperty(editor, FindProperty(row.Name, properties), new GUIContent(row.Label, row.Tooltip));
                }
            }
        }

        private static void DrawProperty(MaterialEditor editor, MaterialProperty property, GUIContent content)
        {
            // Narrow inspectors give range sliders their own line.
            if (property.propertyType == ShaderPropertyType.Range && EditorGUIUtility.currentViewWidth < NARROW_VIEW_WIDTH)
            {
                EditorGUILayout.LabelField(content);
                editor.ShaderProperty(property, GUIContent.none);
            }
            else
            {
                editor.ShaderProperty(property, content);
            }
        }

        private static void DrawTextures(MaterialEditor editor, MaterialProperty[] properties)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                foreach (MaterialProperty property in properties)
                {
                    if (property.propertyType == ShaderPropertyType.Texture)
                    {
                        editor.TexturePropertySingleLine(new GUIContent(property.displayName), property);
                    }
                }
            }
        }

        /// <summary>One material property row: shader name, label, tooltip and whether it reads adjustment data.</summary>
        private readonly struct Row
        {
            public readonly string Name;
            public readonly string Label;
            public readonly string Tooltip;
            public readonly bool NeedsAdjustmentData;

            public Row(string name, string label, string tooltip, bool needsAdjustmentData = false)
            {
                Name = name;
                Label = label;
                Tooltip = tooltip;
                NeedsAdjustmentData = needsAdjustmentData;
            }
        }
    }
}
