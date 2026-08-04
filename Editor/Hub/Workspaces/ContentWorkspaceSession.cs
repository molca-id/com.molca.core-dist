using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ContentPackage;
using Molca.ContentPackage.Editor;
using Molca.Editor.UI;

namespace Molca.Editor.Hub.Workspaces
{
    /// <summary>
    /// State the Content workspace keeps across Hub tab switches and domain reloads.
    /// </summary>
    /// <remarks>
    /// Static, mirroring <c>ReferenceHubSession</c>. A publish is a long operation with a build in
    /// the middle, and rebuilding the view on every tab switch would otherwise lose the staging
    /// directory, the verification report, and the result of a publish the user just ran.
    ///
    /// The authored intent — channel, content version, changelog — persists through
    /// <see cref="MolcaEditorPrefs"/> rather than living only in the view. Plan §10.2 keeps
    /// <em>channel selection</em> out of per-user prefs specifically, so that is deliberately not
    /// stored here: it is read from and written to the project asset, and only the
    /// in-progress-authoring fields are per-user.
    /// </remarks>
    internal static class ContentWorkspaceSession
    {
        private const string VersionKey = "Molca.Content.ContentVersion";
        private const string ChangelogKey = "Molca.Content.Changelog";
        private const string MinAppKey = "Molca.Content.MinAppVersion";
        private const string MaxAppKey = "Molca.Content.MaxAppVersion";
        private const string NodeKey = "Molca.Content.SelectedNode";
        private const string PackageKey = "Molca.Content.SelectedPackage";
        private const string ExpandedKey = "Molca.Content.RailExpanded";

        /// <summary>The rail node id showing in the detail pane.</summary>
        public static string SelectedNode
        {
            get => MolcaEditorPrefs.GetString(NodeKey, ContentWorkspaceNodes.Packages);
            set => MolcaEditorPrefs.SetString(NodeKey, value ?? ContentWorkspaceNodes.Packages);
        }

        /// <summary>
        /// The package whose detail page is showing, or empty.
        /// </summary>
        /// <remarks>
        /// Kept beside <see cref="SelectedNode"/> rather than parsed back out of it, because a package
        /// can be renamed while it is selected — the node id is stale the moment that happens, and this
        /// is what the view re-points before it rebuilds the rail.
        /// </remarks>
        public static string SelectedPackageId
        {
            get => MolcaEditorPrefs.GetString(PackageKey, "");
            set => MolcaEditorPrefs.SetString(PackageKey, value ?? "");
        }

        /// <summary>Reads the rail's persisted expanded-category ids.</summary>
        /// <returns>The expanded ids; empty means first run, which the rail reads as "open everything".</returns>
        public static HashSet<string> ReadExpanded() =>
            new HashSet<string>(
                MolcaEditorPrefs.GetString(ExpandedKey, "")
                    .Split('|')
                    .Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.Ordinal);

        /// <summary>Persists the rail's expanded-category ids.</summary>
        /// <param name="ids">The ids to remember.</param>
        public static void WriteExpanded(IEnumerable<string> ids) =>
            MolcaEditorPrefs.SetString(ExpandedKey, string.Join("|", ids ?? Enumerable.Empty<string>()));

        /// <summary>The content version being authored.</summary>
        public static string ContentVersion
        {
            get => MolcaEditorPrefs.GetString(VersionKey, "1.0.0");
            set => MolcaEditorPrefs.SetString(VersionKey, value ?? "");
        }

        /// <summary>Release notes for the next publish.</summary>
        public static string Changelog
        {
            get => MolcaEditorPrefs.GetString(ChangelogKey, "");
            set => MolcaEditorPrefs.SetString(ChangelogKey, value ?? "");
        }

        /// <summary>Lowest app version the release may activate on, or empty.</summary>
        public static string MinAppVersion
        {
            get => MolcaEditorPrefs.GetString(MinAppKey, "");
            set => MolcaEditorPrefs.SetString(MinAppKey, value ?? "");
        }

        /// <summary>Highest app version the release may activate on, or empty.</summary>
        public static string MaxAppVersion
        {
            get => MolcaEditorPrefs.GetString(MaxAppKey, "");
            set => MolcaEditorPrefs.SetString(MaxAppKey, value ?? "");
        }

        /// <summary>The most recent verification report, or null.</summary>
        public static ContentValidationReport LastReport { get; set; }

        /// <summary>The build graph from the last clean staging build, or null.</summary>
        public static ContentBuildGraph LastGraph { get; set; }

        /// <summary>The staging directory the last build produced, or empty.</summary>
        public static string StagingDirectory { get; set; } = "";

        /// <summary>When the last staging build completed, or default.</summary>
        public static DateTime StagedAtUtc { get; set; }

        /// <summary>A human-readable line describing the last publish attempt, or empty.</summary>
        public static string LastPublishSummary { get; set; } = "";

        /// <summary>True while a build or publish is running; the UI disables re-entry.</summary>
        public static bool Busy { get; set; }

        /// <summary>Progress text for the running operation.</summary>
        public static string BusyStatus { get; set; } = "";

        /// <summary>0..1 for the running operation, or -1 when indeterminate.</summary>
        public static float BusyProgress { get; set; } = -1f;

        /// <summary>
        /// Forgets the staged build.
        /// </summary>
        /// <remarks>
        /// Called whenever package definitions change. A candidate is derived from a staging
        /// directory and a build graph together; keeping a graph from before an edit would let the
        /// workspace report sizes and ownership for a configuration that no longer exists.
        /// </remarks>
        public static void InvalidateBuild()
        {
            LastGraph = null;
            StagingDirectory = "";
            StagedAtUtc = default;
        }
    }
}
