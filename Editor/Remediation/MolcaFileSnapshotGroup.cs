using System;
using System.Collections.Generic;
using System.Linq;
using Molca.Editor.Mcp;

namespace Molca.Editor.Remediation
{
    /// <summary>
    /// Collects the file-snapshot undo records produced by one remediation fix and exposes the oldest
    /// entry, which lets <see cref="McpUndoStack.UndoTo"/> revert the complete operation.
    /// </summary>
    public sealed class MolcaFileSnapshotGroup
    {
        private readonly List<string> _entryIds = new();

        /// <summary>Whether every requested pre-write snapshot was captured.</summary>
        public bool IsReady { get; }

        /// <summary>The oldest undo entry for the operation, or <c>null</c> until one is recorded.</summary>
        public string EntryId => _entryIds.FirstOrDefault();

        /// <summary>Captures all existing files the fix is about to rewrite as one atomic entry.</summary>
        public MolcaFileSnapshotGroup(
            IEnumerable<string> existingPaths, string fixId, string description)
        {
            var paths = (existingPaths ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (paths.Length == 0)
            {
                IsReady = true;
                return;
            }

            string id = McpUndoStack.SnapshotMany(paths, fixId, description);
            IsReady = !string.IsNullOrEmpty(id);
            if (IsReady) _entryIds.Add(id);
        }

        /// <summary>Records an asset created by the fix so the same revert deletes it.</summary>
        public bool RecordCreated(string assetPath, string fixId, string description)
        {
            string id = McpUndoStack.RecordCreated(assetPath, fixId, description);
            if (string.IsNullOrEmpty(id)) return false;
            _entryIds.Add(id);
            return true;
        }

        /// <summary>Discards all records when the fix ultimately made no change.</summary>
        public void Discard()
        {
            foreach (string id in _entryIds)
                McpUndoStack.Discard(id);
            _entryIds.Clear();
        }
    }
}
