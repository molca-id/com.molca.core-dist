namespace Molca.Editor.Automation
{
    /// <summary>
    /// A progress snapshot emitted <em>during</em> a command or workflow run, separate from the final
    /// <see cref="MolcaCommandResult"/> (§16). This is the first-class progress channel every transport
    /// renders — the Hub's existing activity rail (§12), a CLI/Pipeline progress line, and MCP progress
    /// notifications all consume the same stream. A command that wraps a service already producing
    /// progress (e.g. Doctor's per-check callbacks) forwards that signal rather than inventing its own.
    /// </summary>
    /// <remarks>
    /// Progress is advisory: a run may complete without ever reporting one, and callers must not depend
    /// on any particular cadence. Reported on the main thread. Immutable.
    /// </remarks>
    public readonly struct MolcaCommandProgress
    {
        /// <summary>Determinate fraction in [0, 1], or a negative value for indeterminate progress.</summary>
        public float Fraction { get; }

        /// <summary>Short human-facing status message (e.g. "running doc-links").</summary>
        public string Message { get; }

        /// <summary>For a multi-step workflow, the 0-based index of the current step; else -1.</summary>
        public int StepIndex { get; }

        /// <summary>For a multi-step workflow, the total step count; else 0.</summary>
        public int StepCount { get; }

        /// <summary>Optional name of the current step, or null.</summary>
        public string StepName { get; }

        /// <summary>True when the fraction is indeterminate (negative).</summary>
        public bool IsIndeterminate => Fraction < 0f;

        /// <summary>Creates a progress snapshot.</summary>
        /// <param name="fraction">Determinate fraction [0,1], or negative for indeterminate.</param>
        /// <param name="message">Short status message.</param>
        /// <param name="stepIndex">0-based workflow step index, or -1.</param>
        /// <param name="stepCount">Workflow step count, or 0.</param>
        /// <param name="stepName">Optional current step name.</param>
        public MolcaCommandProgress(float fraction, string message, int stepIndex = -1, int stepCount = 0, string stepName = null)
        {
            Fraction = fraction;
            Message = message ?? string.Empty;
            StepIndex = stepIndex;
            StepCount = stepCount;
            StepName = stepName;
        }

        /// <summary>Creates an indeterminate progress snapshot carrying only a message.</summary>
        /// <param name="message">Short status message.</param>
        /// <returns>An indeterminate progress snapshot.</returns>
        public static MolcaCommandProgress Indeterminate(string message) => new MolcaCommandProgress(-1f, message);
    }
}
