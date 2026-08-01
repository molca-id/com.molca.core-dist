using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.ReferenceSystem;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Mcp.Providers
{
    public partial class CoreMcpToolProvider
    {
        /// <summary>
        /// The <c>molca_references_audit</c> tool: a read-only projection of the shared reference audit
        /// snapshot — findings with their <c>REFnnn</c> codes, providers, reference sites, and coverage.
        /// </summary>
        /// <remarks>
        /// This is the same snapshot Molca Doctor, the build gate, Sequence validation and the Inspector
        /// consume, so an assistant investigating a reference problem sees exactly what the editor sees. The
        /// older <c>molca_refids</c> tool had its own reflection-based scan with its own rules, and reported
        /// "unresolved" for any id it did not find among live components — including ids that resolve
        /// perfectly well through the compatibility fallback, and excluding ambiguous ids that the runtime
        /// actually refuses.
        /// </remarks>
        private static McpToolDefinition CreateReferencesAuditTool() => new McpToolDefinition(
            name: "molca_references_audit",
            description: "Read-only reference audit: findings (REFnnn code, severity, title, summary, asset), "
                       + "provider and reference-site inventories, and scan coverage. Reports whether the "
                       + "result is Clean, Warnings, Errors or Incomplete — Incomplete means the scan could "
                       + "not see everything, so it is not a clean result. Modifies nothing.",
            inputSchemaJson:
                "{\"type\":\"object\",\"properties\":{" +
                "\"refresh\":{\"type\":\"boolean\"," +
                "\"description\":\"Force a fresh audit instead of reusing the cached snapshot (default true).\"}," +
                "\"scope\":{\"type\":\"string\",\"enum\":[\"openScenes\",\"project\"]," +
                "\"description\":\"openScenes (default) audits open scenes, configured prefabs and " +
                "ScriptableObjects. project additionally opens closed scenes for reading and restores the " +
                "editor's scene setup afterwards; it is skipped if any open scene has unsaved changes.\"}," +
                "\"minSeverity\":{\"type\":\"string\",\"enum\":[\"Info\",\"Warning\",\"Error\"]," +
                "\"description\":\"Only return findings at or above this severity (default Info).\"}," +
                "\"refId\":{\"type\":\"string\"," +
                "\"description\":\"Restrict providers, sites and findings to this Ref Id.\"}," +
                "\"includeInventory\":{\"type\":\"boolean\"," +
                "\"description\":\"Include the full provider and reference-site lists (default false; " +
                "counts are always returned).\"}," +
                "\"maxItems\":{\"type\":\"integer\"," +
                "\"description\":\"Cap on findings and on each inventory list (default 200).\"}}," +
                "\"additionalProperties\":false}",
            // Asynchronous so a project-wide audit yields the main thread instead of freezing the editor
            // for its duration — an assistant calling this must not look like a hang.
            executeAsync: ExecuteReferencesAuditAsync,
            mode: McpToolMode.Edit,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteReferencesAuditAsync(string argumentsJson)
        {
            var refresh = true;
            var wholeProject = false;
            var minSeverity = ReferenceFindingSeverity.Info;
            string refIdFilter = null;
            var includeInventory = false;
            var maxItems = 200;

            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                refresh = args["refresh"]?.Value<bool>() ?? true;
                wholeProject = string.Equals(args.Value<string>("scope"), "project", StringComparison.OrdinalIgnoreCase);
                if (args["minSeverity"] != null &&
                    Enum.TryParse<ReferenceFindingSeverity>(args.Value<string>("minSeverity"), out var parsed))
                    minSeverity = parsed;
                refIdFilter = args.Value<string>("refId");
                includeInventory = args["includeInventory"]?.Value<bool>() ?? false;
                maxItems = Math.Max(1, args["maxItems"]?.Value<int>() ?? 200);
            }
            catch
            {
                // Defaults: fresh audit of the open scenes, all severities, no inventory.
            }

            var scope = ReferenceAuditService.DefaultScope(mayOpenScenes: wholeProject);
            var snapshot = refresh
                ? await ReferenceAuditService.RefreshAsync(scope)
                : await ReferenceAuditService.GetOrRunAsync(scope);

            var findings = snapshot.Findings.Where(f => f.Severity >= minSeverity).ToList();
            if (!string.IsNullOrEmpty(refIdFilter))
            {
                // Filtering by id needs the site behind each finding, since the id lives on the site rather
                // than on the finding itself.
                var matchingSiteKeys = new HashSet<string>(
                    snapshot.Sites
                        .Where(s => string.Equals(s.StoredRefId, refIdFilter, StringComparison.Ordinal))
                        .Select(s => s.SiteKey),
                    StringComparer.Ordinal);

                var matchingProviderKeys = new HashSet<string>(
                    snapshot.Providers
                        .Where(p => string.Equals(p.RefId, refIdFilter, StringComparison.Ordinal))
                        .Select(p => p.ProviderKey),
                    StringComparer.Ordinal);

                findings = findings
                    .Where(f => matchingSiteKeys.Contains(f.SourceSiteKey)
                             || f.CandidateProviderKeys.Any(matchingProviderKeys.Contains))
                    .ToList();
            }

            var result = new JObject
            {
                ["state"] = snapshot.State.ToString(),
                ["isStale"] = ReferenceAuditService.IsStale,
                ["staleReason"] = ReferenceAuditService.StaleReason,
                ["revision"] = snapshot.Revision,
                ["durationMs"] = (long)snapshot.Duration.TotalMilliseconds,
                ["coverageComplete"] = snapshot.Coverage.IsComplete,
                ["coverage"] = Coverage(snapshot),
                ["providerCount"] = snapshot.Providers.Count,
                ["siteCount"] = snapshot.Sites.Count,
                ["errorCount"] = snapshot.Errors.Count,
                ["warningCount"] = snapshot.Warnings.Count,
                ["findingCount"] = findings.Count,
                ["findings"] = Findings(findings.Take(maxItems)),
            };

            if (findings.Count > maxItems)
                result["findingsTruncated"] = findings.Count - maxItems;

            if (includeInventory)
            {
                var providers = Filtered(snapshot.Providers, p => p.RefId, refIdFilter);
                var sites = Filtered(snapshot.Sites, s => s.StoredRefId, refIdFilter);
                result["providers"] = Providers(providers.Take(maxItems));
                result["sites"] = Sites(snapshot, sites.Take(maxItems));
            }

            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>
        /// The <c>molca_refids</c> tool: deprecated adapter kept so existing callers keep working.
        /// </summary>
        /// <remarks>
        /// Emits the historical field names, now derived from the shared audit snapshot rather than from a
        /// separate reflection scan, so it can no longer disagree with the rest of the tooling. Prefer
        /// <c>molca_references_audit</c>, which reports finding codes, coverage, and the outcome of each
        /// reference rather than a flat "unresolved" list.
        /// </remarks>
        private static McpToolDefinition CreateRefIdsTool() => new McpToolDefinition(
            name: "molca_refids",
            description: "DEPRECATED — use molca_references_audit. Lists Ref Ids provided by IReferenceable "
                       + "components in the loaded scene(s) plus reference fields that do not resolve, "
                       + "derived from the shared reference audit.",
            inputSchemaJson: "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",
            executeAsync: ExecuteRefIdsAsync,
            mode: McpToolMode.Any,
            kind: McpToolKind.ReadOnly);

        private static async Awaitable<string> ExecuteRefIdsAsync(string argumentsJson)
        {
            var snapshot = await ReferenceAuditService.RefreshAsync(ReferenceAuditService.DefaultScope());

            var refIds = new JArray();
            var duplicates = new JArray();
            var empties = new JArray();
            var seenExactKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var provider in snapshot.Providers.Where(p => p.IsRuntimeResolvable))
            {
                if (string.IsNullOrEmpty(provider.RefId))
                {
                    empties.Add(provider.DisplayName);
                    continue;
                }

                // Duplicates are reported on the exact (RefType, RefId) key, matching the runtime registry.
                // The old tool keyed on the id alone and so flagged legal same-id/different-type providers.
                if (!seenExactKeys.Add(provider.RefType + "|" + provider.RefId))
                    duplicates.Add(provider.RefId);

                refIds.Add(new JObject
                {
                    ["refId"] = provider.RefId,
                    ["refType"] = provider.RefType,
                    ["gameObject"] = provider.DisplayName,
                });
            }

            var unresolved = new JArray();
            foreach (var resolution in snapshot.Resolutions.Where(r => !r.IsSuccess && r.Site.IsAssigned))
            {
                unresolved.Add(new JObject
                {
                    ["refId"] = resolution.Site.StoredRefId,
                    ["refType"] = resolution.Site.StoredRefType,
                    ["referencedBy"] = resolution.Site.OwnerLocator.TypeName,
                    ["gameObject"] = resolution.Site.OwnerLocator.ObjectPath,
                    ["outcome"] = resolution.Outcome.ToString(),
                });
            }

            var result = new JObject
            {
                ["deprecated"] = "Use molca_references_audit; it reports finding codes, coverage and per-reference outcomes.",
                ["registeredCount"] = refIds.Count,
                ["refIds"] = refIds,
                ["duplicateRefIds"] = duplicates,
                ["componentsWithEmptyRefId"] = empties,
                ["unresolvedReferences"] = unresolved,
                ["scanErrors"] = snapshot.Findings.Count(f => f.Code == ReferenceFindingCode.AssetScanFailed),
                ["coverageComplete"] = snapshot.Coverage.IsComplete,
            };
            return result.ToString(Newtonsoft.Json.Formatting.None);
        }

        #region Projection helpers

        private static JArray Coverage(ReferenceAuditSnapshot snapshot)
        {
            var array = new JArray();
            foreach (var entry in snapshot.Coverage.Entries)
            {
                array.Add(new JObject
                {
                    ["category"] = entry.Category,
                    ["status"] = entry.Status.ToString(),
                    ["count"] = entry.Count,
                    ["required"] = entry.IsRequired,
                    ["reason"] = entry.Reason,
                });
            }
            return array;
        }

        private static JArray Findings(IEnumerable<ReferenceFinding> findings)
        {
            var array = new JArray();
            foreach (var finding in findings)
            {
                array.Add(new JObject
                {
                    ["code"] = finding.CodeString,
                    ["severity"] = finding.Severity.ToString(),
                    ["title"] = finding.Title,
                    ["summary"] = finding.Summary,
                    ["asset"] = finding.AssetPath,
                    ["siteKey"] = finding.SourceSiteKey,
                    ["outcome"] = finding.Outcome?.ToString(),
                });
            }
            return array;
        }

        private static JArray Providers(IEnumerable<ReferenceProviderRecord> providers)
        {
            var array = new JArray();
            foreach (var provider in providers)
            {
                array.Add(new JObject
                {
                    ["providerKey"] = provider.ProviderKey,
                    ["refId"] = provider.RefId,
                    ["refType"] = provider.RefType,
                    ["displayName"] = provider.DisplayName,
                    ["runtimeType"] = provider.RuntimeTypeName,
                    ["kind"] = provider.Kind.ToString(),
                    ["runtimeResolvable"] = provider.IsRuntimeResolvable,
                    ["asset"] = provider.Locator.AssetPath,
                    ["object"] = provider.Locator.ObjectPath,
                    ["readOnly"] = provider.IsReadOnly,
                });
            }
            return array;
        }

        private static JArray Sites(ReferenceAuditSnapshot snapshot, IEnumerable<ReferenceSiteRecord> sites)
        {
            var array = new JArray();
            foreach (var site in sites)
            {
                array.Add(new JObject
                {
                    ["siteKey"] = site.SiteKey,
                    ["asset"] = site.OwnerLocator.AssetPath,
                    ["owner"] = site.OwnerLocator.ObjectPath,
                    ["ownerType"] = site.OwnerLocator.TypeName,
                    ["property"] = site.PropertyPath,
                    ["refId"] = site.StoredRefId,
                    ["refType"] = site.StoredRefType,
                    ["expectedType"] = site.ExpectedRuntimeTypeName,
                    ["sourceKind"] = site.SourceKind.ToString(),
                    ["outcome"] = snapshot.FindResolution(site.SiteKey)?.Outcome.ToString(),
                });
            }
            return array;
        }

        private static IEnumerable<T> Filtered<T>(
            IEnumerable<T> source, Func<T, string> refIdOf, string refIdFilter) =>
            string.IsNullOrEmpty(refIdFilter)
                ? source
                : source.Where(item => string.Equals(refIdOf(item), refIdFilter, StringComparison.Ordinal));

        #endregion
    }
}
