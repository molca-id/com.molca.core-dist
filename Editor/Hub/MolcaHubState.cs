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

        internal MolcaHubWorkspace Workspace { get; private set; } = MolcaHubWorkspace.Settings;
        internal MolcaHubSection Section { get; private set; } = MolcaHubSection.Project;
        internal string BuildVersionView { get; private set; } = "Build";
        internal string SelectedBuildProfile { get; private set; } = string.Empty;
        internal string SelectedRuntimeModule { get; private set; } = string.Empty;

        internal static MolcaHubState Load()
        {
            return new MolcaHubState
            {
                Workspace = ReadEnum(WorkspaceKey, MolcaHubWorkspace.Settings),
                Section = ReadEnum(SectionKey, MolcaHubSection.Project),
                BuildVersionView = MolcaEditorPrefs.GetString(BuildVersionViewKey, "Build"),
                SelectedBuildProfile = MolcaEditorPrefs.GetString(SelectedBuildProfileKey, string.Empty),
                SelectedRuntimeModule = MolcaEditorPrefs.GetString(SelectedRuntimeModuleKey, string.Empty)
            };
        }

        internal void SetWorkspace(MolcaHubWorkspace workspace)
        {
            Workspace = workspace;
            MolcaEditorPrefs.SetString(WorkspaceKey, workspace.ToString());
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
