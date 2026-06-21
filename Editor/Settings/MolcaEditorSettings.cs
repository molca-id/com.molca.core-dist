using UnityEngine;
using UnityEditor;
using UnityEditorInternal;
using Molca.Settings;
using Molca.Attributes;
using Molca.Editor.Mcp;

namespace Molca.Editor
{
    /// <summary>
    /// Editor-only settings for Molca framework.
    /// Contains build settings, version settings, notification settings, and other editor tools.
    /// Separate from MolcaProjectSettings to respect assembly boundaries.
    /// </summary>
    /// <remarks>
    /// Persisted to <c>ProjectSettings/MolcaEditorSettings.asset</c> (outside the AssetDatabase) so the
    /// settings survive when the package is installed immutably from the UPM cache. References to
    /// <see cref="BuildSettings"/>, <see cref="VersionSettings"/>, and <see cref="NotificationSettings"/>
    /// are external asset references (guid-based) and survive the serialized-file round-trip.
    /// Call <see cref="Save"/> after mutating the instance through a <see cref="SerializedObject"/>.
    /// </remarks>
    public class MolcaEditorSettings : ScriptableObject
    {
        private static MolcaEditorSettings instance;

        // ProjectSettings/ lives outside the AssetDatabase — writable even when the package is immutable.
        private const string SETTINGS_PATH = "ProjectSettings/MolcaEditorSettings.asset";

        // Previous persistence locations, read once for migration. Old assets are intentionally left in
        // place: deleting an asset inside an immutable package would fail.
        private const string PACKAGE_ASSET_PATH = "Packages/com.molca.core/Editor/Settings/MolcaEditorSettings.asset";
        private const string LEGACY_ASSET_PATH = "Assets/_Molca/Resources/MolcaEditorSettings.asset";

        /// <summary>Singleton instance, loaded from ProjectSettings (migrating legacy assets if needed).</summary>
        public static MolcaEditorSettings Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = LoadOrCreate();
                }
                return instance;
            }
        }

        private static MolcaEditorSettings LoadOrCreate()
        {
            if (System.IO.File.Exists(SETTINGS_PATH))
            {
                var objects = InternalEditorUtility.LoadSerializedFileAndForget(SETTINGS_PATH);
                foreach (var obj in objects)
                {
                    if (obj is MolcaEditorSettings loaded)
                    {
                        // Must survive without a backing AssetDatabase asset.
                        loaded.hideFlags = HideFlags.HideAndDontSave;
                        return loaded;
                    }
                }
                Debug.LogWarning($"'{SETTINGS_PATH}' exists but contains no {nameof(MolcaEditorSettings)}. Recreating.");
            }

            // One-time migration from the old in-package / legacy Resources asset locations.
            var oldAsset = AssetDatabase.LoadAssetAtPath<MolcaEditorSettings>(PACKAGE_ASSET_PATH);
            if (oldAsset == null)
                oldAsset = AssetDatabase.LoadAssetAtPath<MolcaEditorSettings>(LEGACY_ASSET_PATH);

            MolcaEditorSettings settings;
            if (oldAsset != null)
            {
                // Copy values off the asset; never mark the source asset HideAndDontSave.
                settings = Instantiate(oldAsset);
                settings.name = nameof(MolcaEditorSettings);
                Debug.Log($"Migrated {nameof(MolcaEditorSettings)} to '{SETTINGS_PATH}'. " +
                          "The old asset was left in place (package assets may be immutable) and can be deleted manually.");
            }
            else
            {
                settings = CreateInstance<MolcaEditorSettings>();
                settings.name = nameof(MolcaEditorSettings);
            }

            settings.hideFlags = HideFlags.HideAndDontSave;
            settings.Save();
            return settings;
        }

        /// <summary>
        /// Writes the settings to <c>ProjectSettings/MolcaEditorSettings.asset</c>.
        /// Call after any mutation made outside the property setters (e.g. via SerializedObject).
        /// </summary>
        public void Save()
        {
            InternalEditorUtility.SaveToSerializedFileAndForget(
                new Object[] { this }, SETTINGS_PATH, allowTextSerialization: true);
        }

        [SerializeField] private string repositoryUrl = "";
        public string RepositoryUrl
        {
            get => repositoryUrl;
            set { repositoryUrl = value; Save(); }
        }

        [SerializeField] private string documentationUrl = "";
        public string DocumentationUrl
        {
            get => documentationUrl;
            set { documentationUrl = value; Save(); }
        }

        [SerializeField] private BuildSettings buildSettings;
        public BuildSettings BuildSettings
        {
            get => buildSettings;
            set { buildSettings = value; Save(); }
        }

        [SerializeField] private VersionSettings versionSettings;
        public VersionSettings VersionSettings
        {
            get => versionSettings;
            set { versionSettings = value; Save(); }
        }

        [SerializeField, Expandable] private NotificationSettings notificationSettings;
        public NotificationSettings NotificationSettings
        {
            get => notificationSettings;
            set { notificationSettings = value; Save(); }
        }

        [SerializeField] private Mcp.McpSettings mcpSettings;
        /// <summary>The MCP bridge settings asset, or null if the MCP bridge is not configured.</summary>
        public Mcp.McpSettings McpSettings
        {
            get => mcpSettings;
            set { mcpSettings = value; Save(); }
        }

        [SerializeField] private Mcp.Assistant.AssistantSettings assistantSettings;
        /// <summary>The in-editor assistant settings asset, or null if the assistant is not configured.</summary>
        public Mcp.Assistant.AssistantSettings AssistantSettings
        {
            get => assistantSettings;
            set { assistantSettings = value; Save(); }
        }
    }
}
