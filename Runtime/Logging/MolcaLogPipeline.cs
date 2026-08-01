using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Logging
{
    /// <summary>
    /// The process-wide log pipeline: captures every <c>Debug.Log</c> from the first frame and fans it out
    /// to registered <see cref="ILogSink"/>s.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/</c>.
    /// <b>Registration:</b> self-installing via
    /// <see cref="RuntimeInitializeOnLoadMethodAttribute"/> at
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>.
    /// <para/>
    /// <b>Why static, in a framework built on subsystems.</b> Logging has to work before the thing that
    /// would own it exists. <c>RuntimeManager</c> is instantiated from a prefab after the first scene
    /// loads, and <c>LogManager</c> initializes somewhere inside a dependency wave after that — yet
    /// bootstrap is exactly the window where a project breaks, and the previous design captured nothing
    /// until <i>every</i> subsystem had finished initializing (its handler returned early on
    /// <c>!IsActive</c>, and <c>MarkActive</c> runs only after the last wave). Every log explaining a
    /// failed bootstrap was discarded by the component whose job was to record it.
    /// <para/>
    /// So capture installs itself at subsystem registration and buffers into
    /// <see cref="MemoryLogSink"/>. <see cref="LogManager"/> then configures thresholds and attaches the
    /// file sink, draining what was already captured into it. <see cref="LogManager"/> is the authoring
    /// surface; this is the mechanism.
    /// <para/>
    /// <b>Threading.</b> <see cref="Dispatch"/> runs on whichever thread logged. The sink list is
    /// copy-on-write, so dispatch never takes a lock and a sink registered mid-flight cannot tear the
    /// array being iterated.
    /// </remarks>
    public static class MolcaLogPipeline
    {
        private static readonly object SinkLock = new object();

        // Copy-on-write: replaced wholesale under SinkLock, read without one. Dispatch happens on every
        // log line from any thread, and taking a lock there would serialise worker threads behind the
        // main thread for the duration of every sink write.
        private static volatile ILogSink[] _sinks = Array.Empty<ILogSink>();

        private static LogCapture _capture;
        private static MemoryLogSink _memory;
        private static int _mainThreadId;
        private static int _pipelineFailures;

        /// <summary>How many entries the pre-bootstrap buffer retains.</summary>
        /// <remarks>
        /// Enough to cover bootstrap comfortably and bounded so a project that never installs
        /// <see cref="LogManager"/> cannot grow it without limit. The buffer keeps running afterwards
        /// because an in-game console and the Hub both want recent history.
        /// </remarks>
        public const int MemoryBufferCapacity = 512;

        /// <summary>Whether capture is currently installed.</summary>
        public static bool IsInstalled => _capture != null;

        /// <summary>The always-present in-memory ring buffer, or <c>null</c> before installation.</summary>
        public static MemoryLogSink Memory => _memory;

        /// <summary>
        /// How many times a sink or the pipeline itself threw while handling a log.
        /// </summary>
        /// <remarks>
        /// Non-zero means log records were lost. Surfaced as a counter rather than a log line because
        /// logging about a broken logger is how a process wedges.
        /// </remarks>
        public static int PipelineFailures => _pipelineFailures;

        /// <summary>
        /// Installs capture. Idempotent, and re-installs if something replaced the handler underneath.
        /// </summary>
        /// <remarks>
        /// Runs at <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/>, which is also the reset
        /// point for "Enter Play Mode without domain reload" — so the static state below is rebuilt for
        /// each play session rather than leaking a dead capture into the next one.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Install()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Domain reload disabled: statics survive, but Unity restores its own handler between
            // sessions, so a surviving _capture may no longer be installed.
            if (_capture != null && ReferenceEquals(Debug.unityLogger.logHandler, _capture)) return;

            _pipelineFailures = 0;
            _memory = new MemoryLogSink(MemoryBufferCapacity);

            lock (SinkLock)
            {
                _sinks = new ILogSink[] { _memory };
            }

            _capture = new LogCapture(_mainThreadId);
        }

        /// <summary>
        /// Removes capture and disposes every sink.
        /// </summary>
        /// <remarks>
        /// For tests and for an explicit teardown. Not called on application quit: a sink flushed by
        /// <see cref="FlushAll"/> during shutdown should keep receiving the logs that shutdown itself
        /// produces, and the process is about to end anyway.
        /// </remarks>
        public static void Uninstall()
        {
            _capture?.Dispose();
            _capture = null;

            ILogSink[] sinks;
            lock (SinkLock)
            {
                sinks = _sinks;
                _sinks = Array.Empty<ILogSink>();
                _memory = null;
            }

            foreach (var sink in sinks)
            {
                try
                {
                    sink.Flush();
                    sink.Dispose();
                }
                catch (Exception exception)
                {
                    ReportPipelineFailure(exception);
                }
            }
        }

        /// <summary>Registers a sink.</summary>
        /// <param name="sink">The sink to add. <c>null</c> and duplicates are ignored.</param>
        /// <returns><c>true</c> when the sink was added.</returns>
        public static bool AddSink(ILogSink sink)
        {
            if (sink == null) return false;

            lock (SinkLock)
            {
                foreach (var existing in _sinks)
                {
                    if (ReferenceEquals(existing, sink)) return false;
                }

                var replacement = new ILogSink[_sinks.Length + 1];
                Array.Copy(_sinks, replacement, _sinks.Length);
                replacement[_sinks.Length] = sink;
                _sinks = replacement;
            }
            return true;
        }

        /// <summary>Unregisters a sink without disposing it.</summary>
        /// <param name="sink">The sink to remove.</param>
        /// <returns><c>true</c> when the sink was registered.</returns>
        /// <remarks>
        /// Disposal is left to the caller that created it: the pipeline does not own sinks it did not
        /// make, and disposing a sink someone else still holds is worse than leaking one.
        /// </remarks>
        public static bool RemoveSink(ILogSink sink)
        {
            if (sink == null) return false;

            lock (SinkLock)
            {
                int index = Array.IndexOf(_sinks, sink);
                if (index < 0) return false;

                var replacement = new ILogSink[_sinks.Length - 1];
                Array.Copy(_sinks, 0, replacement, 0, index);
                Array.Copy(_sinks, index + 1, replacement, index, _sinks.Length - index - 1);
                _sinks = replacement;
            }
            return true;
        }

        /// <summary>Every registered sink, in registration order.</summary>
        /// <returns>A snapshot; safe to iterate while sinks are added or removed.</returns>
        public static IReadOnlyList<ILogSink> GetSinks() => _sinks;

        /// <summary>
        /// Offers an entry to every sink whose threshold admits it.
        /// </summary>
        /// <param name="entry">The captured entry.</param>
        /// <remarks>
        /// A throwing sink is isolated and counted, never rethrown: one broken destination must not stop
        /// the log reaching the others, and it must not propagate back into the <c>Debug.Log</c> call
        /// site, which would turn a logging bug into an application crash.
        /// </remarks>
        internal static void Dispatch(in MolcaLogEntry entry)
        {
            var sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++)
            {
                var sink = sinks[i];
                try
                {
                    if (!MolcaLogLevels.Passes(entry.Level, sink.MinimumLevel)) continue;
                    sink.Write(entry);
                }
                catch (Exception exception)
                {
                    ReportPipelineFailure(exception);
                }
            }
        }

        /// <summary>Flushes every sink. Main thread; may block.</summary>
        public static void FlushAll()
        {
            var sinks = _sinks;
            for (int i = 0; i < sinks.Length; i++)
            {
                try
                {
                    sinks[i].Flush();
                }
                catch (Exception exception)
                {
                    ReportPipelineFailure(exception);
                }
            }
        }

        /// <summary>
        /// Records a failure inside the pipeline itself.
        /// </summary>
        /// <param name="exception">What went wrong.</param>
        /// <remarks>
        /// Reported straight to the handler Unity had before capture was installed, bypassing
        /// <see cref="Dispatch"/> entirely — routing it through the sinks would re-enter the code that
        /// just failed. Only the first few are written; a sink failing on every line would otherwise
        /// produce one report per line. <see cref="PipelineFailures"/> keeps the true count.
        /// </remarks>
        internal static void ReportPipelineFailure(Exception exception)
        {
            int count = Interlocked.Increment(ref _pipelineFailures);
            if (count > 3 || _capture == null) return;

            try
            {
                _capture.Inner.LogFormat(LogType.Error, null,
                    "[MolcaLog] A log sink threw while handling a message; records are being lost. {0}",
                    exception);
            }
            catch (Exception)
            {
                // The fallback handler is broken too. There is nowhere left to report this.
            }
        }
    }
}
