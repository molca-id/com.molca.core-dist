using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// One node in the Molca Hub's nested navigation rail: either a category parent (children, no content)
    /// or a leaf that builds a detail view when selected (settings section or a reference doc).
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Built by <see cref="MolcaHubWindow"/> from the
    /// hardcoded settings sections plus the docs contributed by <see cref="Docs.MolcaDocsRegistry"/>. A leaf's
    /// <see cref="CreateContent"/> is invoked lazily on selection and its result placed in the detail pane.
    /// </remarks>
    internal sealed class MolcaHubRailNode
    {
        /// <summary>Stable identity used for selection persistence, expansion state, and lookup.</summary>
        public string Id { get; }

        /// <summary>Row label shown in the rail.</summary>
        public string Label { get; }

        /// <summary>Optional detail-header description (docs leaves show it; sections hide the header).</summary>
        public string Description { get; }

        /// <summary>Child nodes (empty for leaves).</summary>
        public List<MolcaHubRailNode> Children { get; }

        /// <summary>Builds this node's detail view; <c>null</c> for a pure category parent or a command leaf.</summary>
        public Func<VisualElement> CreateContent { get; }

        /// <summary>
        /// Runs this node's action instead of building detail content; <c>null</c> for every other node kind.
        /// A command leaf is a jump, not a location, so selecting one is never persisted as the active rail node.
        /// </summary>
        public Action Activate { get; }

        /// <summary>True when this node does something on selection (renders content or runs a command).</summary>
        public bool IsLeaf => CreateContent != null || Activate != null;

        /// <summary>Creates a leaf node with a detail-content factory.</summary>
        public MolcaHubRailNode(string id, string label, Func<VisualElement> createContent, string description = null)
        {
            Id = id;
            Label = label;
            Description = description;
            CreateContent = createContent;
            Children = new List<MolcaHubRailNode>();
        }

        /// <summary>
        /// Creates a command leaf: a rail row that runs <paramref name="activate"/> when selected instead of
        /// rendering a detail view. Used by search to offer workspace tabs alongside settings sections.
        /// </summary>
        /// <param name="id">Stable node id (namespaced, e.g. <c>ws:doctor</c>).</param>
        /// <param name="label">Row label shown in the rail.</param>
        /// <param name="activate">The action to run on selection.</param>
        /// <param name="description">Optional description.</param>
        /// <returns>The command-leaf node.</returns>
        /// <remarks>
        /// A static factory rather than a second constructor on purpose: <c>Func&lt;VisualElement&gt;</c> and
        /// <c>Action</c> constructor overloads would both accept an expression-bodied lambda at every existing
        /// call site, and relying on overload resolution to pick the right one is a trap the next reader
        /// should not have to reason about.
        /// </remarks>
        public static MolcaHubRailNode Command(string id, string label, Action activate, string description = null)
            => new MolcaHubRailNode(id, label, description, activate);

        private MolcaHubRailNode(string id, string label, string description, Action activate)
        {
            Id = id;
            Label = label;
            Description = description;
            Activate = activate;
            Children = new List<MolcaHubRailNode>();
        }

        /// <summary>Creates a category parent node holding <paramref name="children"/>.</summary>
        public MolcaHubRailNode(string id, string label, List<MolcaHubRailNode> children)
        {
            Id = id;
            Label = label;
            Children = children ?? new List<MolcaHubRailNode>();
        }
    }

    /// <summary>
    /// The pure part of Hub rail search: deciding whether a label matches a filter, and building the
    /// synthetic "Workspaces" category that lets the search box find workspace tabs, not just settings
    /// sections.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Split out of <see cref="MolcaHubWindow"/> so the
    /// behaviour is testable without a window. Editor-only; main thread.
    /// </remarks>
    internal static class MolcaHubRailFilter
    {
        /// <summary>Id of the synthetic category that holds workspace search results.</summary>
        internal const string WorkspaceCategoryId = "cat:workspaces";

        /// <summary>Node-id prefix for a workspace search result, keeping it out of every other id namespace.</summary>
        internal const string WorkspaceNodePrefix = "ws:";

        /// <summary>Whether <paramref name="label"/> satisfies <paramref name="filter"/> (empty matches all).</summary>
        /// <param name="label">The candidate label.</param>
        /// <param name="filter">The active filter text.</param>
        /// <returns><c>true</c> when the row should survive the filter.</returns>
        internal static bool Matches(string label, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return label != null && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Builds the synthetic workspace category for an active filter, or <c>null</c> when the filter is
        /// empty or nothing matches. Each child is a command leaf that switches workspace.
        /// </summary>
        /// <param name="items">The resolved workspace tabs.</param>
        /// <param name="filter">The active filter text; empty means "no workspace results".</param>
        /// <param name="onSelect">Invoked with the workspace id when a result is chosen.</param>
        /// <returns>The category node, or <c>null</c>.</returns>
        /// <remarks>
        /// The category exists only while filtering: unfiltered, the rail is the settings surface and adding a
        /// permanent duplicate of the toolbar to it would be noise.
        /// </remarks>
        internal static MolcaHubRailNode BuildWorkspaceCategory(
            IReadOnlyList<MolcaHubWorkspaceItem> items, string filter, Action<string> onSelect)
        {
            if (string.IsNullOrEmpty(filter) || items == null) return null;

            List<MolcaHubRailNode> children = null;
            foreach (var item in items)
            {
                var label = string.IsNullOrEmpty(item.Label) ? item.Id : item.Label;
                if (!Matches(label, filter)) continue;

                var id = item.Id;
                children ??= new List<MolcaHubRailNode>();
                children.Add(MolcaHubRailNode.Command(WorkspaceNodePrefix + id, label,
                    () => onSelect?.Invoke(id), "Switch to the " + label + " workspace."));
            }

            return children == null ? null : new MolcaHubRailNode(WorkspaceCategoryId, "Workspaces", children);
        }
    }
}
