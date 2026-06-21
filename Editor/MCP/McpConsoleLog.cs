using System;
using UnityEditor;
using UnityEngine;

namespace Molca.Editor.Mcp
{
    /// <summary>
    /// Editor-lifetime, thread-safe capture of Unity console messages into a bounded ring buffer, so
    /// the MCP <c>molca_read_console</c> tool can report what the editor logged without any dependency
    /// on Unity's internal <c>LogEntries</c> API. Subscribes on editor load via
    /// <see cref="InitializeOnLoadAttribute"/> so capture begins before any tool is invoked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Thread-safety.</b> <see cref="Application.logMessageReceivedThreaded"/> fires on whatever
    /// thread produced the log (jobs, background tasks). All buffer access is guarded by
    /// <see cref="_gate"/>; the MCP bridge reads the snapshot on the main thread. Main thread only is
    /// <i>not</i> assumed here.
    /// </para>
    /// <para>
    /// The buffer is bounded (<see cref="Capacity"/>); once full, the oldest entry is overwritten and
    /// <see cref="DroppedCount"/> increments, so the capture is O(1) and cannot grow without limit
    /// (mirrors the bounded-history pattern used in the networking hardening track).
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class McpConsoleLog
    {
        /// <summary>Maximum number of console entries retained. Older entries are overwritten when full.</summary>
        public const int Capacity = 2000;

        /// <summary>One captured console message.</summary>
        public readonly struct Entry
        {
            /// <summary>Monotonic sequence number assigned at capture (1-based, stable ordering).</summary>
            public readonly long Sequence;

            /// <summary>The message text (Unity's <c>condition</c>).</summary>
            public readonly string Message;

            /// <summary>The captured stack trace; empty for most non-exception logs.</summary>
            public readonly string StackTrace;

            /// <summary>Unity log severity (Log/Warning/Error/Assert/Exception).</summary>
            public readonly LogType Type;

            /// <summary>UTC capture time.</summary>
            public readonly DateTime TimestampUtc;

            internal Entry(long sequence, string message, string stackTrace, LogType type, DateTime timestampUtc)
            {
                Sequence = sequence;
                Message = message ?? string.Empty;
                StackTrace = stackTrace ?? string.Empty;
                Type = type;
                TimestampUtc = timestampUtc;
            }
        }

        private static readonly object _gate = new object();
        private static readonly Entry[] _ring = new Entry[Capacity];

        /// <summary>Count of entries currently held (≤ <see cref="Capacity"/>).</summary>
        private static int _count;

        /// <summary>Index where the next entry will be written (wraps at <see cref="Capacity"/>).</summary>
        private static int _head;

        /// <summary>Total messages ever captured; also the sequence number of the most recent entry.</summary>
        private static long _totalSeen;

        /// <summary>Number of entries discarded because the buffer was full when newer ones arrived.</summary>
        public static long DroppedCount
        {
            get { lock (_gate) { return Math.Max(0, _totalSeen - _count); } }
        }

        /// <summary>Total number of messages captured since the editor session began.</summary>
        public static long TotalSeen
        {
            get { lock (_gate) { return _totalSeen; } }
        }

        static McpConsoleLog()
        {
            // Subscribe to the threaded variant so logs raised off the main thread are not lost.
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        private static void OnLogReceived(string condition, string stackTrace, LogType type)
        {
            // DateTime.UtcNow is fine in editor runtime code; the no-Date.now restriction is workflow-only.
            var stamp = DateTime.UtcNow;
            lock (_gate)
            {
                _totalSeen++;
                _ring[_head] = new Entry(_totalSeen, condition, stackTrace, type, stamp);
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        /// <summary>
        /// Returns a snapshot of the currently buffered entries in chronological (oldest-first) order.
        /// The returned array is a copy; callers may filter/page it freely without holding the lock.
        /// </summary>
        /// <returns>The buffered entries, oldest first; empty when nothing has been logged.</returns>
        public static Entry[] Snapshot()
        {
            lock (_gate)
            {
                var result = new Entry[_count];
                // Oldest entry sits `_count` slots behind the head when the ring is full, else at index 0.
                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                    result[i] = _ring[(start + i) % Capacity];
                return result;
            }
        }
    }
}
