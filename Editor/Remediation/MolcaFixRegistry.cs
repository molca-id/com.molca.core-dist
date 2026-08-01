using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Doctor;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// Discovers and indexes every <see cref="IMolcaFix"/> in the project, from any Molca audit domain.
    /// </summary>
    /// <remarks>
    /// Mirrors the discovery contract of the two registries it replaces (<see cref="SceneFixRegistry"/> and
    /// the add-on's <c>SequenceFixRegistry</c>): <c>TypeCache</c> discovery of parameterless implementations,
    /// plus <see cref="IMolcaFixContributor"/> for adapters that wrap an existing abstraction; de-duplication
    /// by <see cref="IMolcaFix.Id"/> (first wins, in ordinal id order) with the rejections recorded in
    /// <see cref="Errors"/>; indexing by <see cref="IMolcaFix.HandledFindingCode"/>.
    /// <para>Editor-only, main thread only. Results are cached until <see cref="Reset"/>.</para>
    /// </remarks>
    public static class MolcaFixRegistry
    {
        private static List<IMolcaFix> _fixes;
        private static Dictionary<string, List<IMolcaFix>> _byCode;
        private static readonly List<string> _errors = new List<string>();

        /// <summary>Every registered fix, ordered by <see cref="IMolcaFix.Id"/>.</summary>
        public static IReadOnlyList<IMolcaFix> All
        {
            get { EnsureBuilt(); return _fixes; }
        }

        /// <summary>Discovery problems (duplicate ids, empty ids, instantiation failures); empty when clean.</summary>
        public static IReadOnlyList<string> Errors
        {
            get { EnsureBuilt(); return _errors; }
        }

        /// <summary>Clears the discovery cache so the next access re-scans. Intended for tests.</summary>
        public static void Reset()
        {
            _fixes = null;
            _byCode = null;
            _errors.Clear();
        }

        /// <summary>Returns the fix with the given id, or <c>null</c>.</summary>
        /// <param name="id">A fix id.</param>
        /// <returns>The matching fix, or <c>null</c>.</returns>
        public static IMolcaFix ById(string id)
        {
            EnsureBuilt();
            return string.IsNullOrEmpty(id) ? null : _fixes.FirstOrDefault(f => f.Id == id);
        }

        /// <summary>Returns the fixes that remediate <paramref name="findingCode"/>, in id order (never null).</summary>
        /// <param name="findingCode">A namespaced finding code.</param>
        /// <returns>Matching fixes; empty if none is registered for the code.</returns>
        public static IReadOnlyList<IMolcaFix> FixesFor(string findingCode)
        {
            EnsureBuilt();
            return !string.IsNullOrEmpty(findingCode) && _byCode.TryGetValue(findingCode, out var list)
                ? list
                : Array.Empty<IMolcaFix>();
        }

        /// <summary>
        /// Whether <paramref name="policy"/> permits auto-applying <paramref name="fix"/>, decided purely
        /// from the fix's facets.
        /// </summary>
        /// <param name="policy">The remediation policy.</param>
        /// <param name="fix">The candidate fix.</param>
        /// <returns><c>true</c> if the policy allows the fix.</returns>
        /// <remarks>
        /// Bit-for-bit the predicate the add-on's <c>SequenceFixRegistry.PolicyAllows</c> has enforced since
        /// Sprint 41 — kept identical so hoisting the vocabulary changes no existing behaviour.
        /// </remarks>
        public static bool PolicyAllows(RemediationPolicy policy, IMolcaFix fix)
        {
            if (fix == null) return false;
            switch (policy)
            {
                case RemediationPolicy.SafeOnly:
                    return fix.IsDeterministic
                           && !fix.IsDestructive
                           && fix.Reversibility == FixReversibility.UnityUndo;
                case RemediationPolicy.DeterministicReversible:
                    return fix.IsDeterministic && fix.Reversibility != FixReversibility.Irreversible;
                case RemediationPolicy.All:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Selects the fix a blanket pass would run for <paramref name="target"/> under
        /// <paramref name="policy"/>: the first registered fix for the code that is deterministic, permitted
        /// by the policy, and (when <paramref name="fixIdFilter"/> is non-null) explicitly requested.
        /// </summary>
        /// <param name="target">The finding site.</param>
        /// <param name="policy">The remediation policy.</param>
        /// <param name="fixIdFilter">Restricts selection to these fix ids; <c>null</c> means no restriction.</param>
        /// <returns>The fix to run, or <c>null</c> when the pass must decline this target.</returns>
        /// <remarks>
        /// A blanket pass supplies no arguments, so a non-deterministic fix can never be selected here even
        /// under <see cref="RemediationPolicy.All"/>; it must be invoked explicitly with args.
        /// </remarks>
        public static IMolcaFix SelectFor(
            MolcaFixTarget target, RemediationPolicy policy, IReadOnlyCollection<string> fixIdFilter = null)
        {
            if (target == null) return null;
            return FixesFor(target.FindingCode)
                .FirstOrDefault(f => f.IsDeterministic
                                     && PolicyAllows(policy, f)
                                     && (fixIdFilter == null || fixIdFilter.Contains(f.Id)));
        }

        private static void EnsureBuilt()
        {
            if (_fixes != null) return;

            var errors = new List<string>();
            var candidates = new List<IMolcaFix>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaFix>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                // Adapters wrapping a per-domain fix take it in their constructor and are handed to us by a
                // contributor; skipping them here keeps the missing-ctor error meaningful for real mistakes.
                if (type.IsDefined(typeof(MolcaFixSuppliedByContributorAttribute), inherit: false)) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"Fix '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }
                try
                {
                    candidates.Add((IMolcaFix)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    errors.Add($"Fix '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }

            candidates.AddRange(CollectContributed(errors));

            _fixes = BuildFixes(candidates, errors);
            _byCode = IndexByCode(_fixes);
            _errors.Clear();
            _errors.AddRange(errors);
            if (_errors.Count > 0)
                Debug.LogWarning($"[MolcaFixRegistry] discovery issues:\n - {string.Join("\n - ", _errors)}");
        }

        private static IEnumerable<IMolcaFix> CollectContributed(List<string> errors)
        {
            var contributed = new List<IMolcaFix>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaFixContributor>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"Fix contributor '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }
                try
                {
                    var contributor = (IMolcaFixContributor)Activator.CreateInstance(type);
                    var supplied = contributor.Contribute();
                    if (supplied != null) contributed.AddRange(supplied.Where(f => f != null));
                }
                catch (Exception ex)
                {
                    errors.Add($"Fix contributor '{type.FullName}' failed: {ex.Message}");
                }
            }

            return contributed;
        }

        /// <summary>
        /// De-duplicates fix instances by <see cref="IMolcaFix.Id"/> (first wins; the rest recorded in
        /// <paramref name="errors"/>) and orders the survivors by id.
        /// </summary>
        /// <param name="candidates">Candidate fix instances.</param>
        /// <param name="errors">Accumulates skip reasons; may be pre-populated.</param>
        /// <returns>The accepted fixes, ordered by id.</returns>
        /// <remarks>Exposed for tests so dedup/ordering can be exercised without <c>TypeCache</c>.</remarks>
        internal static List<IMolcaFix> BuildFixes(IEnumerable<IMolcaFix> candidates, List<string> errors)
        {
            var accepted = new List<IMolcaFix>();
            var seenIds = new Dictionary<string, IMolcaFix>(StringComparer.Ordinal);

            foreach (var instance in candidates)
            {
                if (instance == null) continue;
                if (string.IsNullOrWhiteSpace(instance.Id))
                {
                    errors.Add($"Fix '{instance.GetType().FullName}' has an empty Id; skipped.");
                    continue;
                }
                var codes = instance.HandledFindingCodes;
                if (codes == null || codes.Count == 0 || codes.All(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"Fix '{instance.Id}' declares no HandledFindingCodes; skipped.");
                    continue;
                }
                if (seenIds.TryGetValue(instance.Id, out var existing))
                {
                    errors.Add($"Duplicate fix Id '{instance.Id}' on '{instance.GetType().FullName}' "
                               + $"(already used by '{existing.GetType().FullName}'); skipped.");
                    continue;
                }
                // Facet coherence: a destructive fix that cannot be reverted is a footgun. It stays
                // registered (an explicit caller may still want it) but the mistake is surfaced loudly.
                if (instance.IsDestructive && instance.Reversibility == FixReversibility.Irreversible)
                    errors.Add($"Fix '{instance.Id}' is destructive AND irreversible — declare a "
                               + "FileSnapshot/UnityUndo Reversibility so its effect can be undone.");

                seenIds[instance.Id] = instance;
                accepted.Add(instance);
            }

            return accepted.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Groups accepted fixes by each handled finding code, preserving id order within every group.
        /// </summary>
        /// <param name="fixes">The accepted fixes, already ordered by id.</param>
        /// <returns>Fixes indexed by finding code; a multi-code fix appears under each of its codes.</returns>
        internal static Dictionary<string, List<IMolcaFix>> IndexByCode(IEnumerable<IMolcaFix> fixes)
        {
            var byCode = new Dictionary<string, List<IMolcaFix>>(StringComparer.Ordinal);
            foreach (var fix in fixes)
            foreach (var code in fix.HandledFindingCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                if (!byCode.TryGetValue(code, out var list))
                    byCode[code] = list = new List<IMolcaFix>();
                list.Add(fix);
            }
            return byCode;
        }
    }
}
