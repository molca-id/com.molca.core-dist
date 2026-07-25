namespace Molca.Editor.Automation
{
    /// <summary>
    /// How safely a command may be re-run by an autonomous loop after a failure (§13). This is the rule
    /// that keeps "never automatically retry a non-idempotent action unless it declares a compensation
    /// strategy" enforceable: the classification is derived from the command's kind and reversibility and
    /// surfaced in <c>describe</c> and the plan preview so the autonomy layer consults it before retrying.
    /// </summary>
    public enum MolcaRetryClassification
    {
        /// <summary>Idempotent (a read, or an action with no observable side effect); safe to retry as-is.</summary>
        Retryable,

        /// <summary>A reversible action; may be retried only after its prior effect is rolled back/compensated.</summary>
        RetryableAfterRollback,

        /// <summary>An irreversible action; must never be silently retried — a repeat re-applies the effect.</summary>
        NotRetryable
    }

    /// <summary>
    /// Classifies a command's retry safety from its declared kind and reversibility (§13). Pure and
    /// side-effect free; the autonomy loop and the plan preview both consult it rather than re-deriving
    /// the rule at each call site.
    /// </summary>
    public static class MolcaRetryPolicy
    {
        /// <summary>Classifies how <paramref name="command"/> may be retried after a failure.</summary>
        /// <param name="command">The command to classify.</param>
        /// <returns>The retry classification.</returns>
        public static MolcaRetryClassification Classify(MolcaCommandDefinition command)
        {
            if (command.Kind == MolcaCommandKind.ReadOnly)
                return MolcaRetryClassification.Retryable;
            return command.Reversibility == MolcaCommandReversibility.None
                ? MolcaRetryClassification.NotRetryable
                : MolcaRetryClassification.RetryableAfterRollback;
        }

        /// <summary>A one-line rationale for a classification, for previews and audit surfaces.</summary>
        /// <param name="classification">The classification to explain.</param>
        /// <returns>A human-facing explanation.</returns>
        public static string Explain(MolcaRetryClassification classification)
        {
            switch (classification)
            {
                case MolcaRetryClassification.Retryable:
                    return "Idempotent — safe to re-run automatically.";
                case MolcaRetryClassification.RetryableAfterRollback:
                    return "Reversible action — retry only after rolling back the prior effect.";
                case MolcaRetryClassification.NotRetryable:
                    return "Irreversible action — must never be retried without explicit human intent.";
                default:
                    return string.Empty;
            }
        }
    }
}
