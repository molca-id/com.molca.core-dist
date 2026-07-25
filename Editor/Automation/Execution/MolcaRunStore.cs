using System;
using System.Collections.Generic;
using System.Linq;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// The live record of one run: its command, transport, status, latest progress, and final result.
    /// Handles are mutated by the executor as a run advances and read by the run-status API and the Hub
    /// activity rail (§5, §12). Reference type so observers see updates without re-fetching.
    /// </summary>
    public sealed class MolcaRunHandle
    {
        /// <summary>Unique run id.</summary>
        public string RunId { get; }

        /// <summary>The command id this run executes.</summary>
        public string CommandId { get; }

        /// <summary>The transport that started the run.</summary>
        public MolcaTransport Transport { get; }

        /// <summary>Current status.</summary>
        public MolcaCommandStatus Status { get; internal set; }

        /// <summary>UTC creation time.</summary>
        public DateTime CreatedAtUtc { get; }

        /// <summary>UTC start time (when it left the queue), or null.</summary>
        public DateTime? StartedAtUtc { get; internal set; }

        /// <summary>UTC completion time, or null while active.</summary>
        public DateTime? CompletedAtUtc { get; internal set; }

        /// <summary>Latest progress snapshot, or null if none reported yet.</summary>
        public MolcaCommandProgress? Progress { get; internal set; }

        /// <summary>Final result once the run reaches a terminal state, or null while active.</summary>
        public MolcaCommandResult Result { get; internal set; }

        /// <summary>True when the run has reached a terminal state.</summary>
        public bool IsTerminal =>
            Status == MolcaCommandStatus.Succeeded || Status == MolcaCommandStatus.Failed ||
            Status == MolcaCommandStatus.Cancelled || Status == MolcaCommandStatus.Refused ||
            Status == MolcaCommandStatus.Interrupted;

        internal MolcaRunHandle(string runId, string commandId, MolcaTransport transport)
        {
            RunId = runId;
            CommandId = commandId;
            Transport = transport;
            Status = MolcaCommandStatus.Queued;
            CreatedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// In-memory registry of runs keyed by <c>runId</c>, so a caller can reconnect and query a
    /// long-running run (§16) and the Hub rail can list active runs (§12). Bounded: completed runs are
    /// retained up to a cap, then the oldest are evicted. Main thread only. Disk-persisted resumable run
    /// metadata is a Phase 3 addition; this store does not survive a domain reload.
    /// </summary>
    public sealed class MolcaRunStore
    {
        private readonly Dictionary<string, MolcaRunHandle> _runs = new Dictionary<string, MolcaRunHandle>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();
        private readonly int _completedRetentionCap;
        private readonly MolcaRunJournal _journal;

        // Runs recovered from disk on construction (prior sessions), keyed by runId. A run from this
        // session that is still live sits in _runs and shadows any same-id entry here.
        private readonly Dictionary<string, MolcaPersistedRun> _history =
            new Dictionary<string, MolcaPersistedRun>(StringComparer.Ordinal);

        /// <summary>Creates a run store retaining up to <paramref name="completedRetentionCap"/> finished runs.</summary>
        /// <param name="completedRetentionCap">Max terminal runs to keep before evicting the oldest.</param>
        public MolcaRunStore(int completedRetentionCap = 200) : this(null, completedRetentionCap) { }

        /// <summary>
        /// Creates a run store backed by <paramref name="journal"/> for durable history (§12). On
        /// construction it loads persisted runs and reconciles any that were still in flight when their
        /// session ended to <see cref="MolcaCommandStatus.Interrupted"/> (interrupted-run recovery).
        /// </summary>
        /// <param name="journal">The disk journal, or null for an in-memory-only store.</param>
        /// <param name="completedRetentionCap">Max terminal runs to keep before evicting the oldest.</param>
        public MolcaRunStore(MolcaRunJournal journal, int completedRetentionCap = 200)
        {
            _completedRetentionCap = Math.Max(1, completedRetentionCap);
            _journal = journal;
            RecoverHistory();
        }

        // Load persisted runs; a record still marked in-flight means its session died mid-run, so it is
        // reconciled to Interrupted and rewritten so the recovery is durable.
        private void RecoverHistory()
        {
            if (_journal == null) return;
            foreach (var run in _journal.LoadAll())
            {
                var reconciled = run.IsTerminal ? run : run.WithStatus(MolcaCommandStatus.Interrupted);
                if (!ReferenceEquals(reconciled, run)) _journal.Write(reconciled);
                _history[reconciled.RunId] = reconciled;
            }
        }

        /// <summary>Creates and registers a new run in the <see cref="MolcaCommandStatus.Queued"/> state.</summary>
        /// <param name="runId">Unique run id.</param>
        /// <param name="commandId">The command id.</param>
        /// <param name="transport">Originating transport.</param>
        /// <returns>The new handle.</returns>
        public MolcaRunHandle Create(string runId, string commandId, MolcaTransport transport)
        {
            // Idempotent by runId: a run pre-registered by the kernel (so it is queryable before its
            // deferred execution begins) is reused by the executor rather than duplicated.
            if (_runs.TryGetValue(runId, out var existing))
                return existing;

            var handle = new MolcaRunHandle(runId, commandId, transport);
            _runs[runId] = handle;
            _order.Add(runId);
            return handle;
        }

        /// <summary>Looks up a run by id.</summary>
        /// <param name="runId">The run id.</param>
        /// <param name="handle">The resolved handle, or null.</param>
        /// <returns>True if found.</returns>
        public bool TryGet(string runId, out MolcaRunHandle handle) =>
            _runs.TryGetValue(runId ?? string.Empty, out handle);

        /// <summary>All non-terminal runs, in creation order (drives the activity rail).</summary>
        /// <returns>The active run handles.</returns>
        public IReadOnlyList<MolcaRunHandle> ActiveRuns() =>
            _order.Select(id => _runs[id]).Where(h => !h.IsTerminal).ToList();

        /// <summary>All tracked runs (active and retained-completed), in creation order.</summary>
        /// <returns>Every tracked run handle.</returns>
        public IReadOnlyList<MolcaRunHandle> AllRuns() => _order.Select(id => _runs[id]).ToList();

        /// <summary>
        /// Durable run history newest-first: this session's live runs merged with runs recovered from the
        /// journal (prior sessions, including any reconciled to <see cref="MolcaCommandStatus.Interrupted"/>).
        /// A live run shadows a same-id persisted record. Drives the Hub History panel (§12).
        /// </summary>
        /// <returns>The combined history, newest first.</returns>
        public IReadOnlyList<MolcaPersistedRun> History()
        {
            var byId = new Dictionary<string, MolcaPersistedRun>(_history, StringComparer.Ordinal);
            foreach (var id in _order)
                byId[id] = MolcaPersistedRun.FromHandle(_runs[id]); // live shadows persisted
            return byId.Values.OrderByDescending(r => r.OrderingTimeUtc).ToList();
        }

        /// <summary>Marks a run as started (left the queue), stamping the time and persisting it (§12).</summary>
        /// <param name="handle">The run handle.</param>
        public void MarkStarted(MolcaRunHandle handle)
        {
            if (handle == null) return;
            handle.Status = MolcaCommandStatus.Running;
            handle.StartedAtUtc = DateTime.UtcNow;
            _journal?.Write(MolcaPersistedRun.FromHandle(handle));
        }

        /// <summary>
        /// Marks a run terminal with its final result, persists it, and evicts old completed runs beyond
        /// the cap (in memory and on disk).
        /// </summary>
        /// <param name="handle">The run handle.</param>
        /// <param name="result">The final result.</param>
        public void Complete(MolcaRunHandle handle, MolcaCommandResult result)
        {
            if (handle == null) return;
            handle.Status = result.Status;
            handle.Result = result;
            handle.CompletedAtUtc = DateTime.UtcNow;
            _journal?.Write(MolcaPersistedRun.FromHandle(handle));
            EvictCompletedBeyondCap();
            _journal?.EvictBeyondCap(_completedRetentionCap);
        }

        private void EvictCompletedBeyondCap()
        {
            var completed = _order.Where(id => _runs[id].IsTerminal).ToList();
            var excess = completed.Count - _completedRetentionCap;
            for (int i = 0; i < excess; i++)
            {
                var id = completed[i];
                _runs.Remove(id);
                _order.Remove(id);
            }
        }
    }
}
