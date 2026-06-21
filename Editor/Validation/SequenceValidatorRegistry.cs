using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Sequence;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// Discovers and runs every <see cref="ISequenceValidator"/> in the project. Mirrors
    /// <see cref="Molca.Editor.Mcp.McpToolRegistry"/>: implementations are found by <c>TypeCache</c>,
    /// instantiated once, ordered deterministically by <see cref="ISequenceValidator.Id"/>, and a
    /// duplicate id is rejected loudly rather than silently shadowing.
    /// </summary>
    /// <remarks>
    /// Validators are cached after first discovery. Call <see cref="Reset"/> in a test teardown when a
    /// test-only validator type should not leak into subsequent runs.
    /// </remarks>
    public static class SequenceValidatorRegistry
    {
        private static List<ISequenceValidator> _validators;
        private static readonly List<string> _errors = new();

        /// <summary>The discovered validators, ordered by <see cref="ISequenceValidator.Id"/>.</summary>
        public static IReadOnlyList<ISequenceValidator> Validators
        {
            get
            {
                EnsureDiscovered();
                return _validators;
            }
        }

        /// <summary>Discovery errors (e.g. duplicate ids, types that failed to instantiate); empty when clean.</summary>
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
            _validators = null;
            _errors.Clear();
        }

        /// <summary>
        /// Validates <paramref name="controller"/> with every discovered validator and returns the merged
        /// findings. The shared <see cref="SequenceValidationContext"/> is built once; a validator that
        /// throws is isolated (the failure is logged and recorded as an <see cref="SequenceValidationSeverity.Error"/>
        /// finding) so one bad validator cannot abort the run.
        /// </summary>
        /// <param name="controller">The controller to validate; <c>null</c> yields an empty list.</param>
        /// <returns>All findings from all validators, in validator order.</returns>
        public static List<SequenceValidationFinding> Run(SequenceController controller)
            => Run(controller, Validators);

        /// <summary>
        /// Runs a specific set of validators against <paramref name="controller"/>. Exposed for tests so
        /// validator behavior (including exception isolation) can be exercised with hand-supplied
        /// validators instead of the globally-discovered set.
        /// </summary>
        /// <param name="controller">The controller to validate; <c>null</c> yields an empty list.</param>
        /// <param name="validators">The validators to run.</param>
        /// <returns>All findings from the supplied validators, in the given order.</returns>
        internal static List<SequenceValidationFinding> Run(
            SequenceController controller, IReadOnlyList<ISequenceValidator> validators)
        {
            var findings = new List<SequenceValidationFinding>();
            if (controller == null) return findings;

            var steps = controller.GetComponentsInChildren<Step>(true);
            var context = new SequenceValidationContext(controller, steps);

            foreach (var validator in validators)
            {
                try
                {
                    var produced = validator.Validate(context);
                    if (produced != null) findings.AddRange(produced);
                }
                catch (Exception ex)
                {
                    // Isolate a faulty validator (mirrors ReferenceManager's event-handler isolation):
                    // surface it as a finding and keep running the others.
                    Debug.LogError($"[SequenceValidatorRegistry] Validator '{validator.Id}' threw: {ex}");
                    findings.Add(new SequenceValidationFinding(
                        validator.Id, "ValidatorError", SequenceValidationSeverity.Error,
                        $"Validator '{validator.Id}' threw an exception: {ex.Message}"));
                }
            }
            return findings;
        }

        private static void EnsureDiscovered()
        {
            if (_validators != null) return;

            var instances = new List<ISequenceValidator>();
            var errors = new List<string>();

            foreach (var type in TypeCache.GetTypesDerivedFrom<ISequenceValidator>())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    errors.Add($"Validator '{type.FullName}' has no public parameterless constructor; skipped.");
                    continue;
                }

                try
                {
                    instances.Add((ISequenceValidator)Activator.CreateInstance(type));
                }
                catch (Exception ex)
                {
                    errors.Add($"Validator '{type.FullName}' failed to instantiate: {ex.Message}");
                }
            }

            _validators = BuildValidators(instances, errors);
            _errors.Clear();
            _errors.AddRange(errors);
            if (_errors.Count > 0)
                Debug.LogWarning($"[SequenceValidatorRegistry] discovery issues:\n - {string.Join("\n - ", _errors)}");
        }

        /// <summary>
        /// Deduplicates validator instances by <see cref="ISequenceValidator.Id"/> (first wins; the rest
        /// recorded in <paramref name="errors"/>), drops empty ids, and orders the survivors by id.
        /// Exposed for tests so the dedup/ordering contract can be exercised without <c>TypeCache</c>.
        /// </summary>
        /// <param name="candidates">Candidate validator instances.</param>
        /// <param name="errors">Accumulates skip reasons (duplicate/empty id); may be pre-populated.</param>
        /// <returns>The accepted validators, ordered by id.</returns>
        internal static List<ISequenceValidator> BuildValidators(
            IEnumerable<ISequenceValidator> candidates, List<string> errors)
        {
            var accepted = new List<ISequenceValidator>();
            var seenIds = new Dictionary<string, ISequenceValidator>();

            foreach (var instance in candidates)
            {
                if (instance == null) continue;
                if (string.IsNullOrWhiteSpace(instance.Id))
                {
                    errors.Add($"Validator '{instance.GetType().FullName}' has an empty Id; skipped.");
                    continue;
                }
                if (seenIds.TryGetValue(instance.Id, out var existing))
                {
                    errors.Add($"Duplicate validator Id '{instance.Id}' on '{instance.GetType().FullName}' "
                               + $"(already used by '{existing.GetType().FullName}'); skipped.");
                    continue;
                }

                seenIds[instance.Id] = instance;
                accepted.Add(instance);
            }

            return accepted.OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
        }
    }
}
