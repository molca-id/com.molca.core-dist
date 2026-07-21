using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Hub.Docs
{
    /// <summary>
    /// Discovers <see cref="MolcaDocsProvider"/>s via <c>TypeCache</c> and resolves the ordered,
    /// de-duplicated, category-grouped set of reference docs the Hub browses.
    /// </summary>
    /// <remarks>
    /// Placement: <c>Packages/com.molca.core/Editor/Hub/Docs/</c>. Mirrors
    /// <see cref="MolcaHubWorkspaceRegistry"/>: a provider that cannot be instantiated or throws while listing
    /// is logged and skipped, never breaking Hub construction. De-duplicates by <see cref="MolcaDocEntry.Id"/>
    /// (first wins). Editor-only; main thread. The resolution step (<see cref="Resolve"/>) is pure and exposed
    /// for testing.
    /// </remarks>
    public static class MolcaDocsRegistry
    {
        /// <summary>Returns every contributed doc, de-duplicated by id and sorted by category/order/title.</summary>
        public static IReadOnlyList<MolcaDocEntry> GetDocs() => Resolve(DiscoverRaw());

        /// <summary>Returns the docs grouped into ordered categories for the rail tree.</summary>
        public static IReadOnlyList<MolcaDocCategory> GetTree() => BuildTree(GetDocs());

        /// <summary>Returns the docs grouped into ordered products, each holding its own category tree.</summary>
        public static IReadOnlyList<MolcaDocProduct> GetProducts() => BuildProducts(GetDocs());

        /// <summary>Grouping key for docs that carry no owning package (project/consumer docs).</summary>
        public const string ProjectProductKey = "project";

        /// <summary>Finds a doc by its <see cref="MolcaDocEntry.Id"/> (case-insensitive), or <c>null</c>.</summary>
        public static MolcaDocEntry FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var doc in GetDocs())
                if (string.Equals(doc.Id, id, StringComparison.OrdinalIgnoreCase))
                    return doc;
            return null;
        }

        private static IEnumerable<MolcaDocEntry> DiscoverRaw()
        {
            var docs = new List<MolcaDocEntry>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaDocsProvider>())
            {
                if (type.IsAbstract) continue;

                MolcaDocsProvider provider;
                try
                {
                    provider = (MolcaDocsProvider)Activator.CreateInstance(type);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Docs provider '{type.FullName}' could not be instantiated (skipped): {ex.Message}");
                    continue;
                }

                try
                {
                    var contributed = provider.GetDocs();
                    if (contributed != null)
                        docs.AddRange(contributed.Where(d => d != null && !string.IsNullOrEmpty(d.Id)));
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Hub] Docs provider '{type.FullName}' threw while listing docs (skipped): {ex.Message}");
                }
            }
            return docs;
        }

        /// <summary>
        /// Pure resolution step: drops id-less/duplicate entries (first id wins) and sorts by category,
        /// then order, then title. Exposed for testing.
        /// </summary>
        public static IReadOnlyList<MolcaDocEntry> Resolve(IEnumerable<MolcaDocEntry> raw)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<MolcaDocEntry>();
            foreach (var doc in raw ?? Enumerable.Empty<MolcaDocEntry>())
            {
                if (doc == null || string.IsNullOrEmpty(doc.Id) || !seen.Add(doc.Id)) continue;
                result.Add(doc);
            }

            result.Sort((a, b) =>
            {
                var c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        /// <summary>
        /// Groups resolved docs into categories ordered by their lowest doc order (then name), each holding
        /// its docs in resolved order. Exposed for testing.
        /// </summary>
        public static IReadOnlyList<MolcaDocCategory> BuildTree(IReadOnlyList<MolcaDocEntry> docs)
        {
            var categories = new List<MolcaDocCategory>();
            if (docs == null) return categories;

            foreach (var group in docs.GroupBy(d => d.Category, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group.ToList();
                var minOrder = ordered.Count > 0 ? ordered.Min(d => d.Order) : 0;
                categories.Add(new MolcaDocCategory(group.Key, minOrder, ordered));
            }

            categories.Sort((a, b) =>
            {
                if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return categories;
        }

        /// <summary>
        /// Groups resolved docs into products (by owning package, un-owned docs under
        /// <see cref="ProjectProductKey"/>), each with its own category tree. Products are ordered Core → SDK →
        /// other <c>com.molca.*</c> packages → forks/other → project, then by label. A product's label is the
        /// first non-blank <see cref="MolcaDocEntry.Product"/> in the group, falling back to a name derived
        /// from the key. Exposed for testing.
        /// </summary>
        /// <param name="docs">The resolved docs (typically from <see cref="Resolve"/>), already sorted.</param>
        /// <returns>The ordered product groups.</returns>
        public static IReadOnlyList<MolcaDocProduct> BuildProducts(IReadOnlyList<MolcaDocEntry> docs)
        {
            var products = new List<MolcaDocProduct>();
            if (docs == null) return products;

            foreach (var group in docs.GroupBy(ProductKey, StringComparer.OrdinalIgnoreCase))
            {
                var groupDocs = group.ToList();
                var label = groupDocs
                                .Select(d => d.Product)
                                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                            ?? DeriveProductLabel(group.Key);
                products.Add(new MolcaDocProduct(group.Key, label, ProductOrder(group.Key), BuildTree(groupDocs)));
            }

            products.Sort((a, b) =>
            {
                if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            return products;
        }

        /// <summary>The product grouping key for a doc: its owning package, or <see cref="ProjectProductKey"/>.</summary>
        private static string ProductKey(MolcaDocEntry doc) =>
            string.IsNullOrEmpty(doc.OwnerPackage) ? ProjectProductKey : doc.OwnerPackage;

        /// <summary>Product sort rank: Core, then SDK, then other Molca packages, then forks/other, then project.</summary>
        private static int ProductOrder(string key)
        {
            if (string.Equals(key, "com.molca.core", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(key, "com.molca.sdk", StringComparison.OrdinalIgnoreCase)) return 10;
            if (key != null && key.StartsWith("com.molca.", StringComparison.OrdinalIgnoreCase)) return 20;
            if (string.Equals(key, ProjectProductKey, StringComparison.OrdinalIgnoreCase)) return 40;
            return 30;
        }

        /// <summary>
        /// Fallback product label when no entry carries an explicit <see cref="MolcaDocEntry.Product"/>:
        /// the project key becomes "Project"; a <c>com.molca.*</c> key is prettified (e.g.
        /// <c>com.molca.sdk.vr</c> → "Molca Sdk Vr"); anything else is shown verbatim.
        /// </summary>
        private static string DeriveProductLabel(string key)
        {
            if (string.IsNullOrEmpty(key) || string.Equals(key, ProjectProductKey, StringComparison.OrdinalIgnoreCase))
                return "Project";

            const string molca = "com.molca.";
            if (!key.StartsWith(molca, StringComparison.OrdinalIgnoreCase))
                return key;

            var stem = key.Substring(molca.Length).Replace('.', ' ').Replace('-', ' ');
            var words = stem.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i].Substring(1);
            return "Molca " + string.Join(" ", words);
        }
    }
}
