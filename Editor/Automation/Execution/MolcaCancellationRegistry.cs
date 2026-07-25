using System;
using System.Collections.Generic;
using System.Threading;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Tracks the <see cref="CancellationTokenSource"/> of every in-flight run by <c>runId</c>, so a
    /// caller (or the Hub activity rail / <c>molca-cancel</c>) can cancel a long-running run it did not
    /// start (§13, §16). Each registered token is linked to the caller's token and, when a positive
    /// timeout is given, a timeout — either trips cancellation. Main thread only.
    /// </summary>
    public sealed class MolcaCancellationRegistry
    {
        private readonly Dictionary<string, CancellationTokenSource> _sources =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        /// <summary>
        /// Registers a run and returns its lifetime token, linked to <paramref name="callerToken"/> and,
        /// if <paramref name="timeoutMs"/> &gt; 0, cancelled after that timeout.
        /// </summary>
        /// <param name="runId">The run id to key on.</param>
        /// <param name="callerToken">The caller's cancellation token to link.</param>
        /// <param name="timeoutMs">Timeout in ms; ≤ 0 for no timeout.</param>
        /// <returns>The run's lifetime cancellation token.</returns>
        public CancellationToken Register(string runId, CancellationToken callerToken, int timeoutMs)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
            _sources[runId] = cts;
            return cts.Token;
        }

        /// <summary>Requests cancellation of a run by id. No-op if the run is unknown or already done.</summary>
        /// <param name="runId">The run to cancel.</param>
        /// <returns>True if a live run was signalled to cancel.</returns>
        public bool Cancel(string runId)
        {
            if (runId != null && _sources.TryGetValue(runId, out var cts))
            {
                try { cts.Cancel(); return true; }
                catch (ObjectDisposedException) { /* already completed */ }
            }
            return false;
        }

        /// <summary>Disposes and forgets a run's token source. Call once the run reaches a terminal state.</summary>
        /// <param name="runId">The run to release.</param>
        public void Release(string runId)
        {
            if (runId != null && _sources.TryGetValue(runId, out var cts))
            {
                _sources.Remove(runId);
                cts.Dispose();
            }
        }

        /// <summary>Whether a run with the given id is currently registered.</summary>
        /// <param name="runId">The run id.</param>
        /// <returns>True if the run is tracked.</returns>
        public bool IsTracked(string runId) => runId != null && _sources.ContainsKey(runId);
    }
}
