using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// A bounded, persisted undo stack for <see cref="McpToolReversibility.FileSnapshot"/> action tools
    /// (Sprint 17). Before such an action edits a file, it calls <see cref="Snapshot"/> to back the file
    /// up under <c>Library/Molca/undo/</c> and push an entry; <see cref="UndoLast"/> restores the most
    /// recent backup. The index is persisted as JSON so the stack survives domain reloads.
    /// </summary>
    public static class McpUndoStack
    {
        /// <summary>Maximum retained undo entries; older ones are pruned (backups deleted).</summary>
        public const int MaxEntries = 20;

        /// <summary>One reversible action's backup record.</summary>
        public sealed class Entry
        {
            /// <summary>Unique id (also the backup subfolder name).</summary>
            public string Id;
            /// <summary>Tool that produced the change.</summary>
            public string Tool;
            /// <summary>Human description of what was changed.</summary>
            public string Description;
            /// <summary>Project-relative path of the edited file.</summary>
            public string TargetPath;
            /// <summary>Absolute path of the stored backup copy.</summary>
            public string BackupPath;
            /// <summary>UTC timestamp.</summary>
            public string TimestampUtc;
        }

        private static string Root
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(projectRoot, "Library", "Molca", "undo");
            }
        }

        private static string IndexPath => Path.Combine(Root, "index.json");

        /// <summary>The current entries, newest last. Never null.</summary>
        public static IReadOnlyList<Entry> Entries => Load();

        /// <summary>True if there is at least one entry to revert.</summary>
        public static bool HasEntries => Load().Count > 0;

        /// <summary>True if an entry with <paramref name="id"/> is still present (i.e. still revertible).</summary>
        public static bool Contains(string id) =>
            !string.IsNullOrEmpty(id) && Load().Any(e => e.Id == id);

        /// <summary>
        /// Backs up <paramref name="targetPath"/> and records an undo entry. Call this immediately
        /// before a FileSnapshot action writes the file. Returns the entry id, or null on failure
        /// (which the caller should treat as "no undo available", not a hard error).
        /// </summary>
        public static string Snapshot(string targetPath, string tool, string description)
        {
            try
            {
                if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
                    return null;

                var id = Guid.NewGuid().ToString("N");
                var dir = Path.Combine(Root, id);
                Directory.CreateDirectory(dir);
                var backupPath = Path.Combine(dir, Path.GetFileName(targetPath));
                File.Copy(targetPath, backupPath, overwrite: true);

                var entries = Load();
                entries.Add(new Entry
                {
                    Id = id,
                    Tool = tool,
                    Description = description,
                    TargetPath = targetPath,
                    BackupPath = backupPath,
                    TimestampUtc = DateTime.UtcNow.ToString("o")
                });
                Prune(entries);
                Save(entries);
                return id;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca MCP] Undo snapshot failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Discards the entry with <paramref name="id"/> (e.g. if the action made no change).</summary>
        public static void Discard(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            var entries = Load();
            var idx = entries.FindIndex(e => e.Id == id);
            if (idx < 0) return;
            DeleteBackup(entries[idx]);
            entries.RemoveAt(idx);
            Save(entries);
        }

        /// <summary>
        /// Restores the most recent backup, reloading the scene if the restored file is the active scene.
        /// Returns a status message.
        /// </summary>
        public static string UndoLast()
        {
            var entries = Load();
            if (entries.Count == 0)
                return "Nothing to revert.";

            var entry = entries[entries.Count - 1];
            try
            {
                if (!File.Exists(entry.BackupPath))
                {
                    entries.RemoveAt(entries.Count - 1);
                    Save(entries);
                    return "Backup file is missing; entry discarded.";
                }

                File.Copy(entry.BackupPath, entry.TargetPath, overwrite: true);
                entries.RemoveAt(entries.Count - 1);
                DeleteBackup(entry);
                Save(entries);

                ReloadIfActiveScene(entry.TargetPath);
                AssetDatabase.Refresh();
                return $"Reverted: {entry.Description}";
            }
            catch (Exception ex)
            {
                return $"Revert failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Reverts every entry from the newest down to and including the one with <paramref name="id"/>
        /// (LIFO — undoing an earlier action necessarily undoes the newer ones stacked on top of it).
        /// Backups are restored oldest-last so the target entry's pre-action state wins for any file that
        /// several reverted actions touched. Returns a status message.
        /// </summary>
        public static string UndoTo(string id)
        {
            if (string.IsNullOrEmpty(id))
                return "Nothing to revert.";

            var entries = Load();
            var targetIdx = entries.FindIndex(e => e.Id == id);
            if (targetIdx < 0)
                return "That change is no longer in the undo history.";

            var reverted = 0;
            string lastDescription = null;
            try
            {
                // Walk newest → target so the target's backup (the oldest in this range) is written last.
                for (var i = entries.Count - 1; i >= targetIdx; i--)
                {
                    var entry = entries[i];
                    if (File.Exists(entry.BackupPath))
                    {
                        File.Copy(entry.BackupPath, entry.TargetPath, overwrite: true);
                        ReloadIfActiveScene(entry.TargetPath);
                        reverted++;
                        lastDescription = entry.Description;
                    }
                    DeleteBackup(entry);
                    entries.RemoveAt(i);
                }
                Save(entries);
                AssetDatabase.Refresh();

                if (reverted == 0) return "Backup files were missing; entries discarded.";
                if (reverted == 1) return $"Reverted: {lastDescription}";
                return $"Reverted {reverted} changes back to: {lastDescription}";
            }
            catch (Exception ex)
            {
                Save(entries);
                return $"Revert failed: {ex.Message}";
            }
        }

        private static void ReloadIfActiveScene(string targetPath)
        {
            // Compare project-relative paths; scene.path is project-relative ("Assets/...").
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
            var rel = Path.GetRelativePath(projectRoot, targetPath).Replace('\\', '/');
            var active = EditorSceneManager.GetActiveScene();
            if (!string.IsNullOrEmpty(active.path) && active.path == rel)
                EditorSceneManager.OpenScene(rel, OpenSceneMode.Single);
        }

        private static void Prune(List<Entry> entries)
        {
            while (entries.Count > MaxEntries)
            {
                DeleteBackup(entries[0]);
                entries.RemoveAt(0);
            }
        }

        private static void DeleteBackup(Entry entry)
        {
            try
            {
                var dir = Path.GetDirectoryName(entry.BackupPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch { /* best effort */ }
        }

        private static List<Entry> Load()
        {
            try
            {
                if (!File.Exists(IndexPath)) return new List<Entry>();
                var arr = JArray.Parse(File.ReadAllText(IndexPath));
                return arr.Select(t => new Entry
                {
                    Id = t.Value<string>("id"),
                    Tool = t.Value<string>("tool"),
                    Description = t.Value<string>("description"),
                    TargetPath = t.Value<string>("targetPath"),
                    BackupPath = t.Value<string>("backupPath"),
                    TimestampUtc = t.Value<string>("timestampUtc")
                }).ToList();
            }
            catch { return new List<Entry>(); }
        }

        private static void Save(List<Entry> entries)
        {
            try
            {
                Directory.CreateDirectory(Root);
                var arr = new JArray(entries.Select(e => new JObject
                {
                    ["id"] = e.Id,
                    ["tool"] = e.Tool,
                    ["description"] = e.Description,
                    ["targetPath"] = e.TargetPath,
                    ["backupPath"] = e.BackupPath,
                    ["timestampUtc"] = e.TimestampUtc
                }));
                File.WriteAllText(IndexPath, arr.ToString(Newtonsoft.Json.Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca MCP] Failed to persist undo index: {ex.Message}");
            }
        }
    }
}
