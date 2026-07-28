using System;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Describes one contributed leaf in the Hub's Settings rail: a single panel of configuration or status
    /// that sits alongside Core's own sections (Network, MCP, …) instead of claiming a whole workspace tab.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Contributed through
    /// <see cref="MolcaHubSettingsLeafProvider"/> and resolved by <see cref="MolcaHubSettingsLeafRegistry"/>.
    /// Choose this seam over <see cref="MolcaHubWorkspaceItem"/> when the surface is one panel rather than a
    /// full-window tool — if it would look at home next to *Network* or *MCP*, it is a leaf.
    /// <see cref="CreateContent"/> is invoked lazily when the rail row is selected, and the built element is
    /// placed in the detail pane; the pane is cleared on the next selection, so the usual
    /// <c>DetachFromPanelEvent</c> cleanup contract applies. Editor-only; main thread.
    /// </remarks>
    public sealed class MolcaHubSettingsLeafItem
    {
        /// <summary>Stable, unique, kebab-case identifier. Namespaced to <c>ext:&lt;id&gt;</c> as a rail node id.</summary>
        public string Id { get; }

        /// <summary>Row label shown in the Settings rail.</summary>
        public string Label { get; }

        /// <summary>Optional description carried on the rail node.</summary>
        public string Description { get; }

        /// <summary>Sort order within <see cref="Group"/> (ascending; ties broken by <see cref="Id"/>).</summary>
        public int Order { get; }

        /// <summary>
        /// The rail category this leaf is appended to: <see cref="MolcaHubSettingsLeafRegistry.Framework"/>,
        /// <see cref="MolcaHubSettingsLeafRegistry.Tooling"/>, or
        /// <see cref="MolcaHubSettingsLeafRegistry.Addons"/>. Anything else (including <c>null</c>) collects
        /// under an "Extensions" root that only exists when at least one leaf lands there.
        /// </summary>
        public string Group { get; }

        /// <summary>Builds this leaf's detail panel. Invoked on each selection.</summary>
        public Func<VisualElement> CreateContent { get; }

        /// <summary>
        /// Optional availability gate; when present and it returns <c>false</c> the leaf is omitted. A gate
        /// that throws is treated as unavailable. Defaults to always available.
        /// </summary>
        public Func<bool> IsAvailable { get; }

        /// <summary>Creates a settings-leaf descriptor.</summary>
        /// <param name="id">Stable unique kebab-case id.</param>
        /// <param name="label">Rail row label.</param>
        /// <param name="createContent">Factory that builds the detail panel on selection.</param>
        /// <param name="order">Sort order within <paramref name="group"/>.</param>
        /// <param name="group">Target rail category; see <see cref="Group"/>.</param>
        /// <param name="description">Optional description.</param>
        /// <param name="isAvailable">Optional availability gate; <c>null</c> means always available.</param>
        public MolcaHubSettingsLeafItem(string id, string label, Func<VisualElement> createContent,
            int order = 0, string group = null, string description = null, Func<bool> isAvailable = null)
        {
            Id = id;
            Label = label;
            CreateContent = createContent;
            Order = order;
            Group = group;
            Description = description;
            IsAvailable = isAvailable;
        }
    }
}
