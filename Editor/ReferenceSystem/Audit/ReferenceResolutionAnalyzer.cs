using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>
    /// The outcome of analysing one reference site, plus the candidates that produced it.
    /// </summary>
    public sealed class ReferenceSiteResolution
    {
        /// <summary>The site that was analysed.</summary>
        public ReferenceSiteRecord Site { get; }

        /// <summary>What resolution would do at runtime.</summary>
        public ReferenceResolveOutcome Outcome { get; }

        /// <summary>Providers that matched. One entry for a successful resolve, several for a collision.</summary>
        public IReadOnlyList<ReferenceProviderRecord> Candidates { get; }

        /// <summary>The single resolved provider, or null when the outcome is not a success.</summary>
        public ReferenceProviderRecord Resolved =>
            Outcome == ReferenceResolveOutcome.ResolvedExact || Outcome == ReferenceResolveOutcome.ResolvedViaLegacyFallback
                ? Candidates.FirstOrDefault()
                : null;

        /// <summary>True when runtime resolution would hand the caller an object.</summary>
        public bool IsSuccess => Resolved != null;

        internal ReferenceSiteResolution(
            ReferenceSiteRecord site, ReferenceResolveOutcome outcome, IReadOnlyList<ReferenceProviderRecord> candidates)
        {
            Site = site;
            Outcome = outcome;
            Candidates = candidates ?? Array.Empty<ReferenceProviderRecord>();
        }
    }

    /// <summary>
    /// The one place that decides what a stored reference resolves to. Pure: no Unity API, no GUI, no
    /// asset IO — so it is unit-testable and cannot drift from the runtime rules it mirrors.
    /// </summary>
    /// <remarks>
    /// The algorithm deliberately reproduces <c>SceneObjectReference.TryResolveCore</c>:
    /// exact <c>(RefType, RefId)</c> first, then an ID-only compatibility fallback that <b>fails</b>
    /// rather than guesses when more than one provider qualifies. Consumers that invented their own
    /// rule are what allowed a build to pass on an ID-only existence check while the runtime rejected
    /// the same reference as ambiguous.
    ///
    /// Only providers reported as runtime-resolvable participate in resolution. A prefab-asset or
    /// ScriptableObject provider carrying the same id is used to <i>explain</i> a miss, never to satisfy
    /// it — an uninstantiated prefab cannot answer a lookup.
    /// </remarks>
    public static class ReferenceResolutionAnalyzer
    {
        /// <summary>
        /// An index over providers supporting exact-key lookup, ID-only fallback, and duplicate
        /// detection. Build once per snapshot and reuse across every site.
        /// </summary>
        public sealed class ProviderIndex
        {
            // Exact (RefType, RefId) -> providers. More than one entry is a genuine collision:
            // runtime registration rejects the second, so load order decides the winner.
            // Keyed on a tuple rather than ReferenceId because ReferenceId's constructor rejects an
            // empty type or id, and both occur in real serialized data.
            private readonly Dictionary<(string Type, string Id), List<ReferenceProviderRecord>> _exact = new();

            // RefId -> providers across all types. Used only by the compatibility fallback, so a
            // legal same-id/different-type pair is never mistaken for a duplicate.
            private readonly Dictionary<string, List<ReferenceProviderRecord>> _byIdOnly =
                new(StringComparer.Ordinal);

            // Providers that cannot answer a runtime lookup, kept to explain a miss.
            private readonly Dictionary<string, List<ReferenceProviderRecord>> _inertById =
                new(StringComparer.Ordinal);

            /// <summary>Every provider in the index, resolvable or not.</summary>
            public IReadOnlyList<ReferenceProviderRecord> All { get; }

            internal ProviderIndex(IEnumerable<ReferenceProviderRecord> providers)
            {
                var all = new List<ReferenceProviderRecord>();

                foreach (var provider in providers ?? Array.Empty<ReferenceProviderRecord>())
                {
                    if (provider == null)
                        continue;

                    all.Add(provider);

                    // A provider with no id cannot be referenced; ProviderIdMissing reports it.
                    if (string.IsNullOrEmpty(provider.RefId))
                        continue;

                    if (!provider.IsRuntimeResolvable)
                    {
                        Add(_inertById, provider.RefId, provider);
                        continue;
                    }

                    Add(_exact, (provider.RefType, provider.RefId), provider);
                    Add(_byIdOnly, provider.RefId, provider);
                }

                All = all;
            }

            private static void Add<TKey>(
                Dictionary<TKey, List<ReferenceProviderRecord>> map, TKey key, ReferenceProviderRecord provider)
            {
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<ReferenceProviderRecord>(1);
                    map[key] = list;
                }
                list.Add(provider);
            }

            /// <summary>Providers claiming exactly this <c>(RefType, RefId)</c>.</summary>
            public IReadOnlyList<ReferenceProviderRecord> MatchExact(string refType, string refId) =>
                !string.IsNullOrEmpty(refId) && _exact.TryGetValue((refType ?? string.Empty, refId), out var list)
                    ? list
                    : Array.Empty<ReferenceProviderRecord>();

            /// <summary>Runtime-resolvable providers carrying this Ref Id under any Ref Type.</summary>
            public IReadOnlyList<ReferenceProviderRecord> MatchIdOnly(string refId) =>
                !string.IsNullOrEmpty(refId) && _byIdOnly.TryGetValue(refId, out var list)
                    ? list
                    : Array.Empty<ReferenceProviderRecord>();

            /// <summary>Non-resolvable providers (prefab assets, ScriptableObjects) carrying this Ref Id.</summary>
            public IReadOnlyList<ReferenceProviderRecord> MatchInert(string refId) =>
                !string.IsNullOrEmpty(refId) && _inertById.TryGetValue(refId, out var list)
                    ? list
                    : Array.Empty<ReferenceProviderRecord>();

            /// <summary>
            /// Every exact key claimed by more than one provider. Reported once per key rather than once
            /// per referencing site, so a widely-referenced collision does not flood the results.
            /// </summary>
            public IEnumerable<IReadOnlyList<ReferenceProviderRecord>> Collisions() =>
                _exact.Where(kv => kv.Value.Count > 1)
                    .OrderBy(kv => kv.Key.Type, StringComparer.Ordinal)
                    .ThenBy(kv => kv.Key.Id, StringComparer.Ordinal)
                    .Select(kv => (IReadOnlyList<ReferenceProviderRecord>)kv.Value)
                    .ToList();
        }

        /// <summary>Builds a reusable provider index.</summary>
        /// <param name="providers">Discovered providers. Null entries are ignored.</param>
        public static ProviderIndex BuildIndex(IEnumerable<ReferenceProviderRecord> providers) =>
            new ProviderIndex(providers);

        /// <summary>
        /// Resolves one site against the index using the runtime rules.
        /// </summary>
        /// <param name="index">The provider index.</param>
        /// <param name="site">The site to resolve.</param>
        /// <returns>The outcome and the candidates that produced it.</returns>
        public static ReferenceSiteResolution Resolve(ProviderIndex index, ReferenceSiteRecord site)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (site == null) throw new ArgumentNullException(nameof(site));

            if (!site.IsAssigned)
                return new ReferenceSiteResolution(site, ReferenceResolveOutcome.NotAssigned, null);

            // 1. Exact key, exactly as the runtime registry is keyed.
            var exact = index.MatchExact(site.StoredRefType, site.StoredRefId);
            if (exact.Count > 1)
                return new ReferenceSiteResolution(site, ReferenceResolveOutcome.DuplicateProvider, exact);

            if (exact.Count == 1)
                return WithTypeCheck(site, exact[0], ReferenceResolveOutcome.ResolvedExact);

            // 2. Compatibility fallback: the Ref Type may have been renamed after the reference was
            //    assigned. Recoverable for a single candidate; the runtime refuses to guess otherwise.
            var byId = index.MatchIdOnly(site.StoredRefId);
            if (byId.Count > 1)
                return new ReferenceSiteResolution(site, ReferenceResolveOutcome.AmbiguousFallback, byId);

            if (byId.Count == 1)
                return WithTypeCheck(site, byId[0], ReferenceResolveOutcome.ResolvedViaLegacyFallback);

            // 3. Nothing resolvable. Surface an inert same-id provider so the message can say
            //    "lives in a prefab that nothing instantiated" instead of just "missing".
            return new ReferenceSiteResolution(
                site, ReferenceResolveOutcome.ProviderMissing, index.MatchInert(site.StoredRefId));
        }

        /// <summary>
        /// Applies the site's expected-type constraint. A <c>SceneObjectReference&lt;T&gt;</c> field
        /// promises the caller a <c>T</c>; a provider that is not one fails at the cast, not the lookup.
        /// </summary>
        private static ReferenceSiteResolution WithTypeCheck(
            ReferenceSiteRecord site, ReferenceProviderRecord provider, ReferenceResolveOutcome success)
        {
            var candidates = new[] { provider };

            // A null expected or provider type means an assembly is missing or the field is untyped —
            // neither is evidence of a mismatch, so do not manufacture one.
            if (site.ExpectedRuntimeType == null || provider.RuntimeType == null)
                return new ReferenceSiteResolution(site, success, candidates);

            return site.ExpectedRuntimeType.IsAssignableFrom(provider.RuntimeType)
                ? new ReferenceSiteResolution(site, success, candidates)
                : new ReferenceSiteResolution(site, ReferenceResolveOutcome.WrongRuntimeType, candidates);
        }

        /// <summary>
        /// Turns providers, sites and coverage into the finding list every consumer projects.
        /// </summary>
        /// <param name="providers">Discovered providers.</param>
        /// <param name="sites">Discovered reference sites.</param>
        /// <param name="coverage">What the scan covered; gaps become a <c>REF016</c> finding.</param>
        /// <param name="policy">Severity policy; <see cref="ReferenceSeverityPolicy.Default"/> when null.</param>
        /// <param name="scanErrors">
        /// Assets or objects the scanner could not read; each becomes a <c>REF015</c> finding. An
        /// unreadable asset makes the result unknown, not clean, so these are never dropped.
        /// </param>
        /// <param name="sceneAvailability">
        /// Resolves whether a target scene is loaded alongside an owner scene, normally
        /// <see cref="ReferenceLoadSetStore.Evaluate"/>. Null skips cross-scene availability checking
        /// entirely.
        /// </param>
        /// <returns>Findings in deterministic order, plus the per-site resolutions that produced them.</returns>
        /// <remarks>
        /// Availability arrives as a delegate rather than being read from project settings here, so this
        /// stays a pure function of its inputs — the property that lets one set of rules serve the
        /// editor, the build gate and runtime-compatible tests without any of them being able to drift.
        /// </remarks>
        public static ReferenceAnalysisResult Analyze(
            IReadOnlyList<ReferenceProviderRecord> providers,
            IReadOnlyList<ReferenceSiteRecord> sites,
            ReferenceCoverage coverage = null,
            ReferenceSeverityPolicy policy = null,
            IReadOnlyList<string> scanErrors = null,
            Func<string, string, ReferenceSceneAvailability> sceneAvailability = null)
        {
            providers ??= Array.Empty<ReferenceProviderRecord>();
            sites ??= Array.Empty<ReferenceSiteRecord>();
            coverage ??= ReferenceCoverage.Empty;
            policy ??= ReferenceSeverityPolicy.Default;

            var index = BuildIndex(providers);
            var findings = new List<ReferenceFinding>();
            var resolutions = new List<ReferenceSiteResolution>(sites.Count);

            // Provider-side findings first: an id-less provider is a defect regardless of who
            // references it, and a collision is reported once per key rather than per referencing site.
            foreach (var provider in providers.Where(p => p != null && string.IsNullOrEmpty(p.RefId)))
            {
                findings.Add(new ReferenceFinding(
                    ReferenceFindingCode.ProviderIdMissing,
                    policy.SeverityFor(ReferenceFindingCode.ProviderIdMissing),
                    "Provider has no Ref Id",
                    $"'{Describe(provider)}' exposes reference type \"{provider.RefType}\" but carries no Ref Id, "
                    + "so nothing can reference it.",
                    provider.Locator.AssetPath,
                    candidateProviderKeys: new[] { provider.ProviderKey }));
            }

            foreach (var collision in index.Collisions())
            {
                var first = collision[0];
                findings.Add(new ReferenceFinding(
                    ReferenceFindingCode.DuplicateProvider,
                    policy.SeverityFor(ReferenceFindingCode.DuplicateProvider),
                    "Duplicate provider",
                    $"Ref Id \"{first.RefId}\" (type \"{first.RefType}\") is claimed by {collision.Count} providers: "
                    + string.Join(", ", collision.Select(Describe))
                    + ". Runtime registration rejects all but the first to register, so which one wins depends "
                    + "on load order. Give each provider its own Ref Id.",
                    first.Locator.AssetPath,
                    candidateProviderKeys: collision.Select(p => p.ProviderKey).ToList(),
                    outcome: ReferenceResolveOutcome.DuplicateProvider));
            }

            foreach (var site in sites)
            {
                if (site == null)
                    continue;

                var resolution = Resolve(index, site);
                resolutions.Add(resolution);

                var finding = DescribeSiteFinding(resolution, policy);
                if (finding != null)
                    findings.Add(finding);

                var scopeFinding = DescribeScopeFinding(resolution, policy, sceneAvailability);
                if (scopeFinding != null)
                    findings.Add(scopeFinding);
            }

            foreach (var error in (scanErrors ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal))
            {
                findings.Add(new ReferenceFinding(
                    ReferenceFindingCode.AssetScanFailed,
                    policy.SeverityFor(ReferenceFindingCode.AssetScanFailed),
                    "Asset could not be scanned",
                    error + " The reference state of this asset is unknown, not clean.",
                    outcome: ReferenceResolveOutcome.CoverageIncomplete));
            }

            // Coverage last: it qualifies everything above rather than describing one site.
            foreach (var gap in coverage.Gaps)
            {
                findings.Add(new ReferenceFinding(
                    ReferenceFindingCode.CoveragePartial,
                    policy.SeverityFor(ReferenceFindingCode.CoveragePartial),
                    "Coverage incomplete",
                    $"{gap.Category} was not fully scanned ({gap.Status}: {gap.Reason}). "
                    + "Reference findings are therefore incomplete — this is not a clean result.",
                    outcome: ReferenceResolveOutcome.CoverageIncomplete));
            }

            return new ReferenceAnalysisResult(
                index, resolutions, ReferenceFinding.InStableOrder(findings).ToList(), coverage);
        }

        /// <summary>
        /// True when a prefab-local site points at a target inside its own prefab — the internal wiring
        /// prefab-local scope exists to express.
        /// </summary>
        /// <remarks>
        /// Such a reference resolves inside every live instance, so the "target is only a template"
        /// warning does not apply to it. Before scopes existed there was no way to tell this apart from a
        /// reference that genuinely depended on someone instantiating the prefab first.
        ///
        /// Deliberately independent of whether a scope root is present: a missing root is reported as
        /// <c>REF007</c>, and adding a second, differently-worded finding for the same mistake would just
        /// make the real one harder to find.
        /// </remarks>
        private static bool IsPrefabInternalWiring(
            ReferenceSiteRecord site, ReferenceSiteResolution resolution)
        {
            if (site.ScopeKind != ReferenceScopeKind.PrefabLocal)
                return false;

            if (site.SourceKind != ReferenceSiteSourceKind.PrefabAsset)
                return false;

            string ownerPath = site.OwnerLocator.AssetPath;
            if (string.IsNullOrEmpty(ownerPath))
                return false;

            return resolution.Candidates.Count > 0 &&
                   resolution.Candidates.All(c =>
                       c.Kind == ReferenceProviderKind.PrefabComponent &&
                       string.Equals(c.Locator.AssetPath, ownerPath, StringComparison.Ordinal));
        }

        /// <summary>
        /// Findings about a site's declared <i>scope</i> rather than about what it resolved to.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="DescribeSiteFinding"/> because the two are independent: a
        /// prefab-local reference can point at a perfectly real target and still be broken because there
        /// is no scope root to resolve it against. Folding them together would let a healthy resolution
        /// mask a scope defect.
        /// </remarks>
        private static ReferenceFinding DescribeScopeFinding(
            ReferenceSiteResolution resolution,
            ReferenceSeverityPolicy policy,
            Func<string, string, ReferenceSceneAvailability> sceneAvailability)
        {
            var site = resolution.Site;

            // A prefab-local key is resolved relative to the nearest scope root. Without one the runtime
            // refuses the registration and the resolve reports WrongScope, neither of which says what is
            // actually missing.
            if (site.ScopeKind == ReferenceScopeKind.PrefabLocal && string.IsNullOrEmpty(site.ScopeRootId))
            {
                return new ReferenceFinding(
                    ReferenceFindingCode.ScopeRootMissing,
                    policy.SeverityFor(ReferenceFindingCode.ScopeRootMissing),
                    "Prefab-local reference has no scope root",
                    $"{site.Describe()} is scoped Prefab Local but nothing above it has a "
                    + "ReferenceScopeRoot, so there is no scope to resolve the target in. Add a "
                    + "ReferenceScopeRoot to the prefab root, or change the field's scope.",
                    site.OwnerLocator.AssetPath, site.SiteKey, outcome: ReferenceResolveOutcome.WrongScope);
            }

            if (sceneAvailability == null || !resolution.IsSuccess || resolution.Candidates.Count != 1)
                return null;

            string ownerScene = site.SourceKind == ReferenceSiteSourceKind.Scene
                ? site.OwnerLocator.AssetPath
                : null;
            var provider = resolution.Candidates[0];
            string targetScene = provider.Kind == ReferenceProviderKind.SceneComponent
                ? provider.Locator.AssetPath
                : null;

            if (string.IsNullOrEmpty(ownerScene) || string.IsNullOrEmpty(targetScene))
                return null;

            // Conditional availability means the author already said this only resolves under a named
            // condition, so an unavailable pairing is the declared state rather than a defect.
            if (site.Availability == ReferenceAvailabilityPolicy.Conditional)
                return null;

            if (sceneAvailability(ownerScene, targetScene) != ReferenceSceneAvailability.Unavailable)
                return null;

            return new ReferenceFinding(
                ReferenceFindingCode.SceneUnavailable,
                policy.SeverityFor(ReferenceFindingCode.SceneUnavailable),
                "Target scene is never loaded with the owner",
                $"{site.Describe()} resolves to '{Describe(provider)}', but no declared load set ever has "
                + $"'{targetScene}' loaded alongside '{ownerScene}', so this reference cannot resolve at "
                + "runtime. Add the scene to a load set, or move the target.",
                site.OwnerLocator.AssetPath, site.SiteKey,
                new[] { provider.ProviderKey }, ReferenceResolveOutcome.ProviderNotLoaded);
        }

        /// <summary>
        /// Maps one resolution to a finding, or null when the outcome needs no reporting.
        /// </summary>
        private static ReferenceFinding DescribeSiteFinding(
            ReferenceSiteResolution resolution, ReferenceSeverityPolicy policy)
        {
            var site = resolution.Site;
            var candidateKeys = resolution.Candidates.Select(c => c.ProviderKey).ToList();

            switch (resolution.Outcome)
            {
                case ReferenceResolveOutcome.ResolvedExact:
                    return null;

                // An unset reference is legal unless the author declared otherwise. Before requiredness
                // existed there was no "otherwise", so a field somebody forgot to wire was
                // indistinguishable from one deliberately left empty and nothing could be validated
                // before Play Mode.
                case ReferenceResolveOutcome.NotAssigned:
                    if (!site.RequiresTarget)
                        return null;

                    return new ReferenceFinding(
                        ReferenceFindingCode.RequiredReferenceUnset,
                        policy.SeverityFor(ReferenceFindingCode.RequiredReferenceUnset),
                        "Required reference is unset",
                        $"{site.Describe()} is declared {site.Requiredness} but has no target, so it cannot "
                        + "resolve. Assign a target, or declare the field Optional if being unset is "
                        + "genuinely legal.",
                        site.OwnerLocator.AssetPath, site.SiteKey, candidateKeys, resolution.Outcome);

                case ReferenceResolveOutcome.ResolvedViaLegacyFallback:
                {
                    var provider = resolution.Candidates[0];
                    return new ReferenceFinding(
                        ReferenceFindingCode.WrongRefTypeMetadata,
                        policy.SeverityFor(ReferenceFindingCode.WrongRefTypeMetadata),
                        "Stale Ref Type",
                        $"{site.Describe()} stores Ref Type \"{site.StoredRefType}\" but Ref Id \"{site.StoredRefId}\" "
                        + $"is now provided by '{Describe(provider)}' with type \"{provider.RefType}\". It still "
                        + "resolves through the compatibility fallback; re-assign the field to refresh the metadata.",
                        site.OwnerLocator.AssetPath, site.SiteKey, candidateKeys, resolution.Outcome);
                }

                case ReferenceResolveOutcome.DuplicateProvider:
                    // The collision itself is already reported once per key; add the referencing site so
                    // the finding can be navigated from either end.
                    return new ReferenceFinding(
                        ReferenceFindingCode.DuplicateProvider,
                        policy.SeverityFor(ReferenceFindingCode.DuplicateProvider),
                        "Reference points at a duplicated Ref Id",
                        $"{site.Describe()} points at Ref Id \"{site.StoredRefId}\" (type \"{site.StoredRefType}\"), "
                        + $"which {resolution.Candidates.Count} providers claim: "
                        + string.Join(", ", resolution.Candidates.Select(Describe))
                        + ". Which one it resolves to depends on load order.",
                        site.OwnerLocator.AssetPath, site.SiteKey, candidateKeys, resolution.Outcome);

                case ReferenceResolveOutcome.AmbiguousFallback:
                    return new ReferenceFinding(
                        ReferenceFindingCode.AmbiguousLegacyFallback,
                        policy.SeverityFor(ReferenceFindingCode.AmbiguousLegacyFallback),
                        "Ambiguous compatibility fallback",
                        $"{site.Describe()} stores Ref Type \"{site.StoredRefType}\", which no provider carries, and "
                        + $"Ref Id \"{site.StoredRefId}\" is carried by {resolution.Candidates.Count} providers under "
                        + $"other types: {string.Join(", ", resolution.Candidates.Select(Describe))}. The runtime "
                        + "rejects an ambiguous fallback, so this reference resolves to nothing. Re-assign the field.",
                        site.OwnerLocator.AssetPath, site.SiteKey, candidateKeys, resolution.Outcome);

                case ReferenceResolveOutcome.WrongRuntimeType:
                {
                    var provider = resolution.Candidates[0];
                    return new ReferenceFinding(
                        ReferenceFindingCode.WrongRuntimeType,
                        policy.SeverityFor(ReferenceFindingCode.WrongRuntimeType),
                        "Wrong target type",
                        $"{site.Describe()} expects a {site.ExpectedRuntimeTypeName}, but Ref Id "
                        + $"\"{site.StoredRefId}\" resolves to '{Describe(provider)}' of type "
                        + $"{provider.RuntimeTypeName}. The cast fails at runtime.",
                        site.OwnerLocator.AssetPath, site.SiteKey, candidateKeys, resolution.Outcome);
                }

                case ReferenceResolveOutcome.ProviderMissing:
                {
                    // A prefab-local reference to a target inside its own prefab is not an unresolvable
                    // template reference — it is the whole point of prefab-local scope, and it resolves
                    // inside every instance. Warning about it would report the correct pattern as a
                    // problem, which is worse than saying nothing.
                    if (IsPrefabInternalWiring(site, resolution))
                        return null;

                    // An inert same-id provider means the target exists but only as a template. That is a
                    // legitimate runtime-instantiation pattern, so it warns rather than errors.
                    var inert = resolution.Candidates.Count > 0;
                    var summary = inert
                        ? $"{site.Describe()} points at Ref Id \"{site.StoredRefId}\" (type \"{site.StoredRefType}\"), "
                          + $"which is carried only by {string.Join(", ", resolution.Candidates.Select(Describe))}. "
                          + "That is not a runtime-resolvable target: it resolves only if that object is instantiated "
                          + "into a loaded scene before the reference is read."
                        : $"{site.Describe()} points at Ref Id \"{site.StoredRefId}\" (type \"{site.StoredRefType}\"), "
                          + "which no scanned provider carries. It will fail to resolve at runtime.";

                    return new ReferenceFinding(
                        ReferenceFindingCode.MissingProvider,
                        inert
                            ? ReferenceFindingSeverity.Warning
                            : policy.SeverityFor(ReferenceFindingCode.MissingProvider),
                        inert ? "Target is not runtime-resolvable" : "Missing provider",
                        summary,
                        site.OwnerLocator.AssetPath, site.SiteKey, candidateKeys, resolution.Outcome);
                }

                default:
                    return null;
            }
        }

        /// <summary>Short provider description used inside finding text.</summary>
        private static string Describe(ReferenceProviderRecord provider)
        {
            var name = string.IsNullOrEmpty(provider.DisplayName)
                ? provider.Locator.ObjectPath
                : provider.DisplayName;
            var where = string.IsNullOrEmpty(provider.Locator.AssetPath)
                ? provider.Locator.ObjectPath
                : provider.Locator.AssetPath;
            return $"{name} ({provider.RuntimeTypeName}) in {where}";
        }
    }

    /// <summary>
    /// Everything one analysis pass produced: the provider index, per-site resolutions, findings, and
    /// the coverage that qualifies them.
    /// </summary>
    public sealed class ReferenceAnalysisResult
    {
        /// <summary>The provider index built for this pass.</summary>
        public ReferenceResolutionAnalyzer.ProviderIndex Index { get; }

        /// <summary>One resolution per analysed site, in input order.</summary>
        public IReadOnlyList<ReferenceSiteResolution> Resolutions { get; }

        /// <summary>Findings in deterministic order.</summary>
        public IReadOnlyList<ReferenceFinding> Findings { get; }

        /// <summary>What the scan covered.</summary>
        public ReferenceCoverage Coverage { get; }

        /// <summary>Findings at <see cref="ReferenceFindingSeverity.Error"/>.</summary>
        public IEnumerable<ReferenceFinding> Errors =>
            Findings.Where(f => f.Severity == ReferenceFindingSeverity.Error);

        /// <summary>Findings at <see cref="ReferenceFindingSeverity.Warning"/>.</summary>
        public IEnumerable<ReferenceFinding> Warnings =>
            Findings.Where(f => f.Severity == ReferenceFindingSeverity.Warning);

        internal ReferenceAnalysisResult(
            ReferenceResolutionAnalyzer.ProviderIndex index,
            IReadOnlyList<ReferenceSiteResolution> resolutions,
            IReadOnlyList<ReferenceFinding> findings,
            ReferenceCoverage coverage)
        {
            Index = index;
            Resolutions = resolutions;
            Findings = findings;
            Coverage = coverage;
        }
    }
}
