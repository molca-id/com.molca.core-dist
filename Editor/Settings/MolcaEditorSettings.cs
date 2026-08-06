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

        // Previous persistence location, read once for migration. The asset lives in consumer space, so it
        // is the project's to delete once migrated. (An earlier build also kept a copy inside the package;
        // that copy is gone — the packages ship no assets, and one inside an immutable package could never
        // have been written back to anyway.)
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
                        // Hidden + not Unity-managed (we persist via Save() to ProjectSettings/),
                        // but NOT NotEditable — that flag (bundled in HideAndDontSave) would render
                        // SerializedObject fields read-only in the settings provider.
                        loaded.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
                        return loaded;
                    }
                }
                Debug.LogWarning($"'{SETTINGS_PATH}' exists but contains no {nameof(MolcaEditorSettings)}. Recreating.");
            }

            // One-time migration from the legacy Resources asset location.
            var oldAsset = AssetDatabase.LoadAssetAtPath<MolcaEditorSettings>(LEGACY_ASSET_PATH);

            MolcaEditorSettings settings;
            if (oldAsset != null)
            {
                // Copy values off the asset; never mark the source asset HideAndDontSave.
                settings = Instantiate(oldAsset);
                settings.name = nameof(MolcaEditorSettings);
                Debug.Log($"Migrated {nameof(MolcaEditorSettings)} to '{SETTINGS_PATH}'. " +
                          $"'{LEGACY_ASSET_PATH}' is no longer read and can be deleted.");
            }
            else
            {
                settings = CreateInstance<MolcaEditorSettings>();
                settings.name = nameof(MolcaEditorSettings);
            }

            settings.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
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
        /// <summary>The MCP bridge settings asset, or null if the project has none.</summary>
        /// <remarks>
        /// Falls back to locating the asset by type when the stored reference is missing — see
        /// <see cref="AssistantSettings"/> for why.
        /// </remarks>
        public Mcp.McpSettings McpSettings
        {
            get
            {
                if (mcpSettings == null) mcpSettings = MolcaEditorSettingsAsset.Find<Mcp.McpSettings>();
                return mcpSettings;
            }
            set { mcpSettings = value; Save(); }
        }

        [SerializeField] private Mcp.Assistant.AssistantSettings assistantSettings;
        /// <summary>The in-editor assistant settings asset, or null if the project has none.</summary>
        /// <remarks>
        /// The stored value is a guid reference, so it dangles whenever the asset exists but this file does not
        /// name it — a clone that never opened the Hub, a re-created asset, a merge that dropped the line.
        /// Other call sites reach the same assets through <c>GetOrCreateSettings()</c>, which locates them by
        /// type, so a dangling reference used to mean the two paths disagreed: one returned <c>null</c> while
        /// the other returned a real (or freshly created, blank) asset. Falling back to a by-type lookup keeps
        /// them consistent. The repair is in-memory only — <see cref="Save"/> is not called from a getter — so
        /// merely reading settings never writes to <c>ProjectSettings/</c>.
        /// </remarks>
        public Mcp.Assistant.AssistantSettings AssistantSettings
        {
            get
            {
                if (assistantSettings == null)
                    assistantSettings = MolcaEditorSettingsAsset.Find<Mcp.Assistant.AssistantSettings>();
                return assistantSettings;
            }
            set { assistantSettings = value; Save(); }
        }
    }
}
