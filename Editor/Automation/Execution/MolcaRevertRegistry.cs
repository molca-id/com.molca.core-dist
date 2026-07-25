using System;
using System.Collections.Generic;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Holds the compensation a successful reversible action registered, keyed by run id, so a later
    /// explicit revert (<c>molca-revert</c> / the Hub) can undo it (§13, Phase 4). A revert is taken once —
    /// retrieving an entry removes it, so the same effect is never rolled back twice. Bounded: the oldest
    /// entries are evicted past a cap. Main thread only; does not survive a domain reload.
    /// </summary>
    public sealed class MolcaRevertRegistry
    {
        private sealed class Entry
        {
            public string CommandId;
            public MolcaCommandReversibility Kind;
            public MolcaCompensation Compensation;
        }

        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();
        private readonly int _cap;

        /// <summary>Creates a revert registry retaining up to <paramref name="cap"/> revertible runs.</summary>
        /// <param name="cap">Max registered reverts to keep before evicting the oldest.</param>
        public MolcaRevertRegistry(int cap = 100)
        {
            _cap = Math.Max(1, cap);
        }

        /// <summary>Registers the compensation for a completed, revertible run.</summary>
        /// <param name="runId">The run whose effect can be reverted.</param>
        /// <param name="commandId">The command that produced the effect.</param>
        /// <param name="kind">The reversibility kind.</param>
        /// <param name="compensation">The undo work; ignored when null.</param>
        public void Register(string runId, string commandId, MolcaCommandReversibility kind, MolcaCompensation compensation)
        {
            if (string.IsNullOrEmpty(runId) || compensation == null) return;
            if (!_entries.ContainsKey(runId)) _order.Add(runId);
            _entries[runId] = new Entry { CommandId = commandId, Kind = kind, Compensation = compensation };
            EvictBeyondCap();
        }

        /// <summary>Whether a revert is currently available for <paramref name="runId"/>.</summary>
        /// <param name="runId">The run id.</param>
        /// <returns>True if a compensation is registered.</returns>
        public bool Has(string runId) => runId != null && _entries.ContainsKey(runId);

        /// <summary>
        /// Retrieves and removes the compensation for a run, so it can be executed exactly once.
        /// </summary>
        /// <param name="runId">The run id.</param>
        /// <param name="commandId">The originating command id.</param>
        /// <param name="kind">The reversibility kind.</param>
        /// <param name="compensation">The compensation to run.</param>
        /// <returns>True if a revert was available and taken.</returns>
        public bool TryTake(string runId, out string commandId, out MolcaCommandReversibility kind, out MolcaCompensation compensation)
        {
            commandId = null;
            kind = MolcaCommandReversibility.None;
            compensation = null;
            if (runId == null || !_entries.TryGetValue(runId, out var entry)) return false;

            commandId = entry.CommandId;
            kind = entry.Kind;
            compensation = entry.Compensation;
            _entries.Remove(runId);
            _order.Remove(runId);
            return true;
        }

        private void EvictBeyondCap()
        {
            var excess = _order.Count - _cap;
            for (int i = 0; i < excess; i++)
            {
                var id = _order[0];
                _order.RemoveAt(0);
                _entries.Remove(id);
            }
        }
    }
}
