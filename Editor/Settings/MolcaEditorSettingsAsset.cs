using System.IO;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Single source of truth for locating and creating Core editor-only settings ScriptableObjects.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Settings/</c>.
    /// Registration: static utility; not an asset.
    /// <para>
    /// Editor-only settings assets (MCP, Assistant, Notification, Integration, …) are <b>singletons by
    /// type</b>: there is at most one per project, found by type wherever the user has moved it. This helper
    /// centralizes the lookup-or-create flow so every settings SO shares one behavior and one canonical
    /// default location, <see cref="CanonicalFolder"/>. Previously each SO hardcoded its own path, which had
    /// drifted between <c>_Molca/Editor</c> and <c>_Molca/_Core/Settings</c>.
    /// </para>
    /// <para>
    /// The canonical folder only governs <i>first creation</i>: an existing asset is always located by type
    /// first, so moving this constant never orphans assets users already have.
    /// </para>
    /// </remarks>
    public static class MolcaEditorSettingsAsset
    {
        /// <summary>
        /// Canonical default folder for Core editor-only settings assets. New settings SOs created by
        /// <see cref="GetOrCreate{T}"/> land here unless one already exists elsewhere in the project.
        /// </summary>
        public const string CanonicalFolder = "Assets/_Molca/Editor";

        /// <summary>
        /// Returns the project's settings asset of type <typeparamref name="T"/>, creating one in
        /// <see cref="CanonicalFolder"/> if none exists.
        /// </summary>
        /// <typeparam name="T">The settings ScriptableObject type (one per project).</typeparam>
        /// <param name="fileName">
        /// File name (including the <c>.asset</c> extension) used only when creating a new asset, e.g.
        /// <c>"MCP Settings.asset"</c>.
        /// </param>
        /// <returns>The existing or newly created settings asset.</returns>
        public static T GetOrCreate<T>(string fileName) where T : ScriptableObject
        {
            // Locate an existing asset by type, wherever it lives in the project.
            var guids = AssetDatabase.FindAssets($"t:{typeof(T).Name}");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var existing = AssetDatabase.LoadAssetAtPath<T>(path);
                if (existing != null)
                    return existing;
            }

            if (!Directory.Exists(CanonicalFolder))
                Directory.CreateDirectory(CanonicalFolder);

            var settings = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(settings, $"{CanonicalFolder}/{fileName}");
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
