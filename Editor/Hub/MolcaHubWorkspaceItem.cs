using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Describes one Molca Hub top-bar workspace tab: a stable id, label, sort order, the content it hosts,
    /// and an optional availability gate. Built-in tabs (Doctor/Assistant/Sequence) and consumer-added tabs
    /// are all expressed as these descriptors and discovered through <see cref="MolcaHubWorkspaceProvider"/>.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. The <c>"settings"</c> id
    /// (<see cref="MolcaHubWorkspaceRegistry.SettingsId"/>) is reserved for the anchored Settings home tab
    /// owned by <see cref="MolcaHubWindow"/> and may not be supplied by a provider.
    /// <see cref="CreateContent"/> builds the hosted view on demand each time the tab is selected and must
    /// tolerate teardown — the workspace host is cleared on every tab switch (which fires the view's
    /// <c>DetachFromPanelEvent</c> cleanup). Editor-only; main thread.
    /// </remarks>
    public sealed class MolcaHubWorkspaceItem
    {
        /// <summary>Stable, unique, kebab-case identifier. Persisted as the selection and hide-config key.</summary>
        public string Id { get; }

        /// <summary>Tab label shown in the workspace toolbar.</summary>
        public string Label { get; }

        /// <summary>Sort order among non-Settings tabs (ascending; ties broken by <see cref="Id"/>).</summary>
        public int Order { get; }

        /// <summary>Builds the hosted content for this workspace. Invoked on each selection.</summary>
        public Func<VisualElement> CreateContent { get; }

        /// <summary>
        /// Optional availability gate; when present and it returns <c>false</c> the tab is omitted. A gate
        /// that throws is treated as unavailable. Defaults to always available.
        /// </summary>
        public Func<bool> IsAvailable { get; }

        /// <summary>Creates a workspace descriptor.</summary>
        /// <param name="id">Stable unique kebab-case id (not <see cref="MolcaHubWorkspaceRegistry.SettingsId"/>).</param>
        /// <param name="label">Toolbar tab label.</param>
        /// <param name="order">Sort order among non-Settings tabs.</param>
        /// <param name="createContent">Factory that builds the hosted content on selection.</param>
        /// <param name="isAvailable">Optional availability gate; <c>null</c> means always available.</param>
        public MolcaHubWorkspaceItem(string id, string label, int order,
            Func<VisualElement> createContent, Func<bool> isAvailable = null)
        {
            Id = id;
            Label = label;
            Order = order;
            CreateContent = createContent;
            IsAvailable = isAvailable;
        }
    }

    /// <summary>
    /// Editor-only seam for contributing Molca Hub workspace tabs. Subclass and return one or more
    /// <see cref="MolcaHubWorkspaceItem"/>; non-abstract subclasses are discovered automatically via
    /// <c>TypeCache</c> (see <see cref="MolcaHubWorkspaceRegistry"/>) — no Core edit and no registration call.
    /// </summary>
    /// <remarks>
    /// Subclasses must have a public parameterless constructor. <see cref="GetWorkspaces"/> runs on the main
    /// thread while the Hub builds its toolbar; keep it cheap and side-effect free, deferring real work to
    /// each item's <see cref="MolcaHubWorkspaceItem.CreateContent"/>. A consumer adds a tab by subclassing
    /// this; it hides a built-in tab by id through <see cref="MolcaHubWorkspaceRegistry.SetHidden"/> — never
    /// by editing Core.
    /// </remarks>
    public abstract class MolcaHubWorkspaceProvider
    {
        /// <summary>Returns the workspace tabs this provider contributes.</summary>
        public abstract IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces();
    }
}
