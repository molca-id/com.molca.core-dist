using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>Overall health of one audit run.</summary>
    public enum ReferenceAuditState
    {
        /// <summary>No findings <b>and</b> complete required coverage. The only state that means healthy.</summary>
        Clean = 0,

        /// <summary>Warnings only, with complete coverage.</summary>
        Warnings = 1,

        /// <summary>At least one error.</summary>
        Errors = 2,

        /// <summary>
        /// Required coverage was not achieved, so nothing can be concluded. Distinct from
        /// <see cref="Clean"/> even when zero findings were produced.
        /// </summary>
        Incomplete = 3,
    }

    /// <summary>
    /// The immutable result of one audit run: what exists, what is broken, and what was not looked at.
    /// </summary>
    /// <remarks>
    /// Every consumer — Doctor, the build gate, Sequence validation, MCP, the Inspector — projects one of
    /// these rather than scanning for itself. That is the whole point: a single snapshot cannot disagree
    /// with itself the way five independent scanners did.
    ///
    /// A snapshot holds no live <see cref="UnityEngine.Object"/>, so it stays valid across a scene close
    /// or a domain reload; navigation goes through <see cref="ReferenceObjectLocator.TryResolve"/>.
    /// </remarks>
    public sealed class ReferenceAuditSnapshot
    {
        /// <summary>Schema version of this record's shape, for cached or serialized copies.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Monotonic revision, used as a repair-plan precondition.</summary>
        public long Revision { get; }

        /// <summary>The scope this run was asked to cover.</summary>
        public ReferenceAuditScope Scope { get; }

        /// <summary>Every discovered provider.</summary>
        public IReadOnlyList<ReferenceProviderRecord> Providers { get; }

        /// <summary>Every discovered reference site.</summary>
        public IReadOnlyList<ReferenceSiteRecord> Sites { get; }

        /// <summary>Per-site resolution outcomes, in <see cref="Sites"/> order.</summary>
        public IReadOnlyList<ReferenceSiteResolution> Resolutions { get; }

        /// <summary>Findings in deterministic order.</summary>
        public IReadOnlyList<ReferenceFinding> Findings { get; }

        /// <summary>What the run covered.</summary>
        public ReferenceCoverage Coverage { get; }

        /// <summary>
        /// Every asset this run read, fingerprinted as it stood on disk at the time.
        /// </summary>
        /// <remarks>
        /// The evidence a persisted index needs in order to prove it is still current after an editor
        /// restart, when no invalidation hook was running to notice a change.
        /// </remarks>
        public IReadOnlyList<ReferenceScannedAsset> ScannedAssets { get; }

        /// <summary>
        /// Whether this snapshot may be written to the on-disk index.
        /// </summary>
        /// <remarks>
        /// False when the run read state that is not on disk — an untitled scene, or a scene or asset with
        /// unsaved changes. Such a result is perfectly good to use right now, but it cannot be
        /// <i>revalidated</i> later: the recorded fingerprints describe files whose contents the audit never
        /// actually read, so a future session would match them and restore a result built from edits that were
        /// subsequently discarded.
        /// </remarks>
        public bool CanPersist { get; }

        /// <summary>Why <see cref="CanPersist"/> is false. Empty when it is true.</summary>
        public string PersistBlockedReason { get; }

        /// <summary>When the run completed.</summary>
        public DateTime CompletedAtUtc { get; }

        /// <summary>How long the run took.</summary>
        public TimeSpan Duration { get; }

        /// <summary>Overall health. Never <see cref="ReferenceAuditState.Clean"/> with incomplete coverage.</summary>
        public ReferenceAuditState State { get; }

        /// <summary>Error-severity findings.</summary>
        public IReadOnlyList<ReferenceFinding> Errors { get; }

        /// <summary>Warning-severity findings.</summary>
        public IReadOnlyList<ReferenceFinding> Warnings { get; }

        private readonly Dictionary<string, ReferenceSiteResolution> _bySiteKey;
        private readonly Dictionary<string, ReferenceProviderRecord> _byProviderKey;

        private static long _revisionCounter;

        internal ReferenceAuditSnapshot(
            ReferenceAuditScope scope,
            IReadOnlyList<ReferenceProviderRecord> providers,
            IReadOnlyList<ReferenceSiteRecord> sites,
            ReferenceAnalysisResult analysis,
            TimeSpan duration,
            IReadOnlyList<ReferenceScannedAsset> scannedAssets = null,
            string persistBlockedReason = null,
            DateTime? completedAtUtc = null)
        {
            Revision = ++_revisionCounter;
            Scope = scope;
            Providers = providers ?? Array.Empty<ReferenceProviderRecord>();
            Sites = sites ?? Array.Empty<ReferenceSiteRecord>();
            Resolutions = analysis.Resolutions;
            Findings = analysis.Findings;
            Coverage = analysis.Coverage;
            Duration = duration;

            // A restored snapshot keeps the completion time of the run that produced it: the header would
            // otherwise report a months-old result as "last audit, just now".
            CompletedAtUtc = completedAtUtc ?? DateTime.UtcNow;

            ScannedAssets = scannedAssets ?? Array.Empty<ReferenceScannedAsset>();
            PersistBlockedReason = persistBlockedReason ?? string.Empty;
            CanPersist = string.IsNullOrEmpty(PersistBlockedReason) && ScannedAssets.Count > 0;

            Errors = Findings.Where(f => f.Severity == ReferenceFindingSeverity.Error).ToList();
            Warnings = Findings.Where(f => f.Severity == ReferenceFindingSeverity.Warning).ToList();

            // Coverage is checked before findings: an incomplete run that happens to have found nothing
            // must not be reported as clean, which is the single most misleading state the old tooling had.
            if (Errors.Count > 0)
                State = ReferenceAuditState.Errors;
            else if (!Coverage.IsComplete)
                State = ReferenceAuditState.Incomplete;
            else if (Warnings.Count > 0)
                State = ReferenceAuditState.Warnings;
            else
                State = ReferenceAuditState.Clean;

            _bySiteKey = new Dictionary<string, ReferenceSiteResolution>(StringComparer.Ordinal);
            foreach (var resolution in Resolutions)
                _bySiteKey[resolution.Site.SiteKey] = resolution;

            _byProviderKey = new Dictionary<string, ReferenceProviderRecord>(StringComparer.Ordinal);
            foreach (var provider in Providers)
                _byProviderKey[provider.ProviderKey] = provider;
        }

        /// <summary>An empty snapshot, used when no audit has run yet.</summary>
        public static ReferenceAuditSnapshot Empty { get; } = new ReferenceAuditSnapshot(
            ReferenceAuditScope.OpenScenes(),
            Array.Empty<ReferenceProviderRecord>(),
            Array.Empty<ReferenceSiteRecord>(),
            ReferenceResolutionAnalyzer.Analyze(
                Array.Empty<ReferenceProviderRecord>(),
                Array.Empty<ReferenceSiteRecord>(),
                new ReferenceCoverage(new[]
                {
                    new ReferenceCoverageEntry(
                        "Audit", ReferenceCoverageStatus.Skipped, 0, "no audit has run yet"),
                })),
            TimeSpan.Zero);

        /// <summary>The resolution for a site key, or null when the key is unknown.</summary>
        /// <param name="siteKey">A <see cref="ReferenceSiteRecord.SiteKey"/>.</param>
        public ReferenceSiteResolution FindResolution(string siteKey) =>
            siteKey != null && _bySiteKey.TryGetValue(siteKey, out var r) ? r : null;

        /// <summary>The provider for a provider key, or null when the key is unknown.</summary>
        /// <param name="providerKey">A <see cref="ReferenceProviderRecord.ProviderKey"/>.</param>
        public ReferenceProviderRecord FindProvider(string providerKey) =>
            providerKey != null && _byProviderKey.TryGetValue(providerKey, out var p) ? p : null;

        /// <summary>Findings anchored to <paramref name="assetPath"/>.</summary>
        /// <param name="assetPath">Project-relative asset path.</param>
        public IEnumerable<ReferenceFinding> FindingsForAsset(string assetPath) =>
            Findings.Where(f => string.Equals(f.AssetPath, assetPath, StringComparison.Ordinal));

        /// <summary>
        /// One-line health caption, e.g. <c>Errors — 3 error(s), 1 warning(s), coverage complete</c>.
        /// Contains no asset paths, so it is safe for a remote-safe status surface.
        /// </summary>
        public string DescribeState() =>
            $"{State} — {Errors.Count} error(s), {Warnings.Count} warning(s), "
            + $"{Providers.Count} provider(s), {Sites.Count} site(s), coverage {Coverage.DescribeGaps()}";

        /// <inheritdoc/>
        public override string ToString() => DescribeState();
    }
}
