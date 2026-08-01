using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Molca.Logging
{
    /// <summary>
    /// One captured log message, with everything a sink might need to render or route it.
    /// </summary>
    /// <remarks>
    /// <b>Folder:</b> <c>Packages/com.molca.core/Runtime/Logging/</c>.
    /// <para/>
    /// Structured rather than pre-formatted. The previous API handed subscribers a single
    /// already-concatenated <c>string</c> through three separate callback fields, which meant a
    /// subscriber could not filter by severity, could not reach the context object, and could not choose
    /// its own formatting — an on-screen console and a crash reporter want very different things from the
    /// same message.
    /// <para/>
    /// A readonly struct: an entry is created for every log line in the process, and an allocation per
    /// line would make logging itself a source of GC pressure.
    /// </remarks>
    public readonly struct MolcaLogEntry
    {
        /// <summary>The severity this entry is filtered at.</summary>
        public MolcaLogLevel Level { get; }

        /// <summary>The original Unity log type, preserved for sinks that route by it.</summary>
        public LogType UnityLogType { get; }

        /// <summary>The message text. Never <c>null</c>; empty when formatting failed.</summary>
        public string Message { get; }

        /// <summary>The stack trace, or <c>null</c> when none was captured.</summary>
        public string StackTrace { get; }

        /// <summary>
        /// The name of the <see cref="UnityEngine.Object"/> context passed to <c>Debug.Log</c>, or
        /// <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The <i>name</i>, not the object. Reading <c>Object.name</c> is a main-thread-only operation, so
        /// it is resolved at capture time on the main thread and never afterwards — holding the reference
        /// would let a sink read it from a worker thread, or resurrect an object that has since been
        /// destroyed. <c>null</c> on a background-thread log even when a context was supplied.
        /// </remarks>
        public string ContextName { get; }

        /// <summary>When the entry was captured, in UTC.</summary>
        /// <remarks>
        /// UTC because log files outlive the session that wrote them and are read across time zones; a
        /// local timestamp with no offset is ambiguous, and two log files from either side of a DST
        /// boundary cannot be ordered.
        /// </remarks>
        public DateTime TimestampUtc { get; }

        /// <summary>Managed ID of the thread that logged.</summary>
        public int ThreadId { get; }

        /// <summary>Whether the entry was captured on Unity's main thread.</summary>
        public bool IsMainThread { get; }

        /// <summary>Creates an entry.</summary>
        /// <param name="logType">The Unity log type.</param>
        /// <param name="message">The message text; <c>null</c> becomes empty.</param>
        /// <param name="stackTrace">The stack trace, or <c>null</c>.</param>
        /// <param name="contextName">The context object's name, or <c>null</c>.</param>
        /// <param name="timestampUtc">Capture time in UTC.</param>
        /// <param name="threadId">Managed thread ID.</param>
        /// <param name="isMainThread">Whether this was Unity's main thread.</param>
        public MolcaLogEntry(LogType logType, string message, string stackTrace, string contextName,
            DateTime timestampUtc, int threadId, bool isMainThread)
        {
            UnityLogType = logType;
            Level = MolcaLogLevels.FromUnity(logType);
            Message = message ?? string.Empty;
            StackTrace = stackTrace;
            ContextName = contextName;
            TimestampUtc = timestampUtc;
            ThreadId = threadId;
            IsMainThread = isMainThread;
        }

        /// <summary>
        /// Renders the entry as one log-file record.
        /// </summary>
        /// <param name="includeStackTrace">Whether to append the stack trace, when there is one.</param>
        /// <returns>A single-line header followed by optional indented stack trace lines.</returns>
        /// <remarks>
        /// The invariant culture and a sortable timestamp, so a log file reads and sorts the same on every
        /// machine. The thread is named only when it is not the main one — annotating every line with
        /// "main" is noise, while a line that came off the main thread is worth flagging.
        /// </remarks>
        public string Format(bool includeStackTrace = true)
        {
            var builder = new StringBuilder(Message.Length + 64);

            builder.Append('[')
                .Append(TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append("Z] [")
                .Append(UnityLogType)
                .Append(']');

            if (!IsMainThread) builder.Append(" [thread ").Append(ThreadId).Append(']');
            if (!string.IsNullOrEmpty(ContextName)) builder.Append(" [").Append(ContextName).Append(']');

            builder.Append(' ').Append(Message);

            if (includeStackTrace && !string.IsNullOrEmpty(StackTrace))
            {
                builder.AppendLine().Append("    ")
                    .Append(StackTrace.TrimEnd().Replace("\n", "\n    "));
            }

            return builder.ToString();
        }

        /// <inheritdoc/>
        public override string ToString() => Format(includeStackTrace: false);
    }
}
