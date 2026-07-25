using System.Collections.Generic;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The outcome of one workflow step: whether it passed, its diagnostics (stable codes), and optional
    /// evidence data folded into the workflow's result bundle (§11). A critical step's failure halts the
    /// workflow; a non-critical step's failure is recorded but the workflow continues.
    /// </summary>
    public sealed class MolcaStepResult
    {
        /// <summary>Whether the step's postcondition held.</summary>
        public bool Passed { get; }

        /// <summary>Structured diagnostics this step produced. Never null.</summary>
        public IReadOnlyList<MolcaDiagnostic> Diagnostics { get; }

        /// <summary>Step-specific evidence merged into the workflow result under this step's id. May be null.</summary>
        public Newtonsoft.Json.Linq.JToken Data { get; }

        private MolcaStepResult(bool passed, IReadOnlyList<MolcaDiagnostic> diagnostics, Newtonsoft.Json.Linq.JToken data)
        {
            Passed = passed;
            Diagnostics = diagnostics ?? System.Array.Empty<MolcaDiagnostic>();
            Data = data;
        }

        /// <summary>A passing step result with optional evidence.</summary>
        /// <param name="data">Optional evidence data.</param>
        /// <param name="diagnostics">Optional non-error diagnostics.</param>
        /// <returns>A passing result.</returns>
        public static MolcaStepResult Pass(Newtonsoft.Json.Linq.JToken data = null, IReadOnlyList<MolcaDiagnostic> diagnostics = null)
            => new MolcaStepResult(true, diagnostics, data);

        /// <summary>A failing step result carrying diagnostics and optional evidence.</summary>
        /// <param name="diagnostics">Failure diagnostics.</param>
        /// <param name="data">Optional evidence data.</param>
        /// <returns>A failing result.</returns>
        public static MolcaStepResult Fail(IReadOnlyList<MolcaDiagnostic> diagnostics, Newtonsoft.Json.Linq.JToken data = null)
            => new MolcaStepResult(false, diagnostics, data);

        /// <summary>A failing step result from a single code/message.</summary>
        /// <param name="code">Stable diagnostic code.</param>
        /// <param name="message">Human message.</param>
        /// <returns>A failing result.</returns>
        public static MolcaStepResult Fail(string code, string message)
            => Fail(new[] { new MolcaDiagnostic(code, message) });
    }

    /// <summary>The body of a workflow step. Runs on the main thread; honors the context's cancellation token.</summary>
    /// <param name="context">The run context (arguments, cancellation, progress).</param>
    /// <returns>An awaitable yielding the step outcome.</returns>
    public delegate Awaitable<MolcaStepResult> MolcaWorkflowStepBody(MolcaCommandContext context);

    /// <summary>
    /// One named, ordered step of a <see cref="MolcaWorkflowDefinition"/> (§11). A <see cref="Critical"/>
    /// step halts the workflow on failure; a non-critical step is recorded and the workflow proceeds.
    /// </summary>
    public sealed class MolcaWorkflowStep
    {
        /// <summary>Stable step id (used as the evidence key and in progress reporting).</summary>
        public string Id { get; }

        /// <summary>Human-facing description of what the step checks or does.</summary>
        public string Description { get; }

        /// <summary>Whether failure of this step halts the rest of the workflow.</summary>
        public bool Critical { get; }

        /// <summary>The step body.</summary>
        public MolcaWorkflowStepBody Run { get; }

        /// <summary>Creates a workflow step.</summary>
        /// <param name="id">Stable step id.</param>
        /// <param name="description">Description.</param>
        /// <param name="run">The step body.</param>
        /// <param name="critical">Whether failure halts the workflow. Defaults to true.</param>
        public MolcaWorkflowStep(string id, string description, MolcaWorkflowStepBody run, bool critical = true)
        {
            Id = id;
            Description = description ?? string.Empty;
            Run = run ?? throw new System.ArgumentNullException(nameof(run));
            Critical = critical;
        }
    }
}
