using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The transport-neutral entry point to Molca Automation (§6, §18 Phase 1). Owns the command registry,
    /// the shared execution coordinator/run store/cancellation registry, and the active policy, and exposes
    /// the <c>status / capabilities / describe / invoke / run-status / cancel</c> operations every transport
    /// (Hub, CLI/Pipeline, MCP, Assistant, batch) routes through. There is exactly one safety and result
    /// model because there is exactly one executor here.
    /// </summary>
    /// <remarks>
    /// A lazily-built singleton. The registry and policy are rebuilt from discovered providers and settings
    /// on construction and after a domain reload (statics reset); the coordinator, run store, and
    /// cancellation registry are created once so in-flight bookkeeping is not disturbed by a policy rebuild.
    /// Editor-only; main thread. Long-running commands are exposed to the CLI via fire-and-poll (start →
    /// <see cref="TryGetRun"/> / <see cref="Cancel"/>), never as a single &gt;30s awaited call (Phase 0).
    /// </remarks>
    public sealed class MolcaAutomationKernel
    {
        private static MolcaAutomationKernel _instance;

        /// <summary>The shared kernel instance, built on first access (and after each domain reload).</summary>
        public static MolcaAutomationKernel Instance => _instance ??= new MolcaAutomationKernel();

        /// <summary>
        /// The kernel instance if it has already been built, else null — for observers (e.g. the Hub
        /// activity rail) that must not force construction just to check for active runs.
        /// </summary>
        internal static MolcaAutomationKernel InstanceOrNull => _instance;

        private readonly MolcaExecutionCoordinator _coordinator = new MolcaExecutionCoordinator();
        private readonly MolcaRunStore _runStore = new MolcaRunStore(new MolcaRunJournal());
        private readonly MolcaCancellationRegistry _cancellations = new MolcaCancellationRegistry();
        private readonly MolcaRevertRegistry _revertRegistry = new MolcaRevertRegistry();

        private MolcaCommandRegistry _registry;
        private IMolcaAutomationPolicy _policy;
        private string _policySource = MolcaAutomationPolicyResolver.SettingsSource;
        private MolcaCommandExecutor _executor;

        private MolcaAutomationKernel() => Rebuild();

        /// <summary>Rebuilds the registry, policy, and executor from current providers and settings.</summary>
        public void Rebuild()
        {
            // The policy is resolved from discovered providers (highest priority wins) so a fork/consumer
            // can replace the authorization logic without editing the kernel; the built-in settings policy
            // is the fallback (§9.3).
            _policy = MolcaAutomationPolicyResolver.ResolveDiscovered(out _policySource);
            _registry = BuildRegistry();
            _executor = new MolcaCommandExecutor(_coordinator, _runStore, _cancellations, _policy, _revertRegistry);
        }

        /// <summary>The label naming which policy source is in force (e.g. <c>settings</c> or a provider's name).</summary>
        public string PolicySource => _policySource;

        /// <summary>The command registry (including collision errors, if any).</summary>
        public MolcaCommandRegistry Registry => _registry;

        /// <summary>The active authorization policy.</summary>
        public IMolcaAutomationPolicy Policy => _policy;

        /// <summary>The shared run store (query/list runs).</summary>
        public MolcaRunStore RunStore => _runStore;

        /// <summary>All discovered commands, in id order.</summary>
        /// <returns>The registered command definitions.</returns>
        public IReadOnlyList<MolcaCommandDefinition> Capabilities() => _registry.Commands;

        /// <summary>Resolves a command by id.</summary>
        /// <param name="commandId">The command id.</param>
        /// <param name="command">The resolved definition, or null.</param>
        /// <returns>True if found.</returns>
        public bool TryGetCommand(string commandId, out MolcaCommandDefinition command)
            => _registry.TryGet(commandId, out command);

        /// <summary>
        /// Invokes a command by id and returns its terminal result. Enforces the same policy, mode,
        /// resource, confirmation, and audit rules as every other path — this is not a bypass (§10).
        /// </summary>
        /// <param name="commandId">The command id to run.</param>
        /// <param name="arguments">Parsed arguments (null → empty).</param>
        /// <param name="transport">Originating transport.</param>
        /// <param name="isConfirmed">Whether interactive confirmation was granted.</param>
        /// <param name="isBatchMode">Whether the caller is headless.</param>
        /// <param name="cancellationToken">Caller cancellation token.</param>
        /// <param name="progress">Optional progress sink.</param>
        /// <param name="runId">Optional caller-supplied run id.</param>
        /// <returns>The terminal result.</returns>
        public async Awaitable<MolcaCommandResult> InvokeAsync(
            string commandId, JObject arguments, MolcaTransport transport,
            bool isConfirmed = false, bool isBatchMode = false, CancellationToken cancellationToken = default,
            Action<MolcaCommandProgress> progress = null, string runId = null)
        {
            if (!_registry.TryGet(commandId, out var command))
            {
                var notFound = MolcaCommandResult.Fail(commandId ?? "(null)", "command.not_found",
                    $"No command with id '{commandId}' is registered.");
                notFound.RunId = runId ?? Guid.NewGuid().ToString();
                notFound.StartedAtUtc = DateTime.UtcNow;
                return notFound;
            }

            return await _executor.ExecuteAsync(
                command, arguments, transport, cancellationToken, isConfirmed, isBatchMode, progress, runId);
        }

        /// <summary>
        /// Previews what would happen if <paramref name="commandId"/> ran now — the mode/policy gates,
        /// confirmation requirement, resource claims, reversibility, and retry classification — without
        /// running it or touching any resource (§13, Phase 4). Read-only and side-effect free.
        /// </summary>
        /// <param name="commandId">The command id to preview.</param>
        /// <param name="arguments">Parsed arguments (null → empty); some policies inspect them.</param>
        /// <param name="transport">The transport the real run would use. Defaults to <see cref="MolcaTransport.Direct"/>.</param>
        /// <returns>The plan preview JSON, or a <c>found: false</c> object when the id is unknown.</returns>
        public JObject PreviewPlan(string commandId, JObject arguments, MolcaTransport transport = MolcaTransport.Direct)
        {
            if (!_registry.TryGet(commandId, out var command))
                return new JObject
                {
                    ["command"] = commandId,
                    ["found"] = false,
                    ["error"] = "command.not_found"
                };

            // Preview as an unconfirmed, interactive caller so the confirmation requirement surfaces
            // rather than being silently assumed granted.
            var context = new MolcaCommandContext(
                "preview", commandId, arguments ?? new JObject(), CancellationToken.None,
                transport, isBatchMode: false, isConfirmed: false);

            var decision = _policy.Authorize(command, context);
            var (modeOk, _, modeMessage) = MolcaModeGate.Check(command.Mode);
            var profile = (_policy as MolcaAutomationPolicy)?.Profile.ToString() ?? _policy.GetType().Name;

            var plan = MolcaCommandDescriptors.DescribePlan(command, decision, modeOk, modeMessage, profile);
            plan["found"] = true;
            return plan;
        }

        /// <summary>Looks up a run by id (for a <c>run-status</c> reconnect, §16).</summary>
        /// <param name="runId">The run id.</param>
        /// <param name="handle">The resolved handle, or null.</param>
        /// <returns>True if found.</returns>
        public bool TryGetRun(string runId, out MolcaRunHandle handle) => _runStore.TryGet(runId, out handle);

        /// <summary>Requests cancellation of a run by id.</summary>
        /// <param name="runId">The run to cancel.</param>
        /// <returns>True if a live run was signalled.</returns>
        public bool Cancel(string runId) => _cancellations.Cancel(runId);

        /// <summary>Whether a completed run left a revert that can still be executed (§13, Phase 4).</summary>
        /// <param name="runId">The run id.</param>
        /// <returns>True if a revert is available.</returns>
        public bool IsRevertAvailable(string runId) => _revertRegistry.Has(runId);

        /// <summary>
        /// Executes the compensation a successful reversible run registered, undoing its effect (§13,
        /// Phase 4). A revert runs once — the registration is consumed — and returns its own result
        /// envelope. Returns a <c>revert.not_available</c> failure when nothing is registered for the run.
        /// </summary>
        /// <param name="runId">The run whose effect to revert.</param>
        /// <param name="transport">The transport requesting the revert.</param>
        /// <param name="cancellationToken">Bounds the rollback.</param>
        /// <returns>The revert result.</returns>
        public async Awaitable<MolcaCommandResult> RevertAsync(
            string runId, MolcaTransport transport = MolcaTransport.Direct, CancellationToken cancellationToken = default)
        {
            var started = DateTime.UtcNow;
            if (!_revertRegistry.TryTake(runId, out var commandId, out var kind, out var compensation))
            {
                var na = MolcaCommandResult.Fail("molca.revert", "revert.not_available",
                    $"No revert is registered for run '{runId}' — it may have already been reverted, been evicted, or never produced a reversible effect.");
                na.RunId = Guid.NewGuid().ToString();
                na.StartedAtUtc = started;
                return na;
            }

            MolcaRevertOutcome outcome;
            try { outcome = await compensation(cancellationToken); }
            catch (OperationCanceledException) { outcome = MolcaRevertOutcome.Failed("The revert was cancelled."); }
            catch (Exception ex) { outcome = MolcaRevertOutcome.Failed(ex.Message); }

            var data = new JObject
            {
                ["revertedRunId"] = runId,
                ["command"] = commandId,
                ["kind"] = MolcaCommandResult.WireStatusName(kind.ToString()),
                ["outcome"] = outcome.ToJson()
            };

            var result = outcome.Succeeded
                ? MolcaCommandResult.Succeeded("molca.revert", data, new MolcaVerification(true, true, outcome.Evidence))
                : MolcaCommandResult.Failed("molca.revert",
                    new[] { new MolcaDiagnostic("revert.failed", outcome.FailureMessage) }, data);
            result.RunId = Guid.NewGuid().ToString();
            result.StartedAtUtc = started;
            return result;
        }

        /// <summary>A snapshot of kernel status for the Hub overview and a <c>status</c> command (§12).</summary>
        /// <returns>Kernel status as JSON.</returns>
        public JObject StatusJson()
        {
            var active = _runStore.ActiveRuns();
            var history = _runStore.History();
            return new JObject
            {
                ["commandCount"] = _registry.Commands.Count,
                ["registryHasErrors"] = _registry.HasErrors,
                ["activeProfile"] = (_policy as MolcaAutomationPolicy)?.Profile.ToString() ?? _policy.GetType().Name,
                ["policySource"] = _policySource,
                ["activeRunCount"] = active.Count,
                ["historyCount"] = history.Count,
                ["interruptedRunCount"] = history.Count(r => r.Status == MolcaCommandStatus.Interrupted),
                ["coordinator"] = new JObject
                {
                    ["activeReaders"] = _coordinator.ActiveReaders,
                    ["hasActiveWriter"] = _coordinator.HasActiveWriter
                }
            };
        }

        private static MolcaCommandRegistry BuildRegistry()
        {
            var providers = new List<MolcaCommandProvider>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<MolcaCommandProvider>())
            {
                // Only auto-discover providers that opt in with a public parameterless constructor. A
                // provider that needs constructor arguments (e.g. a test stub, or one wired up explicitly)
                // is not meant for TypeCache discovery and is skipped silently — not an error.
                if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null) continue;
                try { providers.Add((MolcaCommandProvider)Activator.CreateInstance(type)); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Automation] Provider '{type.FullName}' could not be instantiated (skipped): {ex.Message}");
                }
            }
            return MolcaCommandRegistry.Build(providers);
        }
    }
}
