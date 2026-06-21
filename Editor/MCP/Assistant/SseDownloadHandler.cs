using System;
using System.Text;
using UnityEngine.Networking;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// A <see cref="DownloadHandlerScript"/> that splits a streaming HTTP body into text lines as bytes
    /// arrive and hands each completed line to a callback (Sprint 24.7). Used to consume Server-Sent
    /// Events from the LLM providers. <see cref="DownloadHandlerScript.ReceiveData"/> is invoked on the
    /// main thread during the request's update loop, so the callback may safely touch editor UI.
    /// </summary>
    public sealed class SseDownloadHandler : DownloadHandlerScript
    {
        private readonly Action<string> _onLine;
        private readonly StringBuilder _pending = new StringBuilder();

        /// <summary>Creates the handler with a per-line callback.</summary>
        public SseDownloadHandler(Action<string> onLine) : base(new byte[8192])
        {
            _onLine = onLine;
        }

        /// <inheritdoc/>
        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (data == null || dataLength == 0) return false;

            _pending.Append(Encoding.UTF8.GetString(data, 0, dataLength));
            FlushCompleteLines();
            return true;
        }

        /// <inheritdoc/>
        protected override void CompleteContent()
        {
            // Emit any trailing partial line so the final SSE event is not dropped.
            if (_pending.Length > 0)
            {
                _onLine?.Invoke(_pending.ToString());
                _pending.Length = 0;
            }
        }

        private void FlushCompleteLines()
        {
            var text = _pending.ToString();
            var start = 0;
            int nl;
            while ((nl = text.IndexOf('\n', start)) >= 0)
            {
                var line = text.Substring(start, nl - start).TrimEnd('\r');
                _onLine?.Invoke(line);
                start = nl + 1;
            }

            _pending.Length = 0;
            if (start < text.Length)
                _pending.Append(text, start, text.Length - start);
        }
    }
}
