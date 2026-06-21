using System.Collections.Generic;
using Molca.Sequence;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace Molca.Editor.Validation.Fixes
{
    /// <summary>
    /// Opt-in (non-safe) fix that clears the unresolved <see cref="Molca.ReferenceSystem.SceneObjectReference"/>(s)
    /// on a step (and its auxiliaries) — turning an <c>UnresolvedReference</c> error into an unset reference
    /// the validator ignores. Destructive (it discards the broken target), so it never runs in the safe
    /// pass; an agent should prefer rebinding to a suggested Ref Id and only clear when no target is right.
    /// </summary>
    public sealed class ClearBrokenReferenceFix : SequenceValidatorFixBase
    {
        /// <inheritdoc/>
        public override string Id => "core.clear-broken-reference";

        /// <inheritdoc/>
        public override string Description =>
            "Unsets a step's unresolved SceneObjectReference(s) (destructive; prefer rebinding to a "
            + "suggested Ref Id). Reverts with Ctrl+Z.";

        // Deterministic and Unity-Undo revertible, but it discards the broken target — destructive, so it
        // is excluded from the SafeOnly blanket pass and must be requested explicitly.
        /// <inheritdoc/>
        public override bool IsDestructive => true;

        /// <inheritdoc/>
        public override IReadOnlyCollection<string> HandledCategories { get; } = new[] { "UnresolvedReference" };

        /// <inheritdoc/>
        public override SequenceFixOutcome Apply(SequenceValidationFinding finding, SequenceValidationContext context, JObject args)
        {
            var step = finding?.Step;
            if (step == null) return SequenceFixOutcome.NotApplied("No target step.");

            Undo.RecordObject(step, "Clear broken reference");
            int cleared = ClearUnresolvedOn(step, step, context);

            foreach (var aux in step.Auxiliaries)
            {
                if (aux == null) continue;
                cleared += ClearUnresolvedOn(aux, step, context);
            }

            if (cleared == 0)
                return SequenceFixOutcome.NotApplied("No unresolved references found on the step to clear.");

            EditorUtility.SetDirty(step);
            return new SequenceFixOutcome(true,
                $"Cleared {cleared} unresolved reference(s) on '{finding.StepName}'.");
        }

        // Clears every still-unresolved SceneObjectReference on `owner`; `step` owns the Undo/dirty scope.
        private static int ClearUnresolvedOn(object owner, Step step, SequenceValidationContext context)
        {
            int cleared = 0;
            foreach (var rf in SceneReferenceReflection.Enumerate(owner))
            {
                if (!rf.Value.IsValid) continue;
                if (context.ResolveRefId(rf.Value.RefId).Count != 0) continue; // still resolves — leave it
                rf.Clear();
                cleared++;
            }
            return cleared;
        }
    }
}
