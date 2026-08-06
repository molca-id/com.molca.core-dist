using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Molca.Editor
{
    /// <summary>How a build attempt ended.</summary>
    public enum MolcaBuildOutcome
    {
        /// <summary>A player was produced.</summary>
        Succeeded = 0,

        /// <summary>The build ran and failed, or was cancelled part-way.</summary>
        Failed = 1,

        /// <summary>
        /// The build never ran: a pre-build gate, a build step, or profile validation refused it, or it
        /// was deferred across a build-target switch.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Failed"/> because the two want different next actions from whoever
        /// started the build — one points at the project's state, the other at the build itself. This is
        /// the distinction the Hub's outcome line already drew from a null <c>BuildReport</c>, now
        /// recorded rather than derived at display time.
        /// </remarks>
        Refused = 2,
    }

    /// <summary>
    /// One build attempt, as recorded for later reading: what was built, from what source state, and how
    /// it ended.
    /// </summary>
    /// <remarks>
    /// The same facts <c>build-info.json</c> carries beside a successful build's output, plus the ones
    /// that only exist when a build does <em>not</em> succeed. A manifest beside the output can only
    /// describe builds that produced output, which is why "what happened the last few times we tried"
    /// was previously unanswerable.
    /// </remarks>
    [Serializable]
    public sealed class MolcaBuildRecord
    {
        /// <summary>The build profile name, or the requested name when no profile resolved.</summary>
        public string profile;

        /// <summary>The build target as a string, or empty when it was never resolved.</summary>
        public string target;

        /// <summary>The <see cref="MolcaBuildOutcome"/> name.</summary>
        public string outcome;

        /// <summary>Full semantic version of the attempt.</summary>
        public string semanticVersion;

        /// <summary>Build number of the attempt.</summary>
        public string buildNumber;

        /// <summary>Short git commit hash at build time, or empty.</summary>
        public string commit;

        /// <summary>Git branch at build time, or empty.</summary>
        public string branch;

        /// <summary>Where the artifact was written, or empty when nothing was written.</summary>
        public string outputPath;

        /// <summary>Total artifact size in bytes, or 0.</summary>
        public long totalSizeBytes;

        /// <summary>Wall-clock build duration in seconds, or 0.</summary>
        public double durationSeconds;

        /// <summary>UTC timestamp of the attempt (ISO 8601).</summary>
        public string timestampUtc;

        /// <summary>One line saying what happened — the failure or refusal reason, or a success summary.</summary>
        public string detail;

        /// <summary>
        /// A stable identifier for <em>why</em> the attempt did not ship, or empty when it did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Separate from <see cref="detail"/> because the two have different audiences and different
        /// rules. <c>detail</c> is a sentence for the person at this machine and may name a scene, a path,
        /// or a count. This is a <see cref="MolcaBuildReasonCode"/> — lowercase kebab, no spaces and no
        /// punctuation — and it is the only part of a failure the control plane is told, so that reporting
        /// <em>that</em> a build failed never becomes reporting a developer's console output.
        /// </para>
        /// <para>
        /// Also what makes the local history groupable: five refusals from one gate are one problem, and
        /// grouping by <c>detail</c> would treat five differently-worded sentences as five.
        /// </para>
        /// </remarks>
        public string reasonCode;

        /// <summary>The parsed outcome, defaulting to <see cref="MolcaBuildOutcome.Failed"/> when unreadable.</summary>
        public MolcaBuildOutcome Outcome =>
            Enum.TryParse(outcome, out MolcaBuildOutcome parsed) ? parsed : MolcaBuildOutcome.Failed;

        /// <summary>The recorded timestamp in local time, or <see cref="DateTime.MinValue"/> when unreadable.</summary>
        public DateTime LocalTime =>
            DateTime.TryParse(timestampUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime()
                : DateTime.MinValue;
    }

    /// <summary>
    /// The project's recent build attempts, persisted outside the domain so they survive the reload a
    /// build target switch causes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placement:</b> <c>Packages/com.molca.core/Editor/BuildSystem/</c>. Written to
    /// <c>Library/Molca/build-history.json</c>, following the same convention as the add-on audit log and
    /// the automation run journal.
    /// </para>
    /// <para>
    /// <b>Why not a static field.</b> The Hub used to hold the last outcome in one, which the domain
    /// reload from <c>Restore Original Target</c> — on by default — discarded moments after recording it.
    /// The surface added so that a build would stop reporting nothing therefore reported nothing in the
    /// common case.
    /// </para>
    /// <para>
    /// <b>Why <c>Library/</c> and not <c>ProjectSettings/</c>.</b> This is one developer's or one runner's
    /// account of what they tried, not project configuration: committing it would mean a merge conflict per
    /// build. The facts that belong to the project are recorded in the changelog and in the tag history.
    /// </para>
    /// </remarks>
    public static class MolcaBuildRecordStore
    {
        /// <summary>How many attempts are kept; older ones are dropped as new ones arrive.</summary>
        public const int MaxRecords = 25;

        [Serializable]
        private sealed class HistoryFile
        {
            public List<MolcaBuildRecord> records = new List<MolcaBuildRecord>();
        }

        // Parsed records, and the file stamp they were parsed from. The Hub reads this on a 250 ms
        // refresh loop; without the cache that is a file read and a JSON parse four times a second for
        // the entire time the section is open.
        private static IReadOnlyList<MolcaBuildRecord> _cached;
        private static string _cachedStamp;

        /// <summary>Absolute path of the history file, for display and for tests.</summary>
        public static string HistoryPath
        {
            get
            {
                var root = Directory.GetParent(Application.dataPath)?.FullName;
                return string.IsNullOrEmpty(root)
                    ? null
                    : Path.Combine(root, "Library", "Molca", "build-history.json");
            }
        }

        /// <summary>Appends <paramref name="record"/>, trimming the oldest beyond <see cref="MaxRecords"/>.</summary>
        /// <param name="record">The attempt to record. Ignored when null.</param>
        /// <remarks>Best-effort: a write failure is a warning, never a build failure.</remarks>
        public static void Append(MolcaBuildRecord record)
        {
            if (record == null)
                return;

            if (string.IsNullOrEmpty(record.timestampUtc))
                record.timestampUtc = DateTime.UtcNow.ToString("o");

            try
            {
                var path = HistoryPath;
                if (string.IsNullOrEmpty(path))
                    return;

                var records = new List<MolcaBuildRecord>(Read());
                records.Add(record);
                if (records.Count > MaxRecords)
                    records.RemoveRange(0, records.Count - MaxRecords);

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var file = new HistoryFile();
                file.records.AddRange(records);
                File.WriteAllText(path, JsonUtility.ToJson(file, prettyPrint: true));

                // Invalidate rather than reason about whether the new file's stamp differs from the one
                // just cached.
                _cached = null;
                _cachedStamp = null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MolcaBuildRecordStore] Failed to record the build: {e.Message}");
            }
        }

        /// <summary>
        /// Every recorded attempt, oldest first. Never null.
        /// </summary>
        /// <remarks>
        /// Cached against the file's write time and length, because the Hub polls this while it is open.
        /// A stamp rather than a dirty flag, so a record appended by another process — a batch-mode CI
        /// build in the same project folder — is still picked up.
        /// </remarks>
        public static IReadOnlyList<MolcaBuildRecord> Read()
        {
            try
            {
                var path = HistoryPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    _cached = null;
                    _cachedStamp = null;
                    return Array.Empty<MolcaBuildRecord>();
                }

                var info = new FileInfo(path);
                var stamp = $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
                if (_cached != null && stamp == _cachedStamp)
                    return _cached;

                var file = JsonUtility.FromJson<HistoryFile>(File.ReadAllText(path));
                _cached = file?.records ?? (IReadOnlyList<MolcaBuildRecord>)Array.Empty<MolcaBuildRecord>();
                _cachedStamp = stamp;
                return _cached;
            }
            catch (Exception e)
            {
                // A corrupt history is not worth a single failed operation: report and move on rather
                // than letting an unreadable log block the build it is describing.
                Debug.LogWarning($"[MolcaBuildRecordStore] Failed to read the build history: {e.Message}");
                _cached = null;
                _cachedStamp = null;
                return Array.Empty<MolcaBuildRecord>();
            }
        }

        /// <summary>The most recent <paramref name="count"/> attempts, newest first.</summary>
        /// <param name="count">How many to return.</param>
        public static IReadOnlyList<MolcaBuildRecord> Recent(int count = 10)
        {
            var all = Read();
            var result = new List<MolcaBuildRecord>();
            for (int i = all.Count - 1; i >= 0 && result.Count < count; i--)
                result.Add(all[i]);
            return result;
        }

        /// <summary>The most recent attempt, or null when nothing has been recorded.</summary>
        public static MolcaBuildRecord Last
        {
            get
            {
                var all = Read();
                return all.Count > 0 ? all[all.Count - 1] : null;
            }
        }

        /// <summary>Deletes the history file.</summary>
        public static void Clear()
        {
            try
            {
                _cached = null;
                _cachedStamp = null;

                var path = HistoryPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MolcaBuildRecordStore] Failed to clear the build history: {e.Message}");
            }
        }
    }
}
