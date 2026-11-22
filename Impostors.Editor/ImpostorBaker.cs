using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Captures a source prefab's LOD into the impostor texture arrays and builds its definition.</summary>
    [NoAutoStaticsCleanup]
    public static partial class ImpostorBaker
    {
        private const string IDLE = "Idle";
        private const string CANCEL_MENU = "Tools/Impostors/Cancel Current Bake";
        private const string RUNTIME_SHADER = "Impostors/Shaders/HemiOctahedral.shader";
        private const string INVALID_SETTINGS = "A source and valid capture settings are required.";
        private const string ATLAS_TOO_LARGE = "Atlas-equivalent resolution cannot exceed 4096 × 4096.";
        private const string BAKE_RUNNING = "An impostor bake is already running.";
        private const string MISSING_IMPOSTOR = "The source prefab root must have an Impostor component.";
        private const string NON_STATIC_RENDERER = "Only static MeshRenderers are supported.";
        private const string EMPTY_LOD = "The selected LOD contains no geometry.";
        private const string MISSING_SHADER = "Source materials require a shader.";
        private const string CAPTURE_BUFFER_NAME = "Impostor surface capture";
        private const string FRAME_RECTANGLES_SUFFIX = " Frame rectangles";
        private const string BASE_SUFFIX = " Base";
        private const string SURFACE_SUFFIX = " Surface";
        private const string DEPTH_SUFFIX = " Depth";
        private const string REFLECTANCE_SUFFIX = " Reflectance";
        private const string MATERIAL_DATA_SUFFIX = " Material Data";
        private const string EMISSION_SUFFIX = " Emission";
        private const string MATERIAL_SUFFIX = " Impostor";
        private const int MINIMUM_RESOLUTION = 32;
        private const int ANISO_LEVEL = 4;
        private const int DILATION_ITERATIONS = 8;
        private const int DEPTH_FILTER_VERSION = 1;
        private const float RADIUS_MARGIN = 1.001f;
        private const float CROWN_START = 0.25f;

        private static volatile bool _cancellationRequested;
        private static bool _isBaking;

        public static string CurrentCapture { get; private set; } = IDLE;
        public static bool IsBaking => _isBaking;

        [MenuItem(CANCEL_MENU)]
        public static void RequestCancellation()
        {
            _cancellationRequested = true;
        }

        public static Material CreateRuntimeMaterial(ImpostorDefinition definition, bool additionalLights = true)
        {
            var material = new Material(ImpostorPackageAssets.Load<Shader>(RUNTIME_SHADER))
            {
                name = definition.name + MATERIAL_SUFFIX,
                enableInstancing = true,
            };
            material.SetTexture(ImpostorShaderProperties.BaseArray, definition.BaseColor);
            material.SetTexture(ImpostorShaderProperties.SurfaceArray, definition.Surface);
            material.SetTexture(ImpostorShaderProperties.ReflectanceArray, definition.Reflectance);
            material.SetTexture(ImpostorShaderProperties.MaterialDataArray, definition.MaterialData);
            material.SetTexture(ImpostorShaderProperties.EmissionArray, definition.Emission);
            material.SetFloat(ImpostorShaderProperties.HasEmission, definition.Emission ? 1 : 0);
            ImpostorUniformReflectance.Apply(definition, material);
            material.SetTexture(ImpostorShaderProperties.DepthArray, definition.Depth);
            material.SetTexture(ImpostorShaderProperties.FrameRectangles, definition.FrameRectangles);
            material.SetFloat(ImpostorShaderProperties.HasFrameRectangles, definition.FrameRectangles ? 1 : 0);
            material.SetVector(ImpostorShaderProperties.CaptureCenter, definition.Center);
            material.SetFloat(ImpostorShaderProperties.CaptureRadius, definition.Radius);
            material.SetFloat(ImpostorShaderProperties.Frames, definition.Frames);
            material.SetFloat(ImpostorShaderProperties.Border, definition.Border / (float)definition.Resolution);
            material.SetVector(ImpostorShaderProperties.BoundsMin, definition.Bounds.min);
            material.SetVector(ImpostorShaderProperties.BoundsMax, definition.Bounds.max);
            material.SetFloat(ImpostorShaderProperties.Cutoff, definition.Cutoff);
            material.SetFloat(ImpostorShaderProperties.AdditionalLights, additionalLights ? 1 : 0);
            ImpostorMaterialControls.ApplyKeywords(material);
            if (definition.HasValidSelfShadowVolume)
            {
                definition.SelfShadowLayout.Configure(material);
                material.SetTexture(ImpostorShaderProperties.SelfShadowVolume, definition.SelfShadowVolume);
                material.EnableKeyword(ImpostorShaderKeywords.SELF_SHADOW_VOLUME);
            }

            return material;
        }

        internal static async Task<ImpostorDefinition> Bake(GameObject source, ImpostorBakeSettings settings,
            ImpostorBakeWorkspace workspace)
        {
            Renderer[] renderers = ValidateBake(source, settings, workspace);
            ImpostorBakeFingerprint.SaveInputs(source, settings);
            string inputFingerprint = ImpostorBakeFingerprint.Compute(source, settings);
            _isBaking = true;
            _cancellationRequested = false;
            var parts = new List<Part>();
            var positions = new List<Vector3>();
            BakedTextures textures = null;
            ImpostorDefinition definition = null;
            bool completed = false;
            bool asynchronousShaders = ShaderUtil.allowAsyncCompilation;
            ShaderUtil.allowAsyncCompilation = false;
            try
            {
                CollectParts(source, renderers, parts, positions);
                Bounds bounds = BoundsOf(positions);
                Vector3 center = bounds.center;
                float radius = positions.Max(point => Vector3.Distance(point, center)) * RADIUS_MARGIN;
                var capture = new CaptureView(settings, parts, positions, center, radius);
                textures = BakedTextures.Create(source.name, settings);
                int slices = settings.Frames * settings.Frames;
                bool hasEmission = await CaptureFrames(source.name, slices, settings.Resolution * settings.Supersampling,
                    settings.Resolution, textures,
                    (frame, targets) => capture.Submit(frame, targets, textures.FrameRectangles));
                textures.Apply(hasEmission);
                definition = CreateDefinition(source, settings, textures, positions, bounds, radius);
                definition.ContentHash = inputFingerprint;
                definition.BakeData = new ImpostorBakeData
                {
                    Settings = AssetDatabase.Contains(settings) ? settings : null,
                    SettingsJson = JsonUtility.ToJson(settings),
                    InputFingerprint = inputFingerprint,
                };
                EditorUtility.SetDirty(definition);
                AssetDatabase.SaveAssetIfDirty(definition);
                completed = true;
                workspace.Capture(definition);
                return definition;
            }
            finally
            {
                foreach (Part part in parts)
                {
                    Object.DestroyImmediate(part.Material);
                }

                if (!completed && (!definition || !AssetDatabase.Contains(definition)))
                {
                    DestroyIncomplete(textures, definition);
                }

                EditorUtility.ClearProgressBar();
                CurrentCapture = IDLE;
                _isBaking = false;
                ShaderUtil.allowAsyncCompilation = asynchronousShaders;
            }
        }

        private static Renderer[] ValidateBake(GameObject source, ImpostorBakeSettings settings, ImpostorBakeWorkspace workspace)
        {
            if (!source || !settings || settings.Frames < 2 || settings.Resolution < MINIMUM_RESOLUTION
                || !Mathf.IsPowerOfTwo(settings.Resolution) || settings.Border < 1 || settings.Supersampling < 1
                || settings.Border * 2 >= settings.Resolution)
            {
                throw new ArgumentException(INVALID_SETTINGS);
            }

            Renderer[] renderers = GetCaptureRenderers(source, settings.SourceLod);
            if ((long)settings.Frames * settings.Resolution > ImpostorCaptureLayout.MAXIMUM_ATLAS_RESOLUTION)
            {
                throw new ArgumentException(ATLAS_TOO_LARGE);
            }

            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            if (_isBaking)
            {
                throw new InvalidOperationException(BAKE_RUNNING);
            }

            if (!source.GetComponent<Impostor>())
            {
                throw new ArgumentException(MISSING_IMPOSTOR);
            }

            return renderers;
        }

        private static void CollectParts(GameObject source, Renderer[] renderers, List<Part> parts, List<Vector3> positions)
        {
            foreach (Renderer renderer in renderers)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh)
                {
                    throw new NotSupportedException(NON_STATIC_RENDERER);
                }

                Matrix4x4 matrix = source.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                positions.AddRange(filter.sharedMesh.vertices.Select(matrix.MultiplyPoint3x4));
                for (int submesh = 0; submesh < filter.sharedMesh.subMeshCount; submesh++)
                {
                    Material material = CaptureMaterial(renderer.sharedMaterials[submesh]);
                    parts.Add(new Part { Mesh = filter.sharedMesh, Matrix = matrix, Material = material, Pass = material.FindPass(ImpostorShaderPasses.FORWARD_LIT), Submesh = submesh });
                }
            }

            if (positions.Count == 0)
            {
                throw new ArgumentException(EMPTY_LOD);
            }
        }

        private static Bounds BoundsOf(List<Vector3> positions)
        {
            var bounds = new Bounds(positions[0], Vector3.zero);
            foreach (Vector3 point in positions)
            {
                bounds.Encapsulate(point);
            }

            return bounds;
        }

        private static ImpostorDefinition CreateDefinition(GameObject source, ImpostorBakeSettings settings,
            BakedTextures textures, List<Vector3> positions, Bounds bounds, float radius)
        {
            var definition = ScriptableObject.CreateInstance<ImpostorDefinition>();
            definition.SourcePrefab = source;
            definition.name = source.name;
            definition.Frames = settings.Frames;
            definition.Resolution = settings.Resolution;
            definition.Border = settings.Border;
            definition.Center = bounds.center;
            definition.Radius = radius;
            definition.CrownHeights = new Vector2(bounds.min.y + bounds.size.y * CROWN_START, bounds.max.y);
            definition.Proxy = ImpostorProxy.Build(positions.ToArray(), radius * 2 / settings.Resolution);
            definition.Bounds = definition.Proxy.bounds;
            definition.BaseColor = textures.BaseColor;
            definition.Surface = textures.Surface;
            definition.Depth = textures.Depth;
            definition.Reflectance = textures.Reflectance;
            definition.MaterialData = textures.MaterialData;
            definition.Emission = textures.Emission;
            definition.FrameRectangles = textures.FrameRectangles;
            definition.DepthFilterVersion = DEPTH_FILTER_VERSION;
            definition.Material = CreateRuntimeMaterial(definition, !settings.DisableImpostorAdditionalLights);
            // The material now holds the constant, so the shader never reads this array.
            if (definition.Material.GetFloat(ImpostorShaderProperties.HasUniformReflectance) > .5f)
            {
                definition.Reflectance = null;
                definition.Material.SetTexture(ImpostorShaderProperties.ReflectanceArray, null);
                Object.DestroyImmediate(textures.Reflectance);
                textures.Reflectance = null;
            }

            return definition;
        }

        private static void DestroyIncomplete(BakedTextures textures, ImpostorDefinition definition)
        {
            textures?.Destroy();
            if (!definition)
            {
                return;
            }

            if (definition.Proxy)
            {
                Object.DestroyImmediate(definition.Proxy);
            }

            if (definition.Material)
            {
                Object.DestroyImmediate(definition.Material);
            }

            Object.DestroyImmediate(definition);
        }

        private static RenderTexture Target(int size, RenderTextureFormat format, int depth)
        {
            var target = new RenderTexture(size, size, depth, format, RenderTextureReadWrite.Linear);
            target.Create();
            return target;
        }

        private static Material CaptureMaterial(Material source)
        {
            if (!source || !source.shader)
            {
                throw new ArgumentException(MISSING_SHADER);
            }

            return ImpostorCaptureMaterial.Create(source);
        }

        private static Color[] Read(RenderTexture target)
        {
            RenderTexture previous = RenderTexture.active;
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply(false);
                return texture.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(texture);
            }
        }

        /// <summary>Box-filters every channel by alpha coverage. Normals are averaged decoded, depth keeps the nearest.</summary>
        private static void Downsample(SliceChannels channels, int oldSize, int size)
        {
            if (size == oldSize)
            {
                return;
            }

            int factor = oldSize / size;
            var next = new SliceChannels(size * size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    DownsampleTexel(channels, next, oldSize, factor, x, y, y * size + x);
                }
            }

            channels.CopyFrom(next);
        }

        private static void DownsampleTexel(SliceChannels source, SliceChannels target, int oldSize, int factor, int x, int y,
            int output)
        {
            Color color = Color.clear;
            Color surface = Color.clear;
            Color reflectance = Color.clear;
            Color emission = Color.clear;
            Color material = Color.clear;
            Vector3 normal = Vector3.zero;
            Vector3 geometricNormal = Vector3.zero;
            float depth = 0;
            float coverage = 0;
            for (int dy = 0; dy < factor; dy++)
            {
                for (int dx = 0; dx < factor; dx++)
                {
                    int index = (y * factor + dy) * oldSize + x * factor + dx;
                    float alpha = source.Colors[index].a;
                    color += source.Colors[index] * alpha;
                    surface += source.Surfaces[index] * alpha;
                    reflectance += source.Reflectance[index] * alpha;
                    emission += source.Emission[index] * alpha;
                    material += source.Materials[index] * alpha;
                    normal += DecodeNormal(source.Surfaces[index]) * alpha;
                    geometricNormal += DecodeNormal(source.Materials[index]) * alpha;
                    if (alpha > 0)
                    {
                        depth = Mathf.Max(depth, source.Depths[index].r);
                    }

                    coverage += alpha;
                }
            }

            if (coverage > 0)
            {
                color /= coverage;
                surface = EncodeNormal(surface / coverage, normal);
                reflectance /= coverage;
                emission /= coverage;
                material = EncodeNormal(material / coverage, geometricNormal);
                target.Depths[output] = new Color(depth, 0, 0, 1);
            }

            color.a = coverage / (factor * factor);
            target.Colors[output] = color;
            target.Surfaces[output] = surface;
            target.Reflectance[output] = reflectance;
            target.Emission[output] = emission;
            target.Materials[output] = material;
        }

        private static Vector3 DecodeNormal(Color packed) => ImpostorMath.DecodeNormal(new Vector2(packed.r, packed.g));

        private static Color EncodeNormal(Color packed, Vector3 normal)
        {
            Vector2 encoded = ImpostorMath.EncodeNormal(normal.normalized);
            packed.r = encoded.x;
            packed.g = encoded.y;
            return packed;
        }

        /// <summary>Grows covered texels into empty neighbours so bilinear filtering never reads undefined data.</summary>
        private static void Dilate(SliceChannels channels, int size)
        {
            var valid = new bool[channels.Colors.Length];
            var next = new bool[channels.Colors.Length];
            for (int i = 0; i < valid.Length; i++)
            {
                valid[i] = channels.Colors[i].a > 0;
            }

            for (int iteration = 0; iteration < DILATION_ITERATIONS; iteration++)
            {
                Array.Copy(valid, next, valid.Length);
                if (!DilateOnce(channels, valid, next, size))
                {
                    return;
                }

                (valid, next) = (next, valid);
            }
        }

        private static bool DilateOnce(SliceChannels channels, bool[] valid, bool[] next, int size)
        {
            bool changed = false;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int index = y * size + x;
                    if (valid[index])
                    {
                        continue;
                    }

                    int neighbor = ValidNeighbor(valid, size, x, y);
                    if (neighbor < 0)
                    {
                        continue;
                    }

                    channels.CopyTexel(neighbor, index);
                    next[index] = true;
                    changed = true;
                }
            }

            return changed;
        }

        private static int ValidNeighbor(bool[] valid, int size, int x, int y)
        {
            int index = y * size + x;
            if (x > 0 && valid[index - 1])
            {
                return index - 1;
            }

            if (x + 1 < size && valid[index + 1])
            {
                return index + 1;
            }

            if (y > 0 && valid[index - size])
            {
                return index - size;
            }

            return y + 1 < size && valid[index + size] ? index + size : -1;
        }

        private sealed class Part
        {
            public Mesh Mesh;
            public Matrix4x4 Matrix;
            public Material Material;
            public int Submesh;
            public int Pass;
        }

        /// <summary>Renders one hemi-octahedral view of the source into the capture targets.</summary>
        private sealed class CaptureView
        {
            private readonly ImpostorBakeSettings _settings;
            private readonly List<Part> _parts;
            private readonly List<Vector3> _positions;
            private readonly Vector3 _center;
            private readonly float _radius;

            public CaptureView(ImpostorBakeSettings settings, List<Part> parts, List<Vector3> positions, Vector3 center,
                float radius)
            {
                _settings = settings;
                _parts = parts;
                _positions = positions;
                _center = center;
                _radius = radius;
            }

            public void Submit(int frame, RenderTexture[] targets, Texture2D frameRectangles)
            {
                float interior = 1 - 2 * _settings.Border / (float)_settings.Resolution;
                var view = CaptureFrame.Fit(frame, _settings.Frames, _positions, _center, _radius, interior);
                frameRectangles.SetPixel(frame, 0, view.Rectangle(_radius));
                Draw(targets, view.View, view.Projection, view.CameraProjection, view.Direction);
            }

            private void Draw(RenderTexture[] targets, Matrix4x4 view, Matrix4x4 projection, Matrix4x4 cameraProjection, Vector3 direction)
            {
                using var commands = new CommandBuffer { name = CAPTURE_BUFFER_NAME };
                // Bind the texture before configuring the camera: native projection
                // winding depends on whether the active target is a texture.
                commands.SetRenderTarget(targets[0]);
                using (new ImpostorCaptureState(commands, view, projection, cameraProjection, targets[0].width))
                {
                    var properties = new MaterialPropertyBlock();
                    for (int mode = 0; mode < targets.Length; mode++)
                    {
                        commands.SetRenderTarget(targets[mode]);
                        commands.ClearRenderTarget(true, true, Color.clear);
                        foreach (Part part in _parts)
                        {
                            properties.SetInteger(ImpostorShaderProperties.ImpostorCaptureMode, mode);
                            properties.SetVector(ImpostorShaderProperties.CaptureCenter, _center);
                            properties.SetVector(ImpostorShaderProperties.CaptureDirection, direction);
                            properties.SetFloat(ImpostorShaderProperties.CaptureRadius, _radius);
                            commands.DrawMesh(part.Mesh, part.Matrix, part.Material, part.Submesh, part.Pass, properties);
                        }
                    }
                }

                Graphics.ExecuteCommandBuffer(commands);
            }
        }

        /// <summary>The texture arrays one bake writes, destroyed together when the bake does not complete.</summary>
        private sealed class BakedTextures
        {
            public Texture2DArray BaseColor;
            public Texture2DArray Surface;
            public Texture2DArray Depth;
            public Texture2DArray Reflectance;
            public Texture2DArray MaterialData;
            public Texture2DArray Emission;
            public Texture2D FrameRectangles;

            public static BakedTextures Create(string name, ImpostorBakeSettings settings)
            {
                int size = settings.Resolution;
                int slices = settings.Frames * settings.Frames;
                int surfaceSize = settings.HalfResolutionSurface ? size / 2 : size;
                int depthSize = settings.CompactDepth ? size / 2 : size;
                TextureFormat depthFormat = settings.CompactDepth ? TextureFormat.BC4 : TextureFormat.R16;
                return new BakedTextures
                {
                    FrameRectangles = new Texture2D(slices, 1, TextureFormat.RGBAFloat, false, true)
                    {
                        name = name + FRAME_RECTANGLES_SUFFIX,
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                    },
                    BaseColor = CreateArray(name + BASE_SUFFIX, size, slices, TextureFormat.BC7, false),
                    Surface = CreateArray(name + SURFACE_SUFFIX, surfaceSize, slices, TextureFormat.BC7, true),
                    Depth = CreateArray(name + DEPTH_SUFFIX, depthSize, slices, depthFormat, true),
                    Reflectance = CreateArray(name + REFLECTANCE_SUFFIX, size, slices, TextureFormat.BC7, true),
                    MaterialData = !settings.DiscardAdjustmentData
                        ? CreateArray(name + MATERIAL_DATA_SUFFIX, size, slices, TextureFormat.BC7, true)
                        : null,
                    Emission = CreateArray(name + EMISSION_SUFFIX, size, slices, TextureFormat.RGBAHalf, true),
                };
            }

            /// <summary>Uploads the captured slices and drops the emission array when nothing emits.</summary>
            public void Apply(bool hasEmission)
            {
                BaseColor.Apply(false, true);
                Surface.Apply(false, true);
                Depth.Apply(false, true);
                Reflectance.Apply(false, true);
                if (MaterialData)
                {
                    MaterialData.Apply(false, true);
                }

                if (hasEmission)
                {
                    Emission.Apply(false, true);
                }
                else
                {
                    Object.DestroyImmediate(Emission);
                    Emission = null;
                }

                // This small lookup table must stay CPU-readable for safe prefab subasset cloning.
                FrameRectangles.Apply(false);
            }

            public void Destroy()
            {
                foreach (Object asset in new Object[]
                {
                    BaseColor, Surface, Depth, Reflectance, MaterialData, Emission, FrameRectangles,
                })
                {
                    if (asset)
                    {
                        Object.DestroyImmediate(asset);
                    }
                }
            }

            private static Texture2DArray CreateArray(string name, int size, int slices, TextureFormat format, bool linear)
            {
                return new Texture2DArray(size, size, slices, format, true, linear)
                {
                    name = name,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    anisoLevel = ANISO_LEVEL,
                };
            }
        }

        /// <summary>One capture slice's channels at a single resolution. Only managed arrays, safe on worker threads.</summary>
        private sealed class SliceChannels
        {
            public Color[] Colors;
            public Color[] Surfaces;
            public Color[] Depths;
            public Color[] Reflectance;
            public Color[] Emission;
            public Color[] Materials;

            public SliceChannels(int texels)
            {
                Colors = new Color[texels];
                Surfaces = new Color[texels];
                Depths = new Color[texels];
                Reflectance = new Color[texels];
                Emission = new Color[texels];
                Materials = new Color[texels];
            }

            public SliceChannels(Color[] colors, Color[] surfaces, Color[] depths, Color[] reflectance, Color[] emission,
                Color[] materials)
            {
                Colors = colors;
                Surfaces = surfaces;
                Depths = depths;
                Reflectance = reflectance;
                Emission = emission;
                Materials = materials;
            }

            public void CopyFrom(SliceChannels other)
            {
                Colors = other.Colors;
                Surfaces = other.Surfaces;
                Depths = other.Depths;
                Reflectance = other.Reflectance;
                Emission = other.Emission;
                Materials = other.Materials;
            }

            /// <summary>Copies every channel of one texel. Colour keeps zero alpha so coverage is unchanged.</summary>
            public void CopyTexel(int from, int to)
            {
                Color neighbor = Colors[from];
                Colors[to] = new Color(neighbor.r, neighbor.g, neighbor.b, 0);
                Surfaces[to] = Surfaces[from];
                Depths[to] = Depths[from];
                Reflectance[to] = Reflectance[from];
                Emission[to] = Emission[from];
                Materials[to] = Materials[from];
            }
        }
    }
}
