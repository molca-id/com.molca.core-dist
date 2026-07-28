using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Hub
{
    /// <summary>
    /// Discovers <see cref="MolcaHubSettingsLeafProvider"/>s via <c>TypeCache</c> and resolves the ordered,
    /// deduplicated, availability-filtered set of contributed Settings-rail leaves the Hub renders.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/</c>. Mirrors
    /// <see cref="MolcaHubWorkspaceRegistry"/>'s resolution contract exactly, so there is one pattern to
    /// learn for both Hub seams: a provider that cannot be instantiated or throws while listing is skipped
    /// (logged), never breaking Hub construction. Editor-only; main thread.
    /// </remarks>
    public static class MolcaHubSettingsLeafRegistry
    {
        /// <summary>Rail category id for framework-level configuration (Project, Build &amp; Version, …).</summary>
        public const string Framework = "cat:framework";

        /// <summary>Rail category id for tooling configuration (Integrations, MCP, Network, …).</summary>
        public const string Tooling = "cat:tooling";

        /// <summary>Rail category id for the add-ons branch.</summary>
        public const string Addons = "cat:addons";

        /// <summary>
        /// Rail category id that collects leaves declaring an unknown or empty group. The category is created
        /// only when at least one leaf lands in it.
        /// </summary>
        public const string Extensions = "cat:extensions";

        /// <summary>Display label of the <see cref="Extensions"/> category.</summary>
        internal const string ExtensionsLabel = "Extensions";

        /// <summary>
        /// Node-id prefix applied to every contributed leaf. Rail node ids otherwise carry Core's own shapes
        /// (<c>MolcaHubSection</c> enum names, <c>cat:*</c>, <c>doc:*</c>), so prefixing guarantees a provider
        /// can never collide with a Core section name while leaving rail-node persistence unchanged.
        /// </summary>
        public const string NodePrefix = "ext:";

        /// <summary>Discovers all provider-contributed Settings-rail leaves, resolved and ordered.</summary>
        /// <returns>The ordered, deduplicated, available leaves.</returns>
        public static IReadOnlyList<MolcaHubSettingsLeafItem> GetLeaves() => ResolveItems(DiscoverRaw());

        /// <summary>The rail node id for a leaf id.</summary>
        /// <param name="id">The leaf's <see cref="MolcaHubSettingsLeafItem.Id"/>.</param>
        /// <returns>The namespaced node id, e.g. <c>ext:my-panel</c>.</returns>
        public static string NodeId(string id) => NodePrefix + id;

        /// <summary>
        /// The category a leaf is appended to: its declared group when that is one Core knows, otherwise
        /// <see cref="Extensions"/>.
        /// </summary>
        /// <param name="group">The leaf's declared group.</param>
        /// <returns>A rail category id.</returns>
        public static string ResolveCategory(string group)
        {
            if (string.IsNullOrWhiteSpace(group)) return Extensions;
            var trimmed = group.Trim();
            return trimmed == Framework || trimmed == Tooling || trimmed == Addons ? trimmed : Extensions;
        }

        private static IEnumerable<MolcaHubSettingsLeafItem> DiscoverRaw()
        {
            var items = new List<MolcaHubSettingsLeafItem>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaHubSettingsLeafProvider>())
            {
                if (type.IsAbstract) continue;

                MolcaHubSettingsLeafProvider provider;
                try
                {
                    provider = (MolcaHubSettingsLeafProvider)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Settings leaf provider '{type.FullName}' could not be instantiated (skipped): {ex.Message}");
                    continue;
                }

                try
                {
                    var contributed = provider.GetLeaves();
                    if (contributed != null)
                        items.AddRange(contributed.Where(i => i != null));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Settings leaf provider '{type.FullName}' threw while listing leaves (skipped): {ex.Message}");
                }
            }
            return items;
        }

        /// <summary>
        /// Pure resolution step (filter + dedup + sort), exposed for testing. Drops items with an empty id,
        /// no content factory, or a failing/throwing availability gate; keeps the first of each duplicate id;
        /// orders by resolved category, then <see cref="MolcaHubSettingsLeafItem.Order"/>, then id ordinally.
        /// </summary>
        /// <param name="raw">The raw contributed items.</param>
        /// <returns>The ordered, filtered, deduplicated leaf set.</returns>
        internal static IReadOnlyList<MolcaHubSettingsLeafItem> ResolveItems(IEnumerable<MolcaHubSettingsLeafItem> raw)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<MolcaHubSettingsLeafItem>();

            foreach (var item in raw ?? Enumerable.Empty<MolcaHubSettingsLeafItem>())
            {
                if (item == null || string.IsNullOrEmpty(item.Id)) continue;
                if (item.CreateContent == null) continue;   // a leaf that renders nothing is not a leaf
                if (!IsAvailable(item)) continue;
                if (!seen.Add(item.Id)) continue;           // first registration of a duplicate id wins
                result.Add(item);
            }

            result.Sort((a, b) =>
            {
                var categoryCompare = string.CompareOrdinal(ResolveCategory(a.Group), ResolveCategory(b.Group));
                if (categoryCompare != 0) return categoryCompare;
                return a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.CompareOrdinal(a.Id, b.Id);
            });
            return result;
        }

        private static bool IsAvailable(MolcaHubSettingsLeafItem item)
        {
            if (item.IsAvailable == null) return true;
            try { return item.IsAvailable(); }
            catch { return false; }
        }
    }
}
