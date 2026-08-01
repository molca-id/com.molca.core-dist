using System;
using System.Collections.Generic;
using System.Text;
using Molca.Networking.Configuration;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>
    /// Everything a legacy scan found: the global base URL, each legacy artifact, and the catalog that
    /// already exists, if any.
    /// </summary>
    /// <remarks>
    /// Deterministic — items come out ordered by kind then asset path, so a re-scan of an unchanged
    /// project produces an identical report and the dry-run text can be diffed or asserted against.
    /// <para>
    /// Read-only. The report is the input to <see cref="LegacyMigrationPlan"/>; nothing here has written
    /// to the project.
    /// </para>
    /// </remarks>
    public sealed class LegacyNetworkScanReport
    {
        private readonly List<LegacyNetworkItem> _items;

        /// <summary>The authored <c>HttpModule.BaseUrl</c>, or empty when none is set.</summary>
        public string BaseUrl { get; }

        /// <summary>Whether the project has an <c>HttpModule</c> at all.</summary>
        public bool HasHttpModule { get; }

        /// <summary>GUID of the <c>HttpModule</c> asset, or empty. Recorded as the migration's source.</summary>
        public string HttpModuleGuid { get; }

        /// <summary>The catalog already in the project, or <c>null</c> when there is none yet.</summary>
        public NetworkCatalog ExistingCatalog { get; }

        /// <summary>Every legacy artifact found, in deterministic order.</summary>
        public IReadOnlyList<LegacyNetworkItem> Items => _items;

        /// <summary>Whether there is anything to migrate.</summary>
        public bool HasWork => _items.Count > 0 || !string.IsNullOrEmpty(BaseUrl);

        /// <summary>
        /// Distinct absolute hosts the project reaches, lowercased and ordered. Excludes the base-URL
        /// host, which becomes the legacy default service rather than a host-derived one.
        /// </summary>
        public IReadOnlyList<string> ForeignHosts { get; }

        /// <summary>Creates a report.</summary>
        /// <param name="baseUrl">The authored global base URL, or <c>null</c>.</param>
        /// <param name="hasHttpModule">Whether an <c>HttpModule</c> was found.</param>
        /// <param name="httpModuleGuid">GUID of the <c>HttpModule</c> asset, or <c>null</c>.</param>
        /// <param name="existingCatalog">The catalog already present, or <c>null</c>.</param>
        /// <param name="items">The artifacts found; copied and ordered defensively.</param>
        public LegacyNetworkScanReport(
            string baseUrl,
            bool hasHttpModule,
            string httpModuleGuid,
            NetworkCatalog existingCatalog,
            IEnumerable<LegacyNetworkItem> items)
        {
            BaseUrl = baseUrl ?? string.Empty;
            HasHttpModule = hasHttpModule;
            HttpModuleGuid = httpModuleGuid ?? string.Empty;
            ExistingCatalog = existingCatalog;

            _items = items == null
                ? new List<LegacyNetworkItem>()
                : new List<LegacyNetworkItem>(items);

            _items.Sort((left, right) =>
            {
                int byKind = ((int)left.Kind).CompareTo((int)right.Kind);
                if (byKind != 0) return byKind;
                int byName = string.Compare(left.DisplayName, right.DisplayName, StringComparison.Ordinal);
                return byName != 0
                    ? byName
                    : string.Compare(left.AssetGuid, right.AssetGuid, StringComparison.Ordinal);
            });

            ForeignHosts = CollectForeignHosts(_items, BaseUrl);
        }

        private static List<string> CollectForeignHosts(List<LegacyNetworkItem> items, string baseUrl)
        {
            string baseHost = Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri baseUri)
                ? baseUri.Host.ToLowerInvariant()
                : string.Empty;

            var hosts = new List<string>();
            foreach (var item in items)
            {
                if (!item.IsAbsolute) continue;
                if (string.Equals(item.Host, baseHost, StringComparison.Ordinal)) continue;
                if (!hosts.Contains(item.Host)) hosts.Add(item.Host);
            }

            hosts.Sort(StringComparer.Ordinal);
            return hosts;
        }

        /// <summary>Items of one kind, in report order.</summary>
        /// <param name="kind">The kind to filter to.</param>
        public List<LegacyNetworkItem> OfKind(LegacyNetworkItemKind kind)
        {
            var result = new List<LegacyNetworkItem>();
            foreach (var item in _items)
            {
                if (item.Kind == kind) result.Add(item);
            }
            return result;
        }

        /// <summary>Items whose URL is absolute and whose host is not the base-URL host.</summary>
        public List<LegacyNetworkItem> ForeignHostItems()
        {
            var foreign = new HashSet<string>(ForeignHosts, StringComparer.Ordinal);

            var result = new List<LegacyNetworkItem>();
            foreach (var item in _items)
            {
                if (item.IsAbsolute && foreign.Contains(item.Host))
                    result.Add(item);
            }
            return result;
        }

        /// <summary>A one-line summary, e.g. <c>base URL set, 4 request assets, 2 providers, 1 foreign host</c>.</summary>
        public string Summarize()
        {
            int requests = OfKind(LegacyNetworkItemKind.RequestAsset).Count;
            int providers = _items.Count - requests - OfKind(LegacyNetworkItemKind.GlobalBaseUrl).Count;

            return $"{(string.IsNullOrEmpty(BaseUrl) ? "no base URL" : "base URL set")}, " +
                   $"{requests} request asset{(requests == 1 ? "" : "s")}, " +
                   $"{providers} provider{(providers == 1 ? "" : "s")}, " +
                   $"{ForeignHosts.Count} foreign host{(ForeignHosts.Count == 1 ? "" : "s")}";
        }

        /// <summary>
        /// The human-readable dry-run text: what exists today, without proposing changes.
        /// </summary>
        /// <returns>A multi-line description. Deterministic for a given project state.</returns>
        public string Describe()
        {
            var text = new StringBuilder();
            text.AppendLine("Legacy networking scan");
            text.AppendLine("======================");
            text.AppendLine(Summarize());
            text.AppendLine();

            text.AppendLine(HasHttpModule
                ? $"HttpModule base URL: {(string.IsNullOrEmpty(BaseUrl) ? "<empty>" : BaseUrl)}"
                : "HttpModule: none in this project.");

            text.AppendLine(ExistingCatalog != null
                ? $"Existing catalog: {ExistingCatalog.name} ({ExistingCatalog.Environments.Count} environment(s), " +
                  $"{ExistingCatalog.Services.Count} service(s))"
                : "Existing catalog: none.");

            if (_items.Count == 0)
            {
                text.AppendLine();
                text.AppendLine("No legacy request assets or data providers were found.");
                return text.ToString();
            }

            text.AppendLine();
            text.AppendLine($"Artifacts ({_items.Count}):");
            foreach (var item in _items)
            {
                text.AppendLine($"  - {item}");
                foreach (string note in item.Notes)
                    text.AppendLine($"      note: {note}");
            }

            if (ForeignHosts.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Hosts other than the base URL's:");
                foreach (string host in ForeignHosts)
                    text.AppendLine($"  - {host}");
            }

            return text.ToString();
        }
    }
}
