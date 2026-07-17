using System.Collections.Generic;
using System.Text;
using UnityEngine.Networking;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Incremental download handler for <c>text/event-stream</c> responses: decodes each
    /// received network chunk to UTF-8 text once and queues it for the provider's read
    /// loop. Replaces the previous <see cref="DownloadHandlerBuffer"/> approach, which
    /// buffered the whole stream forever and re-decoded it in full on every poll —
    /// O(n²) CPU and unbounded memory on long-lived streams.
    /// </summary>
    /// <remarks>
    /// A stateful <see cref="Decoder"/> holds multi-byte UTF-8 sequences split across
    /// chunk boundaries, so no character is ever corrupted or duplicated. Unity invokes
    /// <see cref="ReceiveData"/> on the main thread; drain with <see cref="TryDequeue"/>
    /// from the same thread (the provider poll loop). Not thread-safe.
    /// </remarks>
    internal sealed class SSEStreamDownloadHandler : DownloadHandlerScript
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly Queue<string> _chunks = new Queue<string>();

        public SSEStreamDownloadHandler() : base(new byte[8192]) { }

        /// <summary>Whether any payload byte has arrived (used to mark the connection established).</summary>
        public bool ReceivedAnyData { get; private set; }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            FeedBytes(data, dataLength);
            return true; // keep the download running
        }

        // Split out of ReceiveData so tests can exercise chunk-boundary decoding
        // without a live UnityWebRequest.
        internal void FeedBytes(byte[] data, int dataLength)
        {
            if (data == null || dataLength <= 0)
                return;

            ReceivedAnyData = true;

            int charCount = _decoder.GetCharCount(data, 0, dataLength);
            if (charCount == 0)
                return; // chunk ended mid-sequence; the decoder holds the partial bytes

            char[] chars = new char[charCount];
            int written = _decoder.GetChars(data, 0, dataLength, chars, 0);
            if (written > 0)
                _chunks.Enqueue(new string(chars, 0, written));
        }

        /// <summary>Dequeues the next decoded text chunk, if one is pending.</summary>
        /// <param name="chunk">The decoded chunk text.</param>
        /// <returns><c>true</c> while chunks remain queued.</returns>
        public bool TryDequeue(out string chunk)
        {
            if (_chunks.Count > 0)
            {
                chunk = _chunks.Dequeue();
                return true;
            }

            chunk = null;
            return false;
        }
    }
}
