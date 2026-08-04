using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem.Hub
{
    /// <summary>Which table a <see cref="ReferenceHubRow"/> belongs to.</summary>
    public enum ReferenceHubRowKind
    {
        /// <summary>A finding, shown in the Issues view.</summary>
        Issue = 0,

        /// <summary>A reference site and its resolution state, shown in the References view.</summary>
        Site = 1,

        /// <summary>A provider and its inbound count, shown in the Providers view.</summary>
        Provider = 2,
    }

    /// <summary>Whether, and how, the repair system can act on a row.</summary>
    public enum ReferenceHubRepairAvailability
    {
        /// <summary>Nothing to repair, or nothing the repair system will touch.</summary>
        None = 0,

        /// <summary>Covered by <see cref="Repair.ReferenceRepairPlanner.PlanSafeRepairs"/>.</summary>
        Automatic = 1,

        /// <summary>
        /// Repairable only once a human says which target was meant; see
        /// <see cref="Repair.ReferenceRepairPlanner.DescribeChoices"/>.
        /// </summary>
        RequiresChoice = 2,
    }

    /// <summary>
    /// One table row in the References workspace: a flat projection of a snapshot record, carrying every
    /// column the Issues/References/Providers tables display and everything the filters test.
    /// </summary>
    /// <remarks>
    /// Rows are derived, immutable, and hold no live <see cref="UnityEngine.Object"/> — the workspace
    /// re-projects them from <see cref="ReferenceAuditSnapshot"/> rather than caching Unity state, so a
    /// scene close or a domain reload cannot leave the table showing objects that no longer exist.
    /// Navigation goes back through <see cref="ReferenceObjectLocator.TryResolve"/> at click time.
    ///
    /// The projection is deliberately pure so the table's contents and the filters over them are testable
    /// without a panel: the earlier reference UIs could only be verified by looking at them.
    /// </remarks>
    public sealed class ReferenceHubRow
    {
        /// <summary>Which table this row belongs to.</summary>
        public ReferenceHubRowKind Kind { get; }

        /// <summary>Stable identity of the row within its table, used to carry selection across a rescan.</summary>
        public string Key { get; }

        /// <summary>Severity to render. Provider and site rows without a finding are <c>Info</c>.</summary>
        public ReferenceFindingSeverity Severity { get; }

        /// <summary>The <c>REFnnn</c> code, or empty for a row that is not a finding.</summary>
        public string Code { get; }

        /// <summary>Short row title.</summary>
        public string Title { get; }

        /// <summary>Full explanation, shown in the detail panel and as the row tooltip.</summary>
        public string Summary { get; }

        /// <summary>Asset (scene, prefab or ScriptableObject) the row is anchored to. May be empty.</summary>
        public string AssetPath { get; }

        /// <summary>Object path of the owning GameObject or asset.</summary>
        public string Owner { get; }

        /// <summary>Serialized property path of the reference field. Empty for a provider row.</summary>
        public string PropertyPath { get; }

        /// <summary>The stored <c>refType:refId</c> pair, or the provider's own identity.</summary>
        public string StoredTarget { get; }

        /// <summary>
        /// The Ref Type half of <see cref="StoredTarget"/>, unformatted.
        /// </summary>
        /// <remarks>
        /// Carried as its own field rather than parsed back out of the display string. Grouping the Targets
        /// tree by type, and offering a rename, both need the value the data holds — not the rendering of it,
        /// which substitutes "(no type)" for an empty one and would silently create a Ref Type by that name.
        /// </remarks>
        public string StoredRefType { get; }

        /// <summary>The Ref Id half of <see cref="StoredTarget"/>, unformatted. Empty when unset.</summary>
        public string StoredRefId { get; }

        /// <summary>
        /// <see cref="ReferenceObjectLocator.Key"/> of the object this row is anchored to.
        /// </summary>
        /// <remarks>
        /// What selection-scoped planning filters on: a mutation names its target by locator key, so a
        /// selection expressed in the same terms narrows a plan exactly rather than by asset or by name.
        /// </remarks>
        public string OwnerKey { get; }

        /// <summary>
        /// The reference's effective scope. Every v1 reference is <c>Global</c>: v1 identity has no scope
        /// component at all.
        /// </summary>
        /// <remarks>
        /// The column exists before scoped references do on purpose. A project's references are global today
        /// whether or not anyone says so, and that is exactly the fact that makes two placements of one prefab
        /// collide. Naming it now means the day scoped references land, the difference is visible in the same
        /// column rather than in a new one nobody looks at.
        /// </remarks>
        public string Scope { get; }

        /// <summary>Which asset category owns the row.</summary>
        public string SourceKind { get; }

        /// <summary>The type the field promises, or the provider's concrete type. May be empty.</summary>
        public string ExpectedType { get; }

        /// <summary>Human-readable resolution state, e.g. <c>ResolvedExact</c>.</summary>
        public string ResolutionState { get; }

        /// <summary>Whether repair can act on this row.</summary>
        public ReferenceHubRepairAvailability Repair { get; }

        /// <summary>True when the row's asset cannot be written (a package, or a read-only asset).</summary>
        public bool IsReadOnly { get; }

        /// <summary>
        /// True when the site stores a Ref Id, i.e. someone asked for a target and expects to get one.
        /// Always true for a provider row.
        /// </summary>
        public bool IsAssigned { get; }

        /// <summary>
        /// True when the reference stores no Ref Type and therefore depends on the ID-only compatibility
        /// fallback — the path that fails outright as soon as two objects share the id.
        /// </summary>
        public bool IsLegacyFallback { get; }

        /// <summary>Number of references that resolve to this provider. Zero for non-provider rows.</summary>
        public int InboundCount { get; }

        /// <summary>
        /// Number of sites that store this provider's Ref Id, resolving to it or not. Larger than
        /// <see cref="InboundCount"/> exactly when something claims the id and does not get it.
        /// </summary>
        public int ClaimingCount { get; }

        /// <summary><see cref="ReferenceSiteRecord.SiteKey"/> this row concerns, or empty.</summary>
        public string SiteKey { get; }

        /// <summary><see cref="ReferenceProviderRecord.ProviderKey"/> this row concerns, or empty.</summary>
        public string ProviderKey { get; }

        /// <summary>Provider keys offered as candidates for this row. Never null.</summary>
        public IReadOnlyList<string> CandidateProviderKeys { get; }

        internal ReferenceHubRow(
            ReferenceHubRowKind kind,
            string key,
            ReferenceFindingSeverity severity,
            string code,
            string title,
            string summary,
            string assetPath,
            string owner,
            string propertyPath,
            string storedTarget,
            string scope,
            string sourceKind,
            string expectedType,
            string resolutionState,
            ReferenceHubRepairAvailability repair,
            bool isReadOnly,
            bool isAssigned,
            bool isLegacyFallback,
            int inboundCount = 0,
            int claimingCount = 0,
            string siteKey = null,
            string providerKey = null,
            IReadOnlyList<string> candidateProviderKeys = null,
            string storedRefType = null,
            string storedRefId = null,
            string ownerKey = null)
        {
            StoredRefType = storedRefType ?? string.Empty;
            StoredRefId = storedRefId ?? string.Empty;
            OwnerKey = ownerKey ?? string.Empty;
            Kind = kind;
            Key = key ?? string.Empty;
            Severity = severity;
            Code = code ?? string.Empty;
            Title = title ?? string.Empty;
            Summary = summary ?? string.Empty;
            AssetPath = assetPath ?? string.Empty;
            Owner = owner ?? string.Empty;
            PropertyPath = propertyPath ?? string.Empty;
            StoredTarget = storedTarget ?? string.Empty;
            Scope = scope ?? string.Empty;
            SourceKind = sourceKind ?? string.Empty;
            ExpectedType = expectedType ?? string.Empty;
            ResolutionState = resolutionState ?? string.Empty;
            Repair = repair;
            IsReadOnly = isReadOnly;
            IsAssigned = isAssigned;
            IsLegacyFallback = isLegacyFallback;
            InboundCount = inboundCount;
            ClaimingCount = claimingCount;
            SiteKey = siteKey ?? string.Empty;
            ProviderKey = providerKey ?? string.Empty;
            CandidateProviderKeys = candidateProviderKeys ?? Array.Empty<string>();
        }

        /// <summary>
        /// Everything the text query searches, joined. Built once per row rather than per keystroke.
        /// </summary>
        internal string SearchText { get; private set; }

        private void BuildSearchText() =>
            SearchText = string.Join(" ",
                new[] { Code, Title, AssetPath, Owner, PropertyPath, StoredTarget, ExpectedType, ResolutionState }
                    .Where(s => !string.IsNullOrEmpty(s)));

        /// <summary>
        /// Every scope v1 references can have. Named so the Scope column reads the same before and after
        /// scoped references exist.
        /// </summary>
        internal const string GlobalScope = "Global";

        /// <summary>
        /// Projects a snapshot into the three row tables.
        /// </summary>
        /// <param name="snapshot">The audit to project. Null yields empty tables.</param>
        /// <param name="repairAvailability">
        /// Repair availability keyed by <see cref="ReferenceFinding"/> identity; see
        /// <see cref="ReferenceHubRepairIndex"/>. Null means every row reports <c>None</c>.
        /// </param>
        /// <returns>The issue, site and provider rows, each in the snapshot's own deterministic order.</returns>
        public static ReferenceHubTables Project(
            ReferenceAuditSnapshot snapshot,
            ReferenceHubRepairIndex repairAvailability = null)
        {
            if (snapshot == null)
                return ReferenceHubTables.Empty;

            var issues = new List<ReferenceHubRow>();
            var sites = new List<ReferenceHubRow>();
            var providers = new List<ReferenceHubRow>();

            // Inbound counting walks the resolutions once rather than once per provider: a project with
            // thousands of each would otherwise make building the Providers table quadratic.
            var inbound = new Dictionary<string, int>(StringComparer.Ordinal);
            var claiming = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var resolution in snapshot.Resolutions)
            {
                var resolved = resolution.Resolved;
                if (resolved != null)
                    inbound[resolved.ProviderKey] = inbound.TryGetValue(resolved.ProviderKey, out var n) ? n + 1 : 1;

                var storedId = resolution.Site.StoredRefId;
                if (!string.IsNullOrEmpty(storedId))
                    claiming[storedId] = claiming.TryGetValue(storedId, out var c) ? c + 1 : 1;
            }

            foreach (var finding in snapshot.Findings)
                issues.Add(FromFinding(snapshot, finding, repairAvailability));

            foreach (var resolution in snapshot.Resolutions)
                sites.Add(FromSite(resolution));

            foreach (var provider in snapshot.Providers)
            {
                providers.Add(FromProvider(
                    provider,
                    inbound.TryGetValue(provider.ProviderKey, out var i) ? i : 0,
                    string.IsNullOrEmpty(provider.RefId) ? 0
                        : claiming.TryGetValue(provider.RefId, out var c) ? c : 0));
            }

            return new ReferenceHubTables(issues, sites, providers);
        }

        private static ReferenceHubRow FromFinding(
            ReferenceAuditSnapshot snapshot, ReferenceFinding finding, ReferenceHubRepairIndex repair)
        {
            var resolution = snapshot.FindResolution(finding.SourceSiteKey);
            var site = resolution?.Site;

            // A provider-only finding (a missing Ref Id, say) has no site, so its location comes from the
            // candidate provider instead. Reporting "<none>" for those would hide the one thing the user
            // needs in order to act.
            var provider = site == null
                ? finding.CandidateProviderKeys.Select(snapshot.FindProvider).FirstOrDefault(p => p != null)
                : null;

            var row = new ReferenceHubRow(
                ReferenceHubRowKind.Issue,
                key: $"{finding.CodeString}|{finding.SourceSiteKey}|{finding.AssetPath}|{finding.Summary}",
                severity: finding.Severity,
                code: finding.CodeString,
                title: finding.Title,
                summary: finding.Summary,
                assetPath: !string.IsNullOrEmpty(finding.AssetPath)
                    ? finding.AssetPath
                    : site?.OwnerLocator.AssetPath ?? provider?.Locator.AssetPath,
                owner: site?.OwnerLocator.ObjectPath ?? provider?.Locator.ObjectPath,
                propertyPath: site?.PropertyPath,
                storedTarget: site != null
                    ? DescribeStored(site.StoredRefType, site.StoredRefId)
                    : provider != null ? DescribeStored(provider.RefType, provider.RefId) : null,
                scope: GlobalScope,
                sourceKind: site != null ? site.SourceKind.ToString() : provider?.Kind.ToString(),
                expectedType: site?.ExpectedRuntimeTypeName ?? provider?.RuntimeTypeName,
                resolutionState: finding.Outcome?.ToString() ?? resolution?.Outcome.ToString(),
                repair: repair?.For(finding) ?? ReferenceHubRepairAvailability.None,
                isReadOnly: site?.IsReadOnly ?? provider?.IsReadOnly ?? false,
                isAssigned: site?.IsAssigned ?? true,
                isLegacyFallback: site != null && site.IsAssigned && string.IsNullOrEmpty(site.StoredRefType),
                siteKey: finding.SourceSiteKey,
                // Falls through to what the site actually resolves to. A finding such as REF005 has both a
                // site and a target, and without the target key the row could offer neither "Ping target"
                // nor the identity editor — for a finding whose fix is very often to rename that target.
                providerKey: provider?.ProviderKey ?? resolution?.Resolved?.ProviderKey,
                candidateProviderKeys: finding.CandidateProviderKeys,
                storedRefType: site?.StoredRefType ?? provider?.RefType,
                storedRefId: site?.StoredRefId ?? provider?.RefId,
                ownerKey: site?.OwnerLocator.Key ?? provider?.Locator.Key);

            row.BuildSearchText();
            return row;
        }

        private static ReferenceHubRow FromSite(ReferenceSiteResolution resolution)
        {
            var site = resolution.Site;
            var row = new ReferenceHubRow(
                ReferenceHubRowKind.Site,
                key: site.SiteKey,
                // A site row is a statement of fact, not a judgement: the Issues table is where severity
                // lives. Colouring every unassigned optional reference amber here would train the user to
                // ignore the colour.
                severity: ReferenceFindingSeverity.Info,
                code: string.Empty,
                title: site.PropertyPath,
                summary: site.Describe(),
                assetPath: site.OwnerLocator.AssetPath,
                owner: site.OwnerLocator.ObjectPath,
                propertyPath: site.PropertyPath,
                storedTarget: DescribeStored(site.StoredRefType, site.StoredRefId),
                scope: GlobalScope,
                sourceKind: site.SourceKind.ToString(),
                expectedType: site.ExpectedRuntimeTypeName,
                resolutionState: resolution.Outcome.ToString(),
                repair: ReferenceHubRepairAvailability.None,
                isReadOnly: site.IsReadOnly,
                isAssigned: site.IsAssigned,
                isLegacyFallback: site.IsAssigned && string.IsNullOrEmpty(site.StoredRefType),
                siteKey: site.SiteKey,
                providerKey: resolution.Resolved?.ProviderKey,
                candidateProviderKeys: resolution.Candidates.Select(c => c.ProviderKey).ToList(),
                storedRefType: site.StoredRefType,
                storedRefId: site.StoredRefId,
                ownerKey: site.OwnerLocator.Key);

            row.BuildSearchText();
            return row;
        }

        private static ReferenceHubRow FromProvider(ReferenceProviderRecord provider, int inbound, int claiming)
        {
            var row = new ReferenceHubRow(
                ReferenceHubRowKind.Provider,
                key: provider.ProviderKey,
                severity: ReferenceFindingSeverity.Info,
                code: string.Empty,
                title: string.IsNullOrEmpty(provider.DisplayName) ? provider.Locator.ObjectPath : provider.DisplayName,
                summary: provider.ToString(),
                assetPath: provider.Locator.AssetPath,
                owner: provider.Locator.ObjectPath,
                propertyPath: string.Empty,
                storedTarget: DescribeStored(provider.RefType, provider.RefId),
                scope: GlobalScope,
                sourceKind: provider.Kind.ToString(),
                expectedType: provider.RuntimeTypeName,
                // A provider that the runtime registry never holds cannot answer a lookup no matter how
                // correct its id is, and saying so on the row is the difference between "this exists" and
                // "this can be resolved".
                resolutionState: provider.IsRuntimeResolvable ? "Registered at runtime" : "Not runtime-resolvable",
                repair: ReferenceHubRepairAvailability.None,
                isReadOnly: provider.IsReadOnly,
                isAssigned: !string.IsNullOrEmpty(provider.RefId),
                isLegacyFallback: false,
                inboundCount: inbound,
                claimingCount: claiming,
                providerKey: provider.ProviderKey,
                storedRefType: provider.RefType,
                storedRefId: provider.RefId,
                ownerKey: provider.Locator.Key);

            row.BuildSearchText();
            return row;
        }

        private static string DescribeStored(string refType, string refId) =>
            string.IsNullOrEmpty(refId)
                ? "<unset>"
                : string.IsNullOrEmpty(refType) ? $"(no type):{refId}" : $"{refType}:{refId}";

        /// <inheritdoc/>
        public override string ToString() =>
            string.IsNullOrEmpty(Code) ? $"{Kind}: {Title}" : $"{Code} {Title}";
    }

    /// <summary>The three row tables projected from one snapshot.</summary>
    public sealed class ReferenceHubTables
    {
        /// <summary>Finding rows, in the snapshot's deterministic finding order.</summary>
        public IReadOnlyList<ReferenceHubRow> Issues { get; }

        /// <summary>One row per discovered reference site.</summary>
        public IReadOnlyList<ReferenceHubRow> Sites { get; }

        /// <summary>One row per discovered provider.</summary>
        public IReadOnlyList<ReferenceHubRow> Providers { get; }

        internal ReferenceHubTables(
            IReadOnlyList<ReferenceHubRow> issues,
            IReadOnlyList<ReferenceHubRow> sites,
            IReadOnlyList<ReferenceHubRow> providers)
        {
            Issues = issues ?? Array.Empty<ReferenceHubRow>();
            Sites = sites ?? Array.Empty<ReferenceHubRow>();
            Providers = providers ?? Array.Empty<ReferenceHubRow>();
        }

        /// <summary>Empty tables, used before the first audit.</summary>
        public static ReferenceHubTables Empty { get; } = new ReferenceHubTables(null, null, null);
    }
}
