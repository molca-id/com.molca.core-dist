using System.Collections.Generic;
using Molca.Networking.Configuration;
using Molca.Editor.Networking.Validation;

namespace Molca.Editor.Networking.Migration
{
    /// <summary>
    /// Audits the project's legacy networking state and reports it as validation findings the Hub,
    /// Doctor, and MCP can navigate to.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="NetworkCatalogValidator"/> on purpose. That validator is a pure function
    /// of a catalog, which is why it can run on an in-memory instance in a test and inside a build gate
    /// without touching the <c>AssetDatabase</c>. This audit reads the whole project, so it is a distinct
    /// entry point a caller opts into.
    /// <para>
    /// Findings carry the legacy asset as <see cref="NetworkValidationFinding.TargetObject"/>, so
    /// selecting one navigates to the artifact that needs attention — the request asset, the provider, or
    /// the <c>HttpModule</c> — rather than to the catalog.
    /// </para>
    /// </remarks>
    public static class LegacyCompatibilityAudit
    {
        // Finding codes are API — Doctor, MCP, and tests match on them. Add, never rename.

        /// <summary>The project has legacy networking configuration and no catalog.</summary>
        public const string CodeCatalogNotAdopted = "network.legacy.catalog-not-adopted";

        /// <summary>A base URL is set but no service is bound to its origin.</summary>
        public const string CodeBaseUrlNotBound = "network.legacy.base-url-not-bound";

        /// <summary>A request asset reaches a host no catalog service claims.</summary>
        public const string CodeUnclaimedHost = "network.legacy.unclaimed-host";

        /// <summary>A request asset opts into authentication while targeting a full URL.</summary>
        public const string CodeFullUrlWithCredential = "network.legacy.full-url-with-credential";

        /// <summary>A legacy request asset has no migrated endpoint.</summary>
        public const string CodeRequestAssetUnmigrated = "network.legacy.request-asset-unmigrated";

        /// <summary>A streaming provider's origin is not bound to any service.</summary>
        public const string CodeProviderNotBound = "network.legacy.provider-not-bound";

        /// <summary>
        /// Scans the project and reports its legacy networking state.
        /// </summary>
        /// <returns>
        /// A report whose <see cref="NetworkValidationReport.Catalog"/> is the existing catalog, or
        /// <c>null</c> when there is none. Never <c>null</c> itself.
        /// </returns>
        public static NetworkValidationReport Audit() => Audit(LegacyNetworkScanner.Scan());

        /// <summary>
        /// Reports the legacy state described by an existing scan.
        /// </summary>
        /// <param name="report">The scan to audit. <c>null</c> yields an empty report.</param>
        /// <returns>The findings, in deterministic order.</returns>
        /// <remarks>
        /// Takes the scan rather than performing one so the Hub can show a scan and its findings from a
        /// single pass, and so tests can audit a constructed report without a project fixture.
        /// </remarks>
        public static NetworkValidationReport Audit(LegacyNetworkScanReport report)
        {
            if (report == null)
                return new NetworkValidationReport(null, new List<NetworkValidationFinding>());

            var findings = new List<NetworkValidationFinding>();
            var catalog = report.ExistingCatalog;
            var plan = LegacyMigrationPlan.Compute(report);

            AuditAdoption(report, catalog, findings);
            AuditBaseUrl(report, catalog, findings);
            AuditRequestAssets(report, catalog, plan, findings);
            AuditProviders(report, catalog, findings);

            return new NetworkValidationReport(catalog, findings);
        }

        private static void AuditAdoption(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            List<NetworkValidationFinding> findings)
        {
            if (catalog != null || !report.HasWork)
                return;

            findings.Add(new NetworkValidationFinding(
                NetworkValidationSeverity.Warning,
                CodeCatalogNotAdopted,
                NetworkErrorCategory.Configuration,
                NetworkValidationEntityKind.Catalog,
                null,
                $"This project has legacy networking configuration ({report.Summarize()}) but no network " +
                "catalog, so requests cannot be scoped to a route and process-wide credentials still " +
                "travel to every host.",
                "Run the legacy networking migration to create a catalog alongside the existing assets. " +
                "Nothing is deleted or rewritten.",
                targetObject: report.Items.Count > 0 ? report.Items[0].Asset : null));
        }

        private static void AuditBaseUrl(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            List<NetworkValidationFinding> findings)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(report.BaseUrl))
                return;

            if (catalog.FindService(NetworkIds.LegacyDefaultServiceId) != null)
                return;

            var moduleItems = report.OfKind(LegacyNetworkItemKind.GlobalBaseUrl);

            findings.Add(new NetworkValidationFinding(
                NetworkValidationSeverity.Warning,
                CodeBaseUrlNotBound,
                NetworkErrorCategory.Configuration,
                NetworkValidationEntityKind.Service,
                NetworkIds.LegacyDefaultServiceId,
                $"HttpModule.BaseUrl is '{report.BaseUrl}', but no '{NetworkIds.LegacyDefaultServiceId}' " +
                "service is bound to it, so relative request URLs are not routable.",
                "Run the legacy networking migration, or bind a service to the base URL by hand.",
                targetObject: moduleItems.Count > 0 ? moduleItems[0].Asset : null));
        }

        private static void AuditRequestAssets(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            LegacyMigrationPlan plan,
            List<NetworkValidationFinding> findings)
        {
            // A skip recorded as "already migrated" is the successful case, so the unmigrated set is every
            // request asset the plan still has a step for.
            var stillPlanned = new HashSet<string>();
            foreach (var step in plan.Steps)
            {
                if (step.Kind == LegacyMigrationStepKind.CreateEndpoint && !string.IsNullOrEmpty(step.SourceGuid))
                    stillPlanned.Add(step.SourceGuid);
            }

            foreach (var item in report.OfKind(LegacyNetworkItemKind.RequestAsset))
            {
                if (item.IsAbsolute && item.DeclaresCredential)
                {
                    findings.Add(new NetworkValidationFinding(
                        NetworkValidationSeverity.Warning,
                        CodeFullUrlWithCredential,
                        NetworkErrorCategory.SecurityPolicy,
                        NetworkValidationEntityKind.Endpoint,
                        item.DisplayName,
                        $"'{item.DisplayName}' targets the full URL '{item.EffectiveUrl}' and declares a " +
                        "credential header, so a process-wide token reaches that host.",
                        $"Author '{item.Host}' as a service, then scope a credential profile to it — or " +
                        "remove the credential header if the host should not receive one.",
                        targetObject: item.Asset));
                }

                if (catalog != null && item.IsAbsolute && !HostIsBound(catalog, item.Host))
                {
                    findings.Add(new NetworkValidationFinding(
                        NetworkValidationSeverity.Info,
                        CodeUnclaimedHost,
                        NetworkErrorCategory.RouteResolution,
                        NetworkValidationEntityKind.Service,
                        item.Host,
                        $"'{item.DisplayName}' reaches '{item.Host}', which no catalog service binds. " +
                        "Credentials are withheld from it and the routed pipeline cannot execute it.",
                        $"Add a service bound to '{item.Host}'.",
                        targetObject: item.Asset));
                }

                if (catalog != null && !string.IsNullOrEmpty(item.AssetGuid) &&
                    stillPlanned.Contains(item.AssetGuid))
                {
                    findings.Add(new NetworkValidationFinding(
                        NetworkValidationSeverity.Info,
                        CodeRequestAssetUnmigrated,
                        NetworkErrorCategory.Configuration,
                        NetworkValidationEntityKind.Endpoint,
                        item.DisplayName,
                        $"'{item.DisplayName}' has no migrated endpoint yet. It keeps working on the legacy " +
                        "path; it simply is not addressable as a route.",
                        "Run the legacy networking migration to create its endpoint.",
                        targetObject: item.Asset));
                }
            }
        }

        private static void AuditProviders(
            LegacyNetworkScanReport report,
            NetworkCatalog catalog,
            List<NetworkValidationFinding> findings)
        {
            if (catalog == null)
                return;

            foreach (var item in report.Items)
            {
                switch (item.Kind)
                {
                    case LegacyNetworkItemKind.SseProvider:
                    case LegacyNetworkItemKind.WebSocketProvider:
                    case LegacyNetworkItemKind.SocketIoProvider:
                        break;
                    default:
                        continue;
                }

                if (!item.IsAbsolute || HostIsBound(catalog, item.Host))
                    continue;

                findings.Add(new NetworkValidationFinding(
                    NetworkValidationSeverity.Info,
                    CodeProviderNotBound,
                    NetworkErrorCategory.RouteResolution,
                    NetworkValidationEntityKind.Binding,
                    item.Host,
                    $"'{item.DisplayName}' streams from '{item.Host}', which no catalog service binds, so " +
                    "it cannot move onto a routed session.",
                    $"Add a service declaring {item.Protocol} and bind it to '{item.Host}'.",
                    targetObject: item.Asset));
            }
        }

        /// <summary>Whether any service binds an origin on <paramref name="host"/>.</summary>
        private static bool HostIsBound(NetworkCatalog catalog, string host)
        {
            if (catalog?.Services == null || string.IsNullOrEmpty(host))
                return false;

            foreach (var service in catalog.Services)
            {
                if (service == null) continue;

                foreach (string pattern in service.ResolveAllowedHosts())
                {
                    if (NetworkHostRule.Matches(pattern, host))
                        return true;
                }
            }
            return false;
        }
    }
}
