using System;

namespace Molca.Logging
{
    /// <summary>
    /// A destination for captured log entries: a file, an on-screen console, a crash reporter.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/</c>.
    /// <b>Registration:</b> <see cref="MolcaLogPipeline.AddSink"/>.
    /// <para/>
    /// <b>Each sink owns its own threshold.</b> That is the whole point of the interface. The previous
    /// design had one severity filter, applied inside the replacement <c>ILogHandler</c> before Unity's own
    /// handler was called — so a setting that read as "how much to write to my log file" actually muted the
    /// Unity Console, the player log, and every consumer of
    /// <c>Application.logMessageReceived</c>. Filtering belongs to the destination, never to the capture.
    /// <para/>
    /// <b>Threading.</b> Unity invokes log handlers on whichever thread called <c>Debug.Log</c>, including
    /// network and worker threads, so <see cref="Write"/> may be called from any thread and must be
    /// thread-safe and non-blocking. Anything slow — disk, network — belongs behind a queue drained
    /// elsewhere; see <see cref="Flush"/>.
    /// <para/>
    /// <b>Never log from a sink.</b> The pipeline guards re-entrancy per thread, so a log raised while
    /// handling a log is dropped rather than recursing. Report a sink's own failures through
    /// <see cref="MolcaLogPipeline"/>'s failure channel instead.
    /// </remarks>
    public interface ILogSink : IDisposable
    {
        /// <summary>Short identifier for diagnostics and duplicate detection.</summary>
        string Name { get; }

        /// <summary>
        /// The lowest level this sink accepts. Read on every dispatch, so it may change at runtime.
        /// </summary>
        /// <remarks>
        /// <see cref="MolcaLogLevel.None"/> disables the sink without unregistering it, which is what a
        /// runtime toggle should do — unregistering and re-registering would lose a queued tail.
        /// </remarks>
        MolcaLogLevel MinimumLevel { get; }

        /// <summary>
        /// Accepts an entry that has already passed <see cref="MinimumLevel"/>.
        /// </summary>
        /// <param name="entry">The captured entry.</param>
        /// <remarks>
        /// May be called from any thread. Must not block, must not throw, and must not log.
        /// </remarks>
        void Write(in MolcaLogEntry entry);

        /// <summary>
        /// Drains anything buffered to the sink's real destination.
        /// </summary>
        /// <remarks>
        /// Called from the main thread on a cadence, on pause/focus loss, and on shutdown — the three
        /// moments where losing a buffered tail matters. May block; that is why it is separate from
        /// <see cref="Write"/>.
        /// </remarks>
        void Flush();
    }
}
