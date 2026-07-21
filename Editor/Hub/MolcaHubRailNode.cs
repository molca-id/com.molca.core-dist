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

        /// <summary>Builds this node's detail view; <c>null</c> for a pure category parent.</summary>
        public Func<VisualElement> CreateContent { get; }

        /// <summary>True when this node renders a detail view (a section or a doc), false for a category parent.</summary>
        public bool IsLeaf => CreateContent != null;

        /// <summary>Creates a leaf node with a detail-content factory.</summary>
        public MolcaHubRailNode(string id, string label, Func<VisualElement> createContent, string description = null)
        {
            Id = id;
            Label = label;
            Description = description;
            CreateContent = createContent;
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
}
