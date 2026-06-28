using System;
using System.Collections.Generic;
using System.IO;
using Molca.Editor;
using UnityEditor;
using UnityEngine;

namespace Molca.Settings
{
    /// <summary>
    /// Represents one entry in the build changelog. Used as both the runtime type and the JSON DTO.
    /// </summary>
    [Serializable]
    public class VersionHistoryEntry
    {
        public string version;
        public string timestamp;
        public string changeType;
        public string notes;

        // Parameterless constructor required by JsonUtility deserialization.
        public VersionHistoryEntry() { }

        public VersionHistoryEntry(string version, string changeType, string notes = "")
        {
            this.version = version;
            this.timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            this.changeType = changeType;
            this.notes = notes;
        }
    }

    /// <summary>
    /// JSON-serializable root for the changelog file.
    /// </summary>
    [Serializable]
    public class ChangelogFileData
    {
        public List<VersionHistoryEntry> versionHistory = new List<VersionHistoryEntry>();
    }

    /// <summary>
    /// Reads and writes the JSON build changelog. Optionally appends git commit messages to entries.
    /// </summary>
    /// <remarks>
    /// <see cref="LastBuildHashKey"/> is stored in <see cref="EditorPrefs"/> rather than a serialized
    /// field on the owning ScriptableObject to avoid mutating SO assets at runtime. JSON is used instead
    /// of YAML so the dist package has no compile-time dependency on a dev-project-only parser assembly.
    /// </remarks>
    public class ChangelogWriter
    {
        private const int MaxEntries = 50;
        private const string LastBuildHashKey = "Molca.VersionSettings.LastBuildCommitHash";

        private readonly string _changelogPath;
        private readonly bool _includeGitCommits;

        /// <param name="changelogPath">Path to the JSON changelog, relative to the project root.</param>
        /// <param name="includeGitCommits">When true, git commit messages are appended to build entries.</param>
        public ChangelogWriter(string changelogPath, bool includeGitCommits)
        {
            _changelogPath = NormalizePath(changelogPath);
            _includeGitCommits = includeGitCommits;
        }

        /// <summary>Reads all entries from the changelog file. Returns empty array if the file is missing or unreadable.</summary>
        public VersionHistoryEntry[] Read()
        {
            var path = GetFullPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return Array.Empty<VersionHistoryEntry>();

            try
            {
                var json = File.ReadAllText(path);
                var data = JsonUtility.FromJson<ChangelogFileData>(json);
                return data?.versionHistory?.ToArray() ?? Array.Empty<VersionHistoryEntry>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChangelogWriter: Failed to read changelog.\n{ex}");
                return Array.Empty<VersionHistoryEntry>();
            }
        }

        /// <summary>Writes all entries to the changelog file, creating directories if needed.</summary>
        public void Write(VersionHistoryEntry[] entries)
        {
            var path = GetFullPath();
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var data = new ChangelogFileData();
                data.versionHistory.AddRange(entries);

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(path, JsonUtility.ToJson(data, prettyPrint: true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChangelogWriter: Failed to write changelog.\n{ex}");
            }
        }

        /// <summary>
        /// Appends a build entry for <paramref name="currentVersion"/> and populates its notes
        /// with <paramref name="buildNotes"/> and (optionally) git commit messages.
        /// </summary>
        public void AppendBuildEntry(string currentVersion, string buildNotes)
            => AppendEntry(currentVersion, "build", buildNotes);

        /// <summary>
        /// Appends a <c>release</c> entry for <paramref name="version"/> with optional
        /// <paramref name="notes"/> (and git commits when enabled). Used by the release flow.
        /// </summary>
        /// <param name="version">The released version string.</param>
        /// <param name="notes">Optional release notes prepended to the entry.</param>
        public void AppendReleaseEntry(string version, string notes)
            => AppendEntry(version, "release", notes);

        private void AppendEntry(string version, string changeType, string notes)
        {
            var projectRoot = GetProjectRoot();
            if (string.IsNullOrEmpty(projectRoot))
                return;

            var historyList = new List<VersionHistoryEntry>(Read());
            historyList.Add(new VersionHistoryEntry(version, changeType));

            if (historyList.Count > MaxEntries)
                historyList.RemoveRange(0, historyList.Count - MaxEntries);

            Write(historyList.ToArray());
            AppendNotesToLastEntry(historyList, version, notes, projectRoot);
        }

        /// <summary>Clears all entries from the changelog file.</summary>
        public void Clear() => Write(Array.Empty<VersionHistoryEntry>());

        private void AppendNotesToLastEntry(List<VersionHistoryEntry> historyList, string currentVersion, string buildNotes, string projectRoot)
        {
            try
            {
                var notes = "";

                if (_includeGitCommits)
                {
                    var lastHash = MolcaEditorPrefs.GetString(LastBuildHashKey, "");
                    var hasValidHash = !string.IsNullOrWhiteSpace(lastHash) &&
                        GitLogReader.IsCommitAvailable(projectRoot, lastHash);

                    var commits = GitLogReader.GetCommitMessages(
                        projectRoot,
                        hasValidHash ? lastHash : null,
                        out var headHash,
                        out var heading);

                    if (commits.Count > 0 && !string.IsNullOrEmpty(heading))
                    {
                        // Group commits by Conventional Commits category (Breaking/Features/Fixes/Other);
                        // non-conventional subjects fall through to the Other section verbatim.
                        var categorized = ConventionalCommits.Format(commits);
                        notes = string.IsNullOrEmpty(categorized) ? heading : heading + "\n" + categorized;
                    }

                    if (!string.IsNullOrEmpty(headHash))
                        MolcaEditorPrefs.SetString(LastBuildHashKey, headHash);
                }

                if (!string.IsNullOrWhiteSpace(buildNotes))
                    notes = string.IsNullOrEmpty(notes) ? buildNotes : buildNotes + "\n\n" + notes;

                var last = historyList.Count > 0 ? historyList[historyList.Count - 1] : null;
                if (last != null && last.version == currentVersion)
                {
                    last.notes = notes;
                    Write(historyList.ToArray());
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChangelogWriter: Failed to append notes.\n{ex}");
            }
        }

        private static string NormalizePath(string changelogPath)
        {
            if (string.IsNullOrWhiteSpace(changelogPath))
                return changelogPath;

            var trimmed = changelogPath.Trim();
            var extension = Path.GetExtension(trimmed);
            if (extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            {
                return Path.ChangeExtension(trimmed, ".json");
            }

            return trimmed;
        }

        private string GetProjectRoot() => Directory.GetParent(Application.dataPath)?.FullName;

        private string GetFullPath()
        {
            var root = GetProjectRoot();
            if (string.IsNullOrEmpty(root) || string.IsNullOrWhiteSpace(_changelogPath))
                return null;
            return Path.Combine(root, _changelogPath.Trim());
        }
    }
}
