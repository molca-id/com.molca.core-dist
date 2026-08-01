using System;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Data;
using Molca.Networking.Diagnostics;
using Molca.Networking.Routing;
using Molca.Networking.Security;
using Molca.Networking.Utils;

namespace Molca.Networking.Streaming
{
    /// <summary>Lifecycle state of a streaming session.</summary>
    public enum NetworkStreamSessionState
    {
        /// <summary>Created but not started.</summary>
        Idle = 0,

        /// <summary>Resolving the route and acquiring credentials.</summary>
        Resolving,

        /// <summary>Opening the connection.</summary>
        Connecting,

        /// <summary>Connected and receiving.</summary>
        Connected,

        /// <summary>Waiting out a backoff delay before the next attempt.</summary>
        Reconnecting,

        /// <summary>Stopped because something is wrong that reconnecting will not fix.</summary>
        Faulted,

        /// <summary>Stopped cleanly.</summary>
        Closed,
    }

    /// <summary>The outcome of one connection attempt, as the protocol saw it.</summary>
    public readonly struct NetworkStreamAttempt
    {
        /// <summary>Whether the connection was established and carried data.</summary>
        public readonly bool Established;

        /// <summary>Whether the server rejected the credential (401/403-shaped).</summary>
        public readonly bool AuthRejected;

        /// <summary>Seconds the connection stayed up; 0 when it never established.</summary>
        public readonly float ConnectedSeconds;

        /// <summary>A failure message, or empty.</summary>
        public readonly string Error;

        /// <summary>Creates an attempt result.</summary>
        /// <param name="established">Whether data flowed.</param>
        /// <param name="authRejected">Whether the credential was rejected.</param>
        /// <param name="connectedSeconds">How long it stayed up.</param>
        /// <param name="error">A failure message, or <c>null</c>.</param>
        public NetworkStreamAttempt(
            bool established, bool authRejected = false, float connectedSeconds = 0f, string error = null)
        {
            Established = established;
            AuthRejected = authRejected;
            ConnectedSeconds = connectedSeconds;
            Error = error ?? string.Empty;
        }
    }

    /// <summary>
    /// A live streaming connection owned by the network subsystem.
    /// </summary>
    /// <remarks>
    /// <b>Every mutable thing about a stream lives here</b> — connection state, attempt count, last
    /// error, the cancellation source, the reconnect budget, the connected-since stamp, and the socket
    /// handle a subclass holds. None of it is on a <see cref="ScriptableObject"/>, which is the point of
    /// plan §6.7: a provider asset that records its own connection state is writing to project data at
    /// runtime, and two scenes using one asset were sharing that state.
    /// <para>
    /// The connect/reconnect/authenticate loop is here and the protocol is not. A subclass implements
    /// exactly one method — <see cref="ConnectAndPumpAsync"/> — and gets route resolution, allowed-host
    /// and production checks, scoped credential acquisition with one refresh on rejection, bounded
    /// jittered backoff, redaction, and diagnostics from this class. Protocol-specific behaviour stays
    /// protocol-specific; §6.7 explicitly warns against flattening the transports into a
    /// lowest-common-denominator abstraction, so nothing here models frames, events, or acks.
    /// </para>
    /// <para>
    /// Main-thread only, like the rest of the subsystem surface.
    /// </para>
    /// </remarks>
    public abstract class NetworkStreamSession : IDisposable, INetworkStreamStatus
    {
        private readonly INetworkRouteResolver _resolver;
        private readonly NetworkCredentialRegistry _credentials;
        private readonly StreamReconnectSettings _reconnect;

        private CancellationTokenSource _lifetime;
        private StreamReconnectPolicy _policy;
        private bool _disposed;
        private bool _running;

        /// <summary>Stable identifier, usually the provider ID that owns this session.</summary>
        public string Id { get; }

        /// <summary>The route this session connects to.</summary>
        public NetworkStreamRoute Route { get; }

        /// <summary>The protocol this session speaks.</summary>
        public NetworkProtocols Protocol { get; }

        /// <summary>Current lifecycle state.</summary>
        public NetworkStreamSessionState State { get; private set; } = NetworkStreamSessionState.Idle;

        /// <summary>The last failure, redacted, or empty.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>Connection attempts made since the session started.</summary>
        public int AttemptCount { get; private set; }

        /// <summary>When the current connection was established, or <c>null</c> when not connected.</summary>
        public DateTime? ConnectedSinceUtc { get; private set; }

        /// <summary>Messages dispatched to the owner since the session started.</summary>
        public int ReceivedCount { get; private set; }

        /// <summary>The binding the current or most recent attempt used, or <c>null</c>.</summary>
        public NetworkStreamBinding Binding { get; private set; }

        /// <summary>Whether a credential was attached to the most recent attempt.</summary>
        public bool IsAuthenticated { get; private set; }

        /// <summary>Raised when <see cref="State"/> changes. Never raised from a background thread.</summary>
        public event Action<NetworkStreamSession> StateChanged;

        /// <summary>Raised for each message the stream delivered.</summary>
        public event Action<string> MessageReceived;

        /// <inheritdoc />
        public bool IsStreamConnected => State == NetworkStreamSessionState.Connected;

        /// <inheritdoc />
        public string StreamStatus => Describe();

        /// <summary>Creates a session.</summary>
        /// <param name="id">Stable identifier.</param>
        /// <param name="route">Where to connect.</param>
        /// <param name="protocol">The protocol spoken.</param>
        /// <param name="resolver">The shared route resolver.</param>
        /// <param name="credentials">The shared credential registry.</param>
        /// <param name="reconnect">Reconnect settings, or <c>null</c> for the defaults.</param>
        /// <param name="directUri">
        /// An absolute URI to connect to when <paramref name="route"/> names no service. The
        /// compatibility path for a provider that still authors its own URL; see
        /// <see cref="NetworkStreamBinding.Direct"/> for what it does and does not enforce.
        /// </param>
        protected NetworkStreamSession(
            string id,
            NetworkStreamRoute route,
            NetworkProtocols protocol,
            INetworkRouteResolver resolver,
            NetworkCredentialRegistry credentials,
            StreamReconnectSettings reconnect = null,
            string directUri = null)
        {
            Id = string.IsNullOrEmpty(id) ? Guid.NewGuid().ToString("N") : id;
            Route = route;
            Protocol = protocol;
            _resolver = resolver;
            _credentials = credentials;
            _reconnect = reconnect ?? StreamReconnectSettings.Default;
            DirectUri = directUri ?? string.Empty;
        }

        /// <summary>The directly authored URI this session falls back to, or empty.</summary>
        public string DirectUri { get; }

        /// <summary>
        /// Starts the session and runs until it is stopped, disposed, or gives up.
        /// </summary>
        /// <param name="cancellationToken">Cancels the session; linked with its own lifetime.</param>
        /// <returns>An awaitable that completes when the session stops.</returns>
        /// <remarks>
        /// Awaited by the caller rather than fire-and-forget wherever possible: a headless run never
        /// advances an Awaitable nobody awaits. <c>DataProvider.Activate</c> is the one caller that
        /// cannot await, and it keys the session on its own lifetime token instead.
        /// </remarks>
        public async Awaitable RunAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NetworkStreamSession));
            if (_running) return;

            _running = true;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _policy = new StreamReconnectPolicy(
                _reconnect.BaseDelaySeconds, _reconnect.MaxDelaySeconds, _reconnect.MaxAttempts,
                stableResetSeconds: _reconnect.StableConnectionSeconds);

            AttemptCount = 0;
            ReceivedCount = 0;

            try
            {
                await LoopAsync(_lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                SetState(NetworkStreamSessionState.Closed);
            }
            catch (Exception e)
            {
                Fault($"Stream session failed: {e.Message}");
            }
            finally
            {
                _running = false;
                ConnectedSinceUtc = null;
                ReleaseConnection();

                if (State != NetworkStreamSessionState.Faulted)
                    SetState(NetworkStreamSessionState.Closed);
            }
        }

        private async Awaitable LoopAsync(CancellationToken token)
        {
            bool refreshAttempted = false;

            while (!token.IsCancellationRequested)
            {
                SetState(NetworkStreamSessionState.Resolving);

                // Re-resolved per attempt, not cached: the catalog default environment or a binding may
                // have been fixed since the last failure, and a session that cached a broken resolution
                // would stay broken until the game restarted.
                var binding = Route.IsConfigured || string.IsNullOrEmpty(DirectUri)
                    ? NetworkStreamBinding.Resolve(_resolver, Route, Protocol)
                    : NetworkStreamBinding.Direct(DirectUri, Protocol);

                Binding = binding;

                if (!binding.Resolves)
                {
                    // A configuration failure is terminal for this session. Retrying a route that does
                    // not exist just produces the same answer on a timer.
                    Fault(binding.FailureMessage);
                    return;
                }

                if (!TryValidateSecurity(binding, out string securityFailure))
                {
                    Fault(securityFailure);
                    return;
                }

                var credential = await AcquireCredentialAsync(binding, refreshAttempted, token);
                IsAuthenticated = credential.HasValue;

                AttemptCount++;
                SetState(NetworkStreamSessionState.Connecting);

                NetworkStreamAttempt attempt;
                try
                {
                    attempt = await ConnectAndPumpAsync(binding, credential, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    attempt = new NetworkStreamAttempt(false, error: e.Message);
                }
                finally
                {
                    ConnectedSinceUtc = null;
                    ReleaseConnection();
                }

                if (!string.IsNullOrEmpty(attempt.Error))
                    LastError = LogRedaction.RedactUrl(attempt.Error);

                if (attempt.Established)
                {
                    _policy.OnConnectionEnded(attempt.ConnectedSeconds);
                    refreshAttempted = false;
                }

                if (token.IsCancellationRequested || !_reconnect.AutoReconnect)
                    return;

                if (attempt.AuthRejected)
                {
                    if (refreshAttempted)
                    {
                        // A refreshed credential was rejected too. The session is dead; reconnecting
                        // would loop against a server that has already answered the question.
                        Fault("The stream was rejected after a credential refresh, so it will not retry.");
                        return;
                    }

                    refreshAttempted = true;

                    // A catalog credential refreshes by being re-acquired with forceRefresh on the next
                    // attempt. A credential the session obtained elsewhere — a provider reading the auth
                    // session directly — has to be refreshed by whoever owns it.
                    if (binding.Credential == null || !binding.CredentialAppliesToHost)
                    {
                        if (!await TryRefreshExternalCredentialAsync(token))
                        {
                            Fault("The stream was rejected and its credential could not be refreshed.");
                            return;
                        }
                    }

                    // One immediate retry, matching the HTTP pipeline's single re-authentication on a 401
                    // rather than counting it against the backoff budget.
                    continue;
                }

                SetState(NetworkStreamSessionState.Reconnecting);

                if (!await _policy.WaitForNextAttemptAsync(token))
                {
                    Fault($"Reconnect attempts exhausted after {_policy.AttemptCount} attempt(s).");
                    return;
                }
            }
        }

        /// <summary>
        /// Rejects a binding whose destination violates a rule the environment or service imposes.
        /// </summary>
        /// <param name="binding">The binding to check.</param>
        /// <param name="failure">Why it was rejected, or <c>null</c>.</param>
        /// <returns><c>true</c> when the connection may proceed.</returns>
        /// <remarks>
        /// The resolver already refuses an insecure origin under production safety and a host outside an
        /// authored allowlist. This is the second gate for the thing the resolver cannot see: a route
        /// whose credential is scoped away from the resolved host still connects, but it must connect
        /// anonymously rather than silently reusing a token the catalog scoped elsewhere.
        /// </remarks>
        private static bool TryValidateSecurity(NetworkStreamBinding binding, out string failure)
        {
            if (binding.Policy.RequireSecureTransport.Value && !IsEncrypted(binding.Uri))
            {
                failure =
                    $"Route {binding.Route} requires an encrypted transport, but '{binding.Uri}' is not " +
                    "encrypted.";
                return false;
            }

            failure = null;
            return true;
        }

        /// <summary>
        /// Whether a stream URI uses an encrypted scheme.
        /// </summary>
        /// <param name="uri">The absolute URI to test.</param>
        /// <remarks>
        /// Streaming needs its own answer because <c>wss</c> is not an HTTP scheme, and an unparseable
        /// URI counts as not encrypted — the safe direction for a rule that exists to stop a credential
        /// travelling in the clear.
        /// </remarks>
        private static bool IsEncrypted(string uri)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                return false;

            return string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(parsed.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
        }

        private Awaitable<NetworkCredential> AcquireCredentialAsync(
            NetworkStreamBinding binding, bool forceRefresh, CancellationToken token)
        {
            if (_credentials == null || binding.Credential == null || !binding.CredentialAppliesToHost)
            {
                var none = new AwaitableCompletionSource<NetworkCredential>();
                none.SetResult(NetworkCredential.None);
                return none.Awaitable;
            }

            // The same registry, the same scope check, and the same single-flight refresh the HTTP
            // pipeline uses. Scope is checked against the resolved host, not the authored origin.
            return _credentials.AcquireForHostAsync(
                binding.Credential, binding.Route.ServiceId, binding.Host, forceRefresh, token);
        }

        /// <summary>
        /// Opens the connection and pumps it until it ends.
        /// </summary>
        /// <param name="binding">The resolved destination, policy, and credential scope.</param>
        /// <param name="credential">
        /// The credential to attach, or <see cref="NetworkCredential.None"/>. Already scope-checked
        /// against <see cref="NetworkStreamBinding.Host"/> — a subclass must not re-derive it.
        /// </param>
        /// <param name="cancellationToken">Cancels the attempt.</param>
        /// <returns>How the attempt ended.</returns>
        /// <remarks>
        /// Call <see cref="MarkConnected"/> the moment the connection is usable and
        /// <see cref="Dispatch"/> for each message. Do not touch a serialized field.
        /// </remarks>
        protected abstract Awaitable<NetworkStreamAttempt> ConnectAndPumpAsync(
            NetworkStreamBinding binding,
            NetworkCredential credential,
            CancellationToken cancellationToken);

        /// <summary>Releases the protocol's connection handle. Called after every attempt and on dispose.</summary>
        protected virtual void ReleaseConnection() { }

        /// <summary>
        /// Refreshes a credential this session obtained outside the catalog.
        /// </summary>
        /// <param name="cancellationToken">Cancels the refresh.</param>
        /// <returns><c>true</c> when a fresh credential is available and one more attempt is worth making.</returns>
        /// <remarks>
        /// Called once per rejection episode, and only when the binding carries no catalog credential —
        /// a routed credential refreshes through the registry's own single-flight path instead. The
        /// default refuses, which is the safe answer: without a refresh there is nothing new to try, and
        /// reconnecting with a token the server just rejected only burns the budget.
        /// <para>
        /// Overridden by the WebSocket and Socket.IO sessions, which read the auth session directly. The
        /// base class deliberately does not know that <c>AuthManager</c> exists.
        /// </para>
        /// </remarks>
        protected virtual Awaitable<bool> TryRefreshExternalCredentialAsync(
            CancellationToken cancellationToken)
        {
            var completion = new AwaitableCompletionSource<bool>();
            completion.SetResult(false);
            return completion.Awaitable;
        }

        /// <summary>Marks the connection established. Idempotent within one attempt.</summary>
        protected void MarkConnected()
        {
            if (State == NetworkStreamSessionState.Connected) return;

            ConnectedSinceUtc = DateTime.UtcNow;
            LastError = string.Empty;
            SetState(NetworkStreamSessionState.Connected);
        }

        /// <summary>Delivers one message to the session's owner.</summary>
        /// <param name="message">The message payload.</param>
        protected void Dispatch(string message)
        {
            ReceivedCount++;

            try
            {
                MessageReceived?.Invoke(message);
            }
            catch (Exception e)
            {
                // A listener that throws is the owner's bug and must not tear down the stream — the
                // same isolation the HTTP pipeline gives observers.
                Debug.LogError($"[Network] Stream '{Id}' listener threw: {e.Message}");
            }
        }

        /// <summary>Stops the session. Safe to call when it is not running.</summary>
        public void Stop()
        {
            try
            {
                _lifetime?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down; nothing to cancel.
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Stop();
            ReleaseConnection();

            _lifetime?.Dispose();
            _lifetime = null;

            ConnectedSinceUtc = null;
            StateChanged = null;
            MessageReceived = null;
        }

        private void Fault(string message)
        {
            LastError = LogRedaction.RedactUrl(message ?? string.Empty);
            SetState(NetworkStreamSessionState.Faulted);
        }

        private void SetState(NetworkStreamSessionState state)
        {
            if (State == state) return;

            State = state;

            try
            {
                StateChanged?.Invoke(this);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Network] Stream '{Id}' state listener threw: {e.Message}");
            }
        }

        /// <summary>A short redacted description, for the Hub and logs.</summary>
        public string Describe()
        {
            switch (State)
            {
                case NetworkStreamSessionState.Connected:
                    return "Connected";
                case NetworkStreamSessionState.Connecting:
                    return AttemptCount > 1 ? $"Connecting (attempt {AttemptCount})" : "Connecting";
                case NetworkStreamSessionState.Reconnecting:
                    return $"Reconnecting (attempt {AttemptCount + 1})";
                case NetworkStreamSessionState.Faulted:
                    return string.IsNullOrEmpty(LastError) ? "Faulted" : $"Faulted: {LastError}";
                case NetworkStreamSessionState.Resolving:
                    return "Resolving route";
                case NetworkStreamSessionState.Closed:
                    return "Disconnected";
                default:
                    return "Idle";
            }
        }

        /// <inheritdoc />
        public override string ToString() => $"{Id} [{Protocol}] {Route} — {Describe()}";
    }

    /// <summary>
    /// Reconnect behaviour for a streaming session.
    /// </summary>
    /// <remarks>
    /// Serializable so a provider asset can author it, but read once when the session starts — the
    /// session's own <see cref="StreamReconnectPolicy"/> holds the running budget, so no attempt count
    /// is ever written back to the asset.
    /// </remarks>
    [Serializable]
    public sealed class StreamReconnectSettings
    {
        [Tooltip("Reconnect automatically when the stream drops.")]
        [SerializeField] private bool _autoReconnect = true;

        [Tooltip("First reconnect delay in seconds; grows exponentially with jitter up to the max.")]
        [SerializeField] private float _baseDelaySeconds = 2f;

        [Tooltip("Ceiling for the backoff delay.")]
        [SerializeField] private float _maxDelaySeconds = 30f;

        [Tooltip("0 = unbounded (still backed-off).")]
        [SerializeField] private int _maxAttempts;

        [Tooltip("A connection must live this long before a drop resets the backoff budget. 0 = any connection resets.")]
        [SerializeField] private float _stableConnectionSeconds = 10f;

        /// <summary>Whether to reconnect automatically.</summary>
        public bool AutoReconnect => _autoReconnect;

        /// <summary>First backoff delay.</summary>
        public float BaseDelaySeconds => _baseDelaySeconds;

        /// <summary>Backoff ceiling.</summary>
        public float MaxDelaySeconds => _maxDelaySeconds;

        /// <summary>Attempt budget; 0 is unbounded.</summary>
        public int MaxAttempts => _maxAttempts;

        /// <summary>How long a connection must live to reset the budget.</summary>
        public float StableConnectionSeconds => _stableConnectionSeconds;

        /// <summary>The framework defaults.</summary>
        public static StreamReconnectSettings Default => new StreamReconnectSettings();

        /// <summary>Creates settings.</summary>
        /// <param name="autoReconnect">Whether to reconnect.</param>
        /// <param name="baseDelaySeconds">First backoff delay.</param>
        /// <param name="maxDelaySeconds">Backoff ceiling.</param>
        /// <param name="maxAttempts">Attempt budget; 0 is unbounded.</param>
        /// <param name="stableConnectionSeconds">Budget-reset window.</param>
        public static StreamReconnectSettings Create(
            bool autoReconnect = true,
            float baseDelaySeconds = 2f,
            float maxDelaySeconds = 30f,
            int maxAttempts = 0,
            float stableConnectionSeconds = 10f)
        {
            return new StreamReconnectSettings
            {
                _autoReconnect = autoReconnect,
                _baseDelaySeconds = baseDelaySeconds,
                _maxDelaySeconds = maxDelaySeconds,
                _maxAttempts = maxAttempts,
                _stableConnectionSeconds = stableConnectionSeconds,
            };
        }
    }
}
