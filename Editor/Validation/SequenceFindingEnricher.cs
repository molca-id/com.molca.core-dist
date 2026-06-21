using System.Collections.Generic;

namespace Molca.Editor.Validation
{
    /// <summary>
    /// Attaches remediation suggestions and a fix hint to findings on demand, via the
    /// <see cref="SequenceFixSuggesterRegistry"/> (Sprint 41). The validate/remediate tools call this when
    /// assembling a report, keeping the validators themselves free of suggestion-generation cost.
    /// </summary>
    public static class SequenceFindingEnricher
    {
        /// <summary>
        /// Populates <see cref="SequenceValidationFinding.Suggestions"/> and
        /// <see cref="SequenceValidationFinding.FixHint"/> for each finding that a suggester can advise on.
        /// </summary>
        /// <param name="findings">The findings to enrich (mutated in place).</param>
        /// <param name="context">The validation context.</param>
        public static void Enrich(IEnumerable<SequenceValidationFinding> findings, SequenceValidationContext context)
        {
            if (findings == null) return;

            foreach (var finding in findings)
            {
                if (finding == null) continue;
                var suggestions = SequenceFixSuggesterRegistry.SuggestFor(finding, context);
                if (suggestions.Count == 0) continue;

                finding.Suggestions = suggestions;
                finding.FixHint ??=
                    $"Did you mean: {string.Join(", ", suggestions)}? Rebind via molca_sequence_set_step_fields, "
                    + "or clear it with molca_sequence_remediate (fix.category=UnresolvedReference).";
            }
        }
    }
}
