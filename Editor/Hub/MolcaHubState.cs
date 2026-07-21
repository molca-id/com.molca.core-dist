using System;
using System.Collections.Generic;

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
        private const string HiddenWorkspacesKey = "Molca.Hub.HiddenWorkspaces";
        private const string RailNodeKey = "Molca.Hub.RailNode";
        private const string RailExpandedKey = "Molca.Hub.RailExpanded";

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

        /// <summary>The active nested-rail node id (a section enum name or a <c>doc:&lt;id&gt;</c>), or empty.</summary>
        internal string RailNode { get; private set; } = string.Empty;

        /// <summary>The set of expanded rail category node ids. Empty means "expand all by default".</summary>
        internal HashSet<string> RailExpanded { get; private set; } = new HashSet<string>();

        internal static MolcaHubState Load()
        {
            EnsureHiddenWorkspacesKey();

            return new MolcaHubState
            {
                Workspace = NormalizeStoredWorkspace(
                    MolcaEditorPrefs.GetString(WorkspaceKey, MolcaHubWorkspaceRegistry.SettingsId)),
                Section = ReadEnum(SectionKey, MolcaHubSection.Project),
                BuildVersionView = MolcaEditorPrefs.GetString(BuildVersionViewKey, "Build"),
                SelectedBuildProfile = MolcaEditorPrefs.GetString(SelectedBuildProfileKey, string.Empty),
                SelectedRuntimeModule = MolcaEditorPrefs.GetString(SelectedRuntimeModuleKey, string.Empty),
                RailNode = MolcaEditorPrefs.GetString(RailNodeKey, string.Empty),
                RailExpanded = ReadStringSet(RailExpandedKey)
            };
        }

        private static HashSet<string> ReadStringSet(string key)
        {
            var raw = MolcaEditorPrefs.GetString(key, string.Empty);
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var part in raw.Split('\n'))
                if (!string.IsNullOrEmpty(part)) set.Add(part);
            return set;
        }

        private static void EnsureHiddenWorkspacesKey()
        {
            if (!MolcaEditorPrefs.HasKey(HiddenWorkspacesKey))
                MolcaEditorPrefs.SetString(HiddenWorkspacesKey, string.Empty);
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

        /// <summary>Persists the active nested-rail node id.</summary>
        internal void SetRailNode(string nodeId)
        {
            RailNode = nodeId ?? string.Empty;
            MolcaEditorPrefs.SetString(RailNodeKey, RailNode);
        }

        /// <summary>Persists the set of expanded rail category node ids.</summary>
        internal void SetRailExpanded(IEnumerable<string> expandedIds)
        {
            RailExpanded = expandedIds == null ? new HashSet<string>() : new HashSet<string>(expandedIds);
            MolcaEditorPrefs.SetString(RailExpandedKey, string.Join("\n", RailExpanded));
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
