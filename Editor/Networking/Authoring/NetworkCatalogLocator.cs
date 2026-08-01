using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Authoring
{
    /// <summary>
    /// Finds — and, on request, creates — the project's <see cref="NetworkCatalog"/>.
    /// </summary>
    /// <remarks>
    /// No hardcoded lookup path: the catalog is located by type through the
    /// <see cref="AssetDatabase"/>, so moving or renaming the asset never orphans it. The canonical
    /// folder below governs <em>first creation</em> only, matching how the rest of Core's settings
    /// assets behave.
    /// <para>
    /// One catalog is the project default (ADR decision 1). Additional catalogs may exist for tests
    /// and tools; when several are found, <see cref="FindCatalog"/> prefers the one registered on
    /// <see cref="GlobalSettings"/> and otherwise picks deterministically by path so repeated runs
    /// agree.
    /// </para>
    /// </remarks>
    public static class NetworkCatalogLocator
    {
        /// <summary>Folder a newly created catalog is placed in. Existing assets are found by type.</summary>
        public const string CanonicalFolder = "Assets/_Molca/Settings";

        /// <summary>Default file name for a newly created catalog.</summary>
        public const string DefaultFileName = "Network Catalog.asset";

        /// <summary>
        /// Locates the project's catalog without creating one.
        /// </summary>
        /// <returns>The catalog, or <c>null</c> when the project has none.</returns>
        /// <remarks>
        /// Safe on read paths: opening the Hub or running Doctor must never create an asset as a side
        /// effect of merely looking.
        /// </remarks>
        public static NetworkCatalog FindCatalog()
        {
            var catalogs = FindAllCatalogs();
            if (catalogs.Count == 0)
                return null;

            if (catalogs.Count == 1)
                return catalogs[0];

            // Prefer whichever catalog the project has actually wired into its settings; that is the
            // one the runtime will load.
            var registered = FindRegisteredCatalog();
            if (registered != null)
                return registered;

            return catalogs[0];
        }

        /// <summary>
        /// Every catalog asset in the project, ordered by asset path for determinism.
        /// </summary>
        /// <returns>The catalogs found; possibly empty, never <c>null</c>.</returns>
        public static List<NetworkCatalog> FindAllCatalogs()
        {
            var result = new List<NetworkCatalog>();
            var paths = new List<string>();

            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(NetworkCatalog)}"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }

            paths.Sort(System.StringComparer.Ordinal);

            foreach (string path in paths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<NetworkCatalog>(path);
                if (catalog != null)
                    result.Add(catalog);
            }
            return result;
        }

        /// <summary>
        /// The catalog registered on the project's <see cref="GlobalSettings"/>, if any.
        /// </summary>
        /// <returns>The registered catalog, or <c>null</c>.</returns>
        /// <remarks>
        /// This is the catalog the runtime resolves through <c>GlobalSettings.GetModule</c>. A catalog
        /// that exists as an asset but is not registered is authored-but-inactive, which the Hub
        /// surfaces as a distinct state.
        /// </remarks>
        public static NetworkCatalog FindRegisteredCatalog()
        {
            var settings = MolcaProjectSettings.Instance;
            var globalSettings = settings != null ? settings.GlobalSettings : null;
            if (globalSettings == null || globalSettings.modules == null)
                return null;

            foreach (var module in globalSettings.modules)
            {
                if (module is NetworkCatalog catalog)
                    return catalog;
            }
            return null;
        }

        /// <summary>
        /// Whether <paramref name="catalog"/> is registered on the project's <see cref="GlobalSettings"/>
        /// and therefore loaded at runtime.
        /// </summary>
        /// <param name="catalog">The catalog to test.</param>
        public static bool IsRegistered(NetworkCatalog catalog) =>
            catalog != null && FindRegisteredCatalog() == catalog;

        /// <summary>
        /// Locates the project's catalog, creating one in <see cref="CanonicalFolder"/> if none exists.
        /// </summary>
        /// <param name="registerOnGlobalSettings">
        /// When <c>true</c>, a newly created catalog is appended to <see cref="GlobalSettings.modules"/>
        /// so the runtime loads it.
        /// </param>
        /// <returns>The existing or newly created catalog.</returns>
        /// <remarks>
        /// Call only from an explicit user action. Registers Undo for both the asset creation and the
        /// <see cref="GlobalSettings"/> edit.
        /// </remarks>
        public static NetworkCatalog GetOrCreateCatalog(bool registerOnGlobalSettings = true)
        {
            var existing = FindCatalog();
            if (existing != null)
                return existing;

            Directory.CreateDirectory(CanonicalFolder);
            AssetDatabase.Refresh();

            var catalog = ScriptableObject.CreateInstance<NetworkCatalog>();
            string path = AssetDatabase.GenerateUniqueAssetPath(
                CanonicalFolder + "/" + DefaultFileName);

            AssetDatabase.CreateAsset(catalog, path);
            Undo.RegisterCreatedObjectUndo(catalog, "Create Network Catalog");

            if (registerOnGlobalSettings)
                Register(catalog);

            AssetDatabase.SaveAssets();
            return catalog;
        }

        /// <summary>
        /// Appends <paramref name="catalog"/> to the project's <see cref="GlobalSettings.modules"/>
        /// so the runtime loads it.
        /// </summary>
        /// <param name="catalog">The catalog to register.</param>
        /// <returns>
        /// <c>true</c> when the catalog is registered afterwards — including when it already was.
        /// <c>false</c> when the project has no <see cref="GlobalSettings"/> to register it on.
        /// </returns>
        public static bool Register(NetworkCatalog catalog)
        {
            if (catalog == null)
                return false;

            var settings = MolcaProjectSettings.Instance;
            var globalSettings = settings != null ? settings.GlobalSettings : null;
            if (globalSettings == null)
            {
                Debug.LogWarning(
                    "[Network] No GlobalSettings asset is assigned on MolcaProjectSettings, so the " +
                    "catalog cannot be registered. It will not be loaded at runtime until it is.",
                    catalog);
                return false;
            }

            var modules = globalSettings.modules ?? System.Array.Empty<Molca.Settings.SettingModule>();
            foreach (var module in modules)
            {
                if (module == catalog)
                    return true;
            }

            Undo.RecordObject(globalSettings, "Register Network Catalog");

            var updated = new Molca.Settings.SettingModule[modules.Length + 1];
            System.Array.Copy(modules, updated, modules.Length);
            updated[modules.Length] = catalog;
            globalSettings.modules = updated;

            EditorUtility.SetDirty(globalSettings);
            return true;
        }
    }
}
