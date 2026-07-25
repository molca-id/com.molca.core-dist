using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Runs one command through the full <c>Observe → Authorize → Acquire → Execute → Verify → Report</c>
    /// pipeline (§13): mode-gating, policy authorization and confirmation, resource acquisition, timed and
    /// cancellable execution, optional postcondition verification, run-store bookkeeping, and audit. Every
    /// transport routes through this one executor, so all runs share one safety and result model.
    /// </summary>
    /// <remarks>
    /// Constructed once by the kernel with the shared <see cref="MolcaExecutionCoordinator"/>,
    /// <see cref="MolcaRunStore"/>, <see cref="MolcaCancellationRegistry"/>, and policy. Runs on the Unity
    /// main thread. Verification failure fails the run even when the delegate returned successfully (§13).
    /// </remarks>
    public sealed class MolcaCommandExecutor
    {
        /// <summary>Default per-run timeout used when a command declares none.</summary>
        public const int DefaultTimeoutMs = 300_000;

        private readonly MolcaExecutionCoordinator _coordinator;
        private readonly MolcaRunStore _runStore;
        private readonly MolcaCancellationRegistry _cancellations;
        private readonly IMolcaAutomationPolicy _policy;
        private readonly MolcaRevertRegistry _revertRegistry;

        /// <summary>Creates an executor over the shared kernel services.</summary>
        /// <param name="coordinator">Resource coordinator.</param>
        /// <param name="runStore">Run store.</param>
        /// <param name="cancellations">Cancellation registry.</param>
        /// <param name="policy">Authorization policy.</param>
        /// <param name="revertRegistry">Registry that stores compensations of successful reversible runs; a private one is used when null.</param>
        public MolcaCommandExecutor(
            MolcaExecutionCoordinator coordinator, MolcaRunStore runStore,
            MolcaCancellationRegistry cancellations, IMolcaAutomationPolicy policy,
            MolcaRevertRegistry revertRegistry = null)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
            _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
            _policy = policy ?? new MolcaAllowReadOnlyPolicy();
            _revertRegistry = revertRegistry ?? new MolcaRevertRegistry();
        }

        /// <summary>
        /// Executes <paramref name="command"/> and returns its terminal result. The returned result always
        /// carries the run id, start time, and duration; the run is recorded in the store and audited.
        /// </summary>
        /// <param name="command">The command to run.</param>
        /// <param name="arguments">Parsed arguments (null → empty).</param>
        /// <param name="transport">Originating transport.</param>
        /// <param name="callerToken">Caller cancellation token.</param>
        /// <param name="isConfirmed">Whether interactive confirmation was granted.</param>
        /// <param name="isBatchMode">Whether the caller is headless.</param>
        /// <param name="progress">Optional caller progress sink.</param>
        /// <param name="runId">Optional caller-supplied run id; a uuid is generated when null.</param>
        /// <returns>The terminal <see cref="MolcaCommandResult"/>.</returns>
        public async Awaitable<MolcaCommandResult> ExecuteAsync(
            MolcaCommandDefinition command,
            JObject arguments,
            MolcaTransport transport,
            CancellationToken callerToken = default,
            bool isConfirmed = false,
            bool isBatchMode = false,
            Action<MolcaCommandProgress> progress = null,
            string runId = null)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            runId ??= Guid.NewGuid().ToString();
            var handle = _runStore.Create(runId, command.Id, transport);

            // Build the context up front so the pre-run gates can inspect transport/confirmation state.
            var context = new MolcaCommandContext(
                runId, command.Id, arguments, CancellationToken.None, transport, isBatchMode, isConfirmed,
                p => { handle.Progress = p; progress?.Invoke(p); });

            // --- Mode gate (precondition, not an error) ---
            var modeRefusal = CheckMode(command);
            if (modeRefusal != null)
                return Finalize(handle, context, Stamp(modeRefusal, handle, null));

            // --- Policy authorization + confirmation ---
            var decision = _policy.Authorize(command, context);
            if (!decision.Allowed)
                return Finalize(handle, context,
                    Stamp(MolcaCommandResult.Refused(command.Id, decision.Code, decision.Message), handle, null));
            if (decision.NeedsConfirmation && !isConfirmed)
            {
                if (isBatchMode)
                    return Finalize(handle, context, Stamp(MolcaCommandResult.Refused(
                        command.Id, "policy.confirmation_unavailable",
                        "Irreversible action requires confirmation, which is unavailable in batch mode."), handle, null));
                handle.Status = MolcaCommandStatus.NeedsConfirmation;
                return Stamp(MolcaCommandResult.Create(command.Id, MolcaCommandStatus.NeedsConfirmation), handle, null);
            }

            // --- Register lifetime token (linked to caller + timeout) ---
            var timeout = command.ExecutionTimeoutMs > 0 ? command.ExecutionTimeoutMs : DefaultTimeoutMs;
            var runToken = _cancellations.Register(runId, callerToken, timeout);
            var runContext = new MolcaCommandContext(
                runId, command.Id, arguments, runToken, transport, isBatchMode, isConfirmed,
                p => { handle.Progress = p; progress?.Invoke(p); });

            var stopwatch = Stopwatch.StartNew();
            MolcaResourceLease lease = null;
            try
            {
                // --- Acquire resources (queued while waiting) ---
                try
                {
                    lease = await _coordinator.AcquireAsync(command.ResourceClaims, runToken);
                }
                catch (OperationCanceledException)
                {
                    return Finalize(handle, runContext, Stamp(MolcaCommandResult.Cancelled(command.Id), handle, stopwatch));
                }

                _runStore.MarkStarted(handle);

                // --- Execute (single-exit: exceptions become a terminal result so the compensation
                //     path below can still roll back a partial effect, §13) ---
                MolcaCommandResult result;
                try
                {
                    result = command.IsAsync
                        ? await command.ExecuteAsync(runContext)
                        : command.Execute(runContext);
                }
                catch (OperationCanceledException)
                {
                    result = MolcaCommandResult.Cancelled(command.Id);
                }
                catch (Exception ex)
                {
                    result = MolcaCommandResult.Fail(command.Id, "command.exception", ex.Message);
                }

                // --- Verify postcondition (a failed verify fails the run, §13) ---
                if (command.Verify != null && result.Success)
                {
                    try
                    {
                        var verification = await command.Verify(runContext);
                        result = verification.Performed && !verification.Passed
                            ? MolcaCommandResult.Create(command.Id, MolcaCommandStatus.Failed, result.Data,
                                new[] { new MolcaDiagnostic("verify.failed", "Postcondition verification failed.") },
                                result.Artifacts, verification, result.Revert)
                            : MolcaCommandResult.Create(command.Id, result.Status, result.Data,
                                result.Diagnostics, result.Artifacts, verification, result.Revert);
                    }
                    catch (Exception ex)
                    {
                        result = MolcaCommandResult.Fail(command.Id, "verify.exception", ex.Message);
                    }
                }

                // --- Compensation / revert (§13, Phase 4) ---
                if (runContext.HasCompensation)
                    result = await HandleCompensation(command, runContext, result);

                return Finalize(handle, runContext, Stamp(result, handle, stopwatch));
            }
            finally
            {
                lease?.Dispose();
                _cancellations.Release(runId);
            }
        }

        private MolcaCommandResult CheckMode(MolcaCommandDefinition command)
        {
            var (ok, code, message) = MolcaModeGate.Check(command.Mode);
            return ok ? null : MolcaCommandResult.Refused(command.Id, code, message);
        }

        /// <summary>The shared revert registry, so the kernel can execute an explicit revert by run id.</summary>
        internal MolcaRevertRegistry RevertRegistry => _revertRegistry;

        /// <summary>
        /// Applies the compensation a reversible command registered (§13, Phase 4): on success it is stored
        /// for a later explicit revert and advertised on the result; on failure it runs now so a partial
        /// effect does not linger, and the rollback outcome is folded into the diagnostics.
        /// </summary>
        private async Awaitable<MolcaCommandResult> HandleCompensation(
            MolcaCommandDefinition command, MolcaCommandContext runContext, MolcaCommandResult result)
        {
            if (result.Success)
            {
                _revertRegistry.Register(runContext.RunId, command.Id, runContext.RevertKind, runContext.Compensation);
                return MolcaCommandResult.Create(command.Id, result.Status, result.Data,
                    result.Diagnostics, result.Artifacts, result.Verification,
                    new MolcaRevertInfo(runContext.RevertKind, runContext.RunId));
            }

            // Roll back now. The run token may already be cancelled, so bound the rollback on its own timeout.
            MolcaRevertOutcome outcome;
            using var revertCts = new CancellationTokenSource(DefaultTimeoutMs);
            try { outcome = await runContext.Compensation(revertCts.Token); }
            catch (Exception ex) { outcome = MolcaRevertOutcome.Failed(ex.Message); }

            var diagnostics = new List<MolcaDiagnostic>(result.Diagnostics)
            {
                outcome.Succeeded
                    ? new MolcaDiagnostic("revert.performed", "The failed action was rolled back.", MolcaDiagnosticSeverity.Info)
                    : new MolcaDiagnostic("revert.failed", $"Rollback did not complete: {outcome.FailureMessage}")
            };

            return MolcaCommandResult.Create(command.Id, result.Status, result.Data, diagnostics,
                result.Artifacts, result.Verification, new MolcaRevertInfo(runContext.RevertKind, null));
        }

        /// <summary>Stamps run id, start time, and duration onto a result and mirrors them to the handle.</summary>
        private static MolcaCommandResult Stamp(MolcaCommandResult result, MolcaRunHandle handle, Stopwatch stopwatch)
        {
            result.RunId = handle.RunId;
            result.StartedAtUtc = handle.StartedAtUtc ?? handle.CreatedAtUtc;
            result.DurationMs = stopwatch?.ElapsedMilliseconds ?? 0;
            return result;
        }

        private MolcaCommandResult Finalize(MolcaRunHandle handle, MolcaCommandContext context, MolcaCommandResult result)
        {
            _runStore.Complete(handle, result);
            MolcaAutomationAuditLog.Record(context, result);
            // Single terminal chokepoint for every run, so automation adoption is reported once per run
            // regardless of which gate, failure, or success path produced the result. Command id and
            // transport only — arguments and diagnostics can carry project detail.
            Telemetry.MolcaEditorTelemetry.Track("editor.automation.run", new Dictionary<string, object>
            {
                { "command", context.CommandId },
                { "transport", context.Transport.ToString() },
                { "status", result.Status.ToString() },
                { "batchMode", context.IsBatchMode },
            });
            return result;
        }
    }
}
