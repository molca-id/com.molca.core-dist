using System.Collections.Generic;
using System.Linq;
using Molca;
using Molca.Sequence;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation.BuiltIn
{
    /// <summary>
    /// The Runtime Smoke workflow (§11.2): a read-only observation of a <em>live</em> Molca runtime that
    /// confirms <see cref="RuntimeManager"/> bootstrapped, initialization settled within a budget, the
    /// resolved subsystem graph is non-empty and healthy, any caller-declared required subsystems resolve,
    /// and the scene's Sequence controllers are discoverable — returning one evidence bundle.
    /// </summary>
    /// <remarks>
    /// This workflow only <em>reads</em> runtime state; it never enters or exits Play mode. Because it is
    /// declared <see cref="MolcaCommandMode.Play"/>, the kernel refuses it with <c>mode.play_required</c>
    /// when the editor is not playing — so a caller either runs it while a developer is in Play mode, or a
    /// batch/CI harness owns the Play-mode transition (and the play-state restore §11.2 calls for) and
    /// invokes this workflow in between. Keeping the transition out of the workflow is deliberate: entering
    /// Play mode triggers a domain reload that a single in-request <c>Awaitable</c> cannot survive (the
    /// await-in-request finding). The optional performance-budget sampling (§11.2) is left to that harness.
    /// </remarks>
    public static class RuntimeSmokeWorkflow
    {
        /// <summary>The stable command id of the Runtime Smoke workflow.</summary>
        public const string Id = "molca.runtime-smoke";

        /// <summary>Default ceiling for the initialization wait, in seconds, when no argument is supplied.</summary>
        private const float DefaultInitTimeoutSeconds = 20f;

        /// <summary>Builds the Runtime Smoke workflow definition.</summary>
        /// <returns>The workflow definition.</returns>
        public static MolcaWorkflowDefinition Create() => new MolcaWorkflowDefinition(
            id: Id,
            displayName: "Runtime Smoke",
            description: "Read-only Play-mode smoke: confirms RuntimeManager initializes, subsystems resolve and are healthy, and Sequence controllers are discoverable.",
            steps: new[]
            {
                new MolcaWorkflowStep("manager", "Confirm a RuntimeManager exists and bootstrap has started.", ManagerStep),
                new MolcaWorkflowStep("initialization", "Wait for bootstrap to reach Ready within the init budget.", InitializationStep),
                new MolcaWorkflowStep("subsystems", "Confirm the resolved subsystem graph is non-empty and every subsystem is active.", SubsystemsStep),
                new MolcaWorkflowStep("services", "Resolve caller-declared required subsystems.", ServicesStep, critical: false),
                new MolcaWorkflowStep("sequences", "Discover Sequence controllers in the loaded scenes.", SequencesStep, critical: false),
            },
            mode: MolcaCommandMode.Play,
            kind: MolcaCommandKind.ReadOnly,
            resourceClaims: new[] { MolcaResourceClaim.PlayMode });

        /// <summary>Confirms bootstrap has at least started — a missing RuntimeManager halts the smoke.</summary>
        private static Awaitable<MolcaStepResult> ManagerStep(MolcaCommandContext context)
        {
            var state = RuntimeManager.State;
            var data = new JObject { ["bootstrapState"] = state.ToString() };
            return Completed(state == BootstrapState.NotStarted
                ? MolcaStepResult.Fail(new[] { new MolcaDiagnostic("runtime.no_manager",
                    "No RuntimeManager in the loaded scene(s) — nothing bootstrapped.") }, data)
                : MolcaStepResult.Pass(data));
        }

        /// <summary>
        /// Awaits <see cref="RuntimeManager.WaitForInitialization()"/> under a bounded timeout. A failed
        /// bootstrap surfaces as an <see cref="System.InvalidOperationException"/> from the wait; a timeout
        /// is a distinct diagnostic so a slow init is not confused with a hard failure.
        /// </summary>
        private static async Awaitable<MolcaStepResult> InitializationStep(MolcaCommandContext context)
        {
            if (RuntimeManager.IsReady)
                return MolcaStepResult.Pass(new JObject { ["ready"] = true, ["waitedSeconds"] = 0f });

            float budget = context.Arguments["initTimeoutSeconds"] is JValue v && v.Type == JTokenType.Float
                ? (float)v
                : (context.Arguments["initTimeoutSeconds"]?.Value<float?>() ?? DefaultInitTimeoutSeconds);

            using var timeoutCts = new System.Threading.CancellationTokenSource(
                System.TimeSpan.FromSeconds(Mathf.Max(1f, budget)));
            using var linked = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(
                context.CancellationToken, timeoutCts.Token);

            var started = Time.realtimeSinceStartupAsDouble;
            try
            {
                await RuntimeManager.WaitForInitialization(linked.Token);
            }
            catch (System.OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                var timedOut = new JObject
                {
                    ["ready"] = false,
                    ["timedOut"] = true,
                    ["budgetSeconds"] = budget,
                    ["bootstrapState"] = RuntimeManager.State.ToString()
                };
                return MolcaStepResult.Fail(new[] { new MolcaDiagnostic("runtime.init_timeout",
                    $"Bootstrap did not reach Ready within {budget:0.#}s (state: {RuntimeManager.State}).") }, timedOut);
            }
            catch (System.InvalidOperationException ex)
            {
                return MolcaStepResult.Fail(new[] { new MolcaDiagnostic("runtime.bootstrap_failed", ex.Message) },
                    new JObject { ["ready"] = false, ["bootstrapState"] = RuntimeManager.State.ToString() });
            }

            var waited = (float)(Time.realtimeSinceStartupAsDouble - started);
            return MolcaStepResult.Pass(new JObject { ["ready"] = true, ["waitedSeconds"] = waited });
        }

        /// <summary>
        /// Reports the resolved initialization order and flags any subsystem that resolved but is not
        /// active. An empty resolved order after a Ready bootstrap means the graph never sorted — a hard
        /// failure, since dependent code would resolve nothing.
        /// </summary>
        private static Awaitable<MolcaStepResult> SubsystemsStep(MolcaCommandContext context)
        {
            var resolved = RuntimeManager.GetResolvedInitOrder();
            var discovered = RuntimeManager.GetSubsystems();

            var inactive = resolved.Where(s => s != null && !s.IsActive).Select(s => s.GetType().Name).ToList();
            var diagnostics = inactive
                .Select(name => new MolcaDiagnostic("runtime.subsystem_inactive",
                    $"Subsystem '{name}' resolved but is not active.", MolcaDiagnosticSeverity.Warning))
                .ToList();

            var data = new JObject
            {
                ["resolvedCount"] = resolved.Count,
                ["discoveredCount"] = discovered.Count,
                ["resolvedOrder"] = new JArray(resolved.Where(s => s != null).Select(s => s.GetType().Name)),
                ["inactive"] = new JArray(inactive)
            };

            return Completed(resolved.Count == 0
                ? MolcaStepResult.Fail(new[] { new MolcaDiagnostic("runtime.no_subsystems",
                    "Bootstrap is Ready but the resolved subsystem order is empty.") }, data)
                : MolcaStepResult.Pass(data, diagnostics));
        }

        /// <summary>
        /// Resolves each subsystem named in the <c>requiredSubsystems</c> argument (matched by simple or
        /// full type name) against the live graph. A missing required subsystem is an Error — it fails the
        /// workflow — but this step is non-critical so the remaining steps still gather their evidence.
        /// </summary>
        private static Awaitable<MolcaStepResult> ServicesStep(MolcaCommandContext context)
        {
            var required = (context.Arguments["requiredSubsystems"] as JArray)?
                .Select(t => t.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList() ?? new List<string>();

            var present = RuntimeManager.GetSubsystems()
                .Where(s => s != null)
                .Select(s => s.GetType())
                .ToList();

            var missing = new List<string>();
            var resolved = new List<string>();
            foreach (var name in required)
            {
                bool found = present.Any(t =>
                    t.Name == name || t.FullName == name);
                (found ? resolved : missing).Add(name);
            }

            var diagnostics = missing
                .Select(name => new MolcaDiagnostic("runtime.service_missing",
                    $"Required subsystem '{name}' did not resolve in the live runtime."))
                .ToList();

            var data = new JObject
            {
                ["requiredCount"] = required.Count,
                ["resolved"] = new JArray(resolved),
                ["missing"] = new JArray(missing)
            };

            return Completed(missing.Count > 0
                ? MolcaStepResult.Fail(diagnostics, data)
                : MolcaStepResult.Pass(data));
        }

        /// <summary>Discovers Sequence controllers in the loaded scenes (informational evidence).</summary>
        private static Awaitable<MolcaStepResult> SequencesStep(MolcaCommandContext context)
        {
            var controllers = Object.FindObjectsByType<SequenceController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var data = new JObject
            {
                ["sequenceControllerCount"] = controllers.Length,
                ["names"] = new JArray(controllers.Take(50).Select(c => c.gameObject.name))
            };
            return Completed(MolcaStepResult.Pass(data));
        }

        /// <summary>Wraps a synchronous step result in an already-completed awaitable (no yield).</summary>
        private static Awaitable<MolcaStepResult> Completed(MolcaStepResult result)
        {
            var source = new AwaitableCompletionSource<MolcaStepResult>();
            source.SetResult(result);
            return source.Awaitable;
        }
    }
}
