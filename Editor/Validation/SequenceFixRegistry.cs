using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// Which fixes a blanket remediation pass may auto-apply, decided from fix facets (not a
    /// self-declared flag).
    /// </summary>
    public enum RemediationPolicy
    {
        /// <summary>Deterministic, non-destructive, Unity-Undo only — the default safe pass.</summary>
        SafeOnly,

        /// <summary>Deterministic and revertible (Unity-Undo or file-snapshot), including destructive ones.</summary>
        DeterministicReversible,

        /// <summary>Every deterministic fix, regardless of destructiveness/reversibility.</summary>
        All,
    }

    /// <summary>
    /// The result of a blanket fix pass: how many findings of each category were repaired, which revert
    /// mechanisms were used, and whether any change needs a scene reload to be visible.
    /// </summary>
    public sealed class SequenceFixPassResult
    {
        /// <summary>Applied-count keyed by finding category.</summary>
        public Dictionary<string, int> AppliedByCategory { get; } = new();

        /// <summary>Total findings repaired in the pass.</summary>
        public int TotalApplied { get; set; }

        /// <summary>The revert mechanisms actually used by applied fixes (for honest revert reporting).</summary>
        public HashSet<FixReversibility> Mechanisms { get; } = new();

        /// <summary>True if any applied fix needs a scene reload to take effect.</summary>
        public bool RequiresSceneReload { get; set; }
    }

    /// <summary>
    /// Discovers and runs <see cref="ISequenceValidatorFix"/> implementations. Mirrors
    /// <see cref="SequenceValidatorRegistry"/>: <c>TypeCache</c> discovery, dedup by <see cref="ISequenceValidatorFix.Id"/>,
    /// and indexing by the finding categories each fix handles.
    /// </summary>
    public static class SequenceFixRegistry
    {
        private static List<ISequenceValidatorFix> _fixes;
        private static readonly List<string> _errors = new();

        /// <summary>The discovered fixes, ordered by id.</summary>
        public static IReadOnlyList<ISequenceValidatorFix> Fixes
        {
            get { EnsureDiscovered(); return _fixes; }
        }

        /// <summary>Discovery errors (duplicate ids, instantiation failures); empty when clean.</summary>
        public static IReadOnlyList<string> Errors
        {
            get { EnsureDiscovered(); return _errors; }
        }

        /// <summary>Clears the discovery cache so the next access re-scans. Intended for tests.</summary>
        public static void Reset()
        {
            _fixes = null;
            _errors.Clear();
        }

        /// <summary>Returns the fixes that handle the given finding <paramref name="category"/>, in id order.</summary>
        /// <param name="category">A finding category string.</param>
        /// <returns>Matching fixes; empty if none.</returns>
        public static IReadOnlyList<ISequenceValidatorFix> FixesFor(string category)
            => string.IsNullOrEmpty(category)
                ? Array.Empty<ISequenceValidatorFix>()
                : Fixes.Where(f => f.HandledCategories.Contains(category)).ToList();

        /// <summary>Whether <paramref name="policy"/> permits auto-applying <paramref name="fix"/> (by facets).</summary>
        /// <param name="policy">The remediation policy.</param>
        /// <param name="fix">The candidate fix.</param>
        /// <returns><c>true</c> if the policy allows the fix.</returns>
        public static bool PolicyAllows(RemediationPolicy policy, ISequenceValidatorFix fix) => policy switch
        {
            RemediationPolicy.SafeOnly =>
                fix.IsDeterministic && !fix.IsDestructive && fix.Reversibility == FixReversibility.UnityUndo,
            RemediationPolicy.DeterministicReversible =>
                fix.IsDeterministic && fix.Reversibility != FixReversibility.Irreversible,
            RemediationPolicy.All => true,
            _ => false,
        };

        /// <summary>
        /// Backward-compatible safe pass: <see cref="ApplyFixes"/> with <see cref="RemediationPolicy.SafeOnly"/>.
        /// </summary>
        /// <param name="controller">The controller being remediated.</param>
        /// <param name="findings">The findings to attempt to repair.</param>
        /// <returns>A summary of what was applied.</returns>
        public static SequenceFixPassResult ApplySafeFixes(
            SequenceController controller, IReadOnlyList<SequenceValidationFinding> findings)
            => ApplyFixes(controller, findings, RemediationPolicy.SafeOnly);

        /// <summary>
        /// Applies, for each finding, the first registered fix whose category matches and whose facets the
        /// <paramref name="policy"/> permits — in a single collapsed Unity <c>Undo</c> group so the pass
        /// reverts with one Ctrl+Z. Only <b>deterministic</b> fixes run here (a blanket pass supplies no
        /// arguments); parameterized fixes (e.g. the broken-auxiliary reassign) require an explicit
        /// <see cref="ISequenceValidatorFix.Apply"/> call regardless of policy.
        /// </summary>
        /// <param name="controller">The controller being remediated.</param>
        /// <param name="findings">The findings to attempt to repair (typically a fresh validation run).</param>
        /// <param name="policy">Which fixes may auto-apply (default <see cref="RemediationPolicy.SafeOnly"/>).</param>
        /// <returns>A summary of what was applied, including the revert mechanisms used.</returns>
        public static SequenceFixPassResult ApplyFixes(
            SequenceController controller, IReadOnlyList<SequenceValidationFinding> findings,
            RemediationPolicy policy = RemediationPolicy.SafeOnly)
        {
            var result = new SequenceFixPassResult();
            if (controller == null || findings == null || findings.Count == 0) return result;

            var context = new SequenceValidationContext(
                controller, controller.GetComponentsInChildren<Step>(true));

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Sequence remediation");

            foreach (var finding in findings)
            {
                // A blanket pass has no args, so only deterministic fixes can run; policy gates the rest.
                var fix = FixesFor(finding.Category)
                    .FirstOrDefault(f => f.IsDeterministic && PolicyAllows(policy, f));
                if (fix == null) continue;

                SequenceFixOutcome outcome;
                try
                {
                    outcome = fix.Apply(finding, context, null);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SequenceFixRegistry] Fix '{fix.Id}' threw on '{finding.Category}': {ex}");
                    continue;
                }

                if (!outcome.Applied) continue;
                result.TotalApplied++;
                result.Mechanisms.Add(fix.Reversibility);
                result.AppliedByCategory[finding.Category] =
                    (result.AppliedByCategory.TryGetValue(finding.Category, out var n) ? n : 0) + 1;
                if (outcome.RequiresSceneReload) result.RequiresSceneReload = true;
            }

            Undo.CollapseUndoOperations(undoGroup);
            return result;
        }

        private static void EnsureDiscovered()
        {
            if (_fixes != null) return;

            var instances = new List<ISequenceValidatorFix>();
            var errors = new List<string>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<ISequenceValidatorFix>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"Fix '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }
                try
                {
                    instances.Add((ISequenceValidatorFix)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    errors.Add($"Fix '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }

            _fixes = BuildFixes(instances, errors);
            _errors.Clear();
            _errors.AddRange(errors);
            if (_errors.Count > 0)
                Debug.LogWarning($"[SequenceFixRegistry] discovery issues:\n - {string.Join("\n - ", _errors)}");
        }

        /// <summary>
        /// Deduplicates fix instances by <see cref="ISequenceValidatorFix.Id"/> (first wins; the rest
        /// recorded in <paramref name="errors"/>) and orders the survivors by id. Exposed for tests so the
        /// dedup/ordering contract can be exercised without <c>TypeCache</c>.
        /// </summary>
        /// <param name="candidates">Candidate fix instances.</param>
        /// <param name="errors">Accumulates skip reasons; may be pre-populated.</param>
        /// <returns>The accepted fixes, ordered by id.</returns>
        internal static List<ISequenceValidatorFix> BuildFixes(
            IEnumerable<ISequenceValidatorFix> candidates, List<string> errors)
        {
            var accepted = new List<ISequenceValidatorFix>();
            var seenIds = new Dictionary<string, ISequenceValidatorFix>();

            foreach (var instance in candidates)
            {
                if (instance == null) continue;
                if (string.IsNullOrWhiteSpace(instance.Id))
                {
                    errors.Add($"Fix '{instance.GetType().FullName}' has an empty Id; skipped.");
                    continue;
                }
                if (seenIds.TryGetValue(instance.Id, out var existing))
                {
                    errors.Add($"Duplicate fix Id '{instance.Id}' on '{instance.GetType().FullName}' "
                               + $"(already used by '{existing.GetType().FullName}'); skipped.");
                    continue;
                }
                // Facet coherence: a destructive fix that can't be reverted is a footgun — flag it loudly
                // (it's still registered, but the warning surfaces the mistake to the fix author).
                if (instance.IsDestructive && instance.Reversibility == FixReversibility.Irreversible)
                    errors.Add($"Fix '{instance.Id}' is destructive AND irreversible — declare a "
                               + "FileSnapshot/UnityUndo Reversibility so its effect can be undone.");

                seenIds[instance.Id] = instance;
                accepted.Add(instance);
            }

            return accepted.OrderBy(f => f.Id, StringComparer.Ordinal).ToList();
        }
    }
}
