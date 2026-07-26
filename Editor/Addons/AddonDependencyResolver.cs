using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Addons
{
    /// <summary>One reviewed package in a dependency-first installation plan.</summary>
    internal sealed class AddonInstallPlanEntry
    {
        public AddonCatalogPackage Package;
        public AddonCatalogVersion Version;
        public bool Requested;
        public bool AlreadyInstalled;
    }

    /// <summary>Immutable resolution result shown once before any package mutation.</summary>
    internal sealed class AddonInstallPlan
    {
        public string RootId;
        public string RootVersion;
        public readonly List<AddonInstallPlanEntry> Ordered = new List<AddonInstallPlanEntry>();
        public readonly List<ExternalAddonPrerequisite> ExternalPrerequisites =
            new List<ExternalAddonPrerequisite>();

        public int ChangedCount => Ordered.Count(entry => !entry.AlreadyInstalled);
    }

    /// <summary>
    /// Deterministic minimum-version/same-major resolver matching the server protocol-v3 resolver.
    /// Dependencies always precede dependents and package id breaks otherwise equal ordering.
    /// </summary>
    internal static class AddonDependencyResolver
    {
        private sealed class Constraint
        {
            public string Minimum;
            public string Maximum;
            public string RequiredBy;
        }

        internal static bool TryResolve(
            AddonCatalogResponse catalog,
            string rootId,
            string rootVersion,
            InstalledAddonsAsset ledger,
            out AddonInstallPlan plan,
            out string error)
        {
            plan = null;
            error = null;
            if (catalog?.packs == null)
            {
                error = "missing_dependency: catalog is unavailable";
                return false;
            }

            var packs = catalog.packs.ToDictionary(pack => pack.id, StringComparer.Ordinal);
            var selected = new Dictionary<string, AddonCatalogVersion>(StringComparer.Ordinal);
            var constraints = new Dictionary<string, List<Constraint>>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            string resolutionError = null;

            bool Visit(string id, string exactVersion, string parent)
            {
                if (visiting.Contains(id))
                {
                    resolutionError = $"dependency_cycle: {parent} -> {id}";
                    return false;
                }
                if (!packs.TryGetValue(id, out var pack))
                {
                    resolutionError = $"missing_dependency: {id}";
                    return false;
                }

                IEnumerable<AddonCatalogVersion> candidates = pack.versions ?? Array.Empty<AddonCatalogVersion>();
                if (!string.IsNullOrEmpty(exactVersion))
                    candidates = candidates.Where(version => version.version == exactVersion);
                if (constraints.TryGetValue(id, out var incoming))
                    candidates = candidates.Where(version => incoming.All(constraint =>
                        InRange(version.version, constraint.Minimum, constraint.Maximum)));

                var candidateList = candidates.ToList();
                if (candidateList.Count == 0)
                {
                    string detail = constraints.TryGetValue(id, out var required)
                        ? string.Join(", ", required.Select(item =>
                            $"{item.RequiredBy} requires >= {item.Minimum} < {item.Maximum}"))
                        : $"exact version {exactVersion}";
                    resolutionError = $"dependency_version_conflict: {id} ({detail})";
                    return false;
                }

                string installedVersion = ledger?.Find(id)?.version;
                AddonCatalogVersion chosen = candidateList.FirstOrDefault(
                    version => version.version == installedVersion) ?? candidateList[0];
                if (selected.TryGetValue(id, out var prior) && prior.version == chosen.version &&
                    ordered.Contains(id)) return true;

                selected[id] = chosen;
                visiting.Add(id);
                foreach (AddonDependency dependency in
                    (chosen.dependencies ?? Array.Empty<AddonDependency>())
                    .OrderBy(item => item.id, StringComparer.Ordinal))
                {
                    if (!constraints.TryGetValue(dependency.id, out var list))
                    {
                        list = new List<Constraint>();
                        constraints[dependency.id] = list;
                    }
                    list.Add(new Constraint
                    {
                        Minimum = dependency.minimumVersion,
                        Maximum = dependency.maximumMajorExclusive,
                        RequiredBy = id,
                    });
                    if (!Visit(dependency.id, null, id)) return false;
                }
                visiting.Remove(id);
                if (!ordered.Contains(id)) ordered.Add(id);
                return true;
            }

            if (!Visit(rootId, rootVersion, null))
            {
                error = resolutionError;
                return false;
            }

            foreach (var pair in selected)
            {
                InstalledAddonRecord installed = ledger?.Find(pair.Key);
                if (installed == null || installed.version == pair.Value.version) continue;
                string[] outsideDependents = ledger.DependentsOf(pair.Key)
                    .Where(record => !selected.ContainsKey(record.id))
                    .Select(record => record.id).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (outsideDependents.Length > 0)
                {
                    error = $"dependent_would_break: {pair.Key} is still required by " +
                            string.Join(", ", outsideDependents);
                    return false;
                }
            }

            var result = new AddonInstallPlan { RootId = rootId, RootVersion = rootVersion };
            var prerequisites = new Dictionary<string, ExternalAddonPrerequisite>(StringComparer.Ordinal);
            foreach (string id in ordered)
            {
                AddonCatalogVersion version = selected[id];
                result.Ordered.Add(new AddonInstallPlanEntry
                {
                    Package = packs[id],
                    Version = version,
                    Requested = id == rootId,
                    AlreadyInstalled = ledger?.Find(id)?.version == version.version,
                });
                foreach (ExternalAddonPrerequisite prerequisite in
                    version.externalPrerequisites ?? Array.Empty<ExternalAddonPrerequisite>())
                {
                    if (prerequisites.TryGetValue(prerequisite.packageId, out var existing) &&
                        (!string.Equals(existing.source, prerequisite.source, StringComparison.Ordinal) ||
                         !string.Equals(existing.spec, prerequisite.spec, StringComparison.Ordinal)))
                    {
                        error = $"external_prerequisite_version_conflict: {prerequisite.packageId}";
                        return false;
                    }
                    prerequisites[prerequisite.packageId] = prerequisite;
                }
            }
            result.ExternalPrerequisites.AddRange(
                prerequisites.Values.OrderBy(item => item.packageId, StringComparer.Ordinal));
            plan = result;
            return true;
        }

        internal static bool ManifestMatches(
            AddonCatalogVersion version, AddonManifest manifest, out string error)
        {
            error = null;
            string CatalogEdge(AddonDependency item) =>
                $"{item.id}|{item.minimumVersion}|{item.maximumMajorExclusive}";
            string ExternalEdge(ExternalAddonPrerequisite item) =>
                $"{item.packageId}|{item.source}|{item.spec}|{item.resolvedCommit}";
            string[] catalogDependencies = (version.dependencies ?? Array.Empty<AddonDependency>())
                .Select(CatalogEdge).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] signedDependencies = (manifest.dependencies ?? Array.Empty<AddonDependency>())
                .Select(CatalogEdge).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] catalogExternal =
                (version.externalPrerequisites ?? Array.Empty<ExternalAddonPrerequisite>())
                .Select(ExternalEdge).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] signedExternal =
                (manifest.externalPrerequisites ?? Array.Empty<ExternalAddonPrerequisite>())
                .Select(ExternalEdge).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!catalogDependencies.SequenceEqual(signedDependencies) ||
                !catalogExternal.SequenceEqual(signedExternal))
            {
                error = "Signed dependency metadata does not match the resolved catalog graph.";
                return false;
            }
            return true;
        }

        private static bool InRange(string version, string minimum, string maximum) =>
            AddonSemVer.Compare(version, minimum) >= 0 &&
            AddonSemVer.Compare(version, maximum) < 0;
    }
}
