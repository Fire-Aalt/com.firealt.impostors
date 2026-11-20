using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Impostors.Data;
using Impostors.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Impostors.Tests
{
    // All fixtures are owned by the test run. No sample, vendor, or host project assets are read.

    internal static class ImpostorTestAssets
    {
        public const string TemporaryRoot = "Assets/ImpostorsTestTemp";
        private static readonly GameObject[] s_prefabs = new GameObject[2];
        private static readonly ImpostorDefinition[] s_definitions = new ImpostorDefinition[2];
        private static string s_folder;
        private static bool s_createdRoot;
        public const string CaptureGraphPath = "Packages/com.fire.impostors/Impostors.Tests/Fixtures/LitCapture.shadergraph";
        public static Shader CaptureShader => AssetDatabase.LoadAssetAtPath<Shader>(CaptureGraphPath);
        internal static string Folder => s_folder;

        public static GameObject Prefab(int index = 1) => s_prefabs[index - 1];
        public static ImpostorDefinition Definition(int index = 1) => s_definitions[index - 1];

        public static void Initialize()
        {
            s_createdRoot = !AssetDatabase.IsValidFolder(TemporaryRoot);
            if (s_createdRoot) AssetDatabase.CreateFolder("Assets", "ImpostorsTestTemp");
            s_folder = NewFolder("Fixtures");
            for (int index = 0; index < 2; index++) CreatePrototype(index);
        }

        public static string NewFolder(string prefix)
        {
            if (!AssetDatabase.IsValidFolder(TemporaryRoot)) AssetDatabase.CreateFolder("Assets", "ImpostorsTestTemp");
            string name = prefix + "-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(TemporaryRoot, name);
            return TemporaryRoot + "/" + name;
        }

        internal static async Task<(ImpostorDefinition Definition, string Folder)> BakePrimitive()
        {
            string folder = NewFolder("NativePicking");
            GameObject root = null;
            try
            {
                var settings = ScriptableObject.CreateInstance<ImpostorBakeSettings>();
                settings.Frames = 4;
                settings.Resolution = 64;
                settings.Border = 2;
                settings.Supersampling = 1;
                settings.AmbientSamples = 4;
                settings.SelfShadowing = false;
                AssetDatabase.CreateAsset(settings, folder + "/Settings.asset");
                var material = new Material(CaptureShader);
                material.SetColor("_BaseColor", new Color(.35f, .55f, .25f, 1));
                AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                root = new GameObject("Picking primitive", typeof(LODGroup));
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.transform.SetParent(root.transform, false);
                sphere.transform.localPosition = new Vector3(0, 5, 0);
                sphere.transform.localScale = Vector3.one * 8;
                sphere.GetComponent<MeshRenderer>().sharedMaterial = material;
                root.GetComponent<LODGroup>().SetLODs(new[] { new LOD(.1f, new[] { sphere.GetComponent<Renderer>() }) });
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/Source.prefab");
                Object.DestroyImmediate(root);
                root = null;
                return (await ImpostorAuthoring.Rebake(prefab, settings), folder);
            }
            catch
            {
                AssetDatabase.DeleteAsset(folder);
                throw;
            }
            finally { if (root) Object.DestroyImmediate(root); }
        }

        private static void CreatePrototype(int index)
        {
            const int frames = 4;
            const int size = 64;
            const float radius = 6;
            const float sphereRadius = 4;
            var center = new Vector3(0, 5, 0);
            var definition = ScriptableObject.CreateInstance<ImpostorDefinition>();
            definition.name = "Portable sphere " + index;
            definition.Frames = frames;
            definition.Resolution = size;
            definition.Border = 2;
            definition.Radius = radius;
            definition.Center = center;
            definition.Bounds = new Bounds(center, Vector3.one * 9);
            definition.ContentHash = "portable-sphere-" + index;
            AssetDatabase.CreateAsset(definition, s_folder + "/Definition" + index + ".asset");
            definition.BaseColor = Embed(new Texture2DArray(size, size, frames * frames, TextureFormat.RGBA32, true, false), definition, "Color");
            definition.Surface = Embed(new Texture2DArray(size, size, frames * frames, TextureFormat.RGBA32, true, true), definition, "Surface");
            definition.Reflectance = Embed(new Texture2DArray(size, size, frames * frames, TextureFormat.RGBA32, true, true), definition, "Reflectance");
            definition.MaterialData = Embed(new Texture2DArray(size, size, frames * frames, TextureFormat.RGBA32, true, true), definition, "Material data");
            definition.Depth = Embed(new Texture2DArray(size, size, frames * frames, TextureFormat.RFloat, true, true), definition, "Depth");
            definition.BaseColor.wrapMode = definition.Surface.wrapMode = definition.Depth.wrapMode = TextureWrapMode.Clamp;
            definition.Depth.filterMode = FilterMode.Point;
            definition.FrameRectangles = Embed(new Texture2D(frames * frames, 1, TextureFormat.RGBAFloat, false, true), definition, "Rectangles");
            for (int frame = 0; frame < frames * frames; frame++)
            {
                Vector3 direction = ImpostorMath.Decode(new Vector2(frame % frames, frame / frames) / (frames - 1));
                ImpostorMath.Basis(direction, out Vector3 right, out Vector3 up);
                var colors = new Color[size * size];
                var surfaces = new Color[size * size];
                var depths = new float[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float px = ((x + .5f) / size - .5f) * radius * 2;
                    float py = ((y + .5f) / size - .5f) * radius * 2;
                    float squared = px * px + py * py;
                    if (squared >= sphereRadius * sphereRadius) continue;
                    float z = Mathf.Sqrt(sphereRadius * sphereRadius - squared);
                    Vector3 normal = (right * px + up * py + direction * z).normalized;
                    Vector2 encoded = ImpostorMath.EncodeNormal(normal);
                    int pixel = y * size + x;
                    colors[pixel] = new Color(.35f + index * .15f, .55f, .25f, 1);
                    surfaces[pixel] = new Color(encoded.x, encoded.y, 1, 0);
                    depths[pixel] = z / (2 * radius) + .5f;
                }
                definition.BaseColor.SetPixels(colors, frame);
                definition.Surface.SetPixels(surfaces, frame);
                definition.MaterialData.SetPixels(Array.ConvertAll(surfaces, s => new Color(s.r,s.g,0,0)), frame);
                var reflectance = new Color[size * size]; Array.Fill(reflectance, new Color(.04f,.04f,.04f,.96f));
                definition.Reflectance.SetPixels(reflectance, frame);
                definition.Depth.SetPixelData(depths, 0, frame);
                definition.FrameRectangles.SetPixel(frame, 0, new Color(1, 1, 0, 0));
            }
            definition.BaseColor.Apply(true);
            definition.Surface.Apply(true);
            definition.Reflectance.Apply(true);
            definition.MaterialData.Apply(true);
            definition.Depth.Apply(true);
            definition.FrameRectangles.Apply();
            var corners = new List<Vector3>();
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2) corners.Add(center + new Vector3(x, y, z) * 4.5f);
            definition.Proxy = Embed(ImpostorProxy.Build(corners.ToArray(), .01f), definition, "Proxy");
            definition.SelfShadowLayout = ImpostorSelfShadowLayout.ForBounds(definition.Bounds, ImpostorSelfShadowQuality.Low);
            Vector3Int dimensions = definition.SelfShadowLayout.AtlasSize(frames);
            var volume = new Texture3D(dimensions.x, dimensions.y, dimensions.z, TextureFormat.R8, false) { filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp };
            var values = new byte[dimensions.x * dimensions.y * dimensions.z];
            Array.Fill(values, (byte)64);
            volume.SetPixelData(values, 0); volume.Apply();
            definition.SelfShadowVolume = Embed(volume, definition, "Constant visibility");
            definition.SelfShadowSourceHash = definition.ContentHash;
            definition.Material = Embed(ImpostorBaker.CreateRuntimeMaterial(definition), definition, "Impostor material");
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(primitive);
            Vector3[] vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = vertices[i] * (sphereRadius * 2) + center;
            mesh.vertices = vertices; mesh.RecalculateBounds();
            Embed(mesh, definition, "Mesh LOD");
            var material = Embed(new Material(Shader.Find("Hidden/Impostors/Tests/MeshLighting")) { enableInstancing = true }, definition, "Mesh material");
            definition.LodData = new ImpostorLodData { ReferencePoint = center, Size = sphereRadius * 2, MeshLevels = new[] { new ImpostorMeshLod { TransitionScreenHeight = .1f, FadeWidth = 0, Parts = new[] { new ImpostorLodPart { Mesh = mesh, Material = material } } } } };
            var root = new GameObject(definition.name, typeof(Impostor), typeof(LODGroup));
            var child = new GameObject("Mesh", typeof(MeshFilter), typeof(MeshRenderer));
            child.transform.SetParent(root.transform, false);
            child.GetComponent<MeshFilter>().sharedMesh = mesh;
            child.GetComponent<MeshRenderer>().sharedMaterial = material;
            root.GetComponent<Impostor>().Definition = definition;
            root.GetComponent<LODGroup>().SetLODs(new[] { new LOD(.1f, new[] { child.GetComponent<Renderer>() }) });
            s_prefabs[index] = PrefabUtility.SaveAsPrefabAsset(root, s_folder + "/Prototype" + index + ".prefab");
            Object.DestroyImmediate(root);
            definition.SourcePrefab = s_prefabs[index];
            s_definitions[index] = definition;
            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssetIfDirty(definition);
        }

        private static T Embed<T>(T value, ImpostorDefinition definition, string name) where T : Object
        {
            value.name = name;
            AssetDatabase.AddObjectToAsset(value, definition);
            return value;
        }

        public static void Dispose()
        {
            Array.Clear(s_prefabs, 0, s_prefabs.Length);
            Array.Clear(s_definitions, 0, s_definitions.Length);
            if (!string.IsNullOrEmpty(s_folder)) AssetDatabase.DeleteAsset(s_folder);
            if (s_createdRoot && AssetDatabase.IsValidFolder(TemporaryRoot)
                && Directory.GetFileSystemEntries(TemporaryRoot).Length == 0) AssetDatabase.DeleteAsset(TemporaryRoot);
        }
    }
}
