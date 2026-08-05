using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>
    /// Discovers every <see cref="IMolcaBuildStep"/> in the project — Core's own steps plus any
    /// authored by an add-on, SDK layer, or consumer project — and exposes them as one ordered,
    /// de-duplicated set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>. Mirrors
    /// <c>DoctorCheckRegistry</c> deliberately: implementations are found by <c>TypeCache</c>,
    /// instantiated once via their public parameterless constructor, and a duplicate
    /// <see cref="IMolcaBuildStep.Id"/> is rejected loudly rather than silently shadowing another step.
    /// </para>
    /// <para>
    /// Ordering is <see cref="IMolcaBuildStep.Order"/> then <see cref="IMolcaBuildStep.Id"/> (ordinal).
    /// Unlike Doctor checks, order is load-bearing here — steps have side effects and later ones read
    /// facts earlier ones record — so the tie-break exists to make two steps that forgot to order
    /// themselves still run the same way on every machine.
    /// </para>
    /// <para>Results are cached after first discovery. Call <see cref="Reset"/> in a test teardown when
    /// a test-only step type should not leak into subsequent runs.</para>
    /// </remarks>
    public static class MolcaBuildStepRegistry
    {
        private static List<IMolcaBuildStep> _steps;
        private static readonly List<string> _errors = new List<string>();

        /// <summary>The discovered steps, in execution order.</summary>
        public static IReadOnlyList<IMolcaBuildStep> Steps
        {
            get
            {
                EnsureDiscovered();
                return _steps;
            }
        }

        /// <summary>Discovery issues (duplicate ids, uninstantiable types, empty ids); empty when clean.</summary>
        public static IReadOnlyList<string> Errors
        {
            get
            {
                EnsureDiscovered();
                return _errors;
            }
        }

        /// <summary>Clears the discovery cache so the next access re-scans. Intended for tests.</summary>
        public static void Reset()
        {
            _steps = null;
            _errors.Clear();
        }

        /// <summary>
        /// Runs every registered step that applies to <paramref name="context"/>, in order, stopping at
        /// the first failure.
        /// </summary>
        /// <param name="context">The build about to run.</param>
        /// <param name="failure">The abort reason when this returns false; null otherwise.</param>
        /// <returns>True when every applicable step succeeded.</returns>
        /// <remarks>
        /// Stops at the first failure rather than collecting all of them: steps have side effects and
        /// later ones may depend on earlier ones having run, so continuing past a failure would run work
        /// against a state its author never anticipated.
        /// </remarks>
        public static bool RunAll(MolcaBuildContext context, out string failure) =>
            RunAll(Steps, context, out failure);

        /// <summary>
        /// Runs an explicit ordered step list against <paramref name="context"/>, stopping at the first
        /// failure.
        /// </summary>
        /// <param name="steps">The steps to run, already ordered.</param>
        /// <param name="context">The build about to run.</param>
        /// <param name="failure">The abort reason when this returns false; null otherwise.</param>
        /// <returns>True when every applicable step succeeded.</returns>
        /// <remarks>
        /// Exposed so the run contract — skip, stop-at-first-failure, contain a throwing step — can be
        /// tested against a controlled list rather than whatever the project happens to have registered.
        /// </remarks>
        public static bool RunAll(
            IEnumerable<IMolcaBuildStep> steps, MolcaBuildContext context, out string failure)
        {
            failure = null;
            if (context == null || steps == null)
                return true;

            foreach (var step in steps)
            {
                bool applies;
                try
                {
                    applies = step.ShouldRun(context);
                }
                catch (Exception ex)
                {
                    // A step that cannot decide whether it applies is a broken step, and guessing "no"
                    // would ship a player missing whatever it was meant to contribute.
                    failure = $"Build step '{step.Id}' threw deciding whether to run: {ex.Message}";
                    return false;
                }

                if (!applies)
                    continue;

                Debug.Log($"[BuildManager] Build step '{step.DisplayName}' ({step.Id}) running…");

                MolcaBuildStepResult result;
                try
                {
                    result = step.Run(context);
                }
                catch (Exception ex)
                {
                    failure = $"Build step '{step.Id}' threw: {ex.Message}";
                    return false;
                }

                if (!result.Succeeded)
                {
                    failure = string.IsNullOrEmpty(result.Message)
                        ? $"Build step '{step.Id}' failed."
                        : $"Build step '{step.Id}' failed: {result.Message}";
                    return false;
                }

                if (!string.IsNullOrEmpty(result.Message))
                    Debug.Log($"[BuildManager] Build step '{step.Id}': {result.Message}");
            }

            return true;
        }

        private static void EnsureDiscovered()
        {
            if (_steps != null) return;

            var instances = new List<IMolcaBuildStep>();
            var errors = new List<string>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<IMolcaBuildStep>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"Build step '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }

                try
                {
                    instances.Add((IMolcaBuildStep)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    errors.Add($"Build step '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }

            _steps = BuildSteps(instances, errors);
            _errors.Clear();
            _errors.AddRange(errors);
            if (_errors.Count > 0)
                Debug.LogWarning($"[MolcaBuildStepRegistry] discovery issues:\n - {string.Join("\n - ", _errors)}");
        }

        /// <summary>
        /// De-duplicates step instances by <see cref="IMolcaBuildStep.Id"/> (first wins; the rest
        /// recorded in <paramref name="errors"/>), drops empty ids, and orders the survivors by
        /// <see cref="IMolcaBuildStep.Order"/> then id.
        /// </summary>
        /// <param name="candidates">Candidate step instances.</param>
        /// <param name="errors">Accumulates skip reasons; may be pre-populated.</param>
        /// <returns>The accepted steps, in execution order.</returns>
        /// <remarks>Exposed so the dedup/ordering contract can be tested without <c>TypeCache</c>.</remarks>
        public static List<IMolcaBuildStep> BuildSteps(
            IEnumerable<IMolcaBuildStep> candidates, List<string> errors)
        {
            errors ??= new List<string>();
            var accepted = new List<IMolcaBuildStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var step in candidates ?? Enumerable.Empty<IMolcaBuildStep>())
            {
                if (step == null) continue;

                if (string.IsNullOrWhiteSpace(step.Id))
                {
                    errors.Add($"Build step '{step.GetType().FullName}' has an empty Id; skipped.");
                    continue;
                }

                if (!seen.Add(step.Id))
                {
                    errors.Add($"Duplicate build step id '{step.Id}' from '{step.GetType().FullName}'; skipped.");
                    continue;
                }

                accepted.Add(step);
            }

            return accepted
                .OrderBy(s => s.Order)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();
        }
    }
}
