using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>
    /// Plans the changes an author makes on purpose: renaming a target, retyping it, re-pointing
    /// references at it, and moving it between scopes.
    /// </summary>
    /// <remarks>
    /// <para>A sibling of <see cref="ReferenceRepairPlanner"/>, not a second write path. Everything here
    /// produces a <see cref="ReferenceRepairPlan"/> that goes through <see cref="ReferenceRepairExecutor"/>,
    /// so an authoring edit gets the same preview, the same revision precondition, the same per-mutation
    /// verify, the same Undo group and the same measured after-report as a repair. There is deliberately no
    /// "just write it" entry point.</para>
    ///
    /// <para><b>Why authoring belongs here and not in the Inspector.</b> A Ref Id is a name that other
    /// objects have written down. Changing it in a field would leave every one of those objects pointing at
    /// a name nothing answers to — silently, and without the editor being able to tell afterwards what the
    /// old name was. Only a surface holding the whole audit knows the inbound set, so renaming is planned
    /// here and the component keeps its id field read-only.</para>
    ///
    /// <para><b>What it refuses.</b> Renaming or retyping onto an identity another provider already holds
    /// (that is authoring a <c>REF002</c> duplicate), and renaming a provider whose identity is <i>already</i>
    /// duplicated — with two claimants, nothing records which references meant which target, so carrying
    /// "the" inbound set would re-point some of them by coin flip. These are the same refusals the repair
    /// planner makes, for the same reason: a blanket id rewrite is exactly what used to point references at
    /// the wrong objects.</para>
    ///
    /// <para>Planning is pure. It reads a snapshot and allocates records; nothing here touches the project.
    /// Locating the serialized field behind a Ref Type happens at apply time, in the mutation.</para>
    /// </remarks>
    public static class ReferenceAuthoringPlanner
    {
        #region Rename

        /// <summary>
        /// Plans giving a provider a new Ref Id, moving every inbound reference with it.
        /// </summary>
        /// <param name="snapshot">The audit the key came from.</param>
        /// <param name="providerKey">The provider to rename.</param>
        /// <param name="newRefId">The id to give it.</param>
        /// <returns>
        /// A plan containing the provider change plus one change per inbound reference, or an empty plan
        /// whose warnings state the refusal.
        /// </returns>
        public static ReferenceRepairPlan PlanRename(
            ReferenceAuditSnapshot snapshot, string providerKey, string newRefId)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var provider = snapshot.FindProvider(providerKey);
            if (provider == null)
                return ReferenceRepairPlan.Empty(snapshot.Revision);

            newRefId = (newRefId ?? string.Empty).Trim();

            if (!Validate(snapshot, provider, newRefId, provider.RefType, out var refusal))
                return Refuse(snapshot, refusal);

            if (string.Equals(newRefId, provider.RefId, StringComparison.Ordinal))
                return Refuse(snapshot, $"'{provider.DisplayName}' already has Ref Id \"{newRefId}\".");

            var inbound = InboundSites(snapshot, provider);
            var mutations = new List<ReferenceRepairMutation>
            {
                new ReferenceProviderIdMutation(
                    ReferenceRepairKind.RenameProviderId,
                    ReferenceRepairApproval.RequiresUserChoice,
                    provider,
                    newRefId,
                    $"You renamed '{provider.DisplayName}' from \"{provider.RefId}\" to \"{newRefId}\". "
                    + $"{inbound.Count} reference(s) name it and move with it in this same plan."),
            };

            mutations.AddRange(CarryInbound(inbound, provider, newRefId, provider.RefType));

            return Build(snapshot, mutations, inbound, RenameWarnings(inbound, provider));
        }

        #endregion

        #region Retype

        /// <summary>
        /// Plans changing a provider's Ref Type, moving every inbound reference with it.
        /// </summary>
        /// <param name="snapshot">The audit the key came from.</param>
        /// <param name="providerKey">The provider to retype.</param>
        /// <param name="newRefType">The Ref Type to give it.</param>
        /// <returns>A plan, or an empty plan whose warnings state the refusal.</returns>
        public static ReferenceRepairPlan PlanRetype(
            ReferenceAuditSnapshot snapshot, string providerKey, string newRefType)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var provider = snapshot.FindProvider(providerKey);
            if (provider == null)
                return ReferenceRepairPlan.Empty(snapshot.Revision);

            newRefType = (newRefType ?? string.Empty).Trim();

            if (!Validate(snapshot, provider, provider.RefId, newRefType, out var refusal))
                return Refuse(snapshot, refusal);

            if (string.Equals(newRefType, provider.RefType, StringComparison.Ordinal))
                return Refuse(snapshot, $"'{provider.DisplayName}' already has Ref Type \"{newRefType}\".");

            var inbound = InboundSites(snapshot, provider);
            var mutations = new List<ReferenceRepairMutation>
            {
                new ReferenceProviderTypeMutation(
                    provider, newRefType,
                    $"You changed '{provider.DisplayName}' from Ref Type \"{provider.RefType}\" to "
                    + $"\"{newRefType}\". {inbound.Count} reference(s) store the old type and move with it."),
            };

            mutations.AddRange(CarryInbound(inbound, provider, provider.RefId, newRefType));

            return Build(snapshot, mutations, inbound, RenameWarnings(inbound, provider));
        }

        /// <summary>
        /// Plans folding one Ref Type into another across every provider that carries it.
        /// </summary>
        /// <param name="snapshot">The audit to plan against.</param>
        /// <param name="fromRefType">The Ref Type to retire.</param>
        /// <param name="toRefType">The Ref Type to keep.</param>
        /// <returns>A plan covering every affected provider, or an empty plan stating the refusal.</returns>
        /// <remarks>
        /// The vocabulary-cleanup case: a project accumulates <c>valve</c> beside <c>Valve</c> because Ref
        /// Type is free text, and every such near-duplicate is a <c>REF005</c> waiting to happen when a
        /// reference stores one spelling and its target the other. Refused wholesale — not partially applied —
        /// when any provider would collide after the merge, because a half-merged vocabulary is worse than an
        /// inconsistent one.
        /// </remarks>
        public static ReferenceRepairPlan PlanMergeRefType(
            ReferenceAuditSnapshot snapshot, string fromRefType, string toRefType)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            fromRefType = (fromRefType ?? string.Empty).Trim();
            toRefType = (toRefType ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(fromRefType) || string.IsNullOrEmpty(toRefType))
                return Refuse(snapshot, "Both the Ref Type being merged and its destination must be named.");

            if (string.Equals(fromRefType, toRefType, StringComparison.Ordinal))
                return Refuse(snapshot, $"\"{fromRefType}\" is already the destination Ref Type.");

            var moving = snapshot.Providers
                .Where(p => string.Equals(p.RefType, fromRefType, StringComparison.Ordinal))
                .ToList();

            if (moving.Count == 0)
                return Refuse(snapshot, $"No provider carries Ref Type \"{fromRefType}\".");

            var readOnly = moving.Where(p => p.IsReadOnly).ToList();
            if (readOnly.Count > 0)
            {
                return Refuse(
                    snapshot,
                    $"{readOnly.Count} provider(s) with Ref Type \"{fromRefType}\" live in read-only assets "
                    + $"({string.Join(", ", readOnly.Select(p => p.DisplayName))}), so the merge would leave "
                    + "the vocabulary half-moved.");
            }

            // Every id that would exist under the destination type afterwards, so a collision is caught
            // before anything is written rather than reported as a duplicate by the next audit.
            var destinationIds = new HashSet<string>(
                snapshot.Providers
                    .Where(p => string.Equals(p.RefType, toRefType, StringComparison.Ordinal))
                    .Select(p => p.RefId)
                    .Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.Ordinal);

            var collisions = new List<string>();
            foreach (var provider in moving.Where(p => !string.IsNullOrEmpty(p.RefId)))
            {
                if (!destinationIds.Add(provider.RefId))
                    collisions.Add($"\"{provider.RefId}\" ('{provider.DisplayName}')");
            }

            if (collisions.Count > 0)
            {
                return Refuse(
                    snapshot,
                    $"Merging \"{fromRefType}\" into \"{toRefType}\" would put {collisions.Count} duplicate "
                    + $"Ref Id(s) under one type: {string.Join(", ", collisions)}. Rename those first — a "
                    + "duplicate identity is a runtime failure, not a warning.");
            }

            var mutations = new List<ReferenceRepairMutation>();
            var inboundAll = new List<ReferenceSiteRecord>();

            foreach (var provider in moving)
            {
                var inbound = InboundSites(snapshot, provider);
                inboundAll.AddRange(inbound);

                mutations.Add(new ReferenceProviderTypeMutation(
                    provider, toRefType,
                    $"You merged Ref Type \"{fromRefType}\" into \"{toRefType}\"; this provider and its "
                    + $"{inbound.Count} inbound reference(s) move together."));

                mutations.AddRange(CarryInbound(inbound, provider, provider.RefId, toRefType));
            }

            return Build(snapshot, mutations, inboundAll, RenameWarnings(inboundAll, null));
        }

        #endregion

        #region Rewire

        /// <summary>
        /// Plans pointing a set of references at one provider.
        /// </summary>
        /// <param name="snapshot">The audit the keys came from.</param>
        /// <param name="siteKeys">The reference sites to move. Unknown keys are ignored.</param>
        /// <param name="providerKey">The provider to point them at.</param>
        /// <returns>A plan, or an empty plan stating why nothing could be moved.</returns>
        /// <remarks>
        /// Sites whose declared type cannot accept the provider are dropped from the batch and named in a
        /// warning, rather than failing the whole plan. One incompatible field in a selection of forty is an
        /// authoring mistake to report, not a reason to refuse the other thirty-nine — but writing it anyway
        /// would be planning a cast that fails at run time.
        /// </remarks>
        public static ReferenceRepairPlan PlanRewire(
            ReferenceAuditSnapshot snapshot, IEnumerable<string> siteKeys, string providerKey)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var provider = snapshot.FindProvider(providerKey);
            if (provider == null)
                return ReferenceRepairPlan.Empty(snapshot.Revision);

            if (string.IsNullOrEmpty(provider.RefId))
            {
                return Refuse(
                    snapshot,
                    $"'{provider.DisplayName}' has no Ref Id, so nothing can point at it yet. Give it an id "
                    + "first — Preview safe repairs covers that case.");
            }

            var warnings = new List<string>();
            var mutations = new List<ReferenceRepairMutation>();
            var moved = new List<ReferenceSiteRecord>();
            var incompatible = new List<string>();

            foreach (var siteKey in Distinct(siteKeys))
            {
                var resolution = snapshot.FindResolution(siteKey);
                if (resolution == null)
                    continue;

                var site = resolution.Site;

                if (site.ExpectedRuntimeType != null && provider.RuntimeType != null
                    && !site.ExpectedRuntimeType.IsAssignableFrom(provider.RuntimeType))
                {
                    incompatible.Add($"{site.Describe()} expects {site.ExpectedRuntimeTypeName}");
                    continue;
                }

                if (string.Equals(site.StoredRefId, provider.RefId, StringComparison.Ordinal)
                    && string.Equals(site.StoredRefType, provider.RefType, StringComparison.Ordinal))
                    continue;

                mutations.Add(new ReferenceSitePropertyMutation(
                    ReferenceRepairKind.RedirectReference,
                    ReferenceRepairApproval.RequiresUserChoice,
                    site,
                    ReferenceRepairPlanner.CurrentSiteValues(site),
                    ReferenceRepairPlanner.ProviderSiteValues(provider),
                    site.IsAssigned
                        ? $"You pointed this reference at '{provider.DisplayName}'; it previously stored "
                          + $"\"{site.StoredRefId}\"."
                        : $"You assigned '{provider.DisplayName}' to this previously unset reference."));

                moved.Add(site);
            }

            if (incompatible.Count > 0)
            {
                warnings.Add(
                    $"{incompatible.Count} reference(s) were left alone because '{provider.DisplayName}' is a "
                    + $"{provider.RuntimeTypeName} they cannot accept: {string.Join("; ", incompatible)}.");
            }

            if (!provider.IsRuntimeResolvable)
            {
                warnings.Add(
                    $"'{provider.DisplayName}' is a {provider.Kind}, which the runtime registry never holds. "
                    + "These references resolve only if that object is instantiated into a loaded scene "
                    + "before they are read.");
            }

            if (mutations.Count == 0)
            {
                warnings.Add(
                    incompatible.Count > 0
                        ? "Every selected reference either already points here or cannot accept this target."
                        : "Every selected reference already points here.");
                return Refuse(snapshot, warnings);
            }

            return Build(snapshot, mutations, moved, warnings);
        }

        /// <summary>
        /// Plans clearing a set of references.
        /// </summary>
        /// <param name="snapshot">The audit the keys came from.</param>
        /// <param name="siteKeys">The reference sites to unset. Already-unset sites are ignored.</param>
        /// <returns>A plan, or an empty plan when nothing is assigned.</returns>
        /// <remarks>
        /// Batched only because the alternative is forty confirmations for one deliberate act. It is still
        /// never part of the safe batch: an unset reference passes validation, so automating it would turn
        /// "this is broken" into "this is fine" without anything being fixed.
        /// </remarks>
        public static ReferenceRepairPlan PlanClearMany(
            ReferenceAuditSnapshot snapshot, IEnumerable<string> siteKeys)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var mutations = new List<ReferenceRepairMutation>();
            var cleared = new List<ReferenceSiteRecord>();

            foreach (var siteKey in Distinct(siteKeys))
            {
                var site = snapshot.FindResolution(siteKey)?.Site;
                if (site == null || !site.IsAssigned)
                    continue;

                mutations.Add(new ReferenceSitePropertyMutation(
                    ReferenceRepairKind.ClearReference,
                    ReferenceRepairApproval.RequiresUserChoice,
                    site,
                    ReferenceRepairPlanner.CurrentSiteValues(site),
                    ReferenceRepairPlanner.EmptySiteValues(),
                    $"You chose to clear this reference. It previously pointed at Ref Id "
                    + $"\"{site.StoredRefId}\", which will no longer be recorded anywhere."));

                cleared.Add(site);
            }

            if (mutations.Count == 0)
                return Refuse(snapshot, "None of the selected references is assigned.");

            return Build(snapshot, mutations, cleared, new[]
            {
                $"Clearing {mutations.Count} reference(s) discards which object each one meant. Nothing "
                + "records the old targets afterwards, so prefer re-pointing them if you know what they "
                + "should reach.",
            });
        }

        #endregion

        #region Scope

        /// <summary>
        /// Plans moving a provider into a different scope.
        /// </summary>
        /// <param name="snapshot">The audit the key came from.</param>
        /// <param name="providerKey">The provider to move.</param>
        /// <param name="currentScope">The scope the component currently declares.</param>
        /// <param name="newScope">The scope to move it to.</param>
        /// <returns>A plan with a single mutation, or an empty plan stating the refusal.</returns>
        /// <remarks>
        /// Deliberately does not carry inbound references, because there is nothing on the v1 site to carry:
        /// a <c>SceneObjectReference</c> declares no scope at all. Every reference from a v1 field therefore
        /// stops reaching a provider that leaves <see cref="ReferenceScopeKind.LegacyGlobal"/>, and the plan
        /// says so by name rather than letting the next audit discover it.
        /// </remarks>
        public static ReferenceRepairPlan PlanScope(
            ReferenceAuditSnapshot snapshot,
            string providerKey,
            ReferenceScopeKind currentScope,
            ReferenceScopeKind newScope)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var provider = snapshot.FindProvider(providerKey);
            if (provider == null)
                return ReferenceRepairPlan.Empty(snapshot.Revision);

            if (currentScope == newScope)
                return Refuse(snapshot, $"'{provider.DisplayName}' is already scoped {newScope}.");

            if (provider.IsReadOnly)
                return Refuse(snapshot, $"'{provider.DisplayName}' lives in a read-only asset.");

            var inbound = InboundSites(snapshot, provider);
            var stranded = inbound.Where(s => !s.IsScoped).ToList();

            var warnings = new List<string>();
            if (stranded.Count > 0)
            {
                warnings.Add(
                    $"{stranded.Count} reference(s) name this target from an unscoped (v1) field, which cannot "
                    + $"express a {newScope} identity and will stop resolving: "
                    + $"{string.Join("; ", stranded.Take(10).Select(s => s.Describe()))}"
                    + (stranded.Count > 10 ? $", +{stranded.Count - 10} more" : string.Empty));
            }

            if (newScope == ReferenceScopeKind.PrefabLocal)
            {
                warnings.Add(
                    "A prefab-local id needs an enclosing ReferenceScopeRoot. Without one the registration "
                    + "falls back to the legacy global path and the audit reports REF007.");
            }

            // No touched sites, so this plan claims to resolve nothing. A scope change moves a target out of
            // the space its references name it in; predicting which findings that clears would be predicting
            // the opposite of what usually happens, and the executor's measured after-report is the honest
            // answer either way.
            return Build(
                snapshot,
                new ReferenceRepairMutation[]
                {
                    new ReferenceProviderScopeMutation(
                        provider, currentScope, newScope,
                        $"You moved '{provider.DisplayName}' from scope {currentScope} to {newScope}. Scope is "
                        + "part of a reference's identity, so this changes what the runtime registers it as."),
                },
                Array.Empty<ReferenceSiteRecord>(),
                warnings);
        }

        #endregion

        #region Selection-scoped safe repairs

        /// <summary>
        /// The safe-repair batch, narrowed to the objects the author has selected.
        /// </summary>
        /// <param name="snapshot">The audit to derive the plan from.</param>
        /// <param name="ownerLocatorKeys">
        /// <see cref="ReferenceObjectLocator.Key"/> values to keep. Empty or null returns the full batch.
        /// </param>
        /// <returns>A plan containing only the safe repairs that touch those objects.</returns>
        /// <remarks>
        /// A projection of <see cref="ReferenceRepairPlanner.PlanSafeRepairs"/>, never a second opinion about
        /// what is safe: it filters that planner's own mutations. The whole-project button remains, but an
        /// author fixing one scene should not have to approve changes to forty others in order to use it.
        /// </remarks>
        public static ReferenceRepairPlan PlanSafeRepairsWithin(
            ReferenceAuditSnapshot snapshot, IEnumerable<string> ownerLocatorKeys)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var full = ReferenceRepairPlanner.PlanSafeRepairs(snapshot);
            var keys = new HashSet<string>(
                (ownerLocatorKeys ?? Array.Empty<string>()).Where(k => !string.IsNullOrEmpty(k)),
                StringComparer.Ordinal);

            if (keys.Count == 0)
                return full;

            var mutations = full.Mutations.Where(m => keys.Contains(m.Target.Key)).ToList();
            if (mutations.Count == 0)
            {
                return Refuse(
                    snapshot,
                    "None of the safe repairs in this snapshot touches the selected rows. Clear the selection "
                    + "to preview the whole batch.");
            }

            // A finding stays "expected to resolve" only if a mutation that survived the filter can still
            // resolve it. Carrying the full list would promise fixes this narrowed plan does not make.
            var resolved = full.ExpectedResolvedFindings
                .Where(f => keys.Contains(OwnerKeyOf(snapshot, f)))
                .ToList();

            var resolvedIdentities = new HashSet<string>(
                resolved.Select(ReferenceRepairPlanner.FindingIdentity), StringComparer.Ordinal);

            return new ReferenceRepairPlan(
                snapshot.Revision,
                ReferenceRepairPlanner.Order(mutations),
                resolved,
                snapshot.Findings
                    .Where(f => !resolvedIdentities.Contains(ReferenceRepairPlanner.FindingIdentity(f)))
                    .ToList(),
                new[]
                {
                    $"Narrowed to {keys.Count} selected object(s): "
                    + $"{full.Mutations.Count - mutations.Count} safe repair(s) elsewhere in the project are "
                    + "not part of this plan.",
                });
        }

        #endregion

        #region Inbound

        /// <summary>
        /// Every reference that names <paramref name="provider"/>.
        /// </summary>
        /// <param name="snapshot">The audit to read.</param>
        /// <param name="provider">The target.</param>
        /// <returns>The sites, in snapshot order, without duplicates.</returns>
        /// <remarks>
        /// The union of what <i>resolves</i> here and what <i>claims</i> this exact identity. Those differ in
        /// both directions and both belong: a reference resolving through the legacy fallback stores a stale
        /// Ref Type and so claims nothing, while a reference whose target is currently in a closed scene
        /// claims the identity without resolving. Carrying only one of the two sets would strand the other.
        /// </remarks>
        public static IReadOnlyList<ReferenceSiteRecord> InboundSites(
            ReferenceAuditSnapshot snapshot, ReferenceProviderRecord provider)
        {
            if (snapshot == null || provider == null)
                return Array.Empty<ReferenceSiteRecord>();

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sites = new List<ReferenceSiteRecord>();

            foreach (var resolution in snapshot.Resolutions)
            {
                var site = resolution.Site;

                var resolvesHere = resolution.Resolved != null
                    && string.Equals(resolution.Resolved.ProviderKey, provider.ProviderKey, StringComparison.Ordinal);

                var claimsIdentity = site.IsAssigned
                    && string.Equals(site.StoredRefId, provider.RefId, StringComparison.Ordinal)
                    && string.Equals(site.StoredRefType, provider.RefType, StringComparison.Ordinal);

                if ((resolvesHere || claimsIdentity) && seen.Add(site.SiteKey))
                    sites.Add(site);
            }

            return sites;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Shared refusals for the identity-changing plans.
        /// </summary>
        private static bool Validate(
            ReferenceAuditSnapshot snapshot,
            ReferenceProviderRecord provider,
            string refId,
            string refType,
            out string refusal)
        {
            refusal = null;

            if (provider.IsReadOnly)
            {
                refusal = $"'{provider.DisplayName}' lives in a read-only asset, so its identity cannot change.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(refId))
            {
                refusal =
                    "A Ref Id cannot be empty. An empty id is not 'no target' — it is a provider nothing can "
                    + "reference, reported as REF008.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(refType))
            {
                refusal = "A Ref Type cannot be empty; it is half of the identity every reference stores.";
                return false;
            }

            var sharing = snapshot.Providers
                .Where(p => !string.Equals(p.ProviderKey, provider.ProviderKey, StringComparison.Ordinal))
                .ToList();

            // Only meaningful for a target that has an identity. Two providers with no Ref Id at all are not
            // "duplicated" — they are two unnamed targets, and giving one of them a name is the fix, not the
            // thing to refuse.
            if (!string.IsNullOrEmpty(provider.RefId)
                && sharing.Any(p =>
                    string.Equals(p.RefType, provider.RefType, StringComparison.Ordinal)
                    && string.Equals(p.RefId, provider.RefId, StringComparison.Ordinal)))
            {
                refusal =
                    $"\"{provider.RefType}:{provider.RefId}\" is already claimed by more than one provider, so "
                    + "nothing records which references meant this one. Resolve the duplicate first — "
                    + "renaming now would carry some other target's references onto the new id.";
                return false;
            }

            var taken = sharing.FirstOrDefault(p =>
                string.Equals(p.RefType, refType, StringComparison.Ordinal)
                && string.Equals(p.RefId, refId, StringComparison.Ordinal));

            if (taken != null)
            {
                refusal =
                    $"'{taken.DisplayName}' already holds \"{refType}:{refId}\". Two providers on one identity "
                    + "is a runtime failure (REF002), not a warning, so this is refused rather than applied.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// One mutation per inbound reference, moving it onto the provider's new identity.
        /// </summary>
        /// <remarks>
        /// Writes both fields even when only one changed. A reference resolving through the legacy fallback
        /// stores a Ref Type that matches nothing, and leaving it that way would preserve a REF005 that this
        /// plan is already touching the field to fix.
        /// </remarks>
        private static IEnumerable<ReferenceRepairMutation> CarryInbound(
            IReadOnlyList<ReferenceSiteRecord> inbound,
            ReferenceProviderRecord provider,
            string newRefId,
            string newRefType)
        {
            var updated = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refId"] = newRefId,
                ["refType"] = newRefType,
            };

            foreach (var site in inbound)
            {
                var current = ReferenceRepairPlanner.CurrentSiteValues(site);
                if (updated.All(kv =>
                        string.Equals(current.GetValueOrDefault(kv.Key), kv.Value, StringComparison.Ordinal)))
                    continue;

                yield return new ReferenceSitePropertyMutation(
                    ReferenceRepairKind.FollowProviderRename,
                    ReferenceRepairApproval.RequiresUserChoice,
                    site,
                    current,
                    new Dictionary<string, string>(updated, StringComparer.Ordinal),
                    $"This reference names '{provider.DisplayName}', which is being renamed in this same plan. "
                    + "The target does not change.");
            }
        }

        private static IReadOnlyList<string> RenameWarnings(
            IReadOnlyList<ReferenceSiteRecord> inbound, ReferenceProviderRecord provider)
        {
            var warnings = new List<string>();

            var unwritable = inbound.Where(s => s.IsReadOnly).ToList();
            if (unwritable.Count > 0)
            {
                // The one way this plan can leave the project worse than it found it, so it is stated as
                // specifically as the data allows rather than folded into the generic read-only notice.
                warnings.Add(
                    $"{unwritable.Count} reference(s) that name this target live in read-only assets and "
                    + "cannot be moved with it. Applying anyway leaves them pointing at the old identity, "
                    + $"which nothing will answer to: {string.Join("; ", unwritable.Take(10).Select(s => s.Describe()))}"
                    + (unwritable.Count > 10 ? $", +{unwritable.Count - 10} more" : string.Empty));
            }

            if (provider != null && inbound.Count == 0)
            {
                warnings.Add(
                    $"No reference names '{provider.DisplayName}' in this snapshot. If your audit covered only "
                    + "part of the project, references in the unscanned part will not be moved — run a full "
                    + "audit first.");
            }

            return warnings;
        }

        /// <summary>Builds a plan and computes which findings it should clear.</summary>
        private static ReferenceRepairPlan Build(
            ReferenceAuditSnapshot snapshot,
            IReadOnlyList<ReferenceRepairMutation> mutations,
            IReadOnlyList<ReferenceSiteRecord> touchedSites,
            IReadOnlyList<string> warnings)
        {
            var touched = new HashSet<string>(touchedSites.Select(s => s.SiteKey), StringComparer.Ordinal);

            var resolved = snapshot.Findings.Where(f => touched.Contains(f.SourceSiteKey)).ToList();
            var resolvedIdentities = new HashSet<string>(
                resolved.Select(ReferenceRepairPlanner.FindingIdentity), StringComparer.Ordinal);

            return new ReferenceRepairPlan(
                snapshot.Revision,
                ReferenceRepairPlanner.Order(mutations),
                resolved,
                snapshot.Findings
                    .Where(f => !resolvedIdentities.Contains(ReferenceRepairPlanner.FindingIdentity(f)))
                    .ToList(),
                warnings);
        }

        /// <summary>An empty plan carrying the reason it is empty.</summary>
        private static ReferenceRepairPlan Refuse(ReferenceAuditSnapshot snapshot, string reason) =>
            Refuse(snapshot, new[] { reason });

        private static ReferenceRepairPlan Refuse(
            ReferenceAuditSnapshot snapshot, IReadOnlyList<string> reasons) =>
            new ReferenceRepairPlan(
                snapshot.Revision,
                Array.Empty<ReferenceRepairMutation>(),
                Array.Empty<ReferenceFinding>(),
                snapshot.Findings,
                reasons);

        /// <summary>The locator key a finding is anchored to, for selection filtering.</summary>
        private static string OwnerKeyOf(ReferenceAuditSnapshot snapshot, ReferenceFinding finding)
        {
            var site = snapshot.FindResolution(finding.SourceSiteKey)?.Site;
            if (site != null)
                return site.OwnerLocator.Key;

            var provider = finding.CandidateProviderKeys
                .Select(snapshot.FindProvider)
                .FirstOrDefault(p => p != null);
            return provider?.Locator.Key ?? string.Empty;
        }

        private static IEnumerable<string> Distinct(IEnumerable<string> keys) =>
            (keys ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal);

        #endregion
    }
}
