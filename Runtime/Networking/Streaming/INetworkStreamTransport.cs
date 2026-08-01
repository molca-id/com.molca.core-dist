using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Molca.Networking.Streaming
{
    /// <summary>What to open, for a chunked read-only stream.</summary>
    public readonly struct NetworkStreamConnectRequest
    {
        /// <summary>The absolute URI to connect to.</summary>
        public readonly string Uri;

        /// <summary>Headers to send, including any credential the session resolved.</summary>
        public readonly IReadOnlyList<KeyValuePair<string, string>> Headers;

        /// <summary>Seconds between polls of the receive buffer.</summary>
        public readonly float PollIntervalSeconds;

        /// <summary>Creates a request.</summary>
        /// <param name="uri">The absolute URI.</param>
        /// <param name="headers">Headers to send.</param>
        /// <param name="pollIntervalSeconds">Receive-buffer poll interval.</param>
        public NetworkStreamConnectRequest(
            string uri,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            float pollIntervalSeconds = 0.1f)
        {
            Uri = uri;
            Headers = headers ?? Array.Empty<KeyValuePair<string, string>>();
            PollIntervalSeconds = pollIntervalSeconds;
        }
    }

    /// <summary>
    /// One open chunked stream, consumed as an async sequence of text chunks.
    /// </summary>
    /// <remarks>
    /// Shaped as an enumerator rather than a callback so the session's pump loop stays a plain
    /// <c>while</c> that a cancellation token can leave, and so a test can drive a stream chunk by
    /// chunk without a network or a frame loop.
    /// </remarks>
    public interface INetworkStreamConnection : IDisposable
    {
        /// <summary>Waits for the next chunk.</summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns><c>false</c> when the stream has ended; <see cref="Current"/> is then undefined.</returns>
        Awaitable<bool> MoveNextAsync(CancellationToken cancellationToken);

        /// <summary>The chunk most recently read.</summary>
        string Current { get; }

        /// <summary>The HTTP status the server answered with, or 0 when none was seen.</summary>
        long StatusCode { get; }

        /// <summary>A transport-level error message, or empty when the stream ended cleanly.</summary>
        string Error { get; }
    }

    /// <summary>
    /// Opens chunked streams. The seam that keeps <see cref="SseStreamSession"/> testable without a
    /// server.
    /// </summary>
    /// <remarks>
    /// The same role <c>IHttpTransport</c> plays for the request pipeline, and for the same reason:
    /// reconnect, backoff, credential refresh, and give-up rules are the parts worth testing, and none
    /// of them should require a socket to exercise.
    /// </remarks>
    public interface INetworkStreamTransport
    {
        /// <summary>Opens a stream.</summary>
        /// <param name="request">What to open.</param>
        /// <param name="cancellationToken">Cancels the connect.</param>
        /// <returns>The open connection; never <c>null</c>. Inspect <see cref="INetworkStreamConnection.Error"/>.</returns>
        Awaitable<INetworkStreamConnection> ConnectAsync(
            NetworkStreamConnectRequest request, CancellationToken cancellationToken);
    }
}
