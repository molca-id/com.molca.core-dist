using System;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>The transport a run originated from. Recorded in audit and shown on the activity rail (§12).</summary>
    public enum MolcaTransport
    {
        /// <summary>In-Editor Hub UI.</summary>
        Hub,

        /// <summary>Unity CLI via the Pipeline adapter.</summary>
        Pipeline,

        /// <summary>MCP client (IDE proxy).</summary>
        Mcp,

        /// <summary>In-Editor Assistant.</summary>
        Assistant,

        /// <summary>Headless batch/CI entry point.</summary>
        Batch,

        /// <summary>Called directly in-process (tests, internal callers).</summary>
        Direct
    }

    /// <summary>
    /// Everything a command delegate needs to run one invocation: the run id, parsed arguments, the
    /// lifetime cancellation token, a progress sink, and the caller's transport/mode/confirmation state.
    /// The kernel constructs one per run and passes it to the command's executor and verifier.
    /// </summary>
    /// <remarks>
    /// A delegate receives the context on the Unity main thread and may freely touch Editor and runtime
    /// APIs. Thread the <see cref="CancellationToken"/> through every await in the command's chain
    /// (async-contract rule 3). Report progress through <see cref="ReportProgress"/>, never by logging.
    /// </remarks>
    public sealed class MolcaCommandContext
    {
        /// <summary>Unique id (uuid) for this run; correlates progress, audit, and the result.</summary>
        public string RunId { get; }

        /// <summary>The stable id of the command being run.</summary>
        public string CommandId { get; }

        /// <summary>Parsed arguments (never null; empty object when none were supplied).</summary>
        public JObject Arguments { get; }

        /// <summary>The lifetime cancellation token for this run. Cancelled on caller cancel/timeout/teardown.</summary>
        public CancellationToken CancellationToken { get; }

        /// <summary>The transport that started this run.</summary>
        public MolcaTransport Transport { get; }

        /// <summary>True when running headless (batch/CI) — no interactive confirmation is possible.</summary>
        public bool IsBatchMode { get; }

        /// <summary>
        /// True when interactive confirmation for an irreversible action was already granted by the
        /// caller/policy. A command that requires confirmation must refuse when this is false and it
        /// cannot obtain consent (§13).
        /// </summary>
        public bool IsConfirmed { get; }

        private readonly Action<MolcaCommandProgress> _progress;
        private MolcaCompensation _compensation;

        /// <summary>
        /// The reversibility kind a command declared when it registered its compensation, or
        /// <see cref="MolcaCommandReversibility.None"/> when none was registered.
        /// </summary>
        internal MolcaCommandReversibility RevertKind { get; private set; } = MolcaCommandReversibility.None;

        /// <summary>The compensation a reversible command registered during execution, or null.</summary>
        internal MolcaCompensation Compensation => _compensation;

        /// <summary>True when the running command registered a way to undo its effect.</summary>
        public bool HasCompensation => _compensation != null;

        /// <summary>Creates a command context.</summary>
        /// <param name="runId">Unique run id.</param>
        /// <param name="commandId">Stable command id.</param>
        /// <param name="arguments">Parsed arguments; null becomes an empty object.</param>
        /// <param name="cancellationToken">Lifetime cancellation token.</param>
        /// <param name="transport">Originating transport.</param>
        /// <param name="isBatchMode">Whether the caller is headless.</param>
        /// <param name="isConfirmed">Whether interactive confirmation was granted.</param>
        /// <param name="progress">Optional progress sink; null discards progress.</param>
        public MolcaCommandContext(
            string runId, string commandId, JObject arguments, CancellationToken cancellationToken,
            MolcaTransport transport, bool isBatchMode = false, bool isConfirmed = false,
            Action<MolcaCommandProgress> progress = null)
        {
            RunId = runId;
            CommandId = commandId;
            Arguments = arguments ?? new JObject();
            CancellationToken = cancellationToken;
            Transport = transport;
            IsBatchMode = isBatchMode;
            IsConfirmed = isConfirmed;
            _progress = progress;
        }

        /// <summary>
        /// Registers how to undo this command's effect (§13, Phase 4). A reversible command calls this
        /// during execution, after capturing whatever "before" state the rollback needs (e.g. the Undo
        /// group, a file snapshot id, the prior active build target). The executor runs the compensation
        /// automatically if the run fails, or stores it for a later explicit revert if the run succeeds.
        /// </summary>
        /// <param name="kind">The reversibility kind this compensation realizes.</param>
        /// <param name="compensation">The undo work.</param>
        /// <exception cref="ArgumentException">If <paramref name="kind"/> is <see cref="MolcaCommandReversibility.None"/>.</exception>
        /// <exception cref="ArgumentNullException">If <paramref name="compensation"/> is null.</exception>
        public void RegisterCompensation(MolcaCommandReversibility kind, MolcaCompensation compensation)
        {
            if (kind == MolcaCommandReversibility.None)
                throw new ArgumentException("A compensation must declare a revertible kind (not None).", nameof(kind));
            _compensation = compensation ?? throw new ArgumentNullException(nameof(compensation));
            RevertKind = kind;
        }

        /// <summary>Reports a progress snapshot to the caller's sink (no-op when none was supplied).</summary>
        /// <param name="progress">The snapshot to report.</param>
        public void ReportProgress(MolcaCommandProgress progress) => _progress?.Invoke(progress);

        /// <summary>Reports indeterminate progress carrying only a message.</summary>
        /// <param name="message">Short status message.</param>
        public void ReportProgress(string message) => _progress?.Invoke(MolcaCommandProgress.Indeterminate(message));

        /// <summary>Raw arguments JSON string, for delegates that wrap an existing string-based API.</summary>
        /// <returns>Compact JSON of <see cref="Arguments"/>.</returns>
        public string ArgumentsJson() => Arguments.ToString(Newtonsoft.Json.Formatting.None);
    }
}
