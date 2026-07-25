using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Molca.Editor.Automation
{
    /// <summary>
    /// Persists run history to one JSON file per run under <c>Library/Molca/Automation/Runs/</c> (§12) so
    /// history and interrupted-run recovery survive a domain reload — the in-memory
    /// <see cref="MolcaRunStore"/> does not. <c>Library/</c> is per-machine and git-ignored, which is
    /// exactly where run metadata belongs (§12: never commit run logs/reports). Best-effort: an I/O
    /// failure is logged and never breaks a run. Main thread only.
    /// </summary>
    public sealed class MolcaRunJournal
    {
        private readonly string _directory;

        /// <summary>The directory this journal reads and writes.</summary>
        public string Directory => _directory;

        /// <summary>Creates a journal at the default project location, or a caller-supplied directory.</summary>
        /// <param name="directory">Override directory (for tests); defaults to <c>Library/Molca/Automation/Runs</c>.</param>
        public MolcaRunJournal(string directory = null)
        {
            _directory = string.IsNullOrEmpty(directory) ? DefaultDirectory() : directory;
        }

        private static string DefaultDirectory()
        {
            // Application.dataPath is <project>/Assets; the sibling Library holds machine-local, git-ignored state.
            var projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            return Path.Combine(projectRoot, "Library", "Molca", "Automation", "Runs");
        }

        /// <summary>Writes (or overwrites) the record for a run. Best-effort; failures are logged, not thrown.</summary>
        /// <param name="run">The run snapshot to persist.</param>
        public void Write(MolcaPersistedRun run)
        {
            if (run == null || string.IsNullOrEmpty(run.RunId)) return;
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.WriteAllText(FilePath(run.RunId), run.ToJson().ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca Automation] Could not persist run '{run.RunId}': {ex.Message}");
            }
        }

        /// <summary>Loads every persisted run, newest first. Skips and cleans up unreadable files.</summary>
        /// <returns>The persisted runs, newest (by completion/creation time) first.</returns>
        public IReadOnlyList<MolcaPersistedRun> LoadAll()
        {
            var runs = new List<MolcaPersistedRun>();
            if (!System.IO.Directory.Exists(_directory)) return runs;

            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    var record = MolcaPersistedRun.FromJson(JObject.Parse(File.ReadAllText(file)));
                    if (record != null) runs.Add(record);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[Molca Automation] Skipping unreadable run journal file '{file}': {ex.Message}");
                }
            }

            return runs.OrderByDescending(r => r.OrderingTimeUtc).ToList();
        }

        /// <summary>Deletes the persisted record for a run, if present. Best-effort.</summary>
        /// <param name="runId">The run id to forget.</param>
        public void Delete(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return;
            try
            {
                var path = FilePath(runId);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca Automation] Could not delete run journal '{runId}': {ex.Message}");
            }
        }

        /// <summary>Deletes the oldest records so at most <paramref name="cap"/> remain on disk.</summary>
        /// <param name="cap">Maximum records to retain.</param>
        public void EvictBeyondCap(int cap)
        {
            var all = LoadAll(); // newest first
            for (int i = cap; i < all.Count; i++)
                Delete(all[i].RunId);
        }

        // A runId is a uuid, so it is already a safe file name; guard anyway against stray characters.
        private string FilePath(string runId)
        {
            var safe = string.Concat(runId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
            return Path.Combine(_directory, safe + ".json");
        }
    }
}
