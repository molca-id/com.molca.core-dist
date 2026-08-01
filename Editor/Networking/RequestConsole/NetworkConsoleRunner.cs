using System;
using System.Threading;
using UnityEngine;
using Molca.Networking;
using Molca.Networking.Configuration;
using Molca.Networking.Diagnostics;
using Molca.Networking.Http;
using Molca.Networking.Pipeline;
using Molca.Networking.Routing;
using Molca.Networking.Security;

namespace Molca.Editor.Networking.RequestConsole
{
    /// <summary>
    /// Executes request-console sends through the production routed pipeline, over an editor-owned
    /// client that reads the authored catalog.
    /// </summary>
    /// <remarks>
    /// It builds a real <see cref="RoutedHttpClient"/> rather than a console-specific sender, so route
    /// resolution, policy resolution, host and production validation, credential scoping, redirect
    /// handling, retry, the bulkhead, the circuit breaker, and redacted diagnostics are all the same code
    /// a build runs. A second sender is how a console starts reporting a URL the game never requests.
    /// <para>
    /// It is <em>not</em> the runtime subsystem's client, and must not be. The subsystem snapshots the
    /// catalog once at initialization; the console has to follow edits made seconds ago. Owning a separate
    /// client also keeps console traffic out of the running game's circuit breakers and bulkheads, so
    /// probing a failing service from the editor cannot open a circuit for play mode.
    /// </para>
    /// <para>
    /// Credentials go through <see cref="ConsoleCredentialGate"/>: only providers Core can supply in the
    /// editor are registered, and a profile that is not marked
    /// <see cref="NetworkCredentialProfile.UsableFromRequestConsole"/> resolves to no credential at all.
    /// </para>
    /// </remarks>
    internal sealed class NetworkConsoleRunner : IDisposable
    {
        /// <summary>Redacted sends retained for the console's history.</summary>
        internal const int HistoryCapacity = 50;

        private readonly NetworkRouteStateStore _routeStates = new NetworkRouteStateStore();
        private readonly NetworkCredentialRegistry _credentials = new NetworkCredentialRegistry();
        private readonly NetworkResponseCache _cache = new NetworkResponseCache();

        private NetworkDiagnosticStore _diagnostics;
        private RoutedHttpClient _client;
        private CancellationTokenSource _inFlight;
        private IHttpTransport _transport;

        /// <summary>The catalog the current client resolves against, or <c>null</c>.</summary>
        public NetworkCatalog Catalog { get; private set; }

        /// <summary>Redacted diagnostics for sends made from the console. Never <c>null</c> after <see cref="Rebuild"/>.</summary>
        public INetworkDiagnostics Diagnostics => _diagnostics;

        /// <summary>Whether a send is currently in flight.</summary>
        public bool IsSending => _inFlight != null;

        /// <summary>The last outcome, or <c>null</c> before the first send.</summary>
        public RoutedHttpOutcome LastOutcome { get; private set; }

        /// <summary>The last response body, redacted, or empty.</summary>
        public string LastBodyPreview { get; private set; } = string.Empty;

        /// <summary>Raised when a send starts, completes, or is cancelled.</summary>
        public event Action Changed;

        /// <summary>
        /// Rebuilds the client over a catalog.
        /// </summary>
        /// <param name="catalog">The catalog to resolve against, or <c>null</c> to tear down.</param>
        /// <remarks>
        /// Called on every workspace reload. The snapshot is captured here, so an edit made in another
        /// view is visible to the next send — and a send already in flight keeps the snapshot it started
        /// with, because the client it is running on is not disposed until the send unwinds.
        /// </remarks>
        public void Rebuild(NetworkCatalog catalog)
        {
            Catalog = catalog;

            _client?.Dispose();
            _client = null;

            if (catalog == null)
            {
                _diagnostics = null;
                return;
            }

            _diagnostics = new NetworkDiagnosticStore(_routeStates, HistoryCapacity);
            var observers = new NetworkObserverDispatcher(_diagnostics);

            _client = new RoutedHttpClient(
                new NetworkRouteResolver(NetworkCatalogSnapshot.Capture(catalog)),
                _routeStates, _credentials, _diagnostics, observers, _cache);

            if (_transport != null)
                _client.SetTransport(_transport);

            RegisterConsoleCredentialProviders();
        }

        /// <summary>
        /// Replaces the transport. Test seam — the console uses <see cref="UnityWebRequestTransport"/>.
        /// </summary>
        /// <param name="transport">The transport to use, or <c>null</c> to restore the default.</param>
        internal void SetTransport(IHttpTransport transport)
        {
            _transport = transport;
            _client?.SetTransport(transport);
        }

        /// <summary>
        /// Sends a draft.
        /// </summary>
        /// <param name="draft">The draft to send.</param>
        /// <param name="cancellationToken">Cancels the send.</param>
        /// <returns>The outcome, or <c>null</c> when the console has no client or a send is already running.</returns>
        /// <remarks>
        /// One send at a time. The console is a manual tool, and a second concurrent send would make the
        /// result pane ambiguous about which request it is describing.
        /// </remarks>
        public async Awaitable<RoutedHttpOutcome> SendAsync(
            NetworkConsoleRequest draft, CancellationToken cancellationToken = default)
        {
            if (_client == null || draft == null || IsSending)
                return null;

            _inFlight = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            LastOutcome = null;
            LastBodyPreview = string.Empty;
            Changed?.Invoke();

            try
            {
                var outcome = await _client.SendAsync(
                    draft.Route, draft.BuildHttpRequest(), draft.BuildQuery(), _inFlight.Token);

                LastOutcome = outcome;
                LastBodyPreview = RedactedBodyOf(outcome);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not an error. The console shows "cancelled" and keeps the panel intact.
                return null;
            }
            finally
            {
                _inFlight?.Dispose();
                _inFlight = null;
                Changed?.Invoke();
            }
        }

        /// <summary>Cancels the send in flight. No-op when none is running.</summary>
        public void Cancel()
        {
            if (_inFlight == null) return;

            try
            {
                _inFlight.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The send completed between the check and the cancel; nothing to do.
            }
        }

        /// <summary>Drops the console's request history.</summary>
        public void ClearHistory()
        {
            _diagnostics?.Clear();
            LastOutcome = null;
            LastBodyPreview = string.Empty;
            Changed?.Invoke();
        }

        /// <summary>
        /// The console's history as redacted export text.
        /// </summary>
        /// <returns>Export text; a header line only when nothing has been sent.</returns>
        public string ExportHistory() =>
            _diagnostics?.Export() ?? "# Molca network diagnostics — no console client";

        /// <inheritdoc />
        public void Dispose()
        {
            Cancel();
            _client?.Dispose();
            _client = null;
            _diagnostics = null;
            _routeStates.Clear();
            _credentials.ClearCache();
            _cache.Clear();
        }

        /// <summary>
        /// A redacted preview of a response body.
        /// </summary>
        /// <param name="outcome">The outcome to read.</param>
        /// <returns>The preview, truncated and with credential-shaped JSON fields masked.</returns>
        internal static string RedactedBodyOf(RoutedHttpOutcome outcome)
        {
            string text = outcome?.Text;
            if (string.IsNullOrEmpty(text)) return string.Empty;

            string redacted = Molca.Networking.Utils.LogRedaction.RedactJsonBody(text);
            return redacted.Length <= NetworkDiagnosticStore.BodyPreviewLimit
                ? redacted
                : redacted.Substring(0, NetworkDiagnosticStore.BodyPreviewLimit) + "…";
        }

        /// <summary>
        /// Registers the credential providers the editor may use, each behind the console gate.
        /// </summary>
        /// <remarks>
        /// Only <see cref="EnvironmentVariableCredentialProvider"/> qualifies, matching what
        /// <c>NetworkRuntimeSubsystem</c> registers on its own: an environment variable is the developer's
        /// own machine state, not a secret the framework stored. Every other kind stays unregistered, so
        /// a profile that depends on a live auth session or a platform key store resolves to no
        /// credential and the console says so, rather than the editor inventing a way to obtain one.
        /// </remarks>
        private void RegisterConsoleCredentialProviders()
        {
            _credentials.ClearCache();
            _credentials.Register(new ConsoleCredentialGate(new EnvironmentVariableCredentialProvider()));
        }

        /// <summary>
        /// Wraps a credential provider so the console can only obtain credentials from profiles that
        /// opted into it.
        /// </summary>
        /// <remarks>
        /// The enforcement point for
        /// <see cref="NetworkCredentialProfile.UsableFromRequestConsole"/>. It sits at acquisition rather
        /// than in the view, so no console code path — including a future one — can reach a credential
        /// the catalog withheld from it.
        /// </remarks>
        internal sealed class ConsoleCredentialGate : INetworkCredentialProvider
        {
            private readonly INetworkCredentialProvider _inner;

            /// <inheritdoc />
            public NetworkCredentialProviderKind Kind => _inner.Kind;

            /// <summary>Wraps a provider.</summary>
            /// <param name="inner">The provider to gate.</param>
            public ConsoleCredentialGate(INetworkCredentialProvider inner) =>
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));

            /// <inheritdoc />
            public Awaitable<NetworkCredential> AcquireAsync(
                NetworkCredentialProfile profile, bool forceRefresh, CancellationToken cancellationToken)
            {
                if (profile != null && profile.UsableFromRequestConsole)
                    return _inner.AcquireAsync(profile, forceRefresh, cancellationToken);

                var completion = new AwaitableCompletionSource<NetworkCredential>();
                completion.SetResult(NetworkCredential.None);
                return completion.Awaitable;
            }
        }
    }
}
