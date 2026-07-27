using System.Collections.Generic;
using System.Linq;
using System.Text;
using Molca.Editor.Hub;
using UnityEditor;

namespace Molca.Editor.Automation.Hub
{
    /// <summary>
    /// Contributes one chip per active automation run to the Hub's existing bottom activity rail (§12),
    /// through the standard <see cref="MolcaHubActivityProvider"/> seam — no new rail. Every transport's runs
    /// (Hub, CLI/Pipeline, MCP, Assistant, batch) surface here because they share the kernel run store, so a
    /// developer sees active command runs without leaving the Editor.
    /// </summary>
    /// <remarks>
    /// A stateful observer: it polls the kernel run store on <see cref="EditorApplication.update"/> and only
    /// raises <c>Changed</c> when the active-run signature actually changes, so an idle Editor and the common
    /// brief await-in-request runs do not churn the rail. Unsubscribes in <see cref="Dispose"/>.
    /// </remarks>
    internal sealed class MolcaAutomationActivityProvider : MolcaHubActivityProvider
    {
        private string _signature = string.Empty;

        /// <summary>Creates the provider and begins observing the run store.</summary>
        public MolcaAutomationActivityProvider()
        {
            EditorApplication.update += Poll;
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            EditorApplication.update -= Poll;
            base.Dispose();
        }

        private void Poll()
        {
            var kernel = MolcaAutomationKernel.InstanceOrNull;
            var runs = kernel != null ? kernel.RunStore.ActiveRuns() : (IReadOnlyList<MolcaRunHandle>)System.Array.Empty<MolcaRunHandle>();

            var sb = new StringBuilder();
            foreach (var run in runs)
                sb.Append(run.RunId).Append(':').Append(run.Status).Append(':')
                  .Append(run.Progress?.Message).Append('|');
            var signature = sb.ToString();

            if (signature != _signature)
            {
                _signature = signature;
                NotifyChanged();
            }
        }

        /// <inheritdoc/>
        public override IEnumerable<MolcaHubActivity> GetActivities()
        {
            var kernel = MolcaAutomationKernel.InstanceOrNull;
            if (kernel == null) yield break;

            foreach (var run in kernel.RunStore.ActiveRuns())
            {
                var progress = run.Progress;
                var hasMessage = progress.HasValue && !string.IsNullOrEmpty(progress.Value.Message);
                var status = hasMessage
                    ? $"{Wire(run.Status)} · {progress.Value.Message}"
                    : Wire(run.Status);

                float? fraction = progress.HasValue && !progress.Value.IsIndeterminate
                    ? progress.Value.Fraction
                    : (float?)null;

                yield return new MolcaHubActivity(
                    id: "automation-run:" + run.RunId,
                    label: ShortName(run.CommandId),
                    status: $"{status} · {run.Transport}",
                    state: MolcaHubActivityState.Running,
                    progress: fraction,
                    workspaceId: "automation",
                    // The caption embeds the command's own progress message, which is only reviewed text
                    // for Core-shipped commands. A third-party command's run still surfaces remotely
                    // through the automation state block (status, progress, step) — just not through a
                    // chip caption carrying its free text (§8.6).
                    remoteSafe: !hasMessage || BuiltIn.CoreShippedCommands.IsTrusted(run.CommandId));
            }
        }

        private static string Wire(MolcaCommandStatus status) =>
            MolcaCommandResult.WireStatusName(status.ToString());

        private static string ShortName(string commandId)
        {
            if (string.IsNullOrEmpty(commandId)) return "command";
            var slash = commandId.LastIndexOfAny(new[] { '.', '_' });
            return slash >= 0 && slash < commandId.Length - 1 ? commandId.Substring(slash + 1) : commandId;
        }
    }
}
