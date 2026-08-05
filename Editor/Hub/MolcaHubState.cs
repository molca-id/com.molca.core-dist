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
        private const string BuildVersionViewKey = "Molca.Hub.BuildVersionView";
        private const string SelectedBuildProfileKey = "Molca.Hub.SelectedBuildProfile";
        private const string SelectedRuntimeModuleKey = "Molca.Hub.SelectedRuntimeModule";
        private const string RailNodeKey = "Molca.Hub.RailNode";
        private const string RailExpandedKey = "Molca.Hub.RailExpanded";
        private const string WorkspaceMruKey = "Molca.Hub.WorkspaceMru";

        /// <summary>How many recently-used workspace ids are remembered; bounded so the pref cannot grow.</summary>
        internal const int WorkspaceMruCapacity = 8;

        /// <summary>
        /// The selected workspace tab, by stable string id (e.g. <c>"settings"</c>, <c>"doctor"</c>,
        /// <c>"sequence"</c>). Persisted as an id so consumer-added tabs survive across sessions; legacy
        /// enum-name values are migrated on load.
        /// </summary>
        internal string Workspace { get; private set; } = MolcaHubWorkspaceRegistry.SettingsId;
        internal string BuildVersionView { get; private set; } = "Build";
        internal string SelectedBuildProfile { get; private set; } = string.Empty;
        internal string SelectedRuntimeModule { get; private set; } = string.Empty;

        /// <summary>The active nested-rail node id (a section enum name or a <c>doc:&lt;id&gt;</c>), or empty.</summary>
        internal string RailNode { get; private set; } = string.Empty;

        /// <summary>The set of expanded rail category node ids. Empty means "expand all by default".</summary>
        internal HashSet<string> RailExpanded { get; private set; } = new HashSet<string>();

        /// <summary>
        /// Recently activated workspace ids, most recent first, capped at <see cref="WorkspaceMruCapacity"/>.
        /// Used to decide which tabs keep a toolbar slot when the strip has to overflow, and to populate the
        /// overflow menu's "Recent" section.
        /// </summary>
        internal IReadOnlyList<string> WorkspaceMru { get; private set; } = Array.Empty<string>();

        internal static MolcaHubState Load()
        {
            return new MolcaHubState
            {
                Workspace = NormalizeStoredWorkspace(
                    MolcaEditorPrefs.GetString(WorkspaceKey, MolcaHubWorkspaceRegistry.SettingsId)),
                BuildVersionView = MolcaEditorPrefs.GetString(BuildVersionViewKey, "Build"),
                SelectedBuildProfile = MolcaEditorPrefs.GetString(SelectedBuildProfileKey, string.Empty),
                SelectedRuntimeModule = MolcaEditorPrefs.GetString(SelectedRuntimeModuleKey, string.Empty),
                RailNode = MolcaEditorPrefs.GetString(RailNodeKey, string.Empty),
                RailExpanded = ReadStringSet(RailExpandedKey),
                WorkspaceMru = ReadStringList(WorkspaceMruKey)
            };
        }

        private static List<string> ReadStringList(string key)
        {
            var raw = MolcaEditorPrefs.GetString(key, string.Empty);
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var part in raw.Split('\n'))
                if (!string.IsNullOrEmpty(part) && !list.Contains(part)) list.Add(part);
            return list;
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

        internal void SetWorkspace(string workspaceId)
        {
            Workspace = string.IsNullOrEmpty(workspaceId) ? MolcaHubWorkspaceRegistry.SettingsId : workspaceId;
            MolcaEditorPrefs.SetString(WorkspaceKey, Workspace);
            PushMru(Workspace);
        }

        /// <summary>
        /// Moves <paramref name="workspaceId"/> to the front of the MRU list, de-duplicating and trimming to
        /// <see cref="WorkspaceMruCapacity"/>, then persists it.
        /// </summary>
        private void PushMru(string workspaceId)
        {
            WorkspaceMru = PushMru(WorkspaceMru, workspaceId);
            MolcaEditorPrefs.SetString(WorkspaceMruKey, string.Join("\n", WorkspaceMru));
        }

        /// <summary>
        /// Pure MRU push step, exposed for testing: returns the list that results from moving
        /// <paramref name="workspaceId"/> to the front of <paramref name="current"/>.
        /// </summary>
        /// <param name="current">The existing MRU list, most recent first.</param>
        /// <param name="workspaceId">The workspace that was just activated.</param>
        /// <param name="capacity">Maximum retained entries.</param>
        /// <returns>The new MRU list, most recent first.</returns>
        internal static IReadOnlyList<string> PushMru(
            IReadOnlyList<string> current, string workspaceId, int capacity = WorkspaceMruCapacity)
        {
            var mru = new List<string>(current ?? Array.Empty<string>());
            mru.Remove(workspaceId);
            mru.Insert(0, workspaceId);
            if (mru.Count > capacity) mru.RemoveRange(capacity, mru.Count - capacity);
            return mru;
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
    }

    internal enum MolcaHubWorkspace
    {
        Settings,
        Doctor,
        Assistant,
        Visualizer
    }

    /// <summary>
    /// The Settings-workspace rail sections Core owns. Each member has exactly one rail leaf
    /// (<c>MolcaHubWindow.BuildRailNodes</c>) and one content factory
    /// (<c>MolcaHubWindow.CreateSectionContent</c>); a member with no leaf is unreachable UI, so the two
    /// lists are kept in step deliberately.
    /// </summary>
    /// <remarks>
    /// Localization used to be a member here. It is a full authoring workspace tab now
    /// (<c>LocalizationHubWorkspaceProvider</c>), and the leaf was removed when it moved — the enum member,
    /// its descriptor and its factory case outlived it as dead code and were dropped in turn.
    /// </remarks>
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
        Assistant,
        AddOnsBrowse,
        AddOnsInstalled,
        About
    }
}
