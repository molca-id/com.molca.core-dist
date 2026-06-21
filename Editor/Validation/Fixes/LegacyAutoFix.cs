using System.Linq;
using Molca.Editor;
using Molca.Sequence.Auxiliary;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Validation.Fixes
{
    /// <summary>
    /// Safe fix that routes the legacy auto-fixable findings — empty/duplicate Ref Ids and
    /// inactive-parent-with-active-children — through the shipped
    /// <see cref="SequenceValidator.TryAutoFix"/>. Keeps a single source of fix logic: this adapter only
    /// re-derives the live <see cref="SequenceFinding"/> for the targeted step and delegates.
    /// </summary>
    public sealed class LegacyAutoFix : SequenceValidatorFixBase
    {
        // Defaults from the base are correct: deterministic, non-destructive, UnityUndo.

        /// <inheritdoc/>
        public override string Id => "core.legacy-autofix";

        /// <inheritdoc/>
        public override string Description =>
            "Regenerates empty/duplicate step Ref Ids and re-enables inactive parents "
            + "(delegates to the shipped SequenceValidator auto-fix).";

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyCollection<string> HandledCategories => _categories;

        private static readonly string[] _categories =
        {
            nameof(SequenceFindingType.EmptyRefId),
            nameof(SequenceFindingType.DuplicateRefId),
            nameof(SequenceFindingType.InactiveParentWithActiveChildren),
        };

        /// <inheritdoc/>
        public override SequenceFixOutcome Apply(SequenceValidationFinding finding, SequenceValidationContext context, JObject args)
        {
            if (context?.Controller == null || finding?.Step == null)
                return SequenceFixOutcome.NotApplied("No target step.");

            // Re-derive the live legacy finding for this exact step (object identity, not Ref Id, so
            // several empty Ref Ids don't collide), then delegate to the shipped auto-fix.
            var legacy = SequenceValidator.Validate(context.Controller).FirstOrDefault(f =>
                f.Step == finding.Step && f.Type.ToString() == finding.Category && f.IsAutoFixable);
            if (legacy == null)
                return SequenceFixOutcome.NotApplied($"No live auto-fixable '{finding.Category}' finding for the step.");

            return SequenceValidator.TryAutoFix(legacy)
                ? new SequenceFixOutcome(true, $"Auto-fixed '{finding.Category}' on '{finding.StepName}'.")
                : SequenceFixOutcome.NotApplied("Auto-fix could not be applied (see Console).");
        }
    }

    /// <summary>
    /// Opt-in (non-safe) fix that reassigns a step's broken auxiliary to a valid type via
    /// <see cref="SequenceValidator.TryFixBrokenAuxiliary"/>. Rewrites the scene YAML, so it requires a
    /// replacement type (<c>newType</c> arg) and a scene reload — it is never run in the safe pass.
    /// </summary>
    public sealed class BrokenAuxiliaryFix : SequenceValidatorFixBase
    {
        /// <inheritdoc/>
        public override string Id => "core.broken-auxiliary-fix";

        /// <inheritdoc/>
        public override string Description =>
            "Reassigns a step's broken auxiliary to a valid StepAuxiliary type (requires a 'newType' arg; "
            + "rewrites scene YAML, needs a reload).";

        // Deterministic given its 'newType' arg, but it needs that input (so never auto-applied) and it
        // rewrites scene YAML (reverted by a file snapshot, not Unity Undo).
        /// <inheritdoc/>
        public override bool IsDeterministic => false;

        /// <inheritdoc/>
        public override FixReversibility Reversibility => FixReversibility.FileSnapshot;

        /// <inheritdoc/>
        public override System.Collections.Generic.IReadOnlyCollection<string> HandledCategories => _categories;

        private static readonly string[] _categories = { nameof(SequenceFindingType.BrokenAuxiliary) };

        /// <inheritdoc/>
        public override SequenceFixOutcome Apply(SequenceValidationFinding finding, SequenceValidationContext context, JObject args)
        {
            if (context?.Controller == null || finding?.Step == null)
                return SequenceFixOutcome.NotApplied("No target step.");

            var newTypeName = args?.Value<string>("newType");
            if (string.IsNullOrWhiteSpace(newTypeName))
                return SequenceFixOutcome.NotApplied("A 'newType' argument (StepAuxiliary type name) is required.");

            var newType = TypeCache.GetTypesDerivedFrom<StepAuxiliary>()
                .FirstOrDefault(t => t.Name == newTypeName || t.FullName == newTypeName);
            if (newType == null)
                return SequenceFixOutcome.NotApplied($"No StepAuxiliary type named '{newTypeName}'.");

            var legacy = SequenceValidator.Validate(context.Controller).FirstOrDefault(f =>
                f.Step == finding.Step && f.HasFix && f.AuxiliaryIndex == finding.AuxiliaryIndex);
            if (legacy == null)
                return SequenceFixOutcome.NotApplied(
                    $"No live broken-auxiliary finding for the step at index {finding.AuxiliaryIndex}.");

            return SequenceValidator.TryFixBrokenAuxiliary(legacy, newType)
                ? new SequenceFixOutcome(true,
                    $"Auxiliary {finding.AuxiliaryIndex} on '{finding.StepName}' reassigned to {newType.Name}.",
                    requiresSceneReload: true)
                : SequenceFixOutcome.NotApplied("Auxiliary fix could not be applied (see Console).");
        }
    }
}
