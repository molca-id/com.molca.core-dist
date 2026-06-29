using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Discovers <see cref="MolcaHubWorkspaceProvider"/>s via <c>TypeCache</c> and resolves the ordered,
    /// deduplicated, visibility-filtered set of non-Settings workspace tabs the Hub renders.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. The <see cref="SettingsId"/> tab is anchored
    /// and Core-owned, so a provider supplying it is ignored. Hidden ids persist per project via
    /// <see cref="MolcaEditorPrefs"/>, letting a consumer drop a built-in (e.g. Sequence) without editing
    /// Core. A provider that cannot be instantiated or throws while listing is skipped (logged), never
    /// breaking Hub construction. Editor-only; main thread.
    /// </remarks>
    public static class MolcaHubWorkspaceRegistry
    {
        /// <summary>Reserved id of the anchored Settings home tab (never provider-supplied, never hidden).</summary>
        public const string SettingsId = "settings";

        private const string HiddenKey = "Molca.Hub.HiddenWorkspaces";

        /// <summary>Raised when workspace visibility is changed through <see cref="SetHidden"/>.</summary>
        public static event Action VisibilityChanged;

        /// <summary>
        /// Discovers all provider-contributed workspaces, applies hide config + availability, drops the
        /// reserved/duplicate/id-less ones, and returns them ordered by
        /// <see cref="MolcaHubWorkspaceItem.Order"/> then id.
        /// </summary>
        public static IReadOnlyList<MolcaHubWorkspaceItem> GetWorkspaces() =>
            ResolveItems(DiscoverRaw(), HiddenIds());

        /// <summary>
        /// Discovers all currently available non-Settings workspaces without applying the consumer hide list.
        /// Use this for settings UI that must be able to show and re-enable hidden tabs.
        /// </summary>
        /// <returns>The ordered, deduplicated set of configurable workspace tabs.</returns>
        public static IReadOnlyList<MolcaHubWorkspaceItem> GetConfigurableWorkspaces() =>
            ResolveItems(DiscoverRaw(), Array.Empty<string>());

        private static IEnumerable<MolcaHubWorkspaceItem> DiscoverRaw()
        {
            var items = new List<MolcaHubWorkspaceItem>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaHubWorkspaceProvider>())
            {
                if (type.IsAbstract) continue;

                MolcaHubWorkspaceProvider provider;
                try
                {
                    provider = (MolcaHubWorkspaceProvider)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Workspace provider '{type.FullName}' could not be instantiated (skipped): {ex.Message}");
                    continue;
                }

                try
                {
                    var contributed = provider.GetWorkspaces();
                    if (contributed != null)
                        items.AddRange(contributed.Where(i => i != null));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Workspace provider '{type.FullName}' threw while listing workspaces (skipped): {ex.Message}");
                }
            }
            return items;
        }

        /// <summary>
        /// Pure resolution step (filter + dedup + sort), exposed for testing. Drops items that are hidden,
        /// use the reserved <see cref="SettingsId"/>, are unavailable, or have an empty id; keeps the first
        /// of each duplicate id; orders by <see cref="MolcaHubWorkspaceItem.Order"/> then id ordinally.
        /// </summary>
        /// <param name="raw">The raw contributed items.</param>
        /// <param name="hiddenIds">Ids the consumer has hidden.</param>
        /// <returns>The ordered, filtered, deduplicated workspace set.</returns>
        internal static IReadOnlyList<MolcaHubWorkspaceItem> ResolveItems(
            IEnumerable<MolcaHubWorkspaceItem> raw, IReadOnlyCollection<string> hiddenIds)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hidden = new HashSet<string>(hiddenIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var result = new List<MolcaHubWorkspaceItem>();

            foreach (var item in raw ?? Enumerable.Empty<MolcaHubWorkspaceItem>())
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                if (item.Id == SettingsId) continue;           // reserved for the anchored Settings tab
                if (hidden.Contains(item.Id)) continue;        // consumer hid this tab
                if (!IsAvailable(item)) continue;
                if (!seen.Add(item.Id)) continue;              // first registration of a duplicate id wins
                result.Add(item);
            }

            result.Sort((a, b) =>
                a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id));
            return result;
        }

        private static bool IsAvailable(MolcaHubWorkspaceItem item)
        {
            if (item.IsAvailable == null) return true;
            try { return item.IsAvailable(); }
            catch { return false; }
        }

        /// <summary>The set of workspace ids the consumer has hidden in this project.</summary>
        public static IReadOnlyCollection<string> HiddenIds()
        {
            var raw = MolcaEditorPrefs.GetString(HiddenKey, string.Empty);
            return string.IsNullOrEmpty(raw)
                ? Array.Empty<string>()
                : raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        /// <summary>
        /// Shows or hides a workspace tab by id (persisted per project). The anchored
        /// <see cref="SettingsId"/> tab cannot be hidden.
        /// </summary>
        /// <param name="id">The workspace id to toggle.</param>
        /// <param name="hidden"><c>true</c> to hide the tab, <c>false</c> to show it.</param>
        public static void SetHidden(string id, bool hidden)
        {
            if (string.IsNullOrEmpty(id) || id == SettingsId) return;
            var set = new HashSet<string>(HiddenIds(), StringComparer.Ordinal);
            bool changed = hidden ? set.Add(id) : set.Remove(id);
            if (!changed) return;
            MolcaEditorPrefs.SetString(HiddenKey, string.Join(",", set));
            VisibilityChanged?.Invoke();
        }
    }
}
