using System;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Diagnostics;
using Molca.Networking.Pipeline;
using Molca.Networking.Routing;
using Molca.Networking.Security;
using Molca.Networking.Streaming;
using Molca.Settings;

namespace Molca.Networking
{
    /// <summary>
    /// Owns the routed networking stack: the catalog snapshot, the route resolver, the routed client,
    /// the credential registry, per-route pipeline state, the response cache, and diagnostics.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="IRoutedHttpClient"/> and <see cref="INetworkDiagnostics"/> as services, so
    /// call sites and tooling resolve them through DI rather than reaching for a static.
    /// <para>
    /// Every piece of mutable runtime state the routed stack has lives here and is discarded on
    /// <see cref="Teardown"/> — queues, circuit breakers, cached credentials, cached responses,
    /// diagnostics. Nothing survives a domain reload, and no <see cref="ScriptableObject"/> is written to.
    /// </para>
    /// <para>
    /// The catalog snapshot is captured <b>once</b>, during initialization. That is deliberate: a request
    /// resolved against one snapshot must not have the ground shift under it. Editing the catalog and
    /// wanting live traffic to follow means restarting play mode, which is also when a project would
    /// expect configuration to be re-read.
    /// </para>
    /// <para>
    /// Coexists with <see cref="Http.HttpClient"/> rather than replacing it. Phase 3 routes the legacy
    /// client's own sends through this pipeline; until then both are live and independent, and the legacy
    /// client remains the default for existing call sites.
    /// </para>
    /// </remarks>
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-networking.png")]
    public class NetworkRuntimeSubsystem : RuntimeSubsystem
    {
        [Header("Diagnostics")]
        [Tooltip("Redacted request records retained in the ring buffer.")]
        [SerializeField, Min(1)] private int _diagnosticCapacity = NetworkDiagnosticStore.DefaultCapacity;

        [Tooltip("Cached responses retained before least-recently-used eviction.")]
        [SerializeField, Min(1)] private int _responseCacheCapacity = NetworkResponseCache.DefaultCapacity;

        private RoutedHttpClient _client;
        private NetworkCredentialRegistry _credentials;
        private NetworkDiagnosticStore _diagnostics;
        private NetworkRouteStateStore _routeStates;
        private NetworkObserverDispatcher _observers;
        private NetworkResponseCache _cache;
        private NetworkStreamSessionRegistry _streams;

        /// <summary>The catalog snapshot this subsystem resolves against. Never <c>null</c> after init.</summary>
        public NetworkCatalogSnapshot Snapshot { get; private set; } = NetworkCatalogSnapshot.Empty;

        /// <summary>The routed client, or <c>null</c> before initialization.</summary>
        public IRoutedHttpClient Client => _client;

        /// <summary>The route resolver, or <c>null</c> before initialization.</summary>
        public INetworkRouteResolver Resolver { get; private set; }

        /// <summary>Redacted diagnostics, or <c>null</c> before initialization.</summary>
        public INetworkDiagnostics Diagnostics => _diagnostics;

        /// <summary>The credential registry, for projects registering their own providers.</summary>
        public NetworkCredentialRegistry Credentials => _credentials;

        /// <summary>
        /// Live streaming sessions, or <c>null</c> before initialization.
        /// </summary>
        /// <remarks>
        /// The subsystem owns them, so a provider asset holds no socket, no reconnect counter, and no
        /// connection state (plan §6.7). Open one with <see cref="OpenSseSession"/> or by registering a
        /// session a protocol assembly built.
        /// </remarks>
        public NetworkStreamSessionRegistry Streams => _streams;

        /// <summary>Whether a catalog was found and the routed stack can resolve routes.</summary>
        public bool HasCatalog => Snapshot.HasCatalog;

        /// <inheritdoc />
        public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
        {
            Snapshot = NetworkCatalogSnapshot.Capture(ReadCatalog());

            _routeStates = new NetworkRouteStateStore();
            _streams = new NetworkStreamSessionRegistry();
            _diagnostics = new NetworkDiagnosticStore(_routeStates, _diagnosticCapacity, _streams);
            _observers = new NetworkObserverDispatcher(_diagnostics);
            _credentials = new NetworkCredentialRegistry();
            _cache = new NetworkResponseCache(_responseCacheCapacity);

            var resolver = new NetworkRouteResolver(Snapshot);
            Resolver = resolver;

            _client = new RoutedHttpClient(resolver, _routeStates, _credentials, _diagnostics, _observers, _cache);

            RegisterBuiltInCredentialProviders();

            RuntimeManager.RegisterService<IRoutedHttpClient>(_client);
            RuntimeManager.RegisterService<INetworkDiagnostics>(_diagnostics);

            if (!Snapshot.HasCatalog)
            {
                // Not an error: a project that has not adopted the catalog yet keeps using the legacy
                // client. Routed sends fail with a typed Configuration outcome, which is more useful than
                // a subsystem that refuses to initialize.
                Debug.Log(
                    "[Network] No NetworkCatalog is registered on GlobalSettings. Routed requests will " +
                    "report a configuration error until one is added; the legacy HttpClient is unaffected.");
            }
            else
            {
                Debug.Log(
                    $"[Network] Routed networking ready: {Snapshot.Environments.Count} environment(s), " +
                    $"{Snapshot.Services.Count} service(s), default environment " +
                    $"'{(string.IsNullOrEmpty(Snapshot.DefaultEnvironmentId) ? "<none>" : Snapshot.DefaultEnvironmentId)}'.");
            }

            await base.InitializeAsync(cancellationToken);
        }

        /// <summary>
        /// Opens a Server-Sent Events session on a catalog route.
        /// </summary>
        /// <param name="id">Stable session id; usually the owning provider's id.</param>
        /// <param name="route">Where to connect.</param>
        /// <param name="reconnect">Reconnect settings, or <c>null</c> for the defaults.</param>
        /// <param name="transport">A transport to use instead of the default. Test seam.</param>
        /// <param name="pollIntervalSeconds">Seconds between receive-buffer polls.</param>
        /// <param name="directUri">
        /// An absolute URI to stream from when <paramref name="route"/> names no service. The
        /// compatibility path for a provider that still authors its own URL; it is outside the catalog's
        /// allowed-host, production-scheme, and credential-scope rules, because there is no service to
        /// read those from.
        /// </param>
        /// <param name="authHeaderName">
        /// Header to carry an auth-session token in when the catalog supplies no credential, or
        /// <c>null</c> to stay anonymous.
        /// </param>
        /// <param name="authScheme">Prefix prepended to that token, e.g. <c>"Bearer "</c>.</param>
        /// <returns>The session, already registered but <b>not started</b>, or <c>null</c> before init.</returns>
        /// <remarks>
        /// Returned unstarted so the caller decides how the session's lifetime is keyed — a provider keys
        /// it on its own activation token. Opening under an id that is already live closes the previous
        /// session first.
        /// </remarks>
        public SseStreamSession OpenSseSession(
            string id,
            NetworkStreamRoute route,
            StreamReconnectSettings reconnect = null,
            INetworkStreamTransport transport = null,
            float pollIntervalSeconds = 0.1f,
            string directUri = null,
            string authHeaderName = null,
            string authScheme = null)
        {
            if (_streams == null || Resolver == null)
            {
                Debug.LogError(
                    "[Network] Cannot open a stream session before NetworkRuntimeSubsystem has " +
                    "initialized. Add [DependsOn(typeof(NetworkRuntimeSubsystem))] to the calling subsystem.");
                return null;
            }

            var session = new SseStreamSession(
                id, route, Resolver, _credentials, transport, reconnect, pollIntervalSeconds,
                directUri, authHeaderName, authScheme);

            _streams.Open(session);
            return session;
        }

        /// <summary>
        /// Adopts a session a protocol assembly built, so the subsystem owns its lifetime.
        /// </summary>
        /// <param name="session">The session to adopt.</param>
        /// <returns>The same session, or <c>null</c> before init.</returns>
        /// <remarks>
        /// The seam the optional WebSocket and Socket.IO assemblies use: they can name
        /// <see cref="NetworkStreamSession"/> because it lives in the always-compiled assembly, while
        /// Core cannot name their socket types.
        /// </remarks>
        public NetworkStreamSession AdoptSession(NetworkStreamSession session)
        {
            if (_streams == null || session == null)
                return null;

            _streams.Open(session);
            return session;
        }

        /// <summary>
        /// Registers a credential provider, replacing any previous registration for its kind.
        /// </summary>
        /// <param name="provider">The provider to register.</param>
        /// <remarks>
        /// Call from a project subsystem that initializes after this one — declare
        /// <c>[DependsOn(typeof(NetworkRuntimeSubsystem))]</c> so the registry exists.
        /// </remarks>
        public void RegisterCredentialProvider(INetworkCredentialProvider provider)
        {
            if (_credentials == null)
            {
                Debug.LogError(
                    "[Network] Cannot register a credential provider before NetworkRuntimeSubsystem has " +
                    "initialized. Add [DependsOn(typeof(NetworkRuntimeSubsystem))] to the calling subsystem.");
                return;
            }
            _credentials.Register(provider);
        }

        /// <summary>
        /// Registers the providers Core can supply on its own.
        /// </summary>
        /// <remarks>
        /// Only <see cref="NetworkCredentialProviderKind.EnvironmentVariable"/> qualifies. The others need
        /// something Core does not own — a live auth session, editor secure storage, a platform key
        /// store — so they are left for the SDK, the project, or the editor layer to register.
        /// </remarks>
        private void RegisterBuiltInCredentialProviders()
        {
            _credentials.Register(new EnvironmentVariableCredentialProvider());
        }

        /// <summary>
        /// Reads the project's catalog from <see cref="GlobalSettings"/>.
        /// </summary>
        /// <returns>The catalog, or <c>null</c> when none is registered.</returns>
        /// <remarks>
        /// Guarded: <c>GetModule</c> dereferences project settings that may be absent in tests or a
        /// misconfigured bootstrap. A missing catalog degrades to unrouted rather than failing init.
        /// </remarks>
        private static NetworkCatalog ReadCatalog()
        {
            try
            {
                return GlobalSettings.GetModule<NetworkCatalog>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Network] Could not read NetworkCatalog from GlobalSettings: {e.Message}");
                return null;
            }
        }

        /// <inheritdoc />
        public override void Teardown()
        {
            // Dispose before clearing: cancelling the client's lifetime is what unwinds queued requests
            // and in-flight transport operations, and they touch the state cleared below.
            _client?.Dispose();
            _client = null;

            // Sessions first: closing them cancels reconnect loops that would otherwise keep asking the
            // credential registry and route states for things that are about to be cleared.
            _streams?.CloseAll();
            _streams = null;

            _credentials?.ClearCache();
            _cache?.Clear();
            _routeStates?.Clear();
            _observers?.Clear();
            _diagnostics?.Clear();

            Resolver = null;
            Snapshot = NetworkCatalogSnapshot.Empty;

            base.Teardown();
        }
    }
}
