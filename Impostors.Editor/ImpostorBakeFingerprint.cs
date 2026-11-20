using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Impostors.Data;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Impostors.Editor
{
    /// <summary>Uses Unity's serializer and dependency hashes, without a list of authoring fields.</summary>
    [NoAutoStaticsCleanup]
    public static class ImpostorBakeFingerprint
    {
        private const string FORMAT_VERSION = "Impostor inputs v7 original capture shader|";
        private const string PACKAGE_ROOT = "Packages/com.firealt.impostors/";
        private const string MISSING_SCRIPT = "|missing script|";
        private const string MISSING_INPUTS = "A source prefab and bake settings are required.";
        private const string WRITABLE_ROOT = "Assets/";

        // Package sources whose changes alter the captured output.
        private static readonly string[] PackageDependencies =
        {
            "Impostors.Editor/ImpostorBaker.cs",
            "Impostors.Editor/ImpostorBaker.CaptureFrame.cs",
            "Impostors.Editor/ImpostorBaker.Pipeline.cs",
            "Impostors.Editor/ImpostorBaker.Ambient.cs",
            "Impostors.Editor/ImpostorCaptureMaterial.cs",
            "Impostors.Editor/ImpostorCaptureState.cs",
            "Impostors.Editor/ImpostorLodProfileBuilder.cs",
            "Impostors/Shaders/ImpostorCapture.hlsl",
            "Impostors/Shaders/ImpostorCapturePacking.hlsl",
            "Impostors.Editor/Shaders/BakeSelfShadowVolume.compute",
        };

        public static string Compute(GameObject source, ImpostorBakeSettings settings)
        {
            if (!source || !settings)
            {
                throw new ArgumentException(MISSING_INPUTS);
            }

            var fingerprint = new Fingerprint(AssetDatabase.GetAssetPath(source));
            fingerprint.Text.Append(FORMAT_VERSION).Append(ImpostorDefinition.VERSION);
            fingerprint.Text.Append('|').Append(QualitySettings.activeColorSpace);
            foreach (string dependency in PackageDependencies)
            {
                fingerprint.Dependencies.Add(PACKAGE_ROOT + dependency);
            }

            foreach (Transform transform in source.GetComponentsInChildren<Transform>(true))
            {
                fingerprint.Append(transform.gameObject);
                foreach (Component component in transform.GetComponents<Component>())
                {
                    // In particular, never follow Definition back into generated output and the source.
                    if (component is Impostor)
                    {
                        continue;
                    }

                    if (component)
                    {
                        fingerprint.Append(component);
                    }
                    else
                    {
                        fingerprint.Text.Append(MISSING_SCRIPT);
                    }
                }
            }

            fingerprint.Append(settings);
            return fingerprint.ToHash();
        }

        public static void SaveInputs(GameObject source, ImpostorBakeSettings settings)
        {
            // Flush only the actual authoring inputs before capturing their baseline, never all assets.
            SaveWritableInput(settings);
            IEnumerable<Object> inputs = source.GetComponentsInChildren<Transform>(true)
                .SelectMany(transform => transform.GetComponents<Component>().Cast<Object>().Prepend(transform.gameObject))
                .Append(settings);
            var saved = new HashSet<Object>();
            foreach (Object input in inputs)
            {
                if (!input || input is Impostor)
                {
                    continue;
                }

                foreach (Object reference in ObjectReferences(input))
                {
                    if (reference is not Component and not GameObject && EditorUtility.IsPersistent(reference)
                        && saved.Add(reference))
                    {
                        SaveWritableInput(reference);
                    }
                }
            }
        }

        private static IEnumerable<Object> ObjectReferences(Object value)
        {
            using var serialized = new SerializedObject(value);
            SerializedProperty property = serialized.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue)
                {
                    yield return property.objectReferenceValue;
                }
            }
        }

        private static void SaveWritableInput(Object value)
        {
            if (!value || !EditorUtility.IsPersistent(value))
            {
                return;
            }

            string path = AssetDatabase.GetAssetPath(value);
            // Package and built-in assets can be source dependencies, but never bake output.
            // Leave their in-memory edits visible to Compute without attempting to persist them.
            bool readOnly = File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            if (!path.StartsWith(WRITABLE_ROOT, StringComparison.Ordinal) || !AssetDatabase.IsOpenForEdit(path) || readOnly)
            {
                return;
            }

            AssetDatabase.SaveAssetIfDirty(value);
        }

        /// <summary>Serialized inputs of one bake, hashed together with the dependency hashes of every referenced asset.</summary>
        private sealed class Fingerprint
        {
            public readonly StringBuilder Text = new();
            public readonly SortedSet<string> Dependencies = new(StringComparer.Ordinal);

            private readonly SortedDictionary<string, string> _serializedDependencies = new(StringComparer.Ordinal);
            private readonly string _sourcePath;

            public Fingerprint(string sourcePath)
            {
                _sourcePath = sourcePath;
            }

            public void Append(Object value)
            {
                // Unlike JsonUtility, EditorJsonUtility writes persistent GUID/fileID references.
                Text.Append('|').Append(value.GetType().FullName).Append('|').Append(EditorJsonUtility.ToJson(value));
                foreach (Object reference in ObjectReferences(value))
                {
                    // Hierarchy objects are serialized above, not hashed as whole prefab assets.
                    if (reference is Impostor or GameObject or Component)
                    {
                        continue;
                    }

                    string path = AssetDatabase.GetAssetPath(reference);
                    if (string.IsNullOrEmpty(path) || path == _sourcePath)
                    {
                        continue;
                    }

                    Dependencies.Add(path);
                    // Include unsaved serialized edits without reading large mesh/texture payloads.
                    if (reference is Material or ScriptableObject)
                    {
                        string id = GlobalObjectId.GetGlobalObjectIdSlow(reference).ToString();
                        _serializedDependencies[id] = EditorJsonUtility.ToJson(reference);
                    }
                }
            }

            public string ToHash()
            {
                foreach (string path in Dependencies)
                {
                    Text.Append('|').Append(path).Append('|').Append(AssetDatabase.GetAssetDependencyHash(path));
                }

                foreach (KeyValuePair<string, string> dependency in _serializedDependencies)
                {
                    Text.Append('|').Append(dependency.Key).Append('|').Append(dependency.Value);
                }

                return Hash128.Compute(Text.ToString()).ToString();
            }
        }
    }
}
