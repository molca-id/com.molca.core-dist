using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Optional re-activation hook for a workspace view that opted into
    /// <see cref="MolcaHubWorkspaceItem.CacheContent"/>.
    /// </summary>
    /// <remarks>
    /// A cached view is hidden rather than detached on tab switch, so it never sees a second
    /// <c>AttachToPanelEvent</c> and cannot use attach as an "I am being shown again" signal. Implement this
    /// to consume whatever a caller stashed for the next activation, or to re-read state another surface may
    /// have changed while this view was hidden. Called after the view is made visible.
    /// <para>
    /// Every deep-link target that opts into <see cref="MolcaHubWorkspaceItem.CacheContent"/> needs this:
    /// Docs (<see cref="Docs.DocsWorkspaceView.PendingDocId"/>), Network and References all stash a target
    /// on a static and would otherwise honour it only on the very first build of the view — the link would
    /// work once and then silently do nothing for as long as the view stayed cached.
    /// </para>
    /// </remarks>
    internal interface IMolcaHubCachedView
    {
        /// <summary>Called each time this already-built view is shown again by the workspace host.</summary>
        void OnWorkspaceActivated();
    }

    /// <summary>
    /// Owns the Hub's workspace host content: builds a tab's view, and — for items that opt into
    /// <see cref="MolcaHubWorkspaceItem.CacheContent"/> — keeps a small LRU of built views alive as hidden
    /// children instead of rebuilding them on every activation.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Owned by <see cref="MolcaHubWindow"/>.
    /// The mechanism is deliberately <em>hide, do not detach</em>: nothing is removed from the hierarchy on a
    /// switch, so no cached view is ever caught half-torn-down by its own <c>DetachFromPanelEvent</c> cleanup.
    /// Eviction (LRU overflow, or <see cref="RetainOnly"/> when the view's own tab is hidden) does remove the child,
    /// which fires detach and runs that view's normal cleanup. A non-caching item behaves exactly as it always
    /// has: built on activation, removed on the next switch. A factory that throws is reported in-place and
    /// never cached, so one bad view cannot poison the slot. Editor-only; main thread.
    /// </remarks>
    internal sealed class MolcaHubWorkspaceViewCache
    {
        /// <summary>Number of opted-in views kept alive at once.</summary>
        /// <remarks>
        /// Sized above the number of workspaces that currently opt in (Docs, Localization, Network,
        /// References, Content — five), because a capacity below that count quietly defeats the feature:
        /// rotating through the opted-in tabs would evict and rebuild each one just before you returned to
        /// it, which is the exact round trip <c>CacheContent</c> promises to survive. Raise this when a
        /// sixth workspace opts in.
        /// </remarks>
        internal const int DefaultCapacity = 6;

        private readonly VisualElement _host;
        private readonly int _capacity;

        // Least-recently-used first, most-recently-shown last. Small by construction, so a List with linear
        // scans is cheaper and clearer than a dictionary plus an order list.
        private readonly List<Entry> _cached = new List<Entry>();

        // The view of the currently shown non-caching item, removed on the next switch.
        private VisualElement _transient;

        /// <summary>Creates a cache that hosts its content in <paramref name="host"/>.</summary>
        /// <param name="host">The workspace host element that owns the built views.</param>
        /// <param name="capacity">Maximum number of cached views; values below 1 are clamped to 1.</param>
        internal MolcaHubWorkspaceViewCache(VisualElement host, int capacity = DefaultCapacity)
        {
            _host = host;
            _capacity = Mathf.Max(1, capacity);
        }

        /// <summary>The currently cached workspace ids, least-recently-used first. Exposed for tests.</summary>
        internal IReadOnlyList<string> CachedIds
        {
            get
            {
                var ids = new List<string>(_cached.Count);
                foreach (var entry in _cached) ids.Add(entry.Id);
                return ids;
            }
        }

        /// <summary>
        /// Makes <paramref name="item"/>'s view the visible content of the host, reusing a cached view when
        /// the item opted in and one exists, and building it otherwise.
        /// </summary>
        /// <param name="item">The workspace to show; <c>null</c> just clears the visible content.</param>
        internal void Show(MolcaHubWorkspaceItem item)
        {
            if (_host == null) return;

            ShowNone();
            if (item?.CreateContent == null) return;

            if (!item.CacheContent)
            {
                var built = Build(item);
                if (built == null) return;
                _transient = built;
                _host.Add(built);
                return;
            }

            var index = IndexOf(item.Id);
            if (index >= 0)
            {
                var entry = _cached[index];
                _cached.RemoveAt(index);
                _cached.Add(entry);                       // touch: now most-recently-used
                entry.View.style.display = DisplayStyle.Flex;
                Activated(entry.View);
                return;
            }

            var content = Build(item);
            if (content == null) return;

            // A failed build is surfaced as a transient error, never cached: the next activation should get a
            // fresh attempt rather than a permanently broken slot.
            if (content is ErrorView)
            {
                _transient = content;
                _host.Add(content);
                return;
            }

            _host.Add(content);
            _cached.Add(new Entry(item.Id, content));
            EvictExcess();
        }

        /// <summary>Hides every cached view and removes the non-caching view, leaving the host empty-looking.</summary>
        internal void ShowNone()
        {
            if (_host == null) return;

            foreach (var entry in _cached)
                entry.View.style.display = DisplayStyle.None;

            if (_transient == null) return;
            _transient.RemoveFromHierarchy();             // fires the view's DetachFromPanelEvent cleanup
            _transient = null;
        }

        /// <summary>
        /// Hides the visible content and evicts only the cached views whose workspace is no longer in
        /// <paramref name="liveIds"/>. Used when the resolved workspace set changes.
        /// </summary>
        /// <param name="liveIds">The ids that still resolve to a tab.</param>
        /// <remarks>
        /// A cached view whose tab no longer exists has no way back, so it must go. The others must not:
        /// hiding one unrelated tab used to tear down every cached workspace with it, discarding filters,
        /// scroll positions and in-progress scans the cache exists to protect.
        /// </remarks>
        internal void RetainOnly(IReadOnlyCollection<string> liveIds)
        {
            ShowNone();

            var live = new HashSet<string>(liveIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            for (int i = _cached.Count - 1; i >= 0; i--)
            {
                if (live.Contains(_cached[i].Id)) continue;
                _cached[i].View.RemoveFromHierarchy();   // detach → the view's own cleanup runs
                _cached.RemoveAt(i);
            }
        }

        private void EvictExcess()
        {
            while (_cached.Count > _capacity)
            {
                var evicted = _cached[0];
                _cached.RemoveAt(0);
                evicted.View.RemoveFromHierarchy();       // detach → the view's own cleanup runs
            }
        }

        private int IndexOf(string id)
        {
            for (int i = 0; i < _cached.Count; i++)
                if (string.Equals(_cached[i].Id, id, StringComparison.Ordinal))
                    return i;
            return -1;
        }

        private static void Activated(VisualElement view)
        {
            if (view is not IMolcaHubCachedView cached) return;
            try { cached.OnWorkspaceActivated(); }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>
        /// Builds a workspace's content. A consumer workspace that throws while building must not break the
        /// Hub shell, so the failure is surfaced as a compact in-host message instead.
        /// </summary>
        private static VisualElement Build(MolcaHubWorkspaceItem item)
        {
            try
            {
                return item.CreateContent();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                var error = new ErrorView($"Failed to open '{item.Label}': {ex.Message}");
                error.AddToClassList("molca-hub-muted");
                return error;
            }
        }

        /// <summary>Marker type so a build failure is recognizable as one and kept out of the cache.</summary>
        private sealed class ErrorView : Label
        {
            internal ErrorView(string text) : base(text) { }
        }

        private readonly struct Entry
        {
            internal readonly string Id;
            internal readonly VisualElement View;

            internal Entry(string id, VisualElement view)
            {
                Id = id;
                View = view;
            }
        }
    }
}
