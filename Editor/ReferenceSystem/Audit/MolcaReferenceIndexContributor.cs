using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// What a contributor may add to an audit in progress.
    /// </summary>
    /// <remarks>
    /// Deliberately append-only and read-only with respect to the project: a contributor can describe
    /// providers, sites, coverage and scan errors, but has no way to dirty an asset. Collection runs
    /// during a read-only audit, and a contributor that mutated project data mid-scan would make the
    /// resulting snapshot describe a state that no longer exists.
    /// </remarks>
    public sealed class ReferenceCollectionContext
    {
        private readonly List<ReferenceProviderRecord> _providers;
        private readonly List<ReferenceSiteRecord> _sites;
        private readonly List<string> _scanErrors;
        private readonly List<ReferenceCoverageEntry> _coverage;
        private readonly Action<string> _markScanned;

        /// <summary>What the current audit was asked to cover.</summary>
        public ReferenceAuditScope Scope { get; }

        /// <summary>Number of records this context has accepted, used to tell a silent contributor apart.</summary>
        internal int ContributedCount { get; private set; }

        /// <summary>Number of assets the contributor declared it read.</summary>
        internal int DeclaredAssetCount { get; private set; }

        internal ReferenceCollectionContext(
            ReferenceAuditScope scope,
            List<ReferenceProviderRecord> providers,
            List<ReferenceSiteRecord> sites,
            List<string> scanErrors,
            List<ReferenceCoverageEntry> coverage,
            Action<string> markScanned = null)
        {
            Scope = scope;
            _providers = providers;
            _sites = sites;
            _scanErrors = scanErrors;
            _coverage = coverage;
            _markScanned = markScanned;
        }

        /// <summary>
        /// Declares an asset this contributor read, so the persisted index can revalidate it later.
        /// </summary>
        /// <param name="assetPath">Project-relative path of an asset the contributor derived records from.</param>
        /// <remarks>
        /// A contributor that adds records without declaring where they came from makes the whole index
        /// unverifiable — nothing can later prove its inputs are unchanged — so the audit refuses to persist
        /// that run rather than storing a result it cannot revalidate. Declaring an asset another phase
        /// already scanned is free and correct; the fingerprints are deduplicated.
        /// </remarks>
        public void MarkScanned(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return;

            DeclaredAssetCount++;
            _markScanned?.Invoke(assetPath);
        }

        /// <summary>Adds a provider the contributor discovered. Null is ignored.</summary>
        public void AddProvider(ReferenceProviderRecord provider)
        {
            if (provider == null)
                return;

            _providers.Add(provider);
            ContributedCount++;
        }

        /// <summary>Adds a reference site the contributor discovered. Null is ignored.</summary>
        public void AddSite(ReferenceSiteRecord site)
        {
            if (site == null)
                return;

            _sites.Add(site);
            ContributedCount++;
        }

        /// <summary>
        /// Reports that something could not be scanned. Becomes a
        /// <see cref="ReferenceFindingCode.AssetScanFailed"/> finding so the gap is visible.
        /// </summary>
        /// <param name="reason">Human-readable reason, including the asset involved.</param>
        public void ReportScanError(string reason)
        {
            if (!string.IsNullOrEmpty(reason))
                _scanErrors.Add(reason);
        }

        /// <summary>Declares what the contributor covered, or why it could not.</summary>
        /// <param name="category">Category name shown in the coverage report.</param>
        /// <param name="status">Whether the category was scanned.</param>
        /// <param name="count">Number of assets or objects covered.</param>
        /// <param name="reason">Why it was skipped or failed. Ignored when scanned.</param>
        /// <param name="isRequired">
        /// Whether a gap here prevents the project being called clean. Contribute <c>false</c> for
        /// optional enrichment so an add-on cannot make every audit permanently incomplete.
        /// </param>
        public void ReportCoverage(
            string category, ReferenceCoverageStatus status, int count, string reason = null, bool isRequired = false)
        {
            _coverage.Add(new ReferenceCoverageEntry(category, status, count, reason, isRequired));
        }

        /// <summary>
        /// Describes and adds <paramref name="candidate"/> as a provider when it is referenceable.
        /// </summary>
        /// <param name="candidate">The component or asset to describe.</param>
        /// <param name="kind">Which provider category it belongs to.</param>
        /// <param name="assetPathHint">Asset path to record when Unity cannot report one.</param>
        public void AddProviderFor(
            UnityEngine.Object candidate,
            ReferenceProviderKind kind = ReferenceProviderKind.Contributed,
            string assetPathHint = null)
        {
            AddProvider(ReferenceSerializedScanner.TryDescribeProvider(candidate, kind, assetPathHint));
        }

        /// <summary>
        /// Walks <paramref name="owner"/>'s serialized data and adds every reference site it declares.
        /// </summary>
        /// <param name="owner">The component or asset to walk.</param>
        /// <param name="sourceKind">Which asset category owns the sites.</param>
        /// <param name="assetPathHint">Asset path to record when Unity cannot report one.</param>
        public void CollectSitesFrom(
            UnityEngine.Object owner,
            ReferenceSiteSourceKind sourceKind = ReferenceSiteSourceKind.Contributed,
            string assetPathHint = null)
        {
            ReferenceSerializedScanner.CollectSites(owner, sourceKind, _sites, assetPathHint, ReportScanError);
        }
    }

    /// <summary>
    /// Extension point through which a package outside Core adds providers, reference sites and coverage
    /// to the shared audit.
    /// </summary>
    /// <remarks>
    /// Subclass this in an editor assembly, with a public parameterless constructor; the engine finds
    /// implementations by reflection. Sequence uses it to describe <c>Step</c> and
    /// <c>SequenceController</c> providers and the reference sites inside <c>StepAuxiliary</c>
    /// managed-reference graphs, so Core's analysis needs no knowledge of those types.
    ///
    /// A contributor that throws is isolated: the exception is recorded as a coverage failure for that
    /// contributor and the rest of the audit continues, downgraded to incomplete rather than reported as
    /// clean.
    /// </remarks>
    public abstract class MolcaReferenceIndexContributor
    {
        /// <summary>Stable identifier, used in coverage entries and error messages.</summary>
        public abstract string Id { get; }

        /// <summary>Relative run order. Lower runs first; equal orders run in type-name order.</summary>
        public virtual int Order => 0;

        /// <summary>
        /// Adds this contributor's providers and sites to the audit.
        /// </summary>
        /// <param name="context">The collection context. Must not be used after this call returns.</param>
        public abstract void Collect(ReferenceCollectionContext context);

        /// <summary>
        /// Instantiates every contributor in the loaded editor assemblies, in run order.
        /// </summary>
        /// <remarks>
        /// A contributor whose constructor throws is skipped with a warning rather than aborting
        /// discovery, so one broken add-on cannot disable reference auditing for the whole project.
        /// </remarks>
        internal static IReadOnlyList<MolcaReferenceIndexContributor> DiscoverAll()
        {
            var contributors = new List<MolcaReferenceIndexContributor>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaReferenceIndexContributor>())
            {
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                try
                {
                    contributors.Add((MolcaReferenceIndexContributor)Activator.CreateInstance(type));
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[ReferenceAudit] Contributor '{type.FullName}' could not be created and was skipped: {e.Message}");
                }
            }

            return contributors
                .OrderBy(c => c.Order)
                .ThenBy(c => c.GetType().FullName, StringComparer.Ordinal)
                .ToList();
        }
    }
}
