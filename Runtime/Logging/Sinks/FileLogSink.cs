using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Molca.Logging
{
    /// <summary>
    /// Writes log entries to rotating files under a directory, buffering so no <c>Debug.Log</c> call ever
    /// touches the disk.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/Sinks/</c>.
    /// <b>Registration:</b> created and registered by <see cref="LogManager"/>.
    /// <para/>
    /// <b>Writes never happen on the logging thread.</b> <see cref="Write"/> only enqueues;
    /// <see cref="Flush"/> does the I/O and is driven from the main thread. The previous implementation
    /// called <c>File.AppendAllText</c> from inside the log handler once 64 messages had accumulated — so
    /// every 64th <c>Debug.Log</c> paid a synchronous disk write on whatever thread happened to make it. On
    /// the main thread that is a frame hitch; on worker threads the shared lock serialised them all behind
    /// disk latency.
    /// <para/>
    /// <b>Bounded.</b> The queue is capped, and overflow drops the oldest entries and counts them. A sink
    /// that cannot write — a full disk, a revoked permission — must not become a memory leak on top of
    /// being a broken sink.
    /// <para/>
    /// <b>Not available on WebGL.</b> <c>Application.persistentDataPath</c> there is an IndexedDB shim that
    /// only reaches storage on an explicit sync, so per-flush appends are both ineffective and expensive.
    /// The sink compiles to a no-op rather than pretending to work.
    /// </remarks>
    public sealed class FileLogSink : ILogSink
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        private const bool PlatformSupported = false;
#else
        private const bool PlatformSupported = true;
#endif

        private const string FilePrefix = "runtime-log_";
        private const string FileExtension = ".txt";

        private readonly string _directory;
        private readonly int _maxFiles;
        private readonly long _maxBytes;
        private readonly int _maxQueued;
        private readonly bool _includeStackTraces;
        private readonly object _gate = new object();
        private readonly List<MolcaLogEntry> _queue = new List<MolcaLogEntry>();

        private string _path;
        private long _bytesWritten;
        private int _dropped;
        private bool _disabled;

        /// <inheritdoc/>
        public string Name => "file";

        /// <inheritdoc/>
        public MolcaLogLevel MinimumLevel { get; set; }

        /// <summary>Whether this sink can write on this platform and is not disabled by a failure.</summary>
        /// <remarks>
        /// Turns <c>false</c> permanently after a write failure. Retrying a broken destination on every
        /// flush produces one error per flush forever, which is noisier than the original problem.
        /// </remarks>
        public bool IsAvailable => PlatformSupported && !_disabled;

        /// <summary>The file currently being appended to, or <c>null</c> on an unsupported platform.</summary>
        public string CurrentPath => _path;

        /// <summary>How many entries were dropped because the queue was full.</summary>
        public int DroppedCount
        {
            get { lock (_gate) return _dropped; }
        }

        /// <summary>How many entries are waiting to be written.</summary>
        public int PendingCount
        {
            get { lock (_gate) return _queue.Count; }
        }

        /// <summary>Creates the sink and opens a log file.</summary>
        /// <param name="directory">Directory to write into; created if missing.</param>
        /// <param name="minimumLevel">Lowest level to record.</param>
        /// <param name="maxFiles">How many log files to retain, including the new one.</param>
        /// <param name="maxBytes">Rotate once the current file passes this size.</param>
        /// <param name="maxQueued">Entries to buffer before dropping the oldest.</param>
        /// <param name="includeStackTraces">Whether to write stack traces alongside messages.</param>
        public FileLogSink(string directory, MolcaLogLevel minimumLevel, int maxFiles = 5,
            long maxBytes = 10L * 1024 * 1024, int maxQueued = 4096, bool includeStackTraces = true)
        {
            _directory = directory;
            MinimumLevel = minimumLevel;
            _maxFiles = Math.Max(1, maxFiles);
            _maxBytes = Math.Max(64L * 1024, maxBytes);
            _maxQueued = Math.Max(64, maxQueued);
            _includeStackTraces = includeStackTraces;

#if UNITY_WEBGL && !UNITY_EDITOR
            // No usable filesystem here; the sink stays constructed but reports IsAvailable == false.
            return;
#else
            try
            {
                Directory.CreateDirectory(_directory);
                Prune(reserveSlots: 1);
                _path = ClaimPath();
                _bytesWritten = 0;
            }
            catch (Exception)
            {
                // Nothing to report to yet — the pipeline's failure channel is for dispatch-time faults,
                // and a sink that cannot open its directory simply stays unavailable.
                _disabled = true;
            }
#endif
        }

        /// <inheritdoc/>
        public void Write(in MolcaLogEntry entry)
        {
            if (!IsAvailable) return;

            lock (_gate)
            {
                _queue.Add(entry);
                if (_queue.Count <= _maxQueued) return;

                // Drop the oldest: the newest entries are the ones describing whatever is going wrong now.
                int excess = _queue.Count - _maxQueued;
                _queue.RemoveRange(0, excess);
                _dropped += excess;
            }
        }

        /// <inheritdoc/>
        public void Flush()
        {
            if (!IsAvailable) return;

            MolcaLogEntry[] batch;
            lock (_gate)
            {
                if (_queue.Count == 0) return;
                batch = _queue.ToArray();
                // Cleared before the write, not after: a failed write must not leave the same lines queued
                // to be written again by the next flush.
                _queue.Clear();
            }

            var builder = new StringBuilder();
            foreach (var entry in batch)
            {
                builder.AppendLine(entry.Format(_includeStackTraces));
            }

            string text = builder.ToString();
            long bytes = Encoding.UTF8.GetByteCount(text);

            try
            {
                if (_bytesWritten + bytes > _maxBytes) Rotate();

                // An explicit BOM-less UTF-8 writer, so the byte count above matches what lands on disk
                // and the size cap means what it says.
                using (var writer = new StreamWriter(_path, append: true, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                }

                _bytesWritten += bytes;
            }
            catch (Exception exception)
            {
                _disabled = true;
                MolcaLogPipeline.ReportPipelineFailure(exception);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => Flush();

        /// <summary>Starts a new file and prunes old ones.</summary>
        private void Rotate()
        {
            Prune(reserveSlots: 1);
            _path = ClaimPath();
            _bytesWritten = 0;
        }

        /// <summary>
        /// Picks a path nothing else owns and creates it empty.
        /// </summary>
        /// <remarks>
        /// Timestamps are second-precision, and both rotation under load and two sinks constructed in the
        /// same second would otherwise choose the same name. The previous implementation reused the
        /// colliding name and reset its byte counter to zero, which merged two logs into one file and
        /// disabled the size cap for it.
        /// <para/>
        /// The file is <b>created</b> here rather than on first write, and that is the part that makes the
        /// claim real: a name is only unique if taking it is visible to whoever asks next.
        /// <c>FileMode.CreateNew</c> fails rather than truncating, so two racing claimants cannot agree on
        /// the same file.
        /// </remarks>
        private string ClaimPath()
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

            for (int suffix = 1; suffix < 1000; suffix++)
            {
                string candidate = Path.Combine(_directory, suffix == 1
                    ? $"{FilePrefix}{stamp}{FileExtension}"
                    : $"{FilePrefix}{stamp}_{suffix}{FileExtension}");

                try
                {
                    using (new FileStream(candidate, FileMode.CreateNew, FileAccess.Write)) { }
                    return candidate;
                }
                catch (IOException)
                {
                    // Taken. Try the next suffix.
                }
            }

            // A thousand collisions in one second is not a naming problem any more.
            _disabled = true;
            return null;
        }

        /// <summary>
        /// Deletes the oldest files until at most <c>_maxFiles - reserveSlots</c> remain.
        /// </summary>
        /// <param name="reserveSlots">How many files are about to be created.</param>
        /// <remarks>
        /// Reserving the slot the caller is about to fill is what makes <c>maxFiles</c> the total kept
        /// rather than the total kept plus one.
        /// </remarks>
        private void Prune(int reserveSlots)
        {
            try
            {
                var files = new List<string>(
                    Directory.GetFiles(_directory, $"{FilePrefix}*{FileExtension}"));
                if (files.Count == 0) return;

                // The file currently being appended to is never a deletion candidate. Rotation prunes
                // before claiming the next name, so without this the session's own newest log could be
                // chosen — write times tie at whole seconds under churn, which makes "oldest" ambiguous
                // exactly when rotation is busiest.
                if (_path != null) files.Remove(_path);
                if (files.Count == 0) return;

                // Oldest first, tie-broken by name so the order is deterministic when several files share
                // a write time.
                files.Sort((left, right) =>
                {
                    int byTime = File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
                    return byTime != 0 ? byTime : string.CompareOrdinal(left, right);
                });

                // One slot is already accounted for by the open file that was excluded above.
                int keep = Math.Max(0, _maxFiles - reserveSlots - (_path != null ? 1 : 0));
                for (int i = 0; i < files.Count - keep; i++)
                {
                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch (Exception)
                    {
                        // A locked or already-removed old log is not worth failing the new session over.
                    }
                }
            }
            catch (Exception)
            {
                // Enumeration failed; the directory may have vanished. Opening the new file will report it.
            }
        }
    }
}
