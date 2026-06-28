using System;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Project-scoped persisted navigation state for the Molca Hub window.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>.
    /// Base class: plain C# object.
    /// Registration: owned by <see cref="MolcaHubWindow"/>; values persist through
    /// <see cref="MolcaEditorPrefs"/> so identical keys do not leak across Unity projects.
    /// </remarks>
    internal sealed class MolcaHubState
    {
        private const string WorkspaceKey = "Molca.Hub.Workspace";
        private const string SectionKey = "Molca.Hub.Section";
        private const string BuildVersionViewKey = "Molca.Hub.BuildVersionView";
        private const string SelectedBuildProfileKey = "Molca.Hub.SelectedBuildProfile";
        private const string SelectedRuntimeModuleKey = "Molca.Hub.SelectedRuntimeModule";

        /// <summary>
        /// The selected workspace tab, by stable string id (e.g. <c>"settings"</c>, <c>"doctor"</c>,
        /// <c>"sequence"</c>). Persisted as an id so consumer-added tabs survive across sessions; legacy
        /// enum-name values are migrated on load.
        /// </summary>
        internal string Workspace { get; private set; } = MolcaHubWorkspaceRegistry.SettingsId;
        internal MolcaHubSection Section { get; private set; } = MolcaHubSection.Project;
        internal string BuildVersionView { get; private set; } = "Build";
        internal string SelectedBuildProfile { get; private set; } = string.Empty;
        internal string SelectedRuntimeModule { get; private set; } = string.Empty;

        internal static MolcaHubState Load()
        {
            return new MolcaHubState
            {
                Workspace = NormalizeStoredWorkspace(
                    MolcaEditorPrefs.GetString(WorkspaceKey, MolcaHubWorkspaceRegistry.SettingsId)),
                Section = ReadEnum(SectionKey, MolcaHubSection.Project),
                BuildVersionView = MolcaEditorPrefs.GetString(BuildVersionViewKey, "Build"),
                SelectedBuildProfile = MolcaEditorPrefs.GetString(SelectedBuildProfileKey, string.Empty),
                SelectedRuntimeModule = MolcaEditorPrefs.GetString(SelectedRuntimeModuleKey, string.Empty)
            };
        }

        internal void SetWorkspace(string workspaceId)
        {
            Workspace = string.IsNullOrEmpty(workspaceId) ? MolcaHubWorkspaceRegistry.SettingsId : workspaceId;
            MolcaEditorPrefs.SetString(WorkspaceKey, Workspace);
        }

        /// <summary>Maps a built-in <see cref="MolcaHubWorkspace"/> enum value to its stable workspace id.</summary>
        internal static string WorkspaceId(MolcaHubWorkspace workspace) => workspace switch
        {
            MolcaHubWorkspace.Doctor => "doctor",
            MolcaHubWorkspace.Assistant => "assistant",
            MolcaHubWorkspace.Visualizer => "sequence",
            _ => MolcaHubWorkspaceRegistry.SettingsId,
        };

        /// <summary>
        /// Normalizes a persisted workspace value to a current id: legacy enum names
        /// (<c>"Settings"/"Doctor"/"Assistant"/"Visualizer"</c>) map to ids; anything else (already an id,
        /// incl. consumer ids) is returned unchanged. The window falls back to Settings if the id is not
        /// currently registered or is hidden.
        /// </summary>
        internal static string NormalizeStoredWorkspace(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return MolcaHubWorkspaceRegistry.SettingsId;
            return Enum.TryParse<MolcaHubWorkspace>(stored, out var legacy) ? WorkspaceId(legacy) : stored;
        }

        internal void SetSection(MolcaHubSection section)
        {
            Section = section;
            MolcaEditorPrefs.SetString(SectionKey, section.ToString());
        }

        internal void SetBuildVersionView(string view)
        {
            BuildVersionView = string.IsNullOrWhiteSpace(view) ? "Build" : view;
            MolcaEditorPrefs.SetString(BuildVersionViewKey, BuildVersionView);
        }

        internal void SetSelectedBuildProfile(string profileName)
        {
            SelectedBuildProfile = profileName ?? string.Empty;
            MolcaEditorPrefs.SetString(SelectedBuildProfileKey, SelectedBuildProfile);
        }

        internal void SetSelectedRuntimeModule(string moduleName)
        {
            SelectedRuntimeModule = moduleName ?? string.Empty;
            MolcaEditorPrefs.SetString(SelectedRuntimeModuleKey, SelectedRuntimeModule);
        }

        private static T ReadEnum<T>(string key, T defaultValue) where T : struct, Enum
        {
            var value = MolcaEditorPrefs.GetString(key, defaultValue.ToString());
            return Enum.TryParse<T>(value, out var parsed) ? parsed : defaultValue;
        }
    }

    internal enum MolcaHubWorkspace
    {
        Settings,
        Doctor,
        Assistant,
        Visualizer
    }

    internal enum MolcaHubSection
    {
        Project,
        BuildVersion,
        RuntimeGlobal,
        Editor,
        Integrations,
        Tasks,
        Mcp,
        Network,
        Sequences,
        Assistant
    }
}
