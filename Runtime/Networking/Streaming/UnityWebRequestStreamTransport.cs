using System;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
using Molca.Networking.Data;

namespace Molca.Networking.Streaming
{
    /// <summary>
    /// The production chunked-stream transport: a <see cref="UnityWebRequest"/> with a streaming
    /// download handler, polled on the main thread.
    /// </summary>
    /// <remarks>
    /// Each byte is decoded once, by <see cref="SSEStreamDownloadHandler"/>, rather than re-decoding the
    /// whole buffer on every poll — which is what made the original implementation's cost grow with
    /// session length.
    /// </remarks>
    public sealed class UnityWebRequestStreamTransport : INetworkStreamTransport
    {
        /// <inheritdoc />
        public Awaitable<INetworkStreamConnection> ConnectAsync(
            NetworkStreamConnectRequest request, CancellationToken cancellationToken)
        {
            var completion = new AwaitableCompletionSource<INetworkStreamConnection>();

            var handler = new SSEStreamDownloadHandler();
            var web = UnityWebRequest.Get(request.Uri);
            web.downloadHandler = handler;

            foreach (var header in request.Headers)
            {
                if (!string.IsNullOrEmpty(header.Key))
                    web.SetRequestHeader(header.Key, header.Value ?? string.Empty);
            }

            var operation = web.SendWebRequest();
            completion.SetResult(new Connection(web, handler, operation, request.PollIntervalSeconds));
            return completion.Awaitable;
        }

        /// <summary>One open <see cref="UnityWebRequest"/> stream.</summary>
        private sealed class Connection : INetworkStreamConnection
        {
            private readonly UnityWebRequestAsyncOperation _operation;
            private readonly SSEStreamDownloadHandler _handler;
            private readonly float _pollIntervalSeconds;

            private UnityWebRequest _web;
            private bool _drained;

            /// <inheritdoc />
            public string Current { get; private set; } = string.Empty;

            /// <inheritdoc />
            public long StatusCode => _web?.responseCode ?? 0;

            /// <inheritdoc />
            public string Error { get; private set; } = string.Empty;

            internal Connection(
                UnityWebRequest web,
                SSEStreamDownloadHandler handler,
                UnityWebRequestAsyncOperation operation,
                float pollIntervalSeconds)
            {
                _web = web;
                _handler = handler;
                _operation = operation;
                _pollIntervalSeconds = Mathf.Max(0.01f, pollIntervalSeconds);
            }

            /// <inheritdoc />
            public async Awaitable<bool> MoveNextAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (_web == null)
                        return false;

                    if (_handler.TryDequeue(out string chunk))
                    {
                        Current = chunk;
                        return true;
                    }

                    if (_operation.isDone)
                    {
                        // One final drain: bytes can arrive between the last poll and completion, and
                        // dropping them would silently lose the last event of every stream.
                        if (!_drained)
                        {
                            _drained = true;
                            continue;
                        }

                        if (_web.result != UnityWebRequest.Result.Success)
                            Error = string.IsNullOrEmpty(_web.error) ? "transport failure" : _web.error;

                        return false;
                    }

                    await Awaitable.WaitForSecondsAsync(_pollIntervalSeconds, cancellationToken);
                }
            }

            /// <inheritdoc />
            public void Dispose()
            {
                if (_web == null) return;

                var web = _web;
                _web = null;

                try
                {
                    web.Abort();
                }
                catch (Exception)
                {
                    // Aborting an already-finished request is not an error worth reporting.
                }

                web.Dispose();
            }
        }
    }
}
