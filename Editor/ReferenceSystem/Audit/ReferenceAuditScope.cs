using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// What a single audit run is asked to look at, and what it is allowed to do to get there.
    /// </summary>
    /// <remarks>
    /// Scope is explicit rather than implied by the caller, because "clean" only means anything relative
    /// to a declared scope. The scope a run used is recorded on its snapshot, so a result can never be
    /// mistaken for a broader guarantee than it made.
    ///
    /// No scope permits mutation. <see cref="MayOpenScenes"/> allows the engine to <i>read</i> closed
    /// scenes by opening them; the engine restores the user's scene setup afterwards and still writes
    /// nothing.
    /// </remarks>
    public sealed class ReferenceAuditScope
    {
        /// <summary>Scan the scenes currently open in the editor.</summary>
        public bool IncludeOpenScenes { get; private set; } = true;

        /// <summary>
        /// Additional scenes to scan by asset path — typically the enabled build scenes. Ones already
        /// open are scanned in place; the rest need <see cref="MayOpenScenes"/>.
        /// </summary>
        public IReadOnlyList<string> ExplicitScenePaths { get; private set; } = Array.Empty<string>();

        /// <summary>Prefab asset or folder paths to scan. Empty skips prefab scanning.</summary>
        public IReadOnlyList<string> PrefabScanPaths { get; private set; } = Array.Empty<string>();

        /// <summary>
        /// Scan ScriptableObject assets for outbound reference sites. On by default: an SO cannot be a
        /// runtime target, but it very much can hold a reference that resolves a loaded scene object.
        /// </summary>
        public bool IncludeScriptableObjects { get; private set; } = true;

        /// <summary>
        /// Whether the engine may open closed scenes from <see cref="ExplicitScenePaths"/>. When false,
        /// those scenes are reported as skipped coverage rather than silently ignored.
        /// </summary>
        public bool MayOpenScenes { get; private set; }

        /// <summary>
        /// Whether prefab scanning gaps prevent a clean result. On by default.
        /// </summary>
        /// <remarks>
        /// A project with no configured Prefab Scan Paths has not proven its prefab references are sound —
        /// it has only declined to look. Reporting that as <c>Incomplete</c> rather than <c>Clean</c> is the
        /// whole point of tracking coverage: the common configuration here (no scan paths, no enabled build
        /// scenes) used to produce a green result that asserted nothing about the project.
        /// </remarks>
        public bool PrefabCoverageRequired { get; private set; } = true;

        /// <summary>Severity policy applied to the findings this run produces.</summary>
        public ReferenceSeverityPolicy Policy { get; private set; } = ReferenceSeverityPolicy.Default;

        /// <summary>Asset paths the caller wants excluded (e.g. Doctor's ignore list).</summary>
        public Func<string, bool> IsIgnored { get; private set; }

        private ReferenceAuditScope() { }

        /// <summary>
        /// The interactive default: whatever is open, plus prefabs and ScriptableObjects, without
        /// disturbing the user's scene setup. Used by the Inspector, Sequence validation and MCP.
        /// </summary>
        /// <param name="prefabScanPaths">Prefab paths to include. Null means none.</param>
        public static ReferenceAuditScope OpenScenes(IEnumerable<string> prefabScanPaths = null) =>
            new ReferenceAuditScope
            {
                IncludeOpenScenes = true,
                MayOpenScenes = false,
                PrefabScanPaths = Normalize(prefabScanPaths),
            };

        /// <summary>
        /// A scope that asserts nothing beyond the open scenes: prefab gaps do not downgrade it.
        /// </summary>
        /// <remarks>
        /// For callers that genuinely only ask about loaded objects — a picker, a live diagnostic — and would
        /// be misled rather than informed by a permanent prefab-coverage warning.
        /// </remarks>
        public static ReferenceAuditScope LoadedObjectsOnly() =>
            new ReferenceAuditScope
            {
                IncludeOpenScenes = true,
                MayOpenScenes = false,
                IncludeScriptableObjects = false,
                PrefabCoverageRequired = false,
            };

        /// <summary>
        /// The build gate scope: exactly the scenes going into the player, opened as needed, with prefab
        /// coverage treated as required so a misconfigured scan cannot silently pass a build.
        /// </summary>
        /// <param name="buildScenePaths">Scene asset paths in the build.</param>
        /// <param name="prefabScanPaths">Prefab paths to include. Null means none.</param>
        /// <param name="policy">Severity policy; <see cref="ReferenceSeverityPolicy.Default"/> when null.</param>
        public static ReferenceAuditScope ForBuild(
            IEnumerable<string> buildScenePaths,
            IEnumerable<string> prefabScanPaths = null,
            ReferenceSeverityPolicy policy = null) =>
            new ReferenceAuditScope
            {
                // A build's availability set is its own scenes, not whatever the developer left open.
                IncludeOpenScenes = false,
                ExplicitScenePaths = Normalize(buildScenePaths),
                PrefabScanPaths = Normalize(prefabScanPaths),
                MayOpenScenes = true,
                PrefabCoverageRequired = true,
                Policy = policy ?? ReferenceSeverityPolicy.Default,
            };

        /// <summary>
        /// Builds the scope described by a <see cref="ReferenceManagerSettings"/> asset.
        /// </summary>
        /// <param name="settings">
        /// The settings asset supplying prefab scan paths. Null contributes no prefab coverage and does not
        /// otherwise narrow the scope.
        /// </param>
        /// <param name="mayOpenScenes">
        /// Whether this run covers the whole project, opening closed scenes to read them. True is the
        /// user-facing <i>Full audit</i> command; background and incremental callers pass false and get the
        /// open scenes only.
        /// </param>
        /// <remarks>
        /// This is the <b>only</b> gate on closed-scene coverage. It used to be ANDed with a
        /// <c>ComprehensiveSceneScanning</c> setting that stated the same condition in different words, and
        /// which defaulted to off — so the sole live effect of that setting was to let a button labelled
        /// Full audit scan nothing the user did not already have open, and still report Clean.
        /// </remarks>
        public static ReferenceAuditScope FromSettings(ReferenceManagerSettings settings, bool mayOpenScenes = false)
        {
            var scenePaths = mayOpenScenes
                ? UnityEditor.AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                    .Select(UnityEditor.AssetDatabase.GUIDToAssetPath)
                    .Where(p => !ReferenceAssetPolicy.IsExcludedFromScan(p))
                : Enumerable.Empty<string>();

            return new ReferenceAuditScope
            {
                IncludeOpenScenes = true,
                ExplicitScenePaths = Normalize(scenePaths),
                PrefabScanPaths = Normalize(settings?.PrefabScanPaths),
                MayOpenScenes = mayOpenScenes,
            };
        }

        /// <summary>Returns a copy of this scope with <paramref name="isIgnored"/> applied.</summary>
        /// <param name="isIgnored">Predicate returning true for asset paths to skip.</param>
        public ReferenceAuditScope WithIgnoreFilter(Func<string, bool> isIgnored) =>
            new ReferenceAuditScope
            {
                IncludeOpenScenes = IncludeOpenScenes,
                ExplicitScenePaths = ExplicitScenePaths,
                PrefabScanPaths = PrefabScanPaths,
                IncludeScriptableObjects = IncludeScriptableObjects,
                MayOpenScenes = MayOpenScenes,
                PrefabCoverageRequired = PrefabCoverageRequired,
                Policy = Policy,
                IsIgnored = isIgnored,
            };

        /// <summary>Returns a copy of this scope using <paramref name="policy"/>.</summary>
        /// <param name="policy">The severity policy to apply.</param>
        public ReferenceAuditScope WithPolicy(ReferenceSeverityPolicy policy) =>
            new ReferenceAuditScope
            {
                IncludeOpenScenes = IncludeOpenScenes,
                ExplicitScenePaths = ExplicitScenePaths,
                PrefabScanPaths = PrefabScanPaths,
                IncludeScriptableObjects = IncludeScriptableObjects,
                MayOpenScenes = MayOpenScenes,
                PrefabCoverageRequired = PrefabCoverageRequired,
                Policy = policy ?? ReferenceSeverityPolicy.Default,
                IsIgnored = IsIgnored,
            };

        /// <summary>True when <paramref name="assetPath"/> is inside <see cref="PrefabScanPaths"/>.</summary>
        /// <param name="assetPath">Project-relative prefab path.</param>
        public bool IsPrefabInScope(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || PrefabScanPaths.Count == 0)
                return false;

            var normalized = assetPath.Replace('\\', '/');
            foreach (var entry in PrefabScanPaths)
            {
                var trimmed = entry.TrimEnd('/');
                if (normalized.StartsWith(trimmed + "/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalized, trimmed, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when a snapshot produced for <paramref name="other"/> also answers for this scope.
        /// </summary>
        /// <param name="other">The scope a cached snapshot was produced for. Null never matches.</param>
        /// <remarks>
        /// Compared structurally rather than by reference: callers naturally build a fresh scope per request
        /// (<c>ReferenceAuditScope.OpenScenes()</c> allocates), and reference comparison would turn every
        /// cache lookup into a full rescan — including in Framework Graph, which rebuilds on demand.
        ///
        /// The path lists are ordered by <see cref="Normalize"/>, so the sequence comparisons below behave as
        /// set comparisons: two scopes describing the same scenes match even though the query that produced
        /// them promises nothing about order.
        /// </remarks>
        internal bool MatchesCacheOf(ReferenceAuditScope other) =>
            other != null
            && IncludeOpenScenes == other.IncludeOpenScenes
            && IncludeScriptableObjects == other.IncludeScriptableObjects
            && MayOpenScenes == other.MayOpenScenes
            && PrefabCoverageRequired == other.PrefabCoverageRequired
            && ReferenceEquals(Policy, other.Policy)
            // Delegate equality compares target and method, so the same predicate from the same owner
            // matches while a different caller's filter does not.
            && Equals(IsIgnored, other.IsIgnored)
            && ExplicitScenePaths.SequenceEqual(other.ExplicitScenePaths, StringComparer.OrdinalIgnoreCase)
            && PrefabScanPaths.SequenceEqual(other.PrefabScanPaths, StringComparer.OrdinalIgnoreCase);

        /// <summary>True when the caller's ignore filter excludes <paramref name="assetPath"/>.</summary>
        /// <param name="assetPath">Project-relative asset path.</param>
        public bool ShouldSkip(string assetPath) =>
            ReferenceAssetPolicy.IsExcludedFromScan(assetPath) || (IsIgnored?.Invoke(assetPath) ?? false);

        /// <summary>
        /// Cleans a caller's path list into the canonical form a scope stores: trimmed, forward-slashed,
        /// deduplicated and ordered.
        /// </summary>
        /// <param name="paths">The caller's paths. Null yields an empty list.</param>
        /// <remarks>
        /// Ordering is part of the contract, for two reasons. It makes <see cref="MatchesCacheOf"/> a set
        /// comparison in practice, so a cached full-project snapshot is not thrown away because
        /// <c>AssetDatabase.FindAssets</c> — whose order is unspecified — returned the same scenes in a
        /// different sequence; discarding it costs a rescan that now opens every scene in the project. And it
        /// makes scan order reproducible, which the prefab phase already relied on.
        /// </remarks>
        private static IReadOnlyList<string> Normalize(IEnumerable<string> paths) =>
            paths?.Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Replace('\\', '/').Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>();
    }
}
