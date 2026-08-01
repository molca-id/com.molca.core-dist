using Molca.Editor.Icons;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// The Hub's workspace toolbar: builds one tab per resolved workspace, measures them, and degrades the
    /// strip — icon-only, then an overflow menu — so it renders correctly at any provider count and any
    /// window width.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Owned by <see cref="MolcaHubWindow"/>, which
    /// supplies the resolved item list and the selection callback but knows nothing about fitting. The
    /// anchored Settings home tab is synthesized here (it is not provider-contributed) and never collapses to
    /// icon-only, never overflows, and is always effectively pinned. The fitting decision itself is the pure
    /// static <see cref="Fit"/>, so it is testable without a panel. The strip also owns the tab-management
    /// affordances — a per-tab context menu and an always-visible tabs menu button — so showing, hiding, and
    /// pinning never requires a trip to Settings ▸ Editor. Editor-only; main thread.
    /// </remarks>
    internal sealed class MolcaHubTabStrip : VisualElement
    {
        /// <summary>
        /// Width of a tab rendered icon-only. Derived from <c>MolcaHubWindow.uss</c>:
        /// <c>.molca-hub-workspace-tab</c> contributes <c>padding-left: 12px</c> + <c>padding-right: 12px</c>
        /// and <c>.molca-hub-workspace-tab__icon</c> contributes <c>width: 14px</c> — its 6px right margin is
        /// zeroed by <c>.molca-hub-workspace-tab--icon-only</c>, which also hides the label. Keep this in sync
        /// with those rules; each carries a reciprocal comment.
        /// </summary>
        internal const float IconOnlyTabWidth = 38f;

        /// <summary>Width of the <c>»</c> overflow button, including its own padding.</summary>
        internal const float OverflowButtonWidth = 40f;

        /// <summary>
        /// Width of the always-visible tabs menu button. Derived from <c>MolcaHubWindow.uss</c>:
        /// <c>.molca-hub-workspace-tab--tabs-menu</c> contributes <c>padding-left: 8px</c> +
        /// <c>padding-right: 8px</c> and the icon 14px (its right margin is zeroed by the same rule).
        /// Unlike the overflow chevron this button is never hidden, so its width is subtracted from the
        /// strip's budget before fitting rather than added to a candidate layout — see <see cref="TabBudget"/>.
        /// </summary>
        internal const float TabsMenuButtonWidth = 30f;

        /// <summary>Width of one inter-tab divider (<c>.molca-hub-tab-divider</c>, <c>width: 1px</c>).</summary>
        internal const float DividerWidth = 1f;

        private readonly Action<string> _onSelect;
        private readonly Action _onManageTabs;

        private readonly Dictionary<string, Button> _buttons = new Dictionary<string, Button>(StringComparer.Ordinal);
        private readonly Dictionary<string, VisualElement> _dividers = new Dictionary<string, VisualElement>(StringComparer.Ordinal);
        private readonly Dictionary<string, Label> _labels = new Dictionary<string, Label>(StringComparer.Ordinal);
        private readonly Dictionary<string, VisualElement> _pinMarks = new Dictionary<string, VisualElement>(StringComparer.Ordinal);

        // Full-fidelity width per tab id, captured on the first post-build layout pass. Once a tab is
        // collapsed its full width is no longer observable, so the first capture wins and is only discarded
        // when the item set is rebuilt.
        private readonly Dictionary<string, float> _fullWidths = new Dictionary<string, float>(StringComparer.Ordinal);

        private readonly List<TabMeasure> _measures = new List<TabMeasure>();

        private IReadOnlyList<MolcaHubWorkspaceItem> _items = Array.Empty<MolcaHubWorkspaceItem>();
        private Button _overflowButton;
        private Button _tabsMenuButton;
        private string _activeId = MolcaHubWorkspaceRegistry.SettingsId;
        private float _lastAppliedWidth = -1f;
        private bool _applying;

        // Inputs the fit depends on but that do not change per layout pass. Re-read when the selection, the
        // item set, or the pinned set changes, so a geometry storm never turns into a pref-read storm.
        private IReadOnlyList<string> _mru = Array.Empty<string>();
        private IReadOnlyCollection<string> _pinned = Array.Empty<string>();
        private HashSet<string> _pinnedLookup = new HashSet<string>(StringComparer.Ordinal);
        private TabLayout _lastLayout;

        /// <summary>Creates the strip.</summary>
        /// <param name="onSelect">Invoked with a workspace id when the user picks a tab or overflow entry.</param>
        /// <param name="onManageTabs">Invoked for the menus' "Manage tabs…" entry; may be <c>null</c>.</param>
        internal MolcaHubTabStrip(Action<string> onSelect, Action onManageTabs = null)
        {
            _onSelect = onSelect;
            _onManageTabs = onManageTabs;

            AddToClassList("molca-hub-tab-strip");
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        /// <summary>Rebuilds every tab from <paramref name="items"/>, discarding cached measurements.</summary>
        /// <param name="items">The resolved, ordered, non-Settings workspaces.</param>
        internal void SetItems(IReadOnlyList<MolcaHubWorkspaceItem> items)
        {
            _items = items ?? (IReadOnlyList<MolcaHubWorkspaceItem>)Array.Empty<MolcaHubWorkspaceItem>();
            _fullWidths.Clear();
            _lastAppliedWidth = -1f;
            ReadPreferences();
            Build();
        }

        /// <summary>Highlights <paramref name="workspaceId"/> as the active tab without rebuilding.</summary>
        /// <param name="workspaceId">The active workspace id.</param>
        internal void SetActive(string workspaceId)
        {
            _activeId = string.IsNullOrEmpty(workspaceId) ? MolcaHubWorkspaceRegistry.SettingsId : workspaceId;
            ReadPreferences();

            foreach (var pair in _buttons)
                pair.Value.EnableInClassList("molca-hub-workspace-tab--active",
                    string.Equals(pair.Key, _activeId, StringComparison.Ordinal));

            // The active tab never overflows and always keeps its label, so a selection change can change
            // the fit. Re-apply against the width already in effect.
            if (_lastAppliedWidth > 0f) ApplyLayout(_lastAppliedWidth);
        }

        // ---- Build -----------------------------------------------------------------------------------

        private void Build()
        {
            Clear();
            _buttons.Clear();
            _dividers.Clear();
            _labels.Clear();
            _pinMarks.Clear();
            _overflowButton = null;
            _tabsMenuButton = null;

            // Settings is the anchored home tab (Core-owned, always first). Every other tab — Core's own
            // Doctor/Assistant/Sequence and any consumer-contributed workspace — comes from the registry.
            // Left-aligned tabs sit before the flexible spacer; right-anchored tabs (e.g. Docs) after it.
            Add(BuildTab(MolcaHubWorkspaceRegistry.SettingsId, "Settings", "settings"));

            string previousGroup = null;
            var first = true;
            foreach (var item in _items)
            {
                if (item.RightAnchored) continue;
                var group = MolcaHubWorkspaceGroups.Normalize(item.Group);
                // A group boundary gets a wider divider; tabs inside one group keep the thin rule. The very
                // first provider tab always follows Settings, which is its own thing — treat that as a boundary.
                Add(MakeDivider(item.Id, first || !string.Equals(group, previousGroup, StringComparison.Ordinal)));
                Add(BuildTab(item.Id, item.Label, item.Icon));
                previousGroup = group;
                first = false;
            }

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.AddToClassList("molca-hub-spacer");
            Add(spacer);

            var firstRight = true;
            foreach (var item in _items)
            {
                if (!item.RightAnchored) continue;
                if (!firstRight) Add(MakeDivider(item.Id, false));
                Add(BuildTab(item.Id, item.Label, item.Icon));
                firstRight = false;
            }

            _overflowButton = new Button(ShowOverflowMenu) { tooltip = "More workspaces" };
            _overflowButton.AddToClassList("molca-hub-workspace-tab");
            _overflowButton.AddToClassList("molca-hub-workspace-tab--overflow");
            _overflowButton.style.display = DisplayStyle.None;
            Add(_overflowButton);

            // Always-visible tab manager, rightmost in the strip. It is the only affordance that can *show* a
            // hidden tab: a hidden workspace is filtered out of the item set entirely, so there is no tab left
            // to right-click, which previously made Settings ▸ Editor the sole way back. Unlike the overflow
            // chevron this button never hides — the tab set is manageable at any width and any tab count.
            _tabsMenuButton = new Button(ShowTabsMenu) { tooltip = "Show or hide workspace tabs" };
            _tabsMenuButton.AddToClassList("molca-hub-workspace-tab");
            _tabsMenuButton.AddToClassList("molca-hub-workspace-tab--tabs-menu");

            // FindTexture, not ResolveTabIcon: the latter routes through EditorGUIUtility.IconContent, which
            // logs a console error for a name the current skin does not carry. This runs on every Hub open, so
            // a silent lookup plus a text fallback is the only version that cannot become log spam. The USS
            // rule pins the button's width, so either branch matches TabsMenuButtonWidth.
            var menuIcon = EditorGUIUtility.FindTexture("_Menu");
            if (menuIcon != null)
            {
                var image = new Image { image = menuIcon, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("molca-hub-workspace-tab__icon");
                image.pickingMode = PickingMode.Ignore;
                _tabsMenuButton.Add(image);
            }
            else
            {
                _tabsMenuButton.text = "...";
            }

            Add(_tabsMenuButton);

            SetActive(_activeId);
            UpdatePinMarks();
        }

        /// <summary>
        /// Builds one workspace toolbar tab as <c>[icon] [label] [underline]</c>. The selection indicator is a
        /// child <see cref="VisualElement"/> strip (styled via the parent's <c>--active</c> class), not a
        /// button border: Unity's built-in <see cref="Button"/> repaints its own border box on focus, which
        /// would erase a border-based underline the moment the tab is clicked.
        /// </summary>
        private Button BuildTab(string workspaceId, string label, string icon)
        {
            var button = new Button(() => _onSelect?.Invoke(workspaceId));
            button.AddToClassList("molca-hub-workspace-tab");
            button.userData = workspaceId;

            var iconTexture = ResolveTabIcon(icon);
            if (iconTexture != null)
            {
                var image = new Image { image = iconTexture, scaleMode = ScaleMode.ScaleToFit };
                image.AddToClassList("molca-hub-workspace-tab__icon");
                image.pickingMode = PickingMode.Ignore;
                button.Add(image);
            }

            var text = new Label(label) { pickingMode = PickingMode.Ignore };
            text.AddToClassList("molca-hub-workspace-tab__label");
            button.Add(text);

            var underline = new VisualElement { pickingMode = PickingMode.Ignore };
            underline.AddToClassList("molca-hub-workspace-tab__underline");
            button.Add(underline);

            // Pinned marker. Absolutely positioned like the underline, so pinning never changes the tab's
            // measured width — _fullWidths caches each width once, and an in-flow marker would invalidate a
            // cache that has no way to refill itself (a collapsed tab's full width is no longer observable).
            var pinMark = new VisualElement { pickingMode = PickingMode.Ignore };
            pinMark.AddToClassList("molca-hub-workspace-tab__pin");
            pinMark.style.display = DisplayStyle.None;
            button.Add(pinMark);

            button.AddManipulator(new ContextualMenuManipulator(evt => BuildTabContextMenu(evt, workspaceId)));

            _buttons[workspaceId] = button;
            _labels[workspaceId] = text;
            _pinMarks[workspaceId] = pinMark;
            return button;
        }

        private VisualElement MakeDivider(string followingTabId, bool groupBoundary)
        {
            var divider = new VisualElement { pickingMode = PickingMode.Ignore };
            divider.AddToClassList("molca-hub-tab-divider");
            if (groupBoundary) divider.AddToClassList("molca-hub-tab-divider--group");
            _dividers[followingTabId] = divider;
            return divider;
        }

        /// <summary>
        /// Resolves a tab icon by name: first an on-brand Molca family icon shipped in the package
        /// (<see cref="MolcaEditorIcons.Family"/>), then a skin-aware built-in editor icon. Returns
        /// <c>null</c> when the name is empty or nothing matches, in which case the tab renders label-only.
        /// </summary>
        private static Texture ResolveTabIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;

            var family = MolcaEditorIcons.Family(icon);
            if (family != null) return family;

            if (EditorGUIUtility.isProSkin && !icon.StartsWith("d_"))
            {
                var pro = EditorGUIUtility.IconContent("d_" + icon)?.image;
                if (pro != null) return pro;
            }

            return EditorGUIUtility.IconContent(icon)?.image;
        }

        // ---- Measure and degrade ---------------------------------------------------------------------

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (_applying) return;

            var width = evt.newRect.width;
            if (width <= 0f) return;

            CaptureFullWidths();

            // Re-fit only when the budget actually changed; otherwise our own display changes would loop.
            if (Mathf.Approximately(width, _lastAppliedWidth)) return;
            _lastAppliedWidth = width;
            ApplyLayout(width);
        }

        /// <summary>
        /// Records each tab's full-fidelity width the first time layout reports one. <c>flex-shrink: 0</c>
        /// means a tab resolves to its content width even when the strip is too narrow, so a single capture
        /// is trustworthy regardless of the window size the Hub happens to open at.
        /// </summary>
        private void CaptureFullWidths()
        {
            foreach (var pair in _buttons)
            {
                if (_fullWidths.ContainsKey(pair.Key)) continue;
                var resolved = pair.Value.resolvedStyle.width;
                if (resolved > 0f && !float.IsNaN(resolved)) _fullWidths[pair.Key] = resolved;
            }
        }

        private void ApplyLayout(float available)
        {
            if (_buttons.Count == 0) return;

            BuildMeasures();
            if (_measures.Count == 0) return;

            var layout = Fit(_measures, TabBudget(available), _activeId, _pinned, _mru);
            _lastLayout = layout;

            _applying = true;
            try
            {
                var overflow = new HashSet<string>(layout.OverflowIds, StringComparer.Ordinal);
                foreach (var pair in _buttons)
                {
                    var id = pair.Key;
                    var hidden = overflow.Contains(id);
                    pair.Value.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;
                    if (_dividers.TryGetValue(id, out var divider))
                        divider.style.display = hidden ? DisplayStyle.None : DisplayStyle.Flex;

                    var iconOnly = !hidden && layout.IconOnly && !KeepsLabel(id);
                    pair.Value.EnableInClassList("molca-hub-workspace-tab--icon-only", iconOnly);
                    if (_labels.TryGetValue(id, out var label))
                        label.style.display = iconOnly ? DisplayStyle.None : DisplayStyle.Flex;

                    // The pin marker is a bare dot, so the tooltip is what actually names it. A collapsed tab
                    // already needs its label there, so the two are combined rather than one winning.
                    var isPinned = _pinnedLookup.Contains(id);
                    pair.Value.tooltip = iconOnly
                        ? (isPinned ? LabelOf(id) + " (pinned)" : LabelOf(id))
                        : (isPinned ? "Pinned" : null);
                }

                UpdatePinMarks();

                if (_overflowButton != null)
                {
                    var count = layout.OverflowIds.Count;
                    _overflowButton.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                    _overflowButton.text = count > 0 ? "» " + count : "»";
                }
            }
            finally
            {
                _applying = false;
            }
        }

        /// <summary>Re-reads the project prefs the fit depends on (pins, MRU).</summary>
        private void ReadPreferences()
        {
            _pinned = MolcaHubWorkspaceRegistry.PinnedIds();
            _pinnedLookup = new HashSet<string>(_pinned, StringComparer.Ordinal);
            _mru = MolcaHubState.Load().WorkspaceMru;
        }

        /// <summary>
        /// Shows the pinned dot on pinned tabs and hides it elsewhere. Kept separate from the fit so the
        /// marker is correct from <see cref="Build"/> onward, before the first layout pass reports a width.
        /// </summary>
        private void UpdatePinMarks()
        {
            foreach (var pair in _pinMarks)
                pair.Value.style.display =
                    _pinnedLookup.Contains(pair.Key) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// The width available to the tabs themselves: the strip minus the always-visible tabs menu button.
        /// </summary>
        /// <param name="stripWidth">The strip's resolved width in pixels.</param>
        /// <returns>The fitting budget, never negative.</returns>
        internal static float TabBudget(float stripWidth) => Mathf.Max(0f, stripWidth - TabsMenuButtonWidth);

        private bool KeepsLabel(string id) =>
            string.Equals(id, MolcaHubWorkspaceRegistry.SettingsId, StringComparison.Ordinal)
            || string.Equals(id, _activeId, StringComparison.Ordinal);

        private string LabelOf(string id)
        {
            if (string.Equals(id, MolcaHubWorkspaceRegistry.SettingsId, StringComparison.Ordinal)) return "Settings";
            foreach (var item in _items)
                if (string.Equals(item.Id, id, StringComparison.Ordinal))
                    return string.IsNullOrEmpty(item.Label) ? item.Id : item.Label;
            return id;
        }

        private void BuildMeasures()
        {
            _measures.Clear();
            AddMeasure(MolcaHubWorkspaceRegistry.SettingsId, hasIcon: true, rightAnchored: false, anchored: true);
            foreach (var item in _items)
                if (!item.RightAnchored)
                    AddMeasure(item.Id, !string.IsNullOrEmpty(item.Icon), false, false);
            foreach (var item in _items)
                if (item.RightAnchored)
                    AddMeasure(item.Id, !string.IsNullOrEmpty(item.Icon), true, false);
        }

        private void AddMeasure(string id, bool hasIcon, bool rightAnchored, bool anchored)
        {
            // Before the first layout pass a tab has no observed width; fall back to the icon-only constant so
            // an early fit is conservative rather than wildly wrong.
            var width = _fullWidths.TryGetValue(id, out var observed) ? observed : IconOnlyTabWidth;
            _measures.Add(new TabMeasure(id, width, hasIcon, rightAnchored, anchored));
        }

        // ---- Pure fitting ----------------------------------------------------------------------------

        /// <summary>One tab's input to <see cref="Fit"/>.</summary>
        internal readonly struct TabMeasure
        {
            /// <summary>The tab's workspace id.</summary>
            internal readonly string Id;

            /// <summary>The tab's full-fidelity (icon + label) width in pixels.</summary>
            internal readonly float FullWidth;

            /// <summary>Whether the tab resolved an icon; a tab without one blocks icon-only collapse.</summary>
            internal readonly bool HasIcon;

            /// <summary>Whether the tab renders after the flexible spacer.</summary>
            internal readonly bool RightAnchored;

            /// <summary>Whether this is the anchored Settings home tab (never collapses, never overflows).</summary>
            internal readonly bool Anchored;

            /// <summary>Creates a measurement.</summary>
            internal TabMeasure(string id, float fullWidth, bool hasIcon, bool rightAnchored, bool anchored)
            {
                Id = id;
                FullWidth = fullWidth;
                HasIcon = hasIcon;
                RightAnchored = rightAnchored;
                Anchored = anchored;
            }
        }

        /// <summary>The result of a fitting pass: which tabs render, which moved to overflow, and at what fidelity.</summary>
        internal sealed class TabLayout
        {
            /// <summary>Tabs that keep a toolbar slot, in the input's order.</summary>
            internal IReadOnlyList<string> VisibleIds { get; }

            /// <summary>Tabs moved into the overflow menu, in the input's order.</summary>
            internal IReadOnlyList<string> OverflowIds { get; }

            /// <summary>Whether visible tabs render icon-only (except the Settings and active tabs).</summary>
            internal bool IconOnly { get; }

            /// <summary>Creates a layout result.</summary>
            internal TabLayout(IReadOnlyList<string> visibleIds, IReadOnlyList<string> overflowIds, bool iconOnly)
            {
                VisibleIds = visibleIds;
                OverflowIds = overflowIds;
                IconOnly = iconOnly;
            }
        }

        /// <summary>
        /// Decides how the strip renders in <paramref name="available"/> pixels, applying the degradation
        /// ladder: full fidelity, then icon-only, then overflow.
        /// </summary>
        /// <param name="tabs">Every tab, in render order (Settings first).</param>
        /// <param name="available">The strip's available width in pixels.</param>
        /// <param name="activeId">The active workspace id; it never overflows and never loses its label.</param>
        /// <param name="pinnedIds">Pinned ids; they never overflow while any unpinned tab could go instead.</param>
        /// <param name="mruIds">Recently used ids, most recent first; breaks ties among unpinned tabs.</param>
        /// <returns>The layout to apply.</returns>
        /// <remarks>
        /// Icon-only is all-or-nothing and is applied only when <em>every</em> tab resolved an icon — a row of
        /// blanks reads worse than an overflow menu. Overflow removes the lowest-priority removable tab
        /// repeatedly until the rest fit; when nothing removable is left (Settings plus the active tab plus
        /// the chevron already exceed the budget) it stops, because there is no correct answer below that floor.
        /// </remarks>
        internal static TabLayout Fit(IReadOnlyList<TabMeasure> tabs, float available, string activeId,
            IReadOnlyCollection<string> pinnedIds = null, IReadOnlyList<string> mruIds = null)
        {
            if (tabs == null || tabs.Count == 0)
                return new TabLayout(Array.Empty<string>(), Array.Empty<string>(), false);

            var allIds = new List<string>(tabs.Count);
            foreach (var tab in tabs) allIds.Add(tab.Id);

            if (Width(tabs, iconOnly: false, activeId) <= available)
                return new TabLayout(allIds, Array.Empty<string>(), false);

            var everyTabHasIcon = true;
            foreach (var tab in tabs)
                if (!tab.HasIcon) { everyTabHasIcon = false; break; }

            if (everyTabHasIcon && Width(tabs, iconOnly: true, activeId) <= available)
                return new TabLayout(allIds, Array.Empty<string>(), true);

            // Still too wide: keep the icon-only saving (when it is available at all) and start overflowing.
            var iconOnly = everyTabHasIcon;
            var pinned = new HashSet<string>(pinnedIds ?? Array.Empty<string>(), StringComparer.Ordinal);

            var kept = new List<TabMeasure>(tabs);
            var overflowed = new HashSet<string>(StringComparer.Ordinal);

            while (Width(kept, iconOnly, activeId) + OverflowButtonWidth > available)
            {
                var victim = NextVictim(kept, activeId, pinned, mruIds);
                if (victim < 0) break;
                overflowed.Add(kept[victim].Id);
                kept.RemoveAt(victim);
            }

            var visible = new List<string>(kept.Count);
            var overflow = new List<string>(overflowed.Count);
            foreach (var id in allIds)
            {
                if (overflowed.Contains(id)) overflow.Add(id);
                else visible.Add(id);
            }

            return new TabLayout(visible, overflow, iconOnly);
        }

        /// <summary>
        /// Picks the index of the tab to move into overflow next: never Settings, never the active tab, and an
        /// unpinned tab before a pinned one. Within a priority band the least-recently-used tab goes first,
        /// and tabs that were never used go before any that were — so a strip degrades from the end the user
        /// does not touch.
        /// </summary>
        private static int NextVictim(IReadOnlyList<TabMeasure> tabs, string activeId,
            HashSet<string> pinned, IReadOnlyList<string> mruIds)
        {
            var bestIndex = -1;
            var bestPinned = false;
            var bestMru = -1;

            for (int i = 0; i < tabs.Count; i++)
            {
                var tab = tabs[i];
                if (tab.Anchored) continue;
                if (string.Equals(tab.Id, activeId, StringComparison.Ordinal)) continue;

                var isPinned = pinned.Contains(tab.Id);
                var mruIndex = IndexIn(mruIds, tab.Id);

                // `>=` on the MRU comparison makes a later tab win a tie, so an untouched strip degrades from
                // its trailing edge rather than losing the tab next to Settings first.
                if (bestIndex < 0
                    || (bestPinned && !isPinned)                        // unpinned always goes first
                    || (bestPinned == isPinned && mruIndex >= bestMru)) // then the least recently used
                {
                    bestIndex = i;
                    bestPinned = isPinned;
                    bestMru = mruIndex;
                }
            }

            return bestIndex;
        }

        /// <summary>Index of <paramref name="id"/> in the MRU list, or <see cref="int.MaxValue"/> when absent.</summary>
        private static int IndexIn(IReadOnlyList<string> mruIds, string id)
        {
            if (mruIds == null) return int.MaxValue;
            for (int i = 0; i < mruIds.Count; i++)
                if (string.Equals(mruIds[i], id, StringComparison.Ordinal))
                    return i;
            return int.MaxValue;
        }

        private static float Width(IReadOnlyList<TabMeasure> tabs, bool iconOnly, string activeId)
        {
            var total = 0f;
            foreach (var tab in tabs)
            {
                var collapsed = iconOnly
                                && !tab.Anchored
                                && !string.Equals(tab.Id, activeId, StringComparison.Ordinal);
                total += collapsed ? IconOnlyTabWidth : tab.FullWidth;
            }

            if (tabs.Count > 1) total += (tabs.Count - 1) * DividerWidth;
            return total;
        }

        // ---- Menus -----------------------------------------------------------------------------------

        private void ShowOverflowMenu()
        {
            var menu = new GenericMenu();
            var pinned = new HashSet<string>(_pinned, StringComparer.Ordinal);
            var visible = new HashSet<string>(
                _lastLayout?.VisibleIds ?? (IReadOnlyList<string>)Array.Empty<string>(), StringComparer.Ordinal);

            var listed = new HashSet<string>(StringComparer.Ordinal);

            // Pinned first, but only when the section says something the "All" section would not — i.e. when
            // there is at least one unpinned tab to contrast it with.
            var hasUnpinned = false;
            foreach (var item in _items)
                if (!pinned.Contains(item.Id)) { hasUnpinned = true; break; }

            if (hasUnpinned)
            {
                var any = false;
                foreach (var item in _items)
                {
                    if (!pinned.Contains(item.Id)) continue;
                    AddWorkspaceEntry(menu, "Pinned/" + MenuSafe(LabelFor(item)), item.Id);
                    listed.Add(item.Id);
                    any = true;
                }
                if (any) menu.AddSeparator(string.Empty);
            }

            var recent = 0;
            foreach (var id in _mru)
            {
                if (recent >= 5) break;
                if (visible.Contains(id) || listed.Contains(id)) continue;
                var item = FindItem(id);
                if (item == null) continue;
                AddWorkspaceEntry(menu, "Recent/" + MenuSafe(LabelFor(item)), item.Id);
                listed.Add(item.Id);
                recent++;
            }
            if (recent > 0) menu.AddSeparator(string.Empty);

            foreach (var item in _items)
            {
                var group = MolcaHubWorkspaceGroups.Label(item.Group).Replace('/', '-');
                AddWorkspaceEntry(menu, "All/" + group + "/" + MenuSafe(LabelFor(item)), item.Id);
            }

            menu.AddSeparator(string.Empty);
            if (_onManageTabs != null) menu.AddItem(new GUIContent("Manage tabs…"), false, () => _onManageTabs());
            else menu.AddDisabledItem(new GUIContent("Manage tabs…"));

            menu.DropDown(_overflowButton.worldBound);
        }

        /// <summary>
        /// Builds the tabs menu: every workspace the project *could* show, checked when it is currently in the
        /// toolbar, plus a Pin submenu and the deep link to the fuller Settings ▸ Editor card.
        /// </summary>
        /// <remarks>
        /// Built from <see cref="MolcaHubWorkspaceRegistry.GetConfigurableWorkspaces"/>, not from
        /// <c>_items</c>: the strip's item set has already had hidden tabs filtered out of it, so it cannot
        /// answer "what could be shown". Provider discovery runs here rather than being cached, so a tab that
        /// became available since the toolbar was built (an add-on installed, an availability gate flipped)
        /// shows up the next time the menu is opened.
        /// </remarks>
        private void ShowTabsMenu()
        {
            var menu = new GenericMenu();
            var configurable = MolcaHubWorkspaceRegistry.GetConfigurableWorkspaces();
            var hidden = new HashSet<string>(MolcaHubWorkspaceRegistry.HiddenIds(), StringComparer.Ordinal);
            var pinned = new HashSet<string>(MolcaHubWorkspaceRegistry.PinnedIds(), StringComparer.Ordinal);

            // Settings is anchored and cannot be hidden. It is still listed — checked and disabled — so the
            // menu reads as the complete tab set instead of silently omitting the one tab that is always there.
            menu.AddDisabledItem(new GUIContent("Settings"), true);
            menu.AddSeparator(string.Empty);

            if (configurable.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No other workspaces available"));
            }
            else
            {
                string previousGroup = null;
                foreach (var item in configurable)
                {
                    var group = MolcaHubWorkspaceGroups.Normalize(item.Group);
                    if (previousGroup != null && !string.Equals(group, previousGroup, StringComparison.Ordinal))
                        menu.AddSeparator(string.Empty);
                    previousGroup = group;

                    // Captured per iteration: GenericMenu invokes these callbacks long after the loop ends.
                    var id = item.Id;
                    var isHidden = hidden.Contains(id);
                    menu.AddItem(new GUIContent(MenuSafe(LabelFor(item))), !isHidden,
                        () => MolcaHubWorkspaceRegistry.SetHidden(id, !isHidden));
                }
            }

            // Pinning only means something for a tab that is in the toolbar, so hidden tabs are left out of
            // this submenu rather than offered a pin that does nothing until they are shown.
            var pinnable = false;
            foreach (var item in configurable)
            {
                if (hidden.Contains(item.Id)) continue;
                if (!pinnable) { menu.AddSeparator(string.Empty); pinnable = true; }

                var id = item.Id;
                var isPinned = pinned.Contains(id);
                menu.AddItem(new GUIContent("Pin/" + MenuSafe(LabelFor(item))), isPinned,
                    () => MolcaHubWorkspaceRegistry.SetPinned(id, !isPinned));
            }

            menu.AddSeparator(string.Empty);
            if (_onManageTabs != null) menu.AddItem(new GUIContent("Manage tabs…"), false, () => _onManageTabs());
            else menu.AddDisabledItem(new GUIContent("Manage tabs…"));

            menu.DropDown(_tabsMenuButton.worldBound);
        }

        private static string LabelFor(MolcaHubWorkspaceItem item) =>
            string.IsNullOrEmpty(item.Label) ? item.Id : item.Label;

        /// <summary>
        /// Neutralizes <c>/</c> in a menu entry: <see cref="GenericMenu"/> reads it as a submenu separator, so
        /// a label containing one would otherwise nest the entry under a phantom parent.
        /// </summary>
        private static string MenuSafe(string label) =>
            string.IsNullOrEmpty(label) ? label : label.Replace('/', '-');

        private void AddWorkspaceEntry(GenericMenu menu, string path, string workspaceId)
        {
            var isActive = string.Equals(workspaceId, _activeId, StringComparison.Ordinal);
            menu.AddItem(new GUIContent(path), isActive, () => _onSelect?.Invoke(workspaceId));
        }

        private void BuildTabContextMenu(ContextualMenuPopulateEvent evt, string workspaceId)
        {
            if (string.Equals(workspaceId, MolcaHubWorkspaceRegistry.SettingsId, StringComparison.Ordinal))
            {
                // Settings is anchored: it cannot be pinned (it always is) or hidden.
                evt.menu.AppendAction("Manage tabs…", _ => _onManageTabs?.Invoke(),
                    _onManageTabs == null ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
                return;
            }

            var pinned = new HashSet<string>(MolcaHubWorkspaceRegistry.PinnedIds(), StringComparer.Ordinal)
                .Contains(workspaceId);

            evt.menu.AppendAction(pinned ? "Unpin" : "Pin",
                _ => MolcaHubWorkspaceRegistry.SetPinned(workspaceId, !pinned));
            evt.menu.AppendAction("Hide tab", _ => MolcaHubWorkspaceRegistry.SetHidden(workspaceId, true));
            evt.menu.AppendSeparator();
            evt.menu.AppendAction("Manage tabs…", _ => _onManageTabs?.Invoke(),
                _onManageTabs == null ? DropdownMenuAction.Status.Disabled : DropdownMenuAction.Status.Normal);
        }

        private MolcaHubWorkspaceItem FindItem(string id)
        {
            foreach (var item in _items)
                if (string.Equals(item.Id, id, StringComparison.Ordinal))
                    return item;
            return null;
        }

        /// <summary>Re-reads pins/MRU and re-applies the degradation ladder, e.g. after the pinned set changed.</summary>
        internal void Refresh()
        {
            ReadPreferences();
            UpdatePinMarks();
            if (_lastAppliedWidth > 0f) ApplyLayout(_lastAppliedWidth);
        }
    }
}
