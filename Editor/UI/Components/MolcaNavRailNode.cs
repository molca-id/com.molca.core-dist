using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Molca.Editor.UI.Components
{
    /// <summary>
    /// One row in a <see cref="MolcaNavRail"/>: a category that owns other rows, a leaf that builds detail
    /// content, or a command leaf that runs an action instead.
    /// </summary>
    /// <remarks>
    /// Promoted out of the Hub window, where this model was <c>MolcaHubRailNode</c> and private to Settings.
    /// Every rail in the editor now builds from it, so a workspace with a second level describes that level
    /// once rather than growing a navigator of its own inside its body.
    /// </remarks>
    public sealed class MolcaNavRailNode
    {
        /// <summary>Stable identity used for selection persistence, expansion state, and lookup.</summary>
        public string Id { get; }

        /// <summary>Row label shown in the rail.</summary>
        public string Label { get; }

        /// <summary>Optional detail-header description.</summary>
        public string Description { get; }

        /// <summary>Child rows; empty for a leaf.</summary>
        public List<MolcaNavRailNode> Children { get; }

        /// <summary>Builds this row's detail view; null for a category or a command leaf.</summary>
        public Func<VisualElement> CreateContent { get; }

        /// <summary>
        /// Runs this row's action instead of building detail content; null for every other row kind.
        /// </summary>
        /// <remarks>
        /// A command leaf is a jump, not a location, so an owner must not persist it as the current row.
        /// </remarks>
        public Action Activate { get; }

        /// <summary>Status shown ahead of the label, or <see cref="MolcaStatusKind.None"/> for no dot.</summary>
        public MolcaStatusKind Status { get; }

        /// <summary>Row tooltip, or null.</summary>
        public string Tooltip { get; }

        /// <summary>True when selecting this row does something rather than opening a category.</summary>
        public bool IsLeaf => CreateContent != null || Activate != null;

        /// <summary>Creates a leaf that renders detail content when selected.</summary>
        /// <param name="id">Stable node id.</param>
        /// <param name="label">Row label.</param>
        /// <param name="createContent">Builds the detail view.</param>
        /// <param name="description">Optional detail-header description.</param>
        /// <param name="status">Status dot shown ahead of the label.</param>
        /// <param name="tooltip">Row tooltip.</param>
        public MolcaNavRailNode(
            string id,
            string label,
            Func<VisualElement> createContent,
            string description = null,
            MolcaStatusKind status = MolcaStatusKind.None,
            string tooltip = null)
        {
            Id = id;
            Label = label;
            Description = description;
            CreateContent = createContent;
            Status = status;
            Tooltip = tooltip;
            Children = new List<MolcaNavRailNode>();
        }

        /// <summary>Creates a category that owns <paramref name="children"/> and renders nothing itself.</summary>
        /// <param name="id">Stable node id.</param>
        /// <param name="label">Row label.</param>
        /// <param name="children">The rows it owns.</param>
        /// <param name="tooltip">Row tooltip.</param>
        public MolcaNavRailNode(
            string id, string label, List<MolcaNavRailNode> children, string tooltip = null)
        {
            Id = id;
            Label = label;
            Tooltip = tooltip;
            Children = children ?? new List<MolcaNavRailNode>();
        }

        /// <summary>
        /// Creates a command leaf: a row that runs <paramref name="activate"/> when selected instead of
        /// rendering detail content.
        /// </summary>
        /// <param name="id">Stable node id, namespaced so it cannot collide with a content leaf.</param>
        /// <param name="label">Row label.</param>
        /// <param name="activate">The action to run on selection.</param>
        /// <param name="description">Optional description.</param>
        /// <returns>The command-leaf node.</returns>
        /// <remarks>
        /// A static factory rather than a second constructor on purpose: <c>Func&lt;VisualElement&gt;</c> and
        /// <c>Action</c> overloads would both accept an expression-bodied lambda at every call site, and
        /// relying on overload resolution to pick the right one is a trap the next reader should not have to
        /// reason about. (Carried over verbatim from the Hub's model, where it was learned the hard way.)
        /// </remarks>
        public static MolcaNavRailNode Command(
            string id, string label, Action activate, string description = null) =>
            new MolcaNavRailNode(id, label, description, activate);

        private MolcaNavRailNode(string id, string label, string description, Action activate)
        {
            Id = id;
            Label = label;
            Description = description;
            Activate = activate;
            Children = new List<MolcaNavRailNode>();
        }
    }

    /// <summary>The pure half of rail filtering: whether a row survives the search box.</summary>
    /// <remarks>
    /// Split from the rail itself so the behaviour is testable without constructing a <c>TreeView</c>.
    /// </remarks>
    public static class MolcaNavRailFilter
    {
        /// <summary>Whether <paramref name="label"/> satisfies <paramref name="filter"/>.</summary>
        /// <param name="label">The candidate label.</param>
        /// <param name="filter">The active filter text; empty matches everything.</param>
        /// <returns>True when the row should survive.</returns>
        public static bool Matches(string label, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return label != null && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
