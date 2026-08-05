using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Runs a <see cref="MolcaWorkflowDefinition"/> step by step, reporting progress, aggregating each
    /// step's diagnostics and evidence into one versioned result bundle, halting on a critical step's
    /// failure, and honoring cancellation (§11, §13). The workflow's success is the postcondition — a step
    /// delegate that "returned" is not enough; a failed critical step, an Error diagnostic, or (when
    /// <see cref="MolcaWorkflowDefinition.FailOnWarning"/>) a Warning fails the run.
    /// </summary>
    public static class MolcaWorkflowRunner
    {
        /// <summary>
        /// Raised on the main thread just before a workflow step's body runs (Sprint 94.4): workflow id,
        /// run id, and the step about to execute. A hook observes — it must not mutate the run; a throwing
        /// hook is swallowed so observers can never fail a workflow.
        /// </summary>
        public static event Action<string, string, MolcaWorkflowStep> StepStarting;

        /// <summary>
        /// Raised on the main thread after a workflow step's body returned (or threw, already mapped to a
        /// failed result): workflow id, run id, the step, and its outcome. Same observer contract as
        /// <see cref="StepStarting"/>.
        /// </summary>
        public static event Action<string, string, MolcaWorkflowStep, MolcaStepResult> StepFinished;

        /// <summary>Runs the workflow and produces its aggregated result.</summary>
        /// <param name="workflow">The workflow to run.</param>
        /// <param name="context">The run context (arguments, cancellation, progress).</param>
        /// <returns>An awaitable yielding the workflow's terminal result.</returns>
        public static async Awaitable<MolcaCommandResult> RunAsync(
            MolcaWorkflowDefinition workflow, MolcaCommandContext context)
        {
            var stepsJson = new JArray();
            var allDiagnostics = new List<MolcaDiagnostic>();
            var evidence = new List<string>();
            bool halted = false;

            for (int i = 0; i < workflow.Steps.Count; i++)
            {
                if (context.CancellationToken.IsCancellationRequested)
                    return MolcaCommandResult.Cancelled(workflow.Id);

                var step = workflow.Steps[i];
                context.ReportProgress(new MolcaCommandProgress(
                    (float)i / workflow.Steps.Count, step.Description, i, workflow.Steps.Count, step.Id));
                SafeHook(() => StepStarting?.Invoke(workflow.Id, context.RunId, step));

                MolcaStepResult result;
                try
                {
                    result = await step.Run(context);
                }
                catch (System.OperationCanceledException)
                {
                    return MolcaCommandResult.Cancelled(workflow.Id);
                }
                catch (System.Exception ex)
                {
                    result = MolcaStepResult.Fail("step.exception", $"Step '{step.Id}' threw: {ex.Message}");
                }
                SafeHook(() => StepFinished?.Invoke(workflow.Id, context.RunId, step, result));

                allDiagnostics.AddRange(result.Diagnostics);
                stepsJson.Add(new JObject
                {
                    ["id"] = step.Id,
                    ["description"] = step.Description,
                    ["critical"] = step.Critical,
                    ["passed"] = result.Passed,
                    ["diagnostics"] = new JArray(result.Diagnostics.Select(d => d.ToJson())),
                    ["data"] = result.Data
                });
                evidence.Add($"{step.Id}: {(result.Passed ? "pass" : "FAIL")}");

                if (!result.Passed && step.Critical)
                {
                    halted = true;
                    break; // a failed critical step halts the workflow
                }
            }

            bool hasError = allDiagnostics.Any(d => d.Severity == MolcaDiagnosticSeverity.Error);
            bool hasWarning = allDiagnostics.Any(d => d.Severity == MolcaDiagnosticSeverity.Warning);
            bool passed = !halted && !hasError && !(workflow.FailOnWarning && hasWarning);

            var data = new JObject
            {
                ["workflow"] = workflow.Id,
                ["passed"] = passed,
                ["halted"] = halted,
                ["stepCount"] = workflow.Steps.Count,
                ["stepsRun"] = stepsJson.Count,
                ["steps"] = stepsJson
            };
            var verification = new MolcaVerification(true, passed, evidence);

            return passed
                ? MolcaCommandResult.Succeeded(workflow.Id, data, verification)
                : MolcaCommandResult.Create(workflow.Id, MolcaCommandStatus.Failed, data,
                    allDiagnostics.Count > 0 ? allDiagnostics
                        : new[] { new MolcaDiagnostic("workflow.failed", "The workflow did not pass.") },
                    null, verification, null);
        }

        /// <summary>An observer hook must never fail the run it observes.</summary>
        private static void SafeHook(System.Action hook)
        {
            try { hook(); }
            catch (System.Exception ex) { Debug.LogException(ex); }
        }
    }
}
