using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.ContentPackage.Release
{
    /// <summary>Why a package cannot be evicted right now.</summary>
    public enum CacheRetentionReason
    {
        /// <summary>Nothing is holding it; it can be freed.</summary>
        None = 0,

        /// <summary>The active release marks it required.</summary>
        RequiredByRelease,

        /// <summary>Another installed package depends on it.</summary>
        RequiredByInstalledDependent,

        /// <summary>It is not installed, so there is nothing to reclaim.</summary>
        NotInstalled,

        /// <summary>It belongs to a retained previous release still eligible for rollback.</summary>
        RetainedForRollback,
    }

    /// <summary>One package's place in an eviction plan.</summary>
    public sealed class CacheEvictionCandidate
    {
        /// <summary>The package this entry describes.</summary>
        public string PackageId { get; set; }

        /// <summary>True when the plan will evict it.</summary>
        public bool CanEvict { get; set; }

        /// <summary>Why it is held, when <see cref="CanEvict"/> is false.</summary>
        public CacheRetentionReason Reason { get; set; }

        /// <summary>
        /// Bytes this package would reclaim <em>given everything else in the plan</em>.
        /// </summary>
        /// <remarks>
        /// Not the package's download size. An object shared with a package that is staying is not
        /// reclaimed by evicting this one, and an object shared with another package the same plan
        /// also evicts is reclaimed once, attributed to whichever entry is listed first. Summing
        /// these across a plan therefore gives the plan's real total; summing
        /// <c>downloadSizeBytes</c> does not, and would promise the user space that does not exist.
        /// </remarks>
        public long ReclaimableBytes { get; set; }

        /// <summary>Human-readable detail for the retention reason.</summary>
        public string Detail { get; set; } = "";
    }

    /// <summary>A complete, reviewable eviction plan.</summary>
    public sealed class CacheEvictionPlan
    {
        /// <summary>Every considered package, evictable or not, in eviction order.</summary>
        public IReadOnlyList<CacheEvictionCandidate> Candidates { get; set; } = Array.Empty<CacheEvictionCandidate>();

        /// <summary>The packages the plan will evict.</summary>
        public IEnumerable<CacheEvictionCandidate> Evictable => Candidates.Where(entry => entry.CanEvict);

        /// <summary>The packages the plan will keep, with reasons.</summary>
        public IEnumerable<CacheEvictionCandidate> Blocked => Candidates.Where(entry => !entry.CanEvict);

        /// <summary>Total bytes the plan expects to reclaim, counting each object once.</summary>
        public long ExpectedReclaimedBytes => Evictable.Sum(entry => entry.ReclaimableBytes);
    }

    /// <summary>
    /// Decides what may be evicted from the content cache, from the release's own object graph.
    /// </summary>
    /// <remarks>
    /// Ownership comes from the release, not from package status (plan §11.4). That distinction is
    /// what fixes the old behaviour: eviction previously worked package-by-package on labels, so a
    /// bundle shared by two packages was counted twice in the "space you will free" figure, and
    /// clearing one package's cache could delete a bundle the other still needed. A user freed less
    /// space than promised and broke content that was not part of the operation.
    ///
    /// Every method here is pure. Nothing is deleted, no Addressables call is made, and the plan can
    /// be shown to a user and confirmed before anything happens — which the SDK UI is required to do
    /// for destructive removal.
    /// </remarks>
    public static class ReleaseCachePolicy
    {
        /// <summary>
        /// Builds an eviction plan for the given release.
        /// </summary>
        /// <param name="manifest">The active release manifest. Required.</param>
        /// <param name="installedPackageIds">Packages currently installed on the device.</param>
        /// <param name="requestedPackageIds">
        /// Packages the user asked to free, or null to consider every installed optional package.
        /// </param>
        /// <param name="retainedObjectIds">
        /// Objects a retained previous release still needs. Held back so a rollback does not have to
        /// re-download the release it is rolling back to.
        /// </param>
        /// <param name="lastAccessedUtc">
        /// Per-package last use, used only to order the plan. Missing entries sort oldest.
        /// </param>
        /// <returns>The plan; never null.</returns>
        public static CacheEvictionPlan Plan(
            ContentReleaseManifest manifest,
            IEnumerable<string> installedPackageIds,
            IEnumerable<string> requestedPackageIds = null,
            IEnumerable<string> retainedObjectIds = null,
            IReadOnlyDictionary<string, DateTime> lastAccessedUtc = null)
        {
            if (manifest == null) return new CacheEvictionPlan();

            var installed = new HashSet<string>(
                installedPackageIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var retained = new HashSet<string>(
                retainedObjectIds ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            var requested = requestedPackageIds == null
                ? new HashSet<string>(installed, StringComparer.Ordinal)
                : new HashSet<string>(requestedPackageIds, StringComparer.Ordinal);

            // Order by least-recently-used so a size-driven eviction frees the least useful content
            // first. Ordering is presentation and priority only -- it never makes an unevictable
            // package evictable.
            var ordered = requested
                .OrderBy(id => lastAccessedUtc != null && lastAccessedUtc.TryGetValue(id, out var when)
                    ? when
                    : DateTime.MinValue)
                .ToList();

            var evicting = new HashSet<string>(StringComparer.Ordinal);

            // Seed with everything that is eligible on its own terms: installed, and not required by
            // the release. Dependents are resolved afterwards.
            foreach (string packageId in ordered)
            {
                if (!installed.Contains(packageId)) continue;
                var entry = manifest.FindPackage(packageId);
                if (entry != null && entry.required) continue;
                evicting.Add(packageId);
            }

            // Withdraw anything a surviving installed package still depends on, and repeat until the
            // set stops changing.
            //
            // A single pass is order-dependent and therefore wrong: asked to free "a" and its only
            // dependent together, a pass that reaches "a" first sees the dependent still installed
            // and blocks "a" -- while the reverse order frees both. The plan a user is shown would
            // then depend on hash iteration order. Iterating to a fixpoint removes the ordering from
            // the answer entirely.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (string packageId in ordered)
                {
                    if (!evicting.Contains(packageId)) continue;
                    if (InstalledDependentOf(manifest, packageId, installed, evicting) == null) continue;
                    evicting.Remove(packageId);
                    changed = true;
                }
            }

            var candidates = new List<CacheEvictionCandidate>(ordered.Count);
            foreach (string packageId in ordered)
            {
                if (evicting.Contains(packageId))
                {
                    candidates.Add(new CacheEvictionCandidate
                    {
                        PackageId = packageId,
                        CanEvict = true,
                        Reason = CacheRetentionReason.None,
                    });
                    continue;
                }

                if (!installed.Contains(packageId))
                {
                    candidates.Add(new CacheEvictionCandidate
                    {
                        PackageId = packageId,
                        CanEvict = false,
                        Reason = CacheRetentionReason.NotInstalled,
                        Detail = "Not installed; nothing to reclaim.",
                    });
                    continue;
                }

                var entry = manifest.FindPackage(packageId);
                if (entry != null && entry.required)
                {
                    candidates.Add(new CacheEvictionCandidate
                    {
                        PackageId = packageId,
                        CanEvict = false,
                        Reason = CacheRetentionReason.RequiredByRelease,
                        Detail = "The active release requires this package.",
                    });
                    continue;
                }

                string dependent = InstalledDependentOf(manifest, packageId, installed, evicting);
                candidates.Add(new CacheEvictionCandidate
                {
                    PackageId = packageId,
                    CanEvict = false,
                    Reason = CacheRetentionReason.RequiredByInstalledDependent,
                    Detail = dependent != null
                        ? $"'{dependent}' is installed and depends on it."
                        : "Another installed package depends on it.",
                });
            }

            // Objects that survive the plan: everything reachable from packages staying installed,
            // plus anything a retained previous release still needs.
            var survivors = manifest.ObjectClosure(installed.Where(id => !evicting.Contains(id)));
            foreach (string objectId in retained) survivors.Add(objectId);

            // Second pass: attribute bytes. An object shared between two evicted packages is counted
            // once, against the first of them -- so the plan's total is the truth even though no
            // single line is the package's full size.
            var alreadyCounted = new HashSet<string>(StringComparer.Ordinal);
            var sized = new List<CacheEvictionCandidate>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!candidate.CanEvict) { sized.Add(candidate); continue; }

                long bytes = 0;
                foreach (string objectId in manifest.ObjectClosure(new[] { candidate.PackageId }))
                {
                    if (survivors.Contains(objectId)) continue;
                    if (!alreadyCounted.Add(objectId)) continue;
                    var entry = manifest.FindObject(objectId);
                    if (entry != null) bytes += entry.sizeBytes;
                }

                sized.Add(new CacheEvictionCandidate
                {
                    PackageId = candidate.PackageId,
                    CanEvict = true,
                    Reason = CacheRetentionReason.None,
                    ReclaimableBytes = bytes,
                    Detail = bytes == 0 ? "Shares all of its content with packages that are staying." : "",
                });
            }

            return new CacheEvictionPlan { Candidates = sized };
        }

        /// <summary>
        /// Objects that must survive because the active release or an installed package needs them.
        /// </summary>
        /// <param name="manifest">The active release manifest.</param>
        /// <param name="installedPackageIds">Packages installed on the device.</param>
        /// <returns>Distinct object ids to keep.</returns>
        public static HashSet<string> ProtectedObjects(
            ContentReleaseManifest manifest, IEnumerable<string> installedPackageIds)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            if (manifest == null) return keep;

            foreach (string objectId in manifest.ObjectClosure(
                         manifest.RequiredPackages().Select(package => package.packageId)))
                keep.Add(objectId);

            foreach (string objectId in manifest.ObjectClosure(installedPackageIds))
                keep.Add(objectId);

            // The catalog is not owned by any package and is needed for every one of them.
            if (manifest.catalog != null)
            {
                if (!string.IsNullOrEmpty(manifest.catalog.catalogObjectId))
                    keep.Add(manifest.catalog.catalogObjectId);
                if (!string.IsNullOrEmpty(manifest.catalog.catalogHashObjectId))
                    keep.Add(manifest.catalog.catalogHashObjectId);
            }

            return keep;
        }

        /// <summary>
        /// The first installed package that depends on <paramref name="packageId"/> and is not
        /// itself being evicted, or null.
        /// </summary>
        private static string InstalledDependentOf(
            ContentReleaseManifest manifest,
            string packageId,
            HashSet<string> installed,
            HashSet<string> evicting)
        {
            foreach (var package in manifest.packages ?? Array.Empty<ContentReleaseManifest.PackageEntry>())
            {
                if (package == null || string.IsNullOrEmpty(package.packageId)) continue;
                if (string.Equals(package.packageId, packageId, StringComparison.Ordinal)) continue;
                if (!installed.Contains(package.packageId)) continue;
                if (evicting.Contains(package.packageId)) continue;

                foreach (string dependency in package.dependencies ?? Array.Empty<string>())
                    if (string.Equals(dependency, packageId, StringComparison.Ordinal))
                        return package.packageId;
            }
            return null;
        }
    }
}
