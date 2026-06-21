using System.Collections.Generic;
using Molca.ReferenceSystem;
using Molca.Sequence;
using UnityEngine;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// Severity of a <see cref="SequenceValidationFinding"/>. Intentionally distinct from the shipped
    /// <see cref="Molca.Editor.SequenceFindingSeverity"/> (which has no <see cref="Info"/> level) so the
    /// pluggable registry can express advisory findings without altering the legacy data model.
    /// </summary>
    public enum SequenceValidationSeverity
    {
        /// <summary>Advisory only — not a problem, surfaced for visibility.</summary>
        Info,

        /// <summary>A likely mistake that does not by itself make the report invalid.</summary>
        Warning,

        /// <summary>A problem that makes the sequence invalid (the report's <c>valid</c> flag goes false).</summary>
        Error,
    }

    /// <summary>
    /// One finding produced by an <see cref="ISequenceValidator"/>. This is the registry's common
    /// currency: every validator (including the adapter that wraps the legacy
    /// <see cref="Molca.Editor.SequenceValidator"/>) emits these, and the
    /// <c>molca_validate_sequence</c> tool serializes them into its merged report.
    /// </summary>
    /// <remarks>
    /// <see cref="AuxiliaryIndex"/> and <see cref="HasFix"/> exist so the legacy data-integrity findings
    /// (broken auxiliaries) keep the same JSON shape the tool has emitted since Sprint 15.2; new
    /// validators leave them at their defaults.
    /// </remarks>
    public sealed class SequenceValidationFinding
    {
        /// <summary>The validator that produced this finding (<see cref="ISequenceValidator.Id"/>).</summary>
        public string ValidatorId { get; }

        /// <summary>
        /// Open category string for the finding (e.g. <c>UnreachableStep</c>, <c>UnresolvedReference</c>,
        /// or a legacy <see cref="Molca.Editor.SequenceFindingType"/> name). Unlike a closed enum this
        /// lets fork validators name their own categories.
        /// </summary>
        public string Category { get; }

        /// <summary>Severity of the finding.</summary>
        public SequenceValidationSeverity Severity { get; }

        /// <summary>Ref Id of the step the finding is about; <c>null</c> for controller-level findings.</summary>
        public string StepRefId { get; }

        /// <summary>GameObject name of the step the finding is about; <c>null</c> for controller-level findings.</summary>
        public string StepName { get; }

        /// <summary>
        /// The step the finding is about (in-memory only — not serialized). Lets a fix target the exact
        /// step by object identity rather than by an ambiguous Ref Id (e.g. several empty Ref Ids).
        /// </summary>
        public Step Step { get; }

        /// <summary>Human-readable description of the problem.</summary>
        public string Message { get; }

        /// <summary>
        /// Index into <see cref="Step.Auxiliaries"/> for broken-auxiliary findings; <c>-1</c> otherwise.
        /// Preserves the legacy tool output shape.
        /// </summary>
        public int AuxiliaryIndex { get; }

        /// <summary>Whether a fix action exists for this finding (legacy broken-auxiliary findings only).</summary>
        public bool HasFix { get; }

        /// <summary>
        /// Optional human/LLM-facing hint describing how this finding can be remediated; <c>null</c> when
        /// none applies. Attached on demand by <see cref="SequenceFindingEnricher"/> (Sprint 41) rather
        /// than by the validator, so validation stays cheap.
        /// </summary>
        public string FixHint { get; internal set; }

        /// <summary>
        /// Optional remediation suggestions — e.g. the nearest existing Ref Ids for an unresolved
        /// reference, so an agent can pick one and rebind via the edit tools. Never null (empty when
        /// none). Attached on demand by <see cref="SequenceFindingEnricher"/> (Sprint 41).
        /// </summary>
        public IReadOnlyList<string> Suggestions { get; internal set; }

        /// <param name="validatorId">The producing validator's id.</param>
        /// <param name="category">Open category string.</param>
        /// <param name="severity">Finding severity.</param>
        /// <param name="message">Human-readable description.</param>
        /// <param name="step">The step the finding is about, or <c>null</c> for controller-level findings.</param>
        /// <param name="auxiliaryIndex">Auxiliary index for broken-auxiliary findings; <c>-1</c> otherwise.</param>
        /// <param name="hasFix">Whether a fix action exists for this finding.</param>
        /// <param name="fixHint">Optional remediation hint (Sprint 38).</param>
        /// <param name="suggestions">Optional remediation suggestions, e.g. candidate Ref Ids (Sprint 38).</param>
        public SequenceValidationFinding(
            string validatorId,
            string category,
            SequenceValidationSeverity severity,
            string message,
            Step step = null,
            int auxiliaryIndex = -1,
            bool hasFix = false,
            string fixHint = null,
            IReadOnlyList<string> suggestions = null)
        {
            ValidatorId = validatorId;
            Category = category;
            Severity = severity;
            Message = message;
            Step = step;
            StepRefId = step != null ? step.RefId : null;
            StepName = step != null ? step.name : null;
            AuxiliaryIndex = auxiliaryIndex;
            HasFix = hasFix;
            FixHint = fixHint;
            Suggestions = suggestions ?? System.Array.Empty<string>();
        }
    }

    /// <summary>
    /// Shared, read-only input passed to every <see cref="ISequenceValidator"/> in a single run.
    /// Built once by <see cref="SequenceValidatorRegistry.Run"/> so validators don't each re-enumerate
    /// the scene; the scene Ref-Id index is built lazily on first access.
    /// </summary>
    public sealed class SequenceValidationContext
    {
        /// <summary>The controller being validated.</summary>
        public SequenceController Controller { get; }

        /// <summary>
        /// Every <see cref="Step"/> under the controller, including those on inactive GameObjects.
        /// Enumerated via <c>GetComponentsInChildren&lt;Step&gt;(true)</c> — matching the legacy validator
        /// and the edit tools, because <see cref="SequenceController.Steps"/> is populated only at runtime.
        /// </summary>
        public IReadOnlyList<Step> Steps { get; }

        private Dictionary<string, List<IReferenceable>> _refIndex;

        /// <param name="controller">The controller being validated.</param>
        /// <param name="steps">The controller's steps (inactive included).</param>
        public SequenceValidationContext(SequenceController controller, IReadOnlyList<Step> steps)
        {
            Controller = controller;
            Steps = steps ?? System.Array.Empty<Step>();
        }

        /// <summary>
        /// Returns every <see cref="IReferenceable"/> in the loaded scene(s) that carries the given
        /// Ref Id, or an empty list. The index is built once per context from a single scene scan.
        /// </summary>
        /// <param name="refId">The Ref Id to resolve.</param>
        /// <returns>All matching referenceables (usually 0 or 1; more than 1 means an ambiguous Ref Id).</returns>
        public IReadOnlyList<IReferenceable> ResolveRefId(string refId)
        {
            _refIndex ??= BuildReferenceIndex();
            return string.IsNullOrEmpty(refId) || !_refIndex.TryGetValue(refId, out var list)
                ? System.Array.Empty<IReferenceable>()
                : list;
        }

        /// <summary>
        /// Every Ref Id present in the loaded scene(s) (the keys of the reference index). Used to compute
        /// fuzzy nearest-Ref-Id suggestions for unresolved references (Sprint 38).
        /// </summary>
        /// <returns>All known Ref Ids; never null.</returns>
        public IReadOnlyCollection<string> AllKnownRefIds()
        {
            _refIndex ??= BuildReferenceIndex();
            return _refIndex.Keys;
        }

        // One pass over every MonoBehaviour in the loaded scene(s) (inactive included), keeping those
        // that are IReferenceable. Editor-time only — no runtime ReferenceManager registration involved.
        private static Dictionary<string, List<IReferenceable>> BuildReferenceIndex()
        {
            var index = new Dictionary<string, List<IReferenceable>>();
            var behaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var behaviour in behaviours)
            {
                if (behaviour is not IReferenceable referenceable) continue;
                var id = referenceable.RefId;
                if (string.IsNullOrEmpty(id)) continue;

                if (!index.TryGetValue(id, out var list))
                    index[id] = list = new List<IReferenceable>();
                list.Add(referenceable);
            }
            return index;
        }
    }
}
