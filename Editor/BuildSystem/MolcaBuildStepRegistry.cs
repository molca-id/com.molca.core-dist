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
        private static List<IMolcaPostBuildStep> _postSteps;
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

        /// <summary>The discovered post-build steps, in execution order.</summary>
        public static IReadOnlyList<IMolcaPostBuildStep> PostSteps
        {
            get
            {
                EnsureDiscovered();
                return _postSteps;
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
            _postSteps = null;
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

        /// <summary>
        /// Runs every registered post-build step that applies to <paramref name="context"/>, in order.
        /// </summary>
        /// <param name="context">The build that just succeeded.</param>
        /// <param name="failures">One line per step that failed or threw; empty when all succeeded.</param>
        /// <returns>True when no applicable step failed.</returns>
        /// <remarks>
        /// <b>Every step runs, even after one fails</b> — the opposite of <see cref="RunAll(MolcaBuildContext, out string)"/>,
        /// and for the same reason it stops: pre-build steps may depend on each other, post-build steps are
        /// independent consumers of a finished artifact. Skipping a symbol upload because an unrelated
        /// notification webhook was down would lose data that cannot be recovered later.
        /// </remarks>
        public static bool RunAllPost(MolcaPostBuildContext context, out IReadOnlyList<string> failures) =>
            RunAllPost(PostSteps, context, out failures);

        /// <summary>
        /// Runs an explicit ordered post-build step list against <paramref name="context"/>.
        /// </summary>
        /// <param name="steps">The steps to run, already ordered.</param>
        /// <param name="context">The build that just succeeded.</param>
        /// <param name="failures">One line per step that failed or threw; empty when all succeeded.</param>
        /// <returns>True when no applicable step failed.</returns>
        /// <remarks>Exposed so the run-everything contract can be tested against a controlled list.</remarks>
        public static bool RunAllPost(
            IEnumerable<IMolcaPostBuildStep> steps, MolcaPostBuildContext context, out IReadOnlyList<string> failures)
        {
            var collected = new List<string>();
            failures = collected;
            if (context == null || steps == null)
                return true;

            foreach (var step in steps)
            {
                if (step == null)
                    continue;

                bool applies;
                try
                {
                    applies = step.ShouldRun(context);
                }
                catch (Exception ex)
                {
                    collected.Add($"Post-build step '{step.Id}' threw deciding whether to run: {ex.Message}");
                    continue;
                }

                if (!applies)
                    continue;

                Debug.Log($"[BuildManager] Post-build step '{step.DisplayName}' ({step.Id}) running…");

                MolcaBuildStepResult result;
                try
                {
                    result = step.Run(context);
                }
                catch (Exception ex)
                {
                    collected.Add($"Post-build step '{step.Id}' threw: {ex.Message}");
                    continue;
                }

                if (!result.Succeeded)
                {
                    collected.Add(string.IsNullOrEmpty(result.Message)
                        ? $"Post-build step '{step.Id}' failed."
                        : $"Post-build step '{step.Id}' failed: {result.Message}");
                    continue;
                }

                if (!string.IsNullOrEmpty(result.Message))
                    Debug.Log($"[BuildManager] Post-build step '{step.Id}': {result.Message}");
            }

            return collected.Count == 0;
        }

        private static void EnsureDiscovered()
        {
            if (_steps != null) return;

            var errors = new List<string>();
            _steps = BuildSteps(Instantiate<IMolcaBuildStep>("Build step", errors), errors);
            _postSteps = BuildPostSteps(Instantiate<IMolcaPostBuildStep>("Post-build step", errors), errors);

            _errors.Clear();
            _errors.AddRange(errors);
            if (_errors.Count > 0)
                Debug.LogWarning($"[MolcaBuildStepRegistry] discovery issues:\n - {string.Join("\n - ", _errors)}");
        }

        /// <summary>
        /// Instantiates every concrete implementation of <typeparamref name="T"/> found by TypeCache,
        /// recording why any candidate was skipped.
        /// </summary>
        /// <typeparam name="T">The step interface to discover.</typeparam>
        /// <param name="label">How this kind of step is named in skip messages.</param>
        /// <param name="errors">Accumulates skip reasons.</param>
        private static List<T> Instantiate<T>(string label, List<string> errors) where T : class
        {
            var instances = new List<T>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<T>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"{label} '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }

                try
                {
                    instances.Add((T)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    errors.Add($"{label} '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }

            return instances;
        }

        /// <summary>
        /// De-duplicates post-build step instances by id, drops empty ids, and orders the survivors by
        /// <see cref="IMolcaPostBuildStep.Order"/> then id.
        /// </summary>
        /// <param name="candidates">Candidate step instances.</param>
        /// <param name="errors">Accumulates skip reasons; may be pre-populated.</param>
        /// <returns>The accepted steps, in execution order.</returns>
        /// <remarks>Exposed so the dedup/ordering contract can be tested without <c>TypeCache</c>.</remarks>
        public static List<IMolcaPostBuildStep> BuildPostSteps(
            IEnumerable<IMolcaPostBuildStep> candidates, List<string> errors)
        {
            errors ??= new List<string>();
            var accepted = new List<IMolcaPostBuildStep>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var step in candidates ?? Enumerable.Empty<IMolcaPostBuildStep>())
            {
                if (step == null) continue;

                if (string.IsNullOrWhiteSpace(step.Id))
                {
                    errors.Add($"Post-build step '{step.GetType().FullName}' has an empty Id; skipped.");
                    continue;
                }

                if (!seen.Add(step.Id))
                {
                    errors.Add($"Duplicate post-build step id '{step.Id}' from '{step.GetType().FullName}'; skipped.");
                    continue;
                }

                accepted.Add(step);
            }

            return accepted
                .OrderBy(s => s.Order)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();
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
