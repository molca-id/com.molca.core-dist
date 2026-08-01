using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Networking.Compatibility;
using Molca.Networking.Configuration;
using Molca.Networking.Http.Models;
using Molca.Settings;

namespace Molca.Networking.Http
{
    /// <summary>
    /// HTTP subsystem. New code should use the <see cref="IHttpClient"/> instance API
    /// (resolve via <c>RuntimeManager.GetService&lt;IHttpClient&gt;()</c> or
    /// <c>GetSubsystem&lt;HttpClient&gt;()</c> cast to <see cref="IHttpClient"/>);
    /// the static members remain as obsolete compatibility shims.
    /// </summary>
    public class HttpClient : RuntimeSubsystem, IHttpClient
    {
        private static HttpClient _instance;
        private readonly Queue<HttpRequestContext> _requestQueue = new Queue<HttpRequestContext>();
        private readonly HashSet<HttpRequestContext> _activeRequests = new HashSet<HttpRequestContext>();
        private readonly Dictionary<string, string> _defaultHeaders = new Dictionary<string, string>();
        private readonly List<HttpRequestContext> _requestHistory = new List<HttpRequestContext>();
        private readonly object _queueLock = new object();
        private bool _isProcessingQueue = false;

        private IHttpTransport _transport = new UnityWebRequestTransport();
        private HttpRetryPolicy _retryPolicyOverride;
        // Backoff jitter source. Not cryptographic; only needs to de-correlate retry
        // timing across requests. Accessed from the single-threaded Awaitable retry loop.
        private readonly System.Random _retryRng = new System.Random();
        // Static so interceptors can register before/independently of subsystem init
        // (e.g., AuthManager initializing ahead of HttpClient in bootstrap order).
        private static readonly List<IHttpRequestInterceptor> _interceptors = new List<IHttpRequestInterceptor>();
        // Response interceptors (e.g. auth 401 recovery). Populated automatically from
        // AddInterceptorCore when a registered interceptor also implements
        // IHttpResponseInterceptor, so there is no separate registration API.
        private static readonly List<IHttpResponseInterceptor> _responseInterceptors = new List<IHttpResponseInterceptor>();
        // Cancelled (and replaced) by CancelAllRequests; every in-flight request's
        // linked token observes it, which aborts the underlying transport operation.
        private CancellationTokenSource _cancelAllCts = new CancellationTokenSource();

        private HttpModule _httpModule;

        // Catalog-derived compatibility state. Captured once at initialization, like the routed
        // subsystem's own snapshot: a request already in flight must not have its credential scope
        // change underneath it.
        private LegacyRouteMapper _routeMapper;
        private RoutedLegacyHttpAdapter _routedAdapter;
        private bool _routeLegacySends;

        // Compatibility reasons already reported. A leak warning is worth saying once per distinct
        // situation, not once per request — a polling call site would otherwise flood the console.
        private readonly HashSet<string> _reportedCompatibilityReasons = new HashSet<string>(StringComparer.Ordinal);

        // Instance events — the IHttpClient API. Backing fields are shared with
        // the obsolete static events via the Raise* helpers.
        private event Action<HttpRequestContext> _requestStarted;
        private event Action<HttpRequestContext> _requestCompleted;
        private event Action<HttpRequestContext> _requestFailed;
        private event Action<string> _connectionError;

        event Action<HttpRequestContext> IHttpClient.RequestStarted { add => _requestStarted += value; remove => _requestStarted -= value; }
        event Action<HttpRequestContext> IHttpClient.RequestCompleted { add => _requestCompleted += value; remove => _requestCompleted -= value; }
        event Action<HttpRequestContext> IHttpClient.RequestFailed { add => _requestFailed += value; remove => _requestFailed -= value; }
        event Action<string> IHttpClient.ConnectionError { add => _connectionError += value; remove => _connectionError -= value; }

        // Legacy static events (compat shims).
        [Obsolete("Use IHttpClient.RequestStarted (RuntimeManager.GetService<IHttpClient>()).")]
        public static event Action<HttpRequestContext> OnRequestStarted;
        [Obsolete("Use IHttpClient.RequestCompleted (RuntimeManager.GetService<IHttpClient>()).")]
        public static event Action<HttpRequestContext> OnRequestCompleted;
        [Obsolete("Use IHttpClient.RequestFailed (RuntimeManager.GetService<IHttpClient>()).")]
        public static event Action<HttpRequestContext> OnRequestFailed;
        [Obsolete("Use IHttpClient.ConnectionError (RuntimeManager.GetService<IHttpClient>()).")]
        public static event Action<string> OnConnectionError;

        // Instance properties (IHttpClient).
        string IHttpClient.BaseUrl => _httpModule?.BaseUrl ?? "";
        int IHttpClient.MaxConcurrentRequests => MaxConcurrentRequestsInternal;
        IReadOnlyList<HttpRequestContext> IHttpClient.RequestHistory => SnapshotHistory();
        int IHttpClient.ActiveRequestCount => _activeRequests.Count;

        // Snapshot under the lock so a diagnostics reader can't observe the list
        // mid-mutation (AppendHistory eviction) and throw during enumeration.
        private IReadOnlyList<HttpRequestContext> SnapshotHistory()
        {
            lock (_queueLock)
            {
                return new List<HttpRequestContext>(_requestHistory);
            }
        }

        private int MaxConcurrentRequestsInternal => _httpModule?.MaxConcurrentRequests ?? 4;

        // Legacy static properties (compat shims).
        [Obsolete("Use IHttpClient.BaseUrl (RuntimeManager.GetService<IHttpClient>()).")]
        public static string BaseUrl => _instance?._httpModule?.BaseUrl ?? "";
        [Obsolete("Use IHttpClient.MaxConcurrentRequests (RuntimeManager.GetService<IHttpClient>()).")]
        public static int MaxConcurrentRequests => _instance?.MaxConcurrentRequestsInternal ?? 4;
        [Obsolete("Use IHttpClient.RequestHistory (RuntimeManager.GetService<IHttpClient>()).")]
        public static IReadOnlyList<HttpRequestContext> RequestHistory => _instance?._requestHistory ?? new List<HttpRequestContext>();
        [Obsolete("Use IHttpClient.ActiveRequestCount (RuntimeManager.GetService<IHttpClient>()).")]
        public static int ActiveRequestCount => _instance != null ? _instance._activeRequests.Count : 0;

        // Single raise point for both the instance events and the obsolete static ones.
        private void RaiseRequestStarted(HttpRequestContext context)
        {
            _requestStarted?.Invoke(context);
#pragma warning disable CS0618
            OnRequestStarted?.Invoke(context);
#pragma warning restore CS0618
        }

        private void RaiseRequestCompleted(HttpRequestContext context)
        {
            _requestCompleted?.Invoke(context);
#pragma warning disable CS0618
            OnRequestCompleted?.Invoke(context);
#pragma warning restore CS0618
        }

        private void RaiseRequestFailed(HttpRequestContext context)
        {
            _requestFailed?.Invoke(context);
#pragma warning disable CS0618
            OnRequestFailed?.Invoke(context);
#pragma warning restore CS0618
        }

        private void RaiseConnectionError(string error)
        {
            _connectionError?.Invoke(error);
#pragma warning disable CS0618
            OnConnectionError?.Invoke(error);
#pragma warning restore CS0618
        }
        
        public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
        {
            _instance = this;

            // Get HTTP module from global settings. GetModule dereferences project
            // settings that may be absent (tests, misconfigured bootstrap) — degrade
            // to defaults rather than failing initialization.
            try
            {
                _httpModule = GlobalSettings.GetModule<HttpModule>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HttpClient] Could not read HttpModule from GlobalSettings: {e.Message}");
            }
            if (_httpModule == null)
            {
                Debug.LogWarning("HttpModule not found in GlobalSettings. Using default values.");
            }

            InitializeCompatibility();

            finishCallback?.Invoke(this);
        }

        /// <summary>
        /// Captures the network catalog and builds the legacy route mapper.
        /// </summary>
        /// <remarks>
        /// Reads <see cref="GlobalSettings"/> directly rather than depending on
        /// <c>NetworkRuntimeSubsystem</c>, so this works regardless of bootstrap order — the routed
        /// <em>client</em> is resolved lazily at send time instead, by which point every subsystem is up.
        /// A project without a catalog gets a mapper over
        /// <see cref="NetworkCatalogSnapshot.Empty"/>, which classifies everything as unrouted and
        /// changes no behaviour.
        /// </remarks>
        private void InitializeCompatibility()
        {
            NetworkCatalog catalog = null;
            try
            {
                catalog = GlobalSettings.GetModule<NetworkCatalog>();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HttpClient] Could not read NetworkCatalog from GlobalSettings: {e.Message}");
            }

            var snapshot = NetworkCatalogSnapshot.Capture(catalog);
            _routeMapper = new LegacyRouteMapper(snapshot, _httpModule?.BaseUrl);
            _routeLegacySends = snapshot.RouteLegacyHttpThroughPipeline;

            if (snapshot.HasCatalog && snapshot.AllowLegacyGlobalAuthOnExternalUrls)
            {
                Debug.LogWarning(
                    "[HttpClient] The network catalog has AllowLegacyGlobalAuthOnExternalUrls enabled, so " +
                    "process-wide credentials still travel to hosts no service claims. This is a " +
                    "transition setting — author those hosts as services and turn it off.",
                    catalog);
            }
        }

        /// <summary>
        /// Overrides the catalog-derived compatibility configuration. Test seam.
        /// </summary>
        /// <param name="mapper">The mapper to use; <c>null</c> restores an empty (no-catalog) mapper.</param>
        /// <param name="routeLegacySends">Whether mapped requests execute on the routed pipeline.</param>
        /// <param name="adapter">
        /// The routed adapter to use; <c>null</c> resolves <see cref="IRoutedHttpClient"/> from
        /// <c>RuntimeManager</c> on first use, which is what production does.
        /// </param>
        /// <remarks>
        /// Internal because it exists so edit-mode tests can exercise catalog-dependent behaviour without
        /// a project fixture; production configuration always arrives through the catalog asset.
        /// </remarks>
        internal void ConfigureCompatibility(
            LegacyRouteMapper mapper,
            bool routeLegacySends,
            RoutedLegacyHttpAdapter adapter = null)
        {
            _routeMapper = mapper ?? new LegacyRouteMapper(NetworkCatalogSnapshot.Empty, _httpModule?.BaseUrl);
            _routeLegacySends = routeLegacySends;
            _routedAdapter = adapter;
            _reportedCompatibilityReasons.Clear();
        }
        
        #region Instance API (IHttpClient)

        // Explicit implementations: C# forbids a static and an instance method with
        // the same signature on one type, and the legacy statics must survive
        // (protected-zone rule), so the instance API lives on the interface.

        Awaitable<HttpResponse> IHttpClient.SendAsync(HttpRequest request, CancellationToken cancellationToken)
            => SendAsyncCore(request, cancellationToken);

        void IHttpClient.Send(HttpRequest request, CancellationToken cancellationToken,
            Action<HttpResponse> onSuccess, Action<string> onError, Action<float> onProgress)
            => SendCore(request, cancellationToken, onSuccess, onError, onProgress);

        HttpRequestBuilder IHttpClient.CreateRequest() => new HttpRequestBuilder();

        void IHttpClient.AddDefaultHeader(string key, string value) => AddDefaultHeaderCore(key, value);
        void IHttpClient.RemoveDefaultHeader(string key) => RemoveDefaultHeaderCore(key);
        void IHttpClient.ClearDefaultHeaders() => ClearDefaultHeadersCore();
        void IHttpClient.CancelAllRequests() => CancelAllActiveRequests();
        void IHttpClient.AddInterceptor(IHttpRequestInterceptor interceptor) => AddInterceptorCore(interceptor);
        void IHttpClient.RemoveInterceptor(IHttpRequestInterceptor interceptor) => RemoveInterceptorCore(interceptor);
        void IHttpClient.SetRetryPolicy(HttpRetryPolicy policy) => _retryPolicyOverride = policy;
        void IHttpClient.SetTransport(IHttpTransport transport) => _transport = transport ?? new UnityWebRequestTransport();
        void IHttpClient.ClearHistory() => ClearHistoryCore();

        private void ClearHistoryCore()
        {
            lock (_queueLock)
            {
                _requestHistory.Clear();
            }
        }

        private async Awaitable<HttpResponse> SendAsyncCore(HttpRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = new HttpRequestContext(request) { CallerToken = cancellationToken };
            var tcs = new AwaitableCompletionSource<HttpResponse>();

            context.OnCompleted += (response) => tcs.TrySetResult(response);
            // Cancel() (validation failure, exception, token cancel) fires OnError/OnFailed
            // without OnCompleted — resolve the awaitable there too so callers never hang.
            // Complete() also raises OnError on HTTP failures (with context.response set,
            // followed by OnCompleted), so only synthesize a response on the cancel path.
            context.OnError += (error) =>
            {
                if (context.response != null)
                    return;
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled();
                    return;
                }
                tcs.TrySetResult(new HttpResponse
                {
                    isSuccess = false,
                    errorMessage = error,
                    statusMessage = error
                });
            };

            // Queue the request (this starts processing but doesn't wait for completion)
            QueueRequest(context);

            // Wait for the request to complete and return the response
            return await tcs.Awaitable;
        }

        private void SendCore(HttpRequest request, CancellationToken cancellationToken,
            Action<HttpResponse> onSuccess, Action<string> onError, Action<float> onProgress)
        {
            var context = new HttpRequestContext(request) { CallerToken = cancellationToken };
            context.OnSuccess += onSuccess;
            context.OnError += onError;
            context.OnProgress += onProgress;

            QueueRequest(context);
        }

        private void AddDefaultHeaderCore(string key, string value)
        {
            if (_httpModule != null)
                _httpModule.SetDefaultHeader(key, value);
            else
                _defaultHeaders[key] = value;
        }

        private void RemoveDefaultHeaderCore(string key)
        {
            if (_httpModule != null)
                _httpModule.RemoveDefaultHeader(key);
            else
                _defaultHeaders.Remove(key);
        }

        private void ClearDefaultHeadersCore()
        {
            if (_httpModule != null)
                _httpModule.ClearDefaultHeaders();
            else
                _defaultHeaders.Clear();
        }

        // The interceptor list is static so interceptors can register before or
        // independently of subsystem init (e.g., AuthManager ahead of HttpClient in
        // bootstrap order); these internal entry points exist for exactly that case.
        internal static void AddInterceptorCore(IHttpRequestInterceptor interceptor)
        {
            if (interceptor == null)
                return;
            if (!_interceptors.Contains(interceptor))
                _interceptors.Add(interceptor);
            // Dual-role interceptors (e.g. AuthTokenInterceptor) are registered for
            // response callbacks too, via the single AddInterceptor entry point.
            if (interceptor is IHttpResponseInterceptor responder && !_responseInterceptors.Contains(responder))
                _responseInterceptors.Add(responder);
        }

        internal static void RemoveInterceptorCore(IHttpRequestInterceptor interceptor)
        {
            _interceptors.Remove(interceptor);
            if (interceptor is IHttpResponseInterceptor responder)
                _responseInterceptors.Remove(responder);
        }

        /// <summary>
        /// In-assembly plumbing accessor for the live instance (null before init /
        /// after teardown). Lets legacy convenience wrappers reach the instance API
        /// without going through the obsolete static shims.
        /// </summary>
        internal static IHttpClient Current => _instance;

        #endregion

        #region Legacy static API (obsolete shims)

        /// <summary>
        /// Sends an HTTP request asynchronously.
        /// </summary>
        [Obsolete("Use IHttpClient.SendAsync (RuntimeManager.GetService<IHttpClient>()).")]
        public static Awaitable<HttpResponse> SendAsync(HttpRequest request)
            => SendAsync(request, CancellationToken.None);

        /// <summary>
        /// Sends an HTTP request asynchronously. Cancelling the token aborts the
        /// in-flight request (or removes it from the pending queue) and the returned
        /// awaitable completes as cancelled.
        /// </summary>
        /// <param name="request">The request to send.</param>
        /// <param name="cancellationToken">Aborts the request when cancelled.</param>
        /// <exception cref="OperationCanceledException">The token was cancelled before completion.</exception>
        [Obsolete("Use IHttpClient.SendAsync (RuntimeManager.GetService<IHttpClient>()).")]
        public static Awaitable<HttpResponse> SendAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            if (_instance == null)
                throw new InvalidOperationException("HttpClient is not initialized");
            return _instance.SendAsyncCore(request, cancellationToken);
        }

        /// <summary>
        /// Sends an HTTP request with callbacks
        /// </summary>
        [Obsolete("Use IHttpClient.Send (RuntimeManager.GetService<IHttpClient>()).")]
        public static void Send(HttpRequest request,
            Action<HttpResponse> onSuccess = null,
            Action<string> onError = null,
            Action<float> onProgress = null)
            => Send(request, CancellationToken.None, onSuccess, onError, onProgress);

        /// <summary>
        /// Sends an HTTP request with callbacks. Cancelling the token aborts the
        /// in-flight request (or removes it from the pending queue); <paramref name="onError"/>
        /// is invoked with the cancellation reason.
        /// </summary>
        [Obsolete("Use IHttpClient.Send (RuntimeManager.GetService<IHttpClient>()).")]
        public static void Send(HttpRequest request,
            CancellationToken cancellationToken,
            Action<HttpResponse> onSuccess = null,
            Action<string> onError = null,
            Action<float> onProgress = null)
        {
            if (_instance == null)
            {
                onError?.Invoke("HttpClient is not initialized");
                return;
            }
            _instance.SendCore(request, cancellationToken, onSuccess, onError, onProgress);
        }

        /// <summary>
        /// Creates a new HTTP request builder
        /// </summary>
        [Obsolete("Use IHttpClient.CreateRequest (RuntimeManager.GetService<IHttpClient>()), or new HttpRequestBuilder().")]
        public static HttpRequestBuilder CreateRequest()
        {
            return new HttpRequestBuilder();
        }

        /// <summary>
        /// Adds a default header that will be included in all requests
        /// </summary>
        [Obsolete("Use IHttpClient.AddDefaultHeader (RuntimeManager.GetService<IHttpClient>()).")]
        public static void AddDefaultHeader(string key, string value)
        {
            _instance?.AddDefaultHeaderCore(key, value);
        }

        /// <summary>
        /// Removes a default header
        /// </summary>
        [Obsolete("Use IHttpClient.RemoveDefaultHeader (RuntimeManager.GetService<IHttpClient>()).")]
        public static void RemoveDefaultHeader(string key)
        {
            _instance?.RemoveDefaultHeaderCore(key);
        }

        /// <summary>
        /// Clears all default headers
        /// </summary>
        [Obsolete("Use IHttpClient.ClearDefaultHeaders (RuntimeManager.GetService<IHttpClient>()).")]
        public static void ClearDefaultHeaders()
        {
            _instance?.ClearDefaultHeadersCore();
        }

        /// <summary>
        /// Cancels all active requests and clears the pending queue.
        /// </summary>
        [Obsolete("Use IHttpClient.CancelAllRequests (RuntimeManager.GetService<IHttpClient>()).")]
        public static void CancelAllRequests()
        {
            _instance?.CancelAllActiveRequests();
        }

        /// <summary>
        /// Registers an interceptor invoked on every outgoing request's prepared
        /// clone, just before transport send. Registration order = invocation order.
        /// </summary>
        [Obsolete("Use IHttpClient.AddInterceptor (RuntimeManager.GetService<IHttpClient>()).")]
        public static void AddInterceptor(IHttpRequestInterceptor interceptor)
        {
            AddInterceptorCore(interceptor);
        }

        /// <summary>Removes a previously registered interceptor. No-op if absent.</summary>
        [Obsolete("Use IHttpClient.RemoveInterceptor (RuntimeManager.GetService<IHttpClient>()).")]
        public static void RemoveInterceptor(IHttpRequestInterceptor interceptor)
        {
            RemoveInterceptorCore(interceptor);
        }

        /// <summary>
        /// Overrides the retry policy normally sourced from <see cref="HttpModule"/>.
        /// Pass <c>null</c> to restore module-driven configuration. Test seam and
        /// escape hatch for code running before settings are available.
        /// </summary>
        [Obsolete("Use IHttpClient.SetRetryPolicy (RuntimeManager.GetService<IHttpClient>()).")]
        public static void SetRetryPolicy(HttpRetryPolicy policy)
        {
            if (_instance != null)
                _instance._retryPolicyOverride = policy;
        }

        /// <summary>
        /// Replaces the transport used to execute requests. Test seam — production
        /// code should keep the default <see cref="UnityWebRequestTransport"/>.
        /// </summary>
        /// <param name="transport">The transport to use; <c>null</c> restores the default.</param>
        [Obsolete("Use IHttpClient.SetTransport (RuntimeManager.GetService<IHttpClient>()).")]
        public static void SetTransport(IHttpTransport transport)
        {
            if (_instance != null)
                _instance._transport = transport ?? new UnityWebRequestTransport();
        }

        /// <summary>
        /// Clears the request history
        /// </summary>
        [Obsolete("Use IHttpClient.ClearHistory (RuntimeManager.GetService<IHttpClient>()).")]
        public static void ClearHistory()
        {
            _instance?.ClearHistoryCore();
        }

        #endregion
        
        #region Private Methods
        
        private void QueueRequest(HttpRequestContext context)
        {
            // Validate request
            if (!context.request.Validate(out var errors))
            {
                context.Cancel($"Validation failed: {string.Join(", ", errors)}");
                RaiseRequestFailed(context);
                return;
            }
            
            lock (_queueLock)
            {
                _requestQueue.Enqueue(context);
            }
            
            // Start processing the queue
            ProcessQueue();
        }
        
        private void ProcessQueue()
        {
            // Prevent multiple concurrent queue processing
            if (_isProcessingQueue)
                return;
                
            lock (_queueLock)
            {
                if (_isProcessingQueue)
                    return;
                _isProcessingQueue = true;
            }
            
            try
            {
                int maxConcurrent = _httpModule?.MaxConcurrentRequests ?? 4;
                
                // Process as many requests as we can start immediately
                bool canStartMoreRequests = true;
                while (canStartMoreRequests)
                {
                    HttpRequestContext context = null;
                    
                    lock (_queueLock)
                    {
                        // Check if we can start more requests
                        canStartMoreRequests = _activeRequests.Count < maxConcurrent && _requestQueue.Count > 0;
                        
                        if (canStartMoreRequests)
                        {
                            context = _requestQueue.Dequeue();
                        }
                    }
                    
                    if (context != null)
                    {
                        // The caller may have cancelled while the request sat in the queue.
                        if (context.CallerToken.IsCancellationRequested)
                        {
                            context.Cancel("Request cancelled");
                            RaiseRequestFailed(context);
                            continue;
                        }

                        lock (_queueLock)
                        {
                            _activeRequests.Add(context);
                        }

                        RaiseRequestStarted(context);

                        // Start the request without awaiting it to allow concurrent processing
                        _ = SendRequest(context);
                    }
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _isProcessingQueue = false;
                }
            }
        }
        
        /// <summary>
        /// Builds the request actually handed to the transport: a clone of the caller's
        /// request with default headers merged in (request headers win) and the timeout
        /// resolved. Cloning keeps the transport from observing later caller mutations
        /// and keeps requests sourced from ScriptableObject assets untouched.
        /// </summary>
        /// <param name="request">The caller's request. Never mutated.</param>
        /// <param name="context">The context tracking this request.</param>
        /// <param name="decision">
        /// How the network catalog classifies this request's destination. When it withholds credentials,
        /// process-wide credential contributions are skipped — see <see cref="IHttpCredentialInterceptor"/>.
        /// </param>
        private HttpRequest PrepareRequest(
            HttpRequest request,
            HttpRequestContext context,
            LegacyRouteDecision decision)
        {
            var prepared = request.Clone();
            bool allowCredentials = decision.AllowsGlobalCredentials;

            if (_httpModule != null)
            {
                foreach (var header in _httpModule.GetDefaultHeaders())
                {
                    if (!allowCredentials && IsCredentialHeader(header.Key))
                        continue;
                    if (prepared.GetHeaderValue(header.Key) == null)
                        prepared.AddHeader(header.Key, header.Value);
                }
            }

            foreach (var header in _defaultHeaders)
            {
                if (!allowCredentials && IsCredentialHeader(header.Key))
                    continue;
                if (prepared.GetHeaderValue(header.Key) == null)
                    prepared.AddHeader(header.Key, header.Value);
            }

            if (prepared.timeout <= 0)
                prepared.timeout = _httpModule?.DefaultTimeout ?? 30;

            // Interceptors see the clone, never the caller's request instance.
            // Context-aware interceptors also get the request context so they can
            // stash per-request state (e.g. which auth token was actually sent).
            foreach (var interceptor in _interceptors)
            {
                // A credential interceptor is process-wide and cannot tell "our backend" from a
                // third-party host, so the catalog decides for it. Everything else always runs.
                if (!allowCredentials && interceptor is IHttpCredentialInterceptor)
                    continue;

                try
                {
                    if (interceptor is IHttpContextAwareRequestInterceptor contextAware)
                        contextAware.OnRequestPrepared(context, prepared);
                    else
                        interceptor.OnRequestPrepared(prepared);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[HttpClient] Interceptor {interceptor.GetType().Name} threw: {e.Message}");
                }
            }

            return prepared;
        }

        /// <summary>Whether a header name carries a credential, per the catalog and the well-known set.</summary>
        private bool IsCredentialHeader(string headerName) =>
            _routeMapper != null && _routeMapper.IsCredentialHeader(headerName);

        /// <summary>
        /// Classifies a request's destination against the network catalog, reporting any compatibility
        /// concern once.
        /// </summary>
        /// <param name="request">The request about to be sent.</param>
        /// <returns>The decision; unrouted with credentials allowed when no mapper exists yet.</returns>
        private LegacyRouteDecision ClassifyDestination(HttpRequest request)
        {
            if (_routeMapper == null)
                return LegacyRouteDecision.Unrouted();

            var decision = _routeMapper.Map(request);

            if (!string.IsNullOrEmpty(decision.Reason) &&
                _reportedCompatibilityReasons.Add(decision.Reason))
            {
                Debug.LogWarning($"[HttpClient] {decision.Reason}");
            }

            return decision;
        }

        /// <summary>
        /// The routed adapter, resolved on first use.
        /// </summary>
        /// <returns>The adapter, or <c>null</c> when no routed client is registered.</returns>
        /// <remarks>
        /// Resolved lazily rather than at initialization so bootstrap order between this subsystem and
        /// <c>NetworkRuntimeSubsystem</c> does not matter. A missing routed client is not an error: the
        /// legacy path runs instead, which is what an unconfigured project needs.
        /// </remarks>
        private RoutedLegacyHttpAdapter ResolveRoutedAdapter()
        {
            if (_routedAdapter != null)
                return _routedAdapter;

            var routed = RuntimeManager.GetService<IRoutedHttpClient>();
            if (routed == null)
                return null;

            _routedAdapter = new RoutedLegacyHttpAdapter(routed);
            return _routedAdapter;
        }

        private async Awaitable SendRequest(HttpRequestContext context)
        {
            // Linked lifetime: caller token + CancelAllRequests + subsystem shutdown.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                context.CallerToken, _cancelAllCts.Token, ShutdownToken);
            // Monotonic duration source: DateTime.Now can jump (NTP sync, DST) and is
            // slow; Stopwatch measures the exchange without either problem.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Debug.Log($"[HttpClient] Sending {context.request.method} request to {Molca.Networking.Utils.LogRedaction.RedactUrl(context.request.FullUrl)}");

                // Classified once per request, not per attempt: the catalog snapshot is fixed, and a
                // credential must not become permitted partway through a retry sequence.
                var decision = ClassifyDestination(context.request);

                var prepared = PrepareRequest(context.request, context, decision);
                var response = await ExecuteAsync(prepared, context, decision, cts.Token);

                // Response interceptors (e.g. auth 401 recovery) may ask for one
                // transparent retry. Capped at a single retry to prevent loops; the
                // request is re-prepared so the auth interceptor re-injects the
                // refreshed token.
                if (await ShouldRetryAfterResponseAsync(context, response, cts.Token))
                {
                    var retryPrepared = PrepareRequest(context.request, context, decision);
                    response = await ExecuteAsync(retryPrepared, context, decision, cts.Token);
                }

                // Wall-clock duration of the whole exchange (incl. retries), in seconds.
                response.responseTime = (float)stopwatch.Elapsed.TotalSeconds;
                context.Complete(response);
                
                if (response.isSuccess)
                {
                    RaiseRequestCompleted(context);
                }
                else
                {
                    RaiseRequestFailed(context);
                    RaiseConnectionError(response.errorMessage);
                }
                
                // Add to history as a capped ring: oldest entries are evicted once the
                // module's MaxHistorySize is reached so a long session can't grow it
                // without bound. Guarded by _queueLock because RequestHistory is read by
                // diagnostics surfaces (e.g. the Hub Network section) off this mutation.
                if (_httpModule?.EnableRequestHistory == true)
                {
                    AppendHistory(context);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not an error — no error log, no connection-error event.
                context.Cancel("Request cancelled");
                RaiseRequestFailed(context);
            }
            catch (Exception e)
            {
                Debug.LogError($"[HttpClient] Request failed: {e.Message}");
                context.Cancel($"Exception: {e.Message}");
                RaiseRequestFailed(context);
                RaiseConnectionError(e.Message);
            }
            finally
            {
                lock (_queueLock)
                {
                    _activeRequests.Remove(context);
                }

                // Process next request
                ProcessQueue();
            }
        }
        
        /// <summary>
        /// Executes a prepared request, on the routed pipeline when the catalog opts in and the request
        /// maps to a route, and on the legacy retry loop otherwise.
        /// </summary>
        /// <param name="prepared">The prepared per-send clone.</param>
        /// <param name="context">The context tracking this request.</param>
        /// <param name="decision">How the catalog classifies the destination.</param>
        /// <param name="token">Linked caller/cancel-all/shutdown token.</param>
        /// <returns>The response.</returns>
        /// <remarks>
        /// Only the transport-and-retry middle moves. The caller still owns the context, the events, the
        /// history entry, and the response interceptors, so turning the routed pipeline on changes nothing
        /// observable about the <see cref="IHttpClient"/> surface.
        /// </remarks>
        private async Awaitable<HttpResponse> ExecuteAsync(
            HttpRequest prepared,
            HttpRequestContext context,
            LegacyRouteDecision decision,
            CancellationToken token)
        {
            if (_routeLegacySends && decision.Kind == LegacyRouteKind.Routed)
            {
                var adapter = ResolveRoutedAdapter();
                if (adapter != null)
                {
                    return await adapter.SendAsync(
                        decision.Route, decision.RelativePath, prepared, token);
                }

                // Configured to route but the routed subsystem is absent. Fall through rather than
                // failing the send — but say so, because the configuration is not being honoured.
                if (_reportedCompatibilityReasons.Add("routed-client-missing"))
                {
                    Debug.LogWarning(
                        "[HttpClient] The catalog sets RouteLegacyHttpThroughPipeline, but no " +
                        "IRoutedHttpClient is registered. Add NetworkRuntimeSubsystem to the bootstrap; " +
                        "sends run on the legacy pipeline until then.");
                }
            }

            return await SendWithRetry(prepared, context, token);
        }

        /// <summary>
        /// Sends the prepared request, retrying idempotent requests on transient
        /// failures per the configured <see cref="HttpRetryPolicy"/> with exponential
        /// backoff. Cancellation aborts immediately; the last failure (response or
        /// exception) is surfaced unchanged once attempts are exhausted.
        /// </summary>
        private async Awaitable<HttpResponse> SendWithRetry(HttpRequest prepared, HttpRequestContext context, CancellationToken token)
        {
            var policy = _retryPolicyOverride ?? HttpRetryPolicy.FromModule(_httpModule);
            int maxAttempts = policy.Enabled && IsIdempotent(prepared.method)
                ? policy.MaxRetries + 1
                : 1;

            for (int attempt = 1; ; attempt++)
            {
                HttpResponse response;
                try
                {
                    response = await _transport.SendAsync(prepared, context.UpdateProgress, token);
                }
                catch (OperationCanceledException)
                {
                    throw; // never retry cancellation
                }
                catch (Exception e) when (attempt < maxAttempts)
                {
                    Debug.LogWarning($"[HttpClient] Attempt {attempt}/{maxAttempts} threw ({e.GetType().Name}); retrying.");
                    response = null;
                }

                // Retryability is decided from the structured HttpError kind, not a
                // status list. A null response means the transport threw — treated as
                // a transient failure and retried until attempts are exhausted.
                if (response != null && (response.isSuccess || !policy.IsRetryable(response.Error) || attempt >= maxAttempts))
                    return response;

                // A server-directed Retry-After (429/503) overrides the computed
                // backoff — the server told us exactly when it's willing to talk again.
                float delay = TryGetRetryAfterSeconds(response, out float retryAfter)
                    ? Mathf.Min(retryAfter, MaxRetryAfterSeconds)
                    : policy.ComputeBackoffDelay(attempt, _retryRng);
                if (delay > 0f)
                    await Awaitable.WaitForSecondsAsync(delay, token);
                token.ThrowIfCancellationRequested();
            }
        }

        // Upper bound on an honored Retry-After so a hostile/buggy server can't park
        // a retry loop for hours.
        private const float MaxRetryAfterSeconds = 60f;

        /// <summary>
        /// Reads a <c>Retry-After</c> header from a 429/503 response, supporting both
        /// the delta-seconds and HTTP-date forms (RFC 9110 §10.2.3). Internal for tests.
        /// </summary>
        internal static bool TryGetRetryAfterSeconds(HttpResponse response, out float seconds)
        {
            seconds = 0f;
            if (response == null || (response.statusCode != 429 && response.statusCode != 503))
                return false;

            string value = response.GetHeaderValue("Retry-After");
            if (string.IsNullOrEmpty(value))
                return false;

            if (int.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int delta))
            {
                seconds = Mathf.Max(0, delta);
                return true;
            }

            if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var when))
            {
                seconds = Mathf.Max(0f, (float)(when - DateTimeOffset.UtcNow).TotalSeconds);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Runs the registered response interceptors after a response and returns whether
        /// one asked for a single transparent retry. The first <see cref="ResponseAction.RetryOnce"/>
        /// wins; an interceptor that throws is logged and skipped. Called at most once per
        /// request, so a retry can happen at most once (no loop).
        /// </summary>
        private async Awaitable<bool> ShouldRetryAfterResponseAsync(HttpRequestContext context, HttpResponse response, CancellationToken token)
        {
            if (_responseInterceptors.Count == 0)
                return false;

            // Snapshot so a concurrent (un)register can't disturb the iteration.
            var responders = _responseInterceptors.ToArray();
            foreach (var responder in responders)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var action = await responder.OnResponseReceivedAsync(context, response, token);
                    if (action == ResponseAction.RetryOnce)
                        return true;
                }
                catch (OperationCanceledException)
                {
                    throw; // cancellation is not an error
                }
                catch (Exception e)
                {
                    Debug.LogError($"[HttpClient] Response interceptor {responder.GetType().Name} threw: {e.Message}");
                }
            }
            return false;
        }

        private static bool IsIdempotent(HttpMethod method)
        {
            switch (method)
            {
                case HttpMethod.GET:
                case HttpMethod.HEAD:
                case HttpMethod.OPTIONS:
                case HttpMethod.PUT:
                case HttpMethod.DELETE:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Appends a completed request to the bounded history ring, evicting the
        /// oldest entries once <see cref="HttpModule.MaxHistorySize"/> is reached.
        /// </summary>
        private void AppendHistory(HttpRequestContext context)
        {
            int maxHistory = _httpModule?.MaxHistorySize ?? 100;
            // History stores a redaction-applied snapshot, never the live context:
            // the raw context keeps login bodies (passwords) and bearer headers
            // reachable in plaintext for the whole session.
            var snapshot = context.CreateRedactedSnapshot();
            lock (_queueLock)
            {
                if (maxHistory <= 0)
                {
                    _requestHistory.Clear();
                    return;
                }

                _requestHistory.Add(snapshot);
                // Evict from the front until within the cap (handles a shrunk MaxHistorySize too).
                while (_requestHistory.Count > maxHistory)
                {
                    _requestHistory.RemoveAt(0);
                }
            }
        }

        private void CancelAllActiveRequests()
        {
            List<HttpRequestContext> pending;
            lock (_queueLock)
            {
                pending = new List<HttpRequestContext>(_requestQueue);
                _requestQueue.Clear();
            }

            // Cancel queued requests that never started.
            foreach (var context in pending)
            {
                context.Cancel("Request cancelled");
                RaiseRequestFailed(context);
            }

            // Abort everything in flight via the shared token, then arm a fresh
            // source so subsequent requests are unaffected.
            var old = _cancelAllCts;
            _cancelAllCts = new CancellationTokenSource();
            old.Cancel();
            old.Dispose();
        }

        public override void Teardown()
        {
            // ShutdownToken (linked per request) aborts in-flight work; drain the queue too.
            CancelAllActiveRequests();
            // Drop the routed client reference: it belongs to NetworkRuntimeSubsystem, which disposes it
            // on its own teardown, and holding a disposed client across a domain reload would throw.
            _routedAdapter = null;
            _reportedCompatibilityReasons.Clear();
            // Drop the legacy-shim singleton so a torn-down subsystem can't be reached.
            if (_instance == this)
                _instance = null;
            base.Teardown();
        }

        #endregion
    }
    
    /// <summary>
    /// Fluent API builder for HTTP requests
    /// </summary>
    public class HttpRequestBuilder
    {
        private readonly HttpRequest _request = new HttpRequest();
        
        public HttpRequestBuilder Method(HttpMethod method)
        {
            _request.method = method;
            return this;
        }
        
        public HttpRequestBuilder Url(string url)
        {
            _request.url = url;
            return this;
        }
        
        public HttpRequestBuilder FullUrl(string url)
        {
            _request.url = url;
            _request.useFullUrl = true;
            return this;
        }
        
        public HttpRequestBuilder Header(string key, string value)
        {
            _request.AddHeader(key, value);
            return this;
        }
        
        public HttpRequestBuilder Param(string key, string value)
        {
            _request.AddParam(key, value);
            return this;
        }
        
        public HttpRequestBuilder JsonBody(string json)
        {
            _request.SetJsonBody(json);
            return this;
        }
        
        public HttpRequestBuilder JsonBody<T>(T obj) where T : class
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(obj);
            _request.SetJsonBody(json);
            return this;
        }
        
        public HttpRequestBuilder FormField(string key, string value)
        {
            _request.AddFormField(key, value);
            return this;
        }
        
        public HttpRequestBuilder BinaryField(string key, byte[] data, string filename = "")
        {
            _request.AddBinaryField(key, data, filename);
            return this;
        }
        
        public HttpRequestBuilder Timeout(int seconds)
        {
            _request.timeout = seconds;
            return this;
        }
        
        public HttpRequestBuilder ResponseType(ResponseType type)
        {
            _request.expectedResponseType = type;
            return this;
        }
        
        public HttpRequest Build()
        {
            return _request.Clone();
        }
        
        // The builder's convenience senders route through the live instance, with the
        // same not-initialized behavior the legacy static shims had.
        private static IHttpClient ResolveClient() =>
            HttpClient.Current ?? throw new InvalidOperationException("HttpClient is not initialized");

        public async Awaitable<HttpResponse> SendAsync()
        {
            return await ResolveClient().SendAsync(_request);
        }

        /// <summary>Sends the built request; cancelling the token aborts it.</summary>
        public async Awaitable<HttpResponse> SendAsync(CancellationToken cancellationToken)
        {
            return await ResolveClient().SendAsync(_request, cancellationToken);
        }

        public void Send(Action<HttpResponse> onSuccess = null, Action<string> onError = null)
        {
            var http = HttpClient.Current;
            if (http == null)
            {
                onError?.Invoke("HttpClient is not initialized");
                return;
            }
            http.Send(_request, onSuccess: onSuccess, onError: onError);
        }
    }
} 