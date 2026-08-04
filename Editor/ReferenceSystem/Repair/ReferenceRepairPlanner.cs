using System;
using System.Collections.Generic;
using System.Linq;
using Molca.ReferenceSystem;

namespace Molca.Editor.ReferenceSystem.Repair
{
    /// <summary>
    /// A repair the data cannot decide, offered to the user with its options.
    /// </summary>
    public sealed class ReferenceRepairChoice
    {
        /// <summary>The finding that needs the decision.</summary>
        public ReferenceFinding Finding { get; }

        /// <summary>The reference site the decision applies to, or empty for a provider-side choice.</summary>
        public string SiteKey { get; }

        /// <summary>Provider keys the user may pick between. May be empty when the only option is to clear.</summary>
        public IReadOnlyList<string> CandidateProviderKeys { get; }

        /// <summary>Why this cannot be decided automatically.</summary>
        public string Question { get; }

        internal ReferenceRepairChoice(
            ReferenceFinding finding, string siteKey, IReadOnlyList<string> candidateProviderKeys, string question)
        {
            Finding = finding;
            SiteKey = siteKey ?? string.Empty;
            CandidateProviderKeys = candidateProviderKeys ?? Array.Empty<string>();
            Question = question ?? string.Empty;
        }

        /// <inheritdoc/>
        public override string ToString() => $"{Finding.CodeString}: {Question}";
    }

    /// <summary>
    /// Turns audit findings into repair plans, and refuses to plan a repair whose outcome the data does not
    /// determine.
    /// </summary>
    /// <remarks>
    /// <para>This class is where the plan's §12.2–12.4 rules live, and the refusals matter more than the
    /// repairs. Three things it will never plan: a blanket <c>oldId → newId</c> rewrite; re-keying a
    /// duplicate that something references; and clearing a broken reference to make validation pass.
    /// Each of those destroys authoring intent while reporting success.</para>
    ///
    /// <para>Planning is pure: it reads a snapshot and allocates records. Nothing here touches the project,
    /// so a plan can be built, shown, discarded, and rebuilt at no cost beyond the audit it came from.</para>
    /// </remarks>
    public static class ReferenceRepairPlanner
    {
        /// <summary>
        /// Plans every repair whose outcome is unambiguous.
        /// </summary>
        /// <param name="snapshot">The audit to derive the plan from.</param>
        /// <returns>A plan; possibly empty. Never null.</returns>
        /// <remarks>
        /// Covers exactly the three safe cases: a provider with no id (nothing can reference what does not
        /// exist), stale cached metadata on a reference that already resolves (identity untouched), and a
        /// duplicated <c>(RefType, RefId)</c> that no reference points at (no inbound intent to preserve).
        /// Everything else lands in <see cref="ReferenceRepairPlan.ExpectedRemainingFindings"/> and, in more
        /// actionable form, in <see cref="DescribeChoices"/>.
        /// </remarks>
        public static ReferenceRepairPlan PlanSafeRepairs(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var mutations = new List<ReferenceRepairMutation>();
            var resolved = new List<ReferenceFinding>();
            var warnings = new List<string>();

            var referencedIds = ReferencedIds(snapshot);
            var assignedIds = new HashSet<string>(
                snapshot.Providers.Select(p => p.RefId).Where(id => !string.IsNullOrEmpty(id)),
                StringComparer.Ordinal);

            PlanMissingProviderIds(snapshot, mutations, resolved, assignedIds);
            PlanUnreferencedDuplicates(snapshot, mutations, resolved, referencedIds, assignedIds, warnings);
            PlanStaleMetadata(snapshot, mutations, resolved);

            var resolvedKeys = new HashSet<string>(resolved.Select(FindingIdentity), StringComparer.Ordinal);
            var remaining = snapshot.Findings
                .Where(f => !resolvedKeys.Contains(FindingIdentity(f)))
                .ToList();

            return new ReferenceRepairPlan(
                snapshot.Revision,
                Order(mutations),
                ReferenceFinding.InStableOrder(resolved).ToList(),
                remaining,
                warnings);
        }

        /// <summary>
        /// Plans pointing one reference at one provider the user chose.
        /// </summary>
        /// <param name="snapshot">The audit the keys came from.</param>
        /// <param name="siteKey">The reference site to change.</param>
        /// <param name="providerKey">The provider to point it at.</param>
        /// <returns>A plan with a single mutation, or an empty plan when either key is unknown.</returns>
        public static ReferenceRepairPlan PlanRedirect(
            ReferenceAuditSnapshot snapshot, string siteKey, string providerKey)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var resolution = snapshot.FindResolution(siteKey);
            var provider = snapshot.FindProvider(providerKey);
            if (resolution == null || provider == null)
                return ReferenceRepairPlan.Empty(snapshot.Revision);

            var site = resolution.Site;
            var warnings = new List<string>();

            if (!provider.IsRuntimeResolvable)
            {
                warnings.Add(
                    $"'{provider.DisplayName}' is a {provider.Kind}, which the runtime registry never holds. "
                    + "The reference will resolve only if that object is instantiated into a loaded scene "
                    + "before the reference is read.");
            }

            if (site.ExpectedRuntimeType != null && provider.RuntimeType != null
                && !site.ExpectedRuntimeType.IsAssignableFrom(provider.RuntimeType))
            {
                // Refused rather than warned: the field's own type says this cast cannot succeed, so
                // "applied successfully" would be a lie.
                return new ReferenceRepairPlan(
                    snapshot.Revision,
                    Array.Empty<ReferenceRepairMutation>(),
                    Array.Empty<ReferenceFinding>(),
                    snapshot.Findings,
                    new[]
                    {
                        $"Refused: {site.Describe()} expects a {site.ExpectedRuntimeTypeName}, and "
                        + $"'{provider.DisplayName}' is a {provider.RuntimeTypeName}. The cast would fail at "
                        + "runtime, so this is not a repair.",
                    });
            }

            var mutation = new ReferenceSitePropertyMutation(
                ReferenceRepairKind.RedirectReference,
                ReferenceRepairApproval.RequiresUserChoice,
                site,
                CurrentSiteValues(site),
                ProviderSiteValues(provider),
                $"You chose '{provider.DisplayName}' as the target for this reference.");

            return new ReferenceRepairPlan(
                snapshot.Revision,
                new ReferenceRepairMutation[] { mutation },
                FindingsForSite(snapshot, siteKey),
                snapshot.Findings.Where(f => f.SourceSiteKey != siteKey).ToList(),
                warnings);
        }

        /// <summary>
        /// Plans clearing one reference the user chose to abandon.
        /// </summary>
        /// <param name="snapshot">The audit the key came from.</param>
        /// <param name="siteKey">The reference site to clear.</param>
        /// <returns>A plan with a single mutation, or an empty plan when the key is unknown.</returns>
        /// <remarks>
        /// Only ever reached by explicit request. Clearing is never part of a safe batch: an unset reference
        /// passes validation, so automating it would turn "this is broken" into "this is fine" without
        /// anything being fixed.
        /// </remarks>
        public static ReferenceRepairPlan PlanClear(ReferenceAuditSnapshot snapshot, string siteKey)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var resolution = snapshot.FindResolution(siteKey);
            if (resolution == null || !resolution.Site.IsAssigned)
                return ReferenceRepairPlan.Empty(snapshot.Revision);

            var site = resolution.Site;
            var mutation = new ReferenceSitePropertyMutation(
                ReferenceRepairKind.ClearReference,
                ReferenceRepairApproval.RequiresUserChoice,
                site,
                CurrentSiteValues(site),
                EmptySiteValues(),
                $"You chose to clear this reference. It previously pointed at Ref Id "
                + $"\"{site.StoredRefId}\", which will no longer be recorded anywhere.");

            return new ReferenceRepairPlan(
                snapshot.Revision,
                new ReferenceRepairMutation[] { mutation },
                FindingsForSite(snapshot, siteKey),
                snapshot.Findings.Where(f => f.SourceSiteKey != siteKey).ToList(),
                new[]
                {
                    "Clearing a reference discards which object was intended. Nothing records the old target "
                    + "afterwards, so prefer redirecting it if you know what it should point at.",
                });
        }

        /// <summary>
        /// Describes every finding whose repair needs a human decision, with the options.
        /// </summary>
        /// <param name="snapshot">The audit to inspect.</param>
        /// <returns>The open decisions, most severe first.</returns>
        public static IReadOnlyList<ReferenceRepairChoice> DescribeChoices(ReferenceAuditSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var referencedIds = ReferencedIds(snapshot);
            var choices = new List<ReferenceRepairChoice>();

            foreach (var finding in snapshot.Findings)
            {
                switch (finding.Code)
                {
                    case ReferenceFindingCode.DuplicateProvider when
                        finding.CandidateProviderKeys.Count > 1 &&
                        finding.CandidateProviderKeys
                            .Select(snapshot.FindProvider)
                            .Any(p => p != null && referencedIds.Contains(p.RefId)):
                        choices.Add(new ReferenceRepairChoice(
                            finding, finding.SourceSiteKey, finding.CandidateProviderKeys,
                            "Several providers claim this Ref Id and something references it. Decide which "
                            + "provider keeps the id, give the others new ones, then re-point each reference "
                            + "at what it actually meant."));
                        break;

                    case ReferenceFindingCode.AmbiguousLegacyFallback:
                        choices.Add(new ReferenceRepairChoice(
                            finding, finding.SourceSiteKey, finding.CandidateProviderKeys,
                            "The stored Ref Type matches nothing and several objects carry this Ref Id. "
                            + "Choose the intended target."));
                        break;

                    case ReferenceFindingCode.WrongRuntimeType:
                        choices.Add(new ReferenceRepairChoice(
                            finding, finding.SourceSiteKey, Array.Empty<string>(),
                            "The target is not a type this field can accept. Point the field at a compatible "
                            + "object, or change the field's type."));
                        break;

                    case ReferenceFindingCode.MissingProvider:
                        choices.Add(new ReferenceRepairChoice(
                            finding, finding.SourceSiteKey, Array.Empty<string>(),
                            "No object carries this Ref Id. Point the reference at the intended target, or "
                            + "clear it if it is genuinely obsolete."));
                        break;
                }
            }

            return choices;
        }

        #region Safe repairs

        private static void PlanMissingProviderIds(
            ReferenceAuditSnapshot snapshot,
            List<ReferenceRepairMutation> mutations,
            List<ReferenceFinding> resolved,
            HashSet<string> assignedIds)
        {
            foreach (var finding in snapshot.Findings.Where(f => f.Code == ReferenceFindingCode.ProviderIdMissing))
            {
                var provider = finding.CandidateProviderKeys.Select(snapshot.FindProvider).FirstOrDefault();
                if (provider == null || !string.IsNullOrEmpty(provider.RefId))
                    continue;

                if (provider.IsReadOnly)
                    continue;

                var newId = GenerateIdNotIn(provider.RefType, assignedIds);
                mutations.Add(new ReferenceProviderIdMutation(
                    ReferenceRepairKind.AssignMissingProviderId,
                    ReferenceRepairApproval.Automatic,
                    provider,
                    newId,
                    "The provider has no Ref Id, so nothing can reference it yet and assigning one cannot "
                    + "break an existing reference."));

                resolved.Add(finding);
            }
        }

        private static void PlanUnreferencedDuplicates(
            ReferenceAuditSnapshot snapshot,
            List<ReferenceRepairMutation> mutations,
            List<ReferenceFinding> resolved,
            HashSet<string> referencedIds,
            HashSet<string> assignedIds,
            List<string> warnings)
        {
            var collisions = snapshot.Providers
                .Where(p => p.IsRuntimeResolvable && !string.IsNullOrEmpty(p.RefId))
                .GroupBy(p => p.RefType + "|" + p.RefId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var collision in collisions)
            {
                var providers = collision.ToList();
                var refId = providers[0].RefId;

                if (referencedIds.Contains(refId))
                {
                    // The refusal, stated. A count of "0 fixed" with no explanation is what made the old
                    // tooling feel arbitrary.
                    warnings.Add(
                        $"Ref Id \"{refId}\" is claimed by {providers.Count} providers and something "
                        + "references it, so no automatic re-key is possible: nothing records which provider "
                        + "each reference meant. Resolve it yourself and re-point the references.");
                    continue;
                }

                // The first provider keeps the id; the rest get fresh ones. Which one "first" is does not
                // matter here precisely because nothing references the id.
                foreach (var provider in providers.Skip(1))
                {
                    if (provider.IsReadOnly)
                    {
                        warnings.Add(
                            $"'{provider.DisplayName}' duplicates Ref Id \"{refId}\" but lives in a read-only "
                            + "asset, so it cannot be re-keyed here.");
                        continue;
                    }

                    var newId = GenerateIdNotIn(provider.RefType, assignedIds);
                    mutations.Add(new ReferenceProviderIdMutation(
                        ReferenceRepairKind.RekeyUnreferencedDuplicate,
                        ReferenceRepairApproval.Automatic,
                        provider,
                        newId,
                        $"Ref Id \"{refId}\" is claimed by {providers.Count} providers and no reference points "
                        + "at it, so re-keying this one cannot re-point anything."));
                }

                foreach (var finding in snapshot.Findings.Where(f =>
                             f.Code == ReferenceFindingCode.DuplicateProvider
                             && f.CandidateProviderKeys.Any(k => providers.Any(p => p.ProviderKey == k))))
                {
                    resolved.Add(finding);
                }
            }
        }

        private static void PlanStaleMetadata(
            ReferenceAuditSnapshot snapshot,
            List<ReferenceRepairMutation> mutations,
            List<ReferenceFinding> resolved)
        {
            foreach (var finding in snapshot.Findings.Where(f =>
                         f.Code == ReferenceFindingCode.WrongRefTypeMetadata
                         || f.Code == ReferenceFindingCode.StaleEditorMetadata))
            {
                var resolution = snapshot.FindResolution(finding.SourceSiteKey);
                if (resolution?.Resolved == null || resolution.Site.IsReadOnly)
                    continue;

                var site = resolution.Site;
                var provider = resolution.Resolved;
                var previous = CurrentSiteValues(site);
                var updated = ProviderSiteValues(provider);

                // Identity stays put; only the presentation metadata moves. Preserving the Ref Id is what
                // makes this safe to batch.
                updated["refId"] = site.StoredRefId;

                if (updated.All(kv => string.Equals(previous.GetValueOrDefault(kv.Key), kv.Value, StringComparison.Ordinal)))
                    continue;

                mutations.Add(new ReferenceSitePropertyMutation(
                    ReferenceRepairKind.RefreshStaleMetadata,
                    ReferenceRepairApproval.Automatic,
                    site,
                    previous,
                    updated,
                    $"The reference already resolves to '{provider.DisplayName}'; only its cached Ref Type and "
                    + "display name are out of date. The Ref Id is unchanged."));

                resolved.Add(finding);
            }
        }

        #endregion

        #region Helpers

        /// <summary>Every Ref Id that at least one assigned reference site points at.</summary>
        private static HashSet<string> ReferencedIds(ReferenceAuditSnapshot snapshot) =>
            new HashSet<string>(
                snapshot.Sites.Where(s => s.IsAssigned).Select(s => s.StoredRefId), StringComparer.Ordinal);

        /// <summary>
        /// A fresh id that no provider in the snapshot already holds, registered so a single plan cannot
        /// hand the same id to two providers.
        /// </summary>
        private static string GenerateIdNotIn(string refType, HashSet<string> assignedIds)
        {
            var type = string.IsNullOrEmpty(refType) ? "Referenceable" : refType;

            // GUID-based ids make a collision vanishingly unlikely, but a bounded retry costs nothing and
            // turns "vanishingly unlikely" into "cannot happen within this plan".
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var candidate = ReferenceGenerator.GenerateUniqueId(type);
                if (assignedIds.Add(candidate))
                    return candidate;
            }

            throw new InvalidOperationException(
                $"Could not generate a Ref Id for type '{type}' that no provider already holds.");
        }

        /// <summary>The serialized values a site currently holds, as a mutation precondition.</summary>
        /// <param name="site">The site to read.</param>
        /// <remarks>
        /// Internal rather than private so <see cref="ReferenceAuthoringPlanner"/> writes the same field set.
        /// Two planners with their own idea of which fields make up a reference would eventually disagree,
        /// and the disagreement would look like a failing precondition rather than like a defect.
        /// </remarks>
        internal static Dictionary<string, string> CurrentSiteValues(ReferenceSiteRecord site) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refId"] = site.StoredRefId,
                ["refType"] = site.StoredRefType,
            };

        /// <summary>The serialized values a site would hold after being pointed at a provider.</summary>
        /// <param name="provider">The target provider.</param>
        internal static Dictionary<string, string> ProviderSiteValues(ReferenceProviderRecord provider) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refId"] = provider.RefId,
                ["refType"] = provider.RefType,
            };

        /// <summary>The serialized values of an unset reference.</summary>
        internal static Dictionary<string, string> EmptySiteValues() =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["refId"] = string.Empty,
                ["refType"] = string.Empty,
            };

        /// <summary>Findings anchored to one site.</summary>
        /// <param name="snapshot">The audit to search.</param>
        /// <param name="siteKey">The site key to match.</param>
        internal static IReadOnlyList<ReferenceFinding> FindingsForSite(
            ReferenceAuditSnapshot snapshot, string siteKey) =>
            snapshot.Findings
                .Where(f => string.Equals(f.SourceSiteKey, siteKey, StringComparison.Ordinal))
                .ToList();

        /// <summary>
        /// Stable identity for a finding, so "expected to resolve" can be compared against a later audit.
        /// </summary>
        internal static string FindingIdentity(ReferenceFinding finding) =>
            $"{finding.CodeString}|{finding.SourceSiteKey}|{finding.Summary}";

        /// <summary>
        /// Deterministic mutation order: same snapshot, same plan, same preview, same plan id.
        /// </summary>
        /// <param name="mutations">The mutations to order.</param>
        internal static IReadOnlyList<ReferenceRepairMutation> Order(
            IEnumerable<ReferenceRepairMutation> mutations) =>
            mutations
                .OrderBy(m => m.Kind)
                .ThenBy(m => m.AssetPath, StringComparer.Ordinal)
                .ThenBy(m => m.Target.Key, StringComparer.Ordinal)
                .ThenBy(m => m.Describe(), StringComparer.Ordinal)
                .ToList();

        #endregion
    }
}
