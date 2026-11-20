using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Impostors.Editor
{
    /// <summary>Resolves immutable package inputs separately from assets owned by the consuming project.</summary>
    internal static class ImpostorPackageAssets
    {
        private const string ASSETS_FOLDER = "Assets";
        private const string MISSING_PACKAGE = "The Impostors editor assembly must be installed as a Unity package.";
        private const string MISSING_ASSET = "A required Impostors package asset is missing: ";
        private const string WRITABLE_ASSETS_REQUIRED = "Choose a writable asset under the project's Assets folder. "
            + "Copy package sample assets into Assets before rebaking: ";

        private static PackageInfo Package => PackageInfo.FindForAssembly(typeof(ImpostorPackageAssets).Assembly)
            ?? throw new InvalidOperationException(MISSING_PACKAGE);

        public static string AssetPath(string relativePath) => Package.assetPath + "/" + relativePath;

        public static T Load<T>(string relativePath) where T : Object
        {
            string path = AssetPath(relativePath);
            T value = AssetDatabase.LoadAssetAtPath<T>(path);
            if (!value)
            {
                throw new InvalidOperationException(MISSING_ASSET + path);
            }

            return value;
        }

        /// <summary>
        /// Converts a path to an asset path under Assets and verifies that it is writable. Without an
        /// <paramref name="extension"/>, the Assets folder itself is accepted.
        /// </summary>
        public static string RequireWritableAssetPath(string path, string extension = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(WRITABLE_ASSETS_REQUIRED + path, nameof(path));
            }

            string absolute = Path.GetFullPath(path);
            string assets = TrimSeparators(Path.GetFullPath(Application.dataPath));
            if (extension == null && string.Equals(TrimSeparators(absolute), assets, StringComparison.OrdinalIgnoreCase))
            {
                return ASSETS_FOLDER;
            }

            bool insideAssets = absolute.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            bool hasExtension = extension == null || absolute.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
            if (!insideAssets || !hasExtension)
            {
                throw new ArgumentException(WRITABLE_ASSETS_REQUIRED + path, nameof(path));
            }

            string assetPath = ASSETS_FOLDER + "/" + absolute.Substring(assets.Length + 1).Replace('\\', '/');
            if (File.Exists(absolute) && (IsReadOnly(absolute) || !AssetDatabase.IsOpenForEdit(assetPath)))
            {
                throw new InvalidOperationException(WRITABLE_ASSETS_REQUIRED + assetPath);
            }

            return assetPath;
        }

        private static string TrimSeparators(string path)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static bool IsReadOnly(string path) => (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
    }
}
