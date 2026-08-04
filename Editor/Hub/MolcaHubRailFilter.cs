using System;
using System.Collections.Generic;
using Molca.Editor.UI.Components;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// The Hub-specific half of rail search: the synthetic "Workspaces" category that lets the search box
    /// find workspace tabs, not just settings sections.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Split out of <see cref="MolcaHubWindow"/> so the
    /// behaviour is testable without a window. Editor-only; main thread.
    /// <para>
    /// The generic half — whether a label survives a filter — now lives on
    /// <see cref="MolcaNavRailFilter"/> with the rail itself. <see cref="Matches"/> stays here as a
    /// forwarder because it is the same rule and this fixture's callers should not have to care which
    /// assembly owns it.
    /// </para>
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
        internal static bool Matches(string label, string filter) =>
            MolcaNavRailFilter.Matches(label, filter);

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
        internal static MolcaNavRailNode BuildWorkspaceCategory(
            IReadOnlyList<MolcaHubWorkspaceItem> items, string filter, Action<string> onSelect)
        {
            if (string.IsNullOrEmpty(filter) || items == null) return null;

            List<MolcaNavRailNode> children = null;
            foreach (var item in items)
            {
                var label = string.IsNullOrEmpty(item.Label) ? item.Id : item.Label;
                if (!Matches(label, filter)) continue;

                var id = item.Id;
                children ??= new List<MolcaNavRailNode>();
                children.Add(MolcaNavRailNode.Command(WorkspaceNodePrefix + id, label,
                    () => onSelect?.Invoke(id), "Switch to the " + label + " workspace."));
            }

            return children == null ? null : new MolcaNavRailNode(WorkspaceCategoryId, "Workspaces", children);
        }
    }
}
