using System.Collections.Generic;
using System.Text;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Spec-correct (WHATWG / RFC-6502 <c>text/event-stream</c>) incremental parser for
    /// Server-Sent Events. Replaces the previous substring-delta line splitter, which
    /// broke on chunk boundaries and multi-line <c>data:</c> fields.
    /// </summary>
    /// <remarks>
    /// Feed raw stream text as it arrives via <see cref="Feed"/>; complete events are
    /// returned as their field boundaries (blank lines) are seen. Tracks
    /// <see cref="LastEventId"/> (sent as <c>Last-Event-ID</c> on reconnect) and
    /// <see cref="RetryMilliseconds"/> (feeds the reconnect backoff). Not thread-safe;
    /// drive it from a single provider read loop.
    /// </remarks>
    public sealed class SSEEventStreamParser
    {
        /// <summary>A dispatched SSE event (only produced when its data buffer is non-empty).</summary>
        public readonly struct SSEEvent
        {
            /// <summary>The <c>event:</c> type, or <c>null</c> when unspecified (default "message").</summary>
            public readonly string EventType;
            /// <summary>The accumulated <c>data:</c> payload (lines joined with <c>\n</c>).</summary>
            public readonly string Data;
            /// <summary>The last-seen <c>id:</c> at dispatch time, or <c>null</c>.</summary>
            public readonly string Id;

            public SSEEvent(string eventType, string data, string id)
            {
                EventType = eventType;
                Data = data;
                Id = id;
            }
        }

        // Unprocessed tail (a partial line not yet terminated by a newline).
        private readonly StringBuilder _pending = new StringBuilder();
        private readonly StringBuilder _data = new StringBuilder();
        private string _eventType;
        private bool _hasData;

        /// <summary>The most recent event id seen; persists across reconnects for resume.</summary>
        public string LastEventId { get; private set; }

        /// <summary>The server-requested reconnect delay (ms) from a <c>retry:</c> field, if any.</summary>
        public int? RetryMilliseconds { get; private set; }

        /// <summary>
        /// Clears in-progress framing (partial line + current event fields) for a fresh
        /// connection, while preserving <see cref="LastEventId"/>/<see cref="RetryMilliseconds"/>
        /// so a reconnect can resume.
        /// </summary>
        public void ResetStream()
        {
            _pending.Clear();
            _data.Clear();
            _eventType = null;
            _hasData = false;
        }

        /// <summary>
        /// Feeds a chunk of stream text and returns every event completed by it. A chunk
        /// may complete zero, one, or several events; an unterminated trailing line is
        /// retained until a later <see cref="Feed"/> closes it.
        /// </summary>
        public IEnumerable<SSEEvent> Feed(string chunk)
        {
            var events = new List<SSEEvent>();
            if (string.IsNullOrEmpty(chunk))
                return events;

            _pending.Append(chunk);
            string buffered = _pending.ToString();

            int start = 0;
            int i = 0;
            while (i < buffered.Length)
            {
                char c = buffered[i];
                if (c == '\n' || c == '\r')
                {
                    string line = buffered.Substring(start, i - start);
                    ProcessLine(line, events);

                    // Treat \r\n as a single terminator.
                    if (c == '\r' && i + 1 < buffered.Length && buffered[i + 1] == '\n')
                        i++;
                    i++;
                    start = i;
                }
                else
                {
                    i++;
                }
            }

            // Keep the unterminated remainder for the next feed.
            _pending.Clear();
            if (start < buffered.Length)
                _pending.Append(buffered, start, buffered.Length - start);

            return events;
        }

        private void ProcessLine(string line, List<SSEEvent> sink)
        {
            // Blank line → dispatch the buffered event.
            if (line.Length == 0)
            {
                Dispatch(sink);
                return;
            }

            // Comment line.
            if (line[0] == ':')
                return;

            int colon = line.IndexOf(':');
            string field;
            string value;
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line.Substring(0, colon);
                value = line.Substring(colon + 1);
                // A single leading space after the colon is part of the syntax, not the value.
                if (value.Length > 0 && value[0] == ' ')
                    value = value.Substring(1);
            }

            switch (field)
            {
                case "data":
                    _data.Append(value).Append('\n');
                    _hasData = true;
                    break;
                case "event":
                    _eventType = value;
                    break;
                case "id":
                    // The spec ignores an id containing a NUL.
                    if (value.IndexOf('\0') < 0)
                        LastEventId = value;
                    break;
                case "retry":
                    if (int.TryParse(value, out int ms) && ms >= 0)
                        RetryMilliseconds = ms;
                    break;
                // Unknown fields are ignored per spec.
            }
        }

        private void Dispatch(List<SSEEvent> sink)
        {
            // Per spec, an empty data buffer dispatches nothing; field buffers still reset.
            if (_hasData)
            {
                // Drop the single trailing newline accumulated after the last data line.
                string data = _data.Length > 0 ? _data.ToString(0, _data.Length - 1) : string.Empty;
                sink.Add(new SSEEvent(_eventType, data, LastEventId));
            }

            _data.Clear();
            _eventType = null;
            _hasData = false;
        }
    }
}
