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
    /// <c>DetachFromPanelEvent</c> cleanup) unless the item opts into <see cref="CacheContent"/>.
    /// Editor-only; main thread.
    /// </remarks>
    public sealed class MolcaHubWorkspaceItem
    {
        /// <summary>Stable, unique, kebab-case identifier. Persisted as the selection and hide-config key.</summary>
        public string Id { get; }

        /// <summary>Tab label shown in the workspace toolbar.</summary>
        public string Label { get; }

        /// <summary>
        /// Sort order *within* this tab's <see cref="Group"/> (ascending; ties broken by <see cref="Id"/>).
        /// Group rank is compared first, so this number never has to be chosen against a global namespace.
        /// </summary>
        public int Order { get; }

        /// <summary>
        /// Semantic group this tab belongs to; see <see cref="MolcaHubWorkspaceGroups"/>. Null/empty means
        /// the default general group. Decides render order (group rank before <see cref="Order"/>), the
        /// group separators in the toolbar, and the submenu a tab appears under in the overflow menu.
        /// </summary>
        public string Group { get; }

        /// <summary>
        /// Icon rendered before the tab label. Resolved first as an on-brand Molca family icon shipped in
        /// the package (e.g. <c>"doctor"</c>, <c>"sequence"</c>, <c>"themes"</c>), then as a built-in editor
        /// icon name. When omitted, the tab tries the stable <see cref="Id"/> as its family icon name; if
        /// nothing resolves it renders label-only. Use a distinct icon for each workspace so collapsed tabs
        /// remain identifiable.
        /// </summary>
        public string Icon { get; }

        /// <summary>Builds the hosted content for this workspace. Invoked on each selection.</summary>
        public Func<VisualElement> CreateContent { get; }

        /// <summary>
        /// Optional availability gate; when present and it returns <c>false</c> the tab is omitted. A gate
        /// that throws is treated as unavailable. Defaults to always available.
        /// </summary>
        public Func<bool> IsAvailable { get; }

        /// <summary>
        /// When <c>true</c>, the tab is anchored to the right of the toolbar (after the flexible spacer)
        /// rather than sitting inline with the primary tabs. Used for auxiliary surfaces such as Docs that
        /// are conceptually set apart from the main workspaces. Defaults to <c>false</c> (left-aligned).
        /// </summary>
        public bool RightAnchored { get; }

        /// <summary>
        /// When <c>true</c>, the built view is kept alive and hidden on tab switch instead of being detached
        /// and rebuilt, so scroll position, filters, and in-progress view state survive a round trip.
        /// </summary>
        /// <remarks>
        /// Opting in changes the view's lifecycle contract: the view must tolerate being hidden while still
        /// attached — its work keeps running, and it will <em>not</em> receive a <c>DetachFromPanelEvent</c>
        /// between activations. Detach still fires when the cached view is evicted (the cache keeps a small
        /// number of views) or when the toolbar is rebuilt, so cleanup code is still required, just no longer
        /// guaranteed on every switch. Defaults to <c>false</c>, which is exactly today's behaviour.
        /// </remarks>
        public bool CacheContent { get; }

        /// <summary>Creates a workspace descriptor.</summary>
        /// <param name="id">Stable unique kebab-case id (not <see cref="MolcaHubWorkspaceRegistry.SettingsId"/>).</param>
        /// <param name="label">Toolbar tab label.</param>
        /// <param name="order">Sort order within <paramref name="group"/>.</param>
        /// <param name="createContent">Factory that builds the hosted content on selection.</param>
        /// <param name="isAvailable">Optional availability gate; <c>null</c> means always available.</param>
        /// <param name="rightAnchored">When <c>true</c>, anchors the tab to the right of the toolbar.</param>
        /// <param name="icon">Tab icon (Molca family or built-in editor name); omitted uses <paramref name="id"/>.</param>
        /// <param name="group">Semantic group; <c>null</c> means <see cref="MolcaHubWorkspaceGroups.General"/>.</param>
        /// <param name="cacheContent">When <c>true</c>, opts the view into hide-instead-of-rebuild caching; see <see cref="CacheContent"/>.</param>
        public MolcaHubWorkspaceItem(string id, string label, int order,
            Func<VisualElement> createContent, Func<bool> isAvailable = null, bool rightAnchored = false,
            string icon = null, string group = null, bool cacheContent = false)
        {
            Id = id;
            Label = label;
            Order = order;
            CreateContent = createContent;
            IsAvailable = isAvailable;
            RightAnchored = rightAnchored;
            Icon = icon;
            Group = group;
            CacheContent = cacheContent;
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
