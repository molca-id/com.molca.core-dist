using System.Collections.Generic;

namespace Molca.Editor.Validation.Validators
{
    /// <summary>
    /// Adapts the shipped static <see cref="Molca.Editor.SequenceValidator"/> (Sprint 15.2) into the
    /// pluggable registry. The legacy validator is unchanged and still drives the visualizer, the graph
    /// editor, the importer, and the <c>molca_sequence_fix</c> tools directly; this adapter is purely how
    /// its findings (broken auxiliaries, empty/duplicate Ref Ids, inactive-parent-with-active-children)
    /// enter the registry so they appear alongside the newer structural and reference checks.
    /// </summary>
    public sealed class LegacyDataIntegrityValidator : ISequenceValidator
    {
        /// <inheritdoc/>
        public string Id => "core.data-integrity";

        /// <inheritdoc/>
        public string Description =>
            "Broken auxiliaries, empty/duplicate step Ref Ids, and disabled parents gating active children "
            + "(wraps the legacy SequenceValidator; findings are auto-fixable via molca_sequence_fix).";

        /// <inheritdoc/>
        public IEnumerable<SequenceValidationFinding> Validate(SequenceValidationContext context)
        {
            if (context?.Controller == null) yield break;

            foreach (var f in Molca.Editor.SequenceValidator.Validate(context.Controller))
            {
                yield return new SequenceValidationFinding(
                    validatorId: Id,
                    category: f.Type.ToString(),
                    severity: MapSeverity(f.Severity),
                    message: f.Message,
                    step: f.Step,
                    auxiliaryIndex: f.AuxiliaryIndex,
                    hasFix: f.HasFix);
            }
        }

        private static SequenceValidationSeverity MapSeverity(Molca.Editor.SequenceFindingSeverity severity)
            => severity == Molca.Editor.SequenceFindingSeverity.Error
                ? SequenceValidationSeverity.Error
                : SequenceValidationSeverity.Warning;
    }
}
