using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Migration;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem
{
    /// <summary>Why a legacy site cannot be migrated automatically.</summary>
    public enum ReferenceScopeMigrationBlocker
    {
        /// <summary>Nothing blocks it.</summary>
        None = 0,

        /// <summary>The site stores no id, so there is nothing to re-home.</summary>
        NotAssigned = 1,

        /// <summary>No provider carries the stored id, so the correct scope cannot be determined.</summary>
        NoProvider = 2,

        /// <summary>
        /// More than one provider carries the stored id. Picking one would be a guess about which
        /// target the author meant.
        /// </summary>
        AmbiguousProvider = 3,

        /// <summary>The owning asset cannot be written.</summary>
        ReadOnly = 4,

        /// <summary>
        /// The site and its provider are in different assets and neither a scene nor a global scope
        /// can be justified from the data alone.
        /// </summary>
        CrossAssetNeedsChoice = 5,

        /// <summary>
        /// A prefab instance overrides this site's stored id, so a scope chosen for the source would be
        /// inherited by an instance pointing somewhere else.
        /// </summary>
        /// <remarks>
        /// Two distinct problems, one refusal. First, the scope is derived from where the <i>source's</i>
        /// id resolves; an instance overriding <c>refId</c> may target something in another scene
        /// entirely, and would inherit a narrowed scope under which its reference cannot resolve.
        /// Second, v2 renames the field — <c>refId</c> becomes <c>targetId</c> — so once the rewrite
        /// lands, the override names a field nothing reads and the instance silently falls back to the
        /// source's target.
        /// <para/>
        /// Neither is visible in the audit, because an override is serialized as a
        /// <c>propertyPath</c>/<c>value</c> modification rather than as a field on any object.
        /// </remarks>
        OverriddenByPrefabInstance = 6,
    }

    /// <summary>One legacy reference site and the v2 scope proposed for it.</summary>
    public sealed class ReferenceScopeMigration
    {
        /// <summary>The site being migrated.</summary>
        public string SiteKey { get; }

        /// <summary>Where the site lives, for display.</summary>
        public string AssetPath { get; }

        /// <summary>The serialized property path.</summary>
        public string PropertyPath { get; }

        /// <summary>The stored <c>(RefType, RefId)</c>, unchanged by migration.</summary>
        public string StoredTarget { get; }

        /// <summary>The provider this site resolves to, when exactly one does.</summary>
        public string ProviderKey { get; }

        /// <summary>The scope proposed for the migrated reference.</summary>
        public ReferenceScopeKind ProposedScope { get; }

        /// <summary>The scope id to write, empty for the global kinds.</summary>
        public string ProposedScopeId { get; }

        /// <summary>Why this site cannot be migrated automatically, or <see cref="ReferenceScopeMigrationBlocker.None"/>.</summary>
        public ReferenceScopeMigrationBlocker Blocker { get; }

        /// <summary>True when this migration can be applied without asking anyone anything.</summary>
        public bool IsAutomatic => Blocker == ReferenceScopeMigrationBlocker.None;

        /// <summary>Why this scope was chosen, or why none could be.</summary>
        public string Rationale { get; }

        internal ReferenceScopeMigration(
            string siteKey,
            string assetPath,
            string propertyPath,
            string storedTarget,
            string providerKey,
            ReferenceScopeKind proposedScope,
            string proposedScopeId,
            ReferenceScopeMigrationBlocker blocker,
            string rationale)
        {
            SiteKey = siteKey ?? string.Empty;
            AssetPath = assetPath ?? string.Empty;
            PropertyPath = propertyPath ?? string.Empty;
            StoredTarget = storedTarget ?? string.Empty;
            ProviderKey = providerKey ?? string.Empty;
            ProposedScope = proposedScope;
            ProposedScopeId = proposedScopeId ?? string.Empty;
            Blocker = blocker;
            Rationale = rationale ?? string.Empty;
        }

        /// <inheritdoc/>
        public override string ToString() =>
            IsAutomatic
                ? $"{PropertyPath} → {ProposedScope} ({Rationale})"
                : $"{PropertyPath} → needs choice: {Rationale}";
    }

    /// <summary>The full migration proposal for one audit snapshot.</summary>
    public sealed class ReferenceScopeMigrationPlan
    {
        /// <summary>The snapshot revision this plan was built from.</summary>
        public long Revision { get; }

        /// <summary>Every legacy site considered.</summary>
        public IReadOnlyList<ReferenceScopeMigration> Migrations { get; }

        /// <summary>Migrations that can be applied without user input.</summary>
        public IReadOnlyList<ReferenceScopeMigration> Automatic { get; }

        /// <summary>Migrations that need someone to decide.</summary>
        public IReadOnlyList<ReferenceScopeMigration> NeedsChoice { get; }

        internal ReferenceScopeMigrationPlan(long revision, IReadOnlyList<ReferenceScopeMigration> migrations)
        {
            Revision = revision;
            Migrations = migrations ?? Array.Empty<ReferenceScopeMigration>();
            Automatic = Migrations.Where(m => m.IsAutomatic).ToList();
            NeedsChoice = Migrations.Where(m => !m.IsAutomatic).ToList();
        }

        /// <summary>One-line summary for the Hub.</summary>
        public string Describe() =>
            Migrations.Count == 0
                ? "No legacy references to migrate."
                : $"{Migrations.Count} legacy reference(s): {Automatic.Count} unambiguous, "
                  + $"{NeedsChoice.Count} need a decision";
    }

    /// <summary>
    /// Proposes a v2 scope for each v1 reference site, and says plainly which ones it cannot decide.
    /// </summary>
    /// <remarks>
    /// <para>Planning only — nothing here writes. The plan is what a preview shows before anything is
    /// applied, which is the rule the whole repair system is built on: no bulk migration happens
    /// without someone having seen what it would do.</para>
    ///
    /// <para>The planner is deliberately conservative. It proposes a narrower scope only when the
    /// data forces one conclusion: the site and its single provider are inside the same prefab, or
    /// the same scene. Everything else stays <see cref="ReferenceScopeKind.LegacyGlobal"/> or is
    /// handed back as a decision. A wrong scope is worse than no migration — it turns a working
    /// reference into one that cannot resolve, and does it silently across a whole project.</para>
    /// </remarks>
    public static class ReferenceScopeMigrationPlanner
    {
        /// <summary>The v1 identity fields an instance can override.</summary>
        /// <remarks>
        /// Spelled without the underscore because that is how <c>SceneObjectReference</c> serializes
        /// them. v2's counterparts (<c>targetId</c>, <c>expectedRefType</c>) are named differently on
        /// purpose, which is exactly why an override of the v1 names does not survive the rewrite.
        /// </remarks>
        private static readonly string[] LegacyIdentityFields = { "refId", "refType" };

        /// <summary>Builds the migration proposal for a snapshot.</summary>
        /// <param name="snapshot">The audit to plan from.</param>
        /// <returns>The plan; never null.</returns>
        public static ReferenceScopeMigrationPlan Plan(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null)
                return new ReferenceScopeMigrationPlan(0, Array.Empty<ReferenceScopeMigration>());

            // One pass over the providers, so a large project does not turn this into a quadratic scan.
            var byId = new Dictionary<string, List<ReferenceProviderRecord>>(StringComparer.Ordinal);
            foreach (var provider in snapshot.Providers)
            {
                if (string.IsNullOrEmpty(provider.RefId))
                    continue;

                if (!byId.TryGetValue(provider.RefId, out var list))
                    byId[provider.RefId] = list = new List<ReferenceProviderRecord>();

                list.Add(provider);
            }

            // One project-wide scan for every site, and only when there is at least one to ask about.
            var overrides = snapshot.Sites.Count == 0
                ? null
                : PrefabInstanceOverrideIndex.Scan(IsLegacyIdentityPath, LegacyIdentityFields);

            var migrations = snapshot.Sites.Select(site => PlanSite(site, byId, overrides)).ToList();
            return new ReferenceScopeMigrationPlan(snapshot.Revision, migrations);
        }

        /// <summary>Whether a serialized path reaches one of a v1 reference's identity fields.</summary>
        private static bool IsLegacyIdentityPath(string propertyPath) =>
            LegacyIdentityFields.Any(field =>
                propertyPath.EndsWith("." + field, StringComparison.Ordinal)
                || string.Equals(propertyPath, field, StringComparison.Ordinal));

        private static ReferenceScopeMigration PlanSite(
            ReferenceSiteRecord site, Dictionary<string, List<ReferenceProviderRecord>> byId,
            PrefabInstanceOverrideSnapshot overrides)
        {
            string storedTarget = site.IsAssigned ? $"{site.StoredRefType}:{site.StoredRefId}" : string.Empty;
            string ownerPath = site.OwnerLocator.AssetPath;

            if (!site.IsAssigned)
            {
                return Blocked(site, storedTarget, ReferenceScopeMigrationBlocker.NotAssigned,
                    "the reference is unset, so it carries no identity to re-home");
            }

            if (site.IsReadOnly)
            {
                return Blocked(site, storedTarget, ReferenceScopeMigrationBlocker.ReadOnly,
                    "the owning asset is not writable");
            }

            if (!byId.TryGetValue(site.StoredRefId, out var candidates) || candidates.Count == 0)
            {
                return Blocked(site, storedTarget, ReferenceScopeMigrationBlocker.NoProvider,
                    "no provider carries this id, so the correct scope cannot be determined");
            }

            // Checked before any scope is proposed, because the answer invalidates the proposal rather
            // than qualifying it: a scope derived from the source's id is not a scope the overriding
            // instance can inherit.
            var overriding = OverridingInstances(site, overrides);
            if (overriding.Count > 0)
            {
                return Blocked(site, storedTarget, ReferenceScopeMigrationBlocker.OverriddenByPrefabInstance,
                    $"{overriding.Count} prefab instance(s) override this reference's id "
                    + $"({string.Join(", ", overriding.Take(3))}"
                    + (overriding.Count > 3 ? ", …" : "")
                    + "); a scope chosen for this asset's id would be inherited by an instance pointing "
                    + "elsewhere, and v2 renames the field so the override would stop being read");
            }

            // Prefer the provider whose RefType also matches; falling straight to the id-only set
            // would call a perfectly exact reference ambiguous whenever some unrelated type reused
            // the id.
            var exact = candidates
                .Where(p => string.Equals(p.RefType, site.StoredRefType, StringComparison.Ordinal))
                .ToList();
            var considered = exact.Count > 0 ? exact : candidates;

            if (considered.Count > 1)
            {
                return Blocked(site, storedTarget, ReferenceScopeMigrationBlocker.AmbiguousProvider,
                    $"{considered.Count} providers carry this id; choosing one would be a guess about "
                    + "which target was meant");
            }

            var provider = considered[0];
            string providerPath = provider.Locator.AssetPath;

            // Same prefab asset: the reference is internal wiring, which is exactly what prefab-local
            // scope exists for — and the only scope under which duplicating the prefab keeps working.
            if (site.SourceKind == ReferenceSiteSourceKind.PrefabAsset &&
                provider.Kind == ReferenceProviderKind.PrefabComponent &&
                SamePath(ownerPath, providerPath))
            {
                return new ReferenceScopeMigration(
                    site.SiteKey, ownerPath, site.PropertyPath, storedTarget, provider.ProviderKey,
                    ReferenceScopeKind.PrefabLocal, ownerPath, ReferenceScopeMigrationBlocker.None,
                    "site and target are inside the same prefab, so the reference is internal wiring");
            }

            // Same scene: scene scope is the narrowest claim the data supports.
            if (site.SourceKind == ReferenceSiteSourceKind.Scene &&
                provider.Kind == ReferenceProviderKind.SceneComponent &&
                SamePath(ownerPath, providerPath))
            {
                return new ReferenceScopeMigration(
                    site.SiteKey, ownerPath, site.PropertyPath, storedTarget, provider.ProviderKey,
                    ReferenceScopeKind.Scene, ownerPath, ReferenceScopeMigrationBlocker.None,
                    "site and target are in the same scene");
            }

            // A ScriptableObject or contributed site has no scene or prefab of its own to scope to,
            // and its single target is unique project-wide, so Global is the honest description.
            if (site.SourceKind == ReferenceSiteSourceKind.ScriptableObjectAsset ||
                site.SourceKind == ReferenceSiteSourceKind.Contributed)
            {
                return new ReferenceScopeMigration(
                    site.SiteKey, ownerPath, site.PropertyPath, storedTarget, provider.ProviderKey,
                    ReferenceScopeKind.Global, null, ReferenceScopeMigrationBlocker.None,
                    "the owning asset has no scene scope and the target is unique project-wide");
            }

            return Blocked(site, storedTarget, ReferenceScopeMigrationBlocker.CrossAssetNeedsChoice,
                $"the target lives in '{Short(providerPath)}' rather than alongside the site; "
                + "Scene or Global must be chosen deliberately");
        }

        /// <summary>The assets holding prefab instances that override this site's stored id.</summary>
        private static IReadOnlyList<string> OverridingInstances(
            ReferenceSiteRecord site, PrefabInstanceOverrideSnapshot overrides)
        {
            if (overrides == null || string.IsNullOrEmpty(site.OwnerLocator.AssetGuid))
                return Array.Empty<string>();

            // Scoped to this one reference field: a component may hold several, and an override of one
            // says nothing about the scope the others can take.
            string prefix = site.PropertyPath + ".";

            return overrides
                .ForObject(site.OwnerLocator.AssetGuid, site.OwnerLocator.LocalFileId)
                .Where(entry => entry.PropertyPath.StartsWith(prefix, StringComparison.Ordinal))
                .Select(entry => entry.ContainingAssetPath)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static ReferenceScopeMigration Blocked(
            ReferenceSiteRecord site, string storedTarget, ReferenceScopeMigrationBlocker blocker, string rationale) =>
            new ReferenceScopeMigration(
                site.SiteKey, site.OwnerLocator.AssetPath, site.PropertyPath, storedTarget, null,
                ReferenceScopeKind.LegacyGlobal, null, blocker, rationale);

        private static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);

        private static string Short(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "<unknown>";

            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }
    }
}
