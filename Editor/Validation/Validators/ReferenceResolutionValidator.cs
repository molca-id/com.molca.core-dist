using System.Collections.Generic;
using Molca.ReferenceSystem;
using Molca.Sequence;

namespace Molca.Editor.Validation.Validators
{
    /// <summary>
    /// Resolves every outbound <see cref="SceneObjectReference"/> on a step (and its auxiliaries) against
    /// the loaded scene(s) at author time. Where the legacy validator checks each step's <i>own</i> Ref Id,
    /// this checks the references a step <i>points at</i> — the gap that previously surfaced only as a
    /// runtime "could not resolve" failure.
    /// </summary>
    /// <remarks>
    /// Resolution is static (a single scene scan via <see cref="SequenceValidationContext.ResolveRefId"/>),
    /// never <see cref="SceneObjectReference.ResolveAsync{T}"/> — that waits on runtime
    /// <c>ReferenceManager</c> registration and cannot run in edit mode.
    /// </remarks>
    public sealed class ReferenceResolutionValidator : ISequenceValidator
    {
        /// <inheritdoc/>
        public string Id => "core.reference-resolution";

        /// <inheritdoc/>
        public string Description =>
            "Outbound SceneObjectReference fields on steps and auxiliaries resolve to a referenceable "
            + "object in the loaded scene(s), with no ambiguous or type-mismatched targets.";

        /// <inheritdoc/>
        public IEnumerable<SequenceValidationFinding> Validate(SequenceValidationContext context)
        {
            var findings = new List<SequenceValidationFinding>();
            if (context == null) return findings;

            foreach (var step in context.Steps)
            {
                if (step == null) continue;

                foreach (var rf in SceneReferenceReflection.Enumerate(step))
                    Resolve(context, step, $"field '{rf.Label}'", rf.Value, findings);

                var auxiliaries = step.Auxiliaries;
                for (int i = 0; i < auxiliaries.Count; i++)
                {
                    var aux = auxiliaries[i];
                    if (aux == null) continue;
                    foreach (var rf in SceneReferenceReflection.Enumerate(aux))
                        Resolve(context, step, $"auxiliary {i} ({aux.GetType().Name}) field '{rf.Label}'", rf.Value, findings);
                }
            }
            return findings;
        }

        private void Resolve(
            SequenceValidationContext context,
            Step step,
            string source,
            SceneObjectReference reference,
            List<SequenceValidationFinding> findings)
        {
            if (!reference.IsValid) return; // unset/optional — nothing to resolve.

            var matches = context.ResolveRefId(reference.RefId);
            if (matches.Count == 0)
            {
                // Suggestions are attached later by SequenceFindingEnricher (Sprint 41) — keep Validate cheap.
                findings.Add(new SequenceValidationFinding(
                    Id, "UnresolvedReference", SequenceValidationSeverity.Error,
                    $"Step '{step.name}' {source} references Ref Id '{reference.RefId}', but no object in "
                    + "the loaded scene(s) carries it.", step));
                return;
            }

            if (matches.Count > 1)
            {
                findings.Add(new SequenceValidationFinding(
                    Id, "AmbiguousReference", SequenceValidationSeverity.Warning,
                    $"Step '{step.name}' {source} references Ref Id '{reference.RefId}', which is carried by "
                    + $"{matches.Count} objects; it will resolve to an arbitrary one.", step));
            }

            // Type mismatch is only meaningful for an unambiguous match with an expected type recorded.
            if (matches.Count == 1 && !string.IsNullOrEmpty(reference.RefType)
                && !string.IsNullOrEmpty(matches[0].RefType)
                && matches[0].RefType != reference.RefType)
            {
                findings.Add(new SequenceValidationFinding(
                    Id, "ReferenceTypeMismatch", SequenceValidationSeverity.Warning,
                    $"Step '{step.name}' {source} expects Ref Type '{reference.RefType}' but '{reference.RefId}' "
                    + $"is a '{matches[0].RefType}'.", step));
            }
        }
    }
}
