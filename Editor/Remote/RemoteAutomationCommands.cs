using System;
using System.Threading;
using Molca.Editor.Automation;
using Molca.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Remote
{
    /// <summary>
    /// The <c>automation.*</c> half of the Molca Remote companion: the bounded adapter that lets an
    /// authorized browser session read the automation catalog, preview a plan, start a run, follow it,
    /// cancel it, and revert it — always <em>through</em> the kernel's own policy, mode, confirmation,
    /// verification, and audit seams, never around them.
    /// </summary>
    /// <remarks>
    /// Editor-only; every entry point runs on the Unity main thread (the agent marshals through
    /// <see cref="McpMainThreadDispatcher"/>).
    /// <para>
    /// <b>Accept-fast.</b> A remote command row expires 60 s after creation, which bounds delivery and
    /// acceptance, not the work. <c>automation.invoke</c> therefore returns as soon as the run is
    /// <em>accepted</em> and the run itself proceeds in an owned task that the interactive Editor's update
    /// loop drives — the same way the Hub's own Run button drives it. Progress reaches the browser through
    /// the activity and automation state blocks; terminal detail through a later
    /// <c>automation.run-status</c> call.
    /// </para>
    /// <para>
    /// <b>Remote is additive to automation policy.</b> A remote caller passes the control plane's gates and
    /// then, here, the Editor's own: <see cref="MolcaRemoteSettings.AllowActions"/>, the remote action
    /// allowlist, then <see cref="MolcaAutomationPolicy"/> and the mode gate inside the kernel. Nothing on
    /// this path can raise the active profile, extend its allowlist, or mark a command confirmed on the
    /// user's behalf.
    /// </para>
    /// </remarks>
    internal static class RemoteAutomationCommands
    {
        /// <summary>Maximum encoded size of a caller-supplied argument object.</summary>
        internal const int MaxArgumentBytes = 4096;

        private const int MaxRunIdChars = 64;
        private const int MaxCommandIdChars = 128;

        // The one remote-initiated run allowed at a time, and its cancellation source. The kernel's
        // coordinator would serialize mutating runs anyway, but a remote queue with no visible owner is
        // worse than an explicit refusal: the caller cannot see why their run is not starting.
        private static string _inFlightRunId;
        private static CancellationTokenSource _inFlightCancellation;

        /// <summary>Whether a remote-initiated run is currently in flight (for tests and diagnostics).</summary>
        internal static bool HasRunInFlight => _inFlightRunId != null;

        /// <summary>
        /// Cancels the remote-initiated run, if any. Called when the socket drops, when authorization is
        /// lost, and when Molca Remote is disabled for the project — a run nobody can still observe or stop
        /// from the browser must not keep mutating the project.
        /// </summary>
        internal static void CancelForAuthorizationLoss()
        {
            var runId = _inFlightRunId;
            if (runId == null) return;
            try { _inFlightCancellation?.Cancel(); } catch (ObjectDisposedException) { }
            MolcaAutomationKernel.InstanceOrNull?.Cancel(runId);
        }

        /// <summary>
        /// Dispatches one <c>automation.*</c> command payload.
        /// </summary>
        /// <param name="payload">The <c>command.invoke</c> payload, including its <c>type</c>.</param>
        /// <returns>The <c>command.result</c> body: <c>{ ok, result }</c> or <c>{ ok: false, error }</c>.</returns>
        internal static JObject Execute(JObject payload)
        {
            var kernel = MolcaAutomationKernel.Instance;
            switch (payload.Value<string>("type"))
            {
                case "automation.capabilities":
                    return Ok(RemoteAutomationProjection.Catalog(kernel));

                case "automation.preview":
                    return Preview(kernel, payload);

                case "automation.run-status":
                {
                    var status = RemoteAutomationProjection.RunStatus(kernel, RunId(payload));
                    return status == null ? Failure("automation.unknown_run") : Ok(status);
                }

                case "automation.invoke":
                    return Invoke(kernel, payload);

                case "automation.cancel":
                {
                    // Signalling only: a command that does not honour its token simply runs to completion,
                    // so "signalled" is reported rather than "cancelled".
                    var runId = RunId(payload);
                    var signalled = kernel.Cancel(runId);
                    if (runId == _inFlightRunId)
                        try { _inFlightCancellation?.Cancel(); } catch (ObjectDisposedException) { }
                    return Ok(new JObject { ["runId"] = runId, ["signalled"] = signalled });
                }

                case "automation.revert":
                    return Revert(kernel, payload);

                default:
                    return Failure("unsupported_command");
            }
        }

        private static JObject Preview(MolcaAutomationKernel kernel, JObject payload)
        {
            var commandId = CommandId(payload);
            if (!kernel.TryGetCommand(commandId, out _)) return Failure("automation.unknown_command");
            if (!TryReadArguments(payload, out var arguments, out var argumentError))
                return Failure(argumentError);

            // PreviewPlan is side-effect free and runs the same gates a real invoke would, as an
            // unconfirmed interactive caller, so the confirmation requirement surfaces rather than being
            // assumed granted.
            return Ok(RemoteAutomationProjection.BoundPlan(
                kernel.PreviewPlan(commandId, arguments, MolcaTransport.Remote)));
        }

        private static JObject Invoke(MolcaAutomationKernel kernel, JObject payload)
        {
            var commandId = CommandId(payload);
            if (!kernel.TryGetCommand(commandId, out var command)) return Failure("automation.unknown_command");
            if (!TryReadArguments(payload, out var arguments, out var argumentError))
                return Failure(argumentError);

            // The control plane authorized this request against its cached copy of the catalog. If the
            // Editor's catalog has moved since — a package installed, a policy profile changed — the
            // capability that was checked may no longer be the right one, so refuse rather than guess.
            var expectedDigest = payload.Value<string>("catalogDigest");
            if (!string.IsNullOrEmpty(expectedDigest) &&
                expectedDigest != RemoteAutomationProjection.Catalog(kernel).Value<string>("catalogDigest"))
                return Failure("automation.catalog_stale");

            if (_inFlightRunId != null) return Failure("automation.run_in_flight");

            var localRefusal = ValidateLocalRemotePolicy(command);
            if (localRefusal != null) return Failure(localRefusal);

            // Checked last of the pre-run gates, not first: batch mode only prevents *hosting* a run, so a
            // caller with a bad command id or a stale catalog deserves that specific answer rather than a
            // blanket "headless". A headless Editor does not drive Awaitable continuations from an update
            // loop, so an owned run task would silently stall — CI uses the CLI entry points instead.
            if (Application.isBatchMode) return Failure("automation.batch_mode_refused");

            var confirmed = payload.Value<bool>("confirmed");
            var runId = RunId(payload);
            if (string.IsNullOrEmpty(runId)) runId = Guid.NewGuid().ToString();

            // Pre-empt the confirmation case instead of letting the executor produce it. The executor's
            // NeedsConfirmation path deliberately does not finalize its run — it expects the same caller to
            // come back confirmed — so invoking here would leave a handle stuck in the active-run list for
            // the rest of the session. Preview answers the same question without creating a run.
            var plan = kernel.PreviewPlan(commandId, arguments, MolcaTransport.Remote);
            if (plan.Value<bool>("needsConfirmationToRun") && !confirmed)
                return Ok(new JObject
                {
                    ["runId"] = runId,
                    ["status"] = "needs_confirmation",
                    ["reason"] = (plan["authorization"] as JObject)?.Value<string>("message")
                });

            _inFlightRunId = runId;
            _inFlightCancellation = new CancellationTokenSource();
            StartOwnedRun(kernel, commandId, arguments, confirmed, runId, _inFlightCancellation.Token);

            // Accept-fast: the run is now the Editor's business and reports through state. Anything the
            // caller still needs — diagnostics, evidence, revert availability — comes from run-status.
            return Ok(new JObject
            {
                ["runId"] = runId,
                ["status"] = "running",
                ["commandId"] = commandId
            });
        }

        /// <summary>
        /// Starts the owned run and returns immediately. <c>async void</c> is deliberate and confined to
        /// this one shim: it is the entry point from the main-thread dispatcher (async-contract rule 2), it
        /// wraps its whole body in try/catch so nothing escapes into Unity's synchronization context, and
        /// it always clears the in-flight slot.
        /// </summary>
        private static async void StartOwnedRun(
            MolcaAutomationKernel kernel, string commandId, JObject arguments,
            bool confirmed, string runId, CancellationToken cancellationToken)
        {
            try
            {
                await kernel.InvokeAsync(
                    commandId, arguments, MolcaTransport.Remote,
                    isConfirmed: confirmed, isBatchMode: false,
                    cancellationToken: cancellationToken, runId: runId);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca Remote] Automation run '{commandId}' failed: {exception.Message}");
            }
            finally
            {
                if (_inFlightRunId == runId)
                {
                    _inFlightRunId = null;
                    _inFlightCancellation?.Dispose();
                    _inFlightCancellation = null;
                }
            }
        }

        private static JObject Revert(MolcaAutomationKernel kernel, JObject payload)
        {
            if (!MolcaRemoteSettings.AllowActions) return Failure("remote_actions_disabled");

            var runId = RunId(payload);
            if (!kernel.IsRevertAvailable(runId)) return Failure("automation.revert_not_available");
            if (_inFlightRunId != null) return Failure("automation.run_in_flight");
            if (Application.isBatchMode) return Failure("automation.batch_mode_refused");

            // A revert undoes a real effect, so it is an action in its own right and reaches here only
            // after the control plane's confirmation flow. It is short by construction — a compensation the
            // command already prepared — so it is awaited rather than owned like a run.
            _inFlightRunId = runId;
            _inFlightCancellation = new CancellationTokenSource();
            StartOwnedRevert(kernel, runId, _inFlightCancellation.Token);
            return Ok(new JObject { ["runId"] = runId, ["status"] = "running" });
        }

        /// <summary>Owned-revert shim; same contract as <see cref="StartOwnedRun"/>.</summary>
        private static async void StartOwnedRevert(
            MolcaAutomationKernel kernel, string runId, CancellationToken cancellationToken)
        {
            try { await kernel.RevertAsync(runId, MolcaTransport.Remote, cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Molca Remote] Revert of run '{runId}' failed: {exception.Message}");
            }
            finally
            {
                if (_inFlightRunId == runId)
                {
                    _inFlightRunId = null;
                    _inFlightCancellation?.Dispose();
                    _inFlightCancellation = null;
                }
            }
        }

        /// <summary>
        /// The Editor-local remote gate, applied before the kernel's own. Read-only commands need nothing
        /// beyond Remote being enabled; an action additionally needs the remote action opt-in and a place on
        /// the remote allowlist. This is a *second* allowlist, independent of automation's — being on one
        /// never implies the other.
        /// </summary>
        /// <param name="command">The command being invoked.</param>
        /// <returns>A stable refusal code, or null when the local gate permits the call.</returns>
        internal static string ValidateLocalRemotePolicy(MolcaCommandDefinition command)
        {
            if (command == null) return "automation.unknown_command";
            if (command.Kind != MolcaCommandKind.Action) return null;
            if (!MolcaRemoteSettings.AllowActions) return "remote_actions_disabled";
            return McpSettings.GetOrCreateSettings().IsActionAllowed(command.Id)
                ? null : "action_not_allowlisted";
        }

        private static bool TryReadArguments(JObject payload, out JObject arguments, out string error)
        {
            arguments = payload["arguments"] as JObject ?? new JObject();
            var encoded = arguments.ToString(Formatting.None);
            if (System.Text.Encoding.UTF8.GetByteCount(encoded) > MaxArgumentBytes)
            {
                error = "automation.arguments_too_large";
                arguments = null;
                return false;
            }
            error = null;
            return true;
        }

        private static string RunId(JObject payload) => Truncate(payload.Value<string>("runId"), MaxRunIdChars);

        private static string CommandId(JObject payload) =>
            Truncate(payload.Value<string>("commandId"), MaxCommandIdChars);

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }

        private static JObject Ok(JToken result) => new JObject { ["ok"] = true, ["result"] = result };

        private static JObject Failure(string error) => new JObject { ["ok"] = false, ["error"] = error };
    }
}
