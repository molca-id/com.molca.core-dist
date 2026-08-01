using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using Molca.Networking.Configuration;
using Molca.Networking.Diagnostics;
using Molca.Networking.Http;
using Molca.Networking.Http.Models;
using Molca.Networking.Pipeline;
using Molca.Networking.Routing;
using Molca.Networking.Security;

namespace Molca.Networking
{
    /// <summary>
    /// The routed HTTP pipeline. Resolves a route, freezes the request, then runs the documented
    /// pipeline: host and production validation, default headers, scoped credentials, cache, per-route
    /// bulkhead, attempts with typed classification, redirect and retry evaluation, circuit and cache
    /// updates, redacted diagnostics, and isolated observer notification.
    /// </summary>
    /// <remarks>
    /// Owned and disposed by <see cref="NetworkRuntimeSubsystem"/>. A plain C# service, not a
    /// MonoBehaviour — the subsystem is the Unity-lifecycle object.
    /// <para>
    /// Two deliberate departures from the legacy client:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Redirects are followed here, not by the transport.</b> The transport request always carries
    /// <c>followRedirects = false</c>, because <c>UnityWebRequest</c>'s internal following gives no
    /// opportunity to revalidate the target host or strip a credential — which is exactly what
    /// plan §6.6 requires on a cross-origin 3xx.
    /// </description></item>
    /// <item><description>
    /// <b>The attempt timeout is clamped to the remaining overall budget.</b> That is what makes the
    /// overall deadline include queueing, authentication, and retry delays rather than only wire time.
    /// </description></item>
    /// </list>
    /// <para>
    /// Main-thread only, matching the rest of the subsystem surface.
    /// </para>
    /// </remarks>
    public sealed class RoutedHttpClient : IRoutedHttpClient, IDisposable
    {
        /// <summary>Header carrying a caller-supplied idempotency key.</summary>
        public const string IdempotencyKeyHeader = "Idempotency-Key";

        private readonly NetworkRouteStateStore _routeStates;
        private readonly NetworkCredentialRegistry _credentials;
        private readonly NetworkDiagnosticStore _diagnostics;
        private readonly NetworkObserverDispatcher _observers;
        private readonly NetworkResponseCache _cache;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        // Backoff jitter. Not cryptographic — it only needs to de-correlate retry timing so clients that
        // failed together do not retry in lockstep.
        private readonly System.Random _jitter = new System.Random();

        private IHttpTransport _transport = new UnityWebRequestTransport();
        private bool _disposed;

        /// <inheritdoc />
        public INetworkRouteResolver Resolver { get; }

        /// <summary>Creates a client.</summary>
        /// <param name="resolver">The route resolver, reading an immutable snapshot.</param>
        /// <param name="routeStates">Per-route bulkhead and circuit state.</param>
        /// <param name="credentials">Credential registry enforcing scope.</param>
        /// <param name="diagnostics">Redacted diagnostic store.</param>
        /// <param name="observers">Observer dispatcher.</param>
        /// <param name="cache">Response cache.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <c>null</c>.</exception>
        public RoutedHttpClient(
            INetworkRouteResolver resolver,
            NetworkRouteStateStore routeStates,
            NetworkCredentialRegistry credentials,
            NetworkDiagnosticStore diagnostics,
            NetworkObserverDispatcher observers,
            NetworkResponseCache cache)
        {
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _routeStates = routeStates ?? new NetworkRouteStateStore();
            _credentials = credentials ?? new NetworkCredentialRegistry();
            _diagnostics = diagnostics;
            _observers = observers ?? new NetworkObserverDispatcher(diagnostics);
            _cache = cache ?? new NetworkResponseCache();
        }

        /// <inheritdoc />
        public void AddObserver(IRoutedHttpObserver observer) => _observers.Add(observer);

        /// <inheritdoc />
        public void RemoveObserver(IRoutedHttpObserver observer) => _observers.Remove(observer);

        /// <inheritdoc />
        public void SetTransport(IHttpTransport transport) =>
            _transport = transport ?? new UnityWebRequestTransport();

        /// <inheritdoc />
        public Awaitable<RoutedHttpOutcome> SendToServiceAsync(
            string serviceId,
            HttpRequest request,
            NetworkRouteQuery query = default,
            CancellationToken cancellationToken = default)
        {
            string environmentId = Resolver.Snapshot.DefaultEnvironmentId;

            if (string.IsNullOrEmpty(environmentId) || string.IsNullOrEmpty(serviceId))
            {
                return Completed(UnresolvedOutcome(
                    Resolver.ResolveDefault(serviceId ?? string.Empty, query)));
            }

            return SendAsync(new NetworkRouteKey(environmentId, serviceId), request, query, cancellationToken);
        }

        /// <inheritdoc />
        public async Awaitable<RoutedHttpOutcome> SendAsync(
            NetworkRouteKey route,
            HttpRequest request,
            NetworkRouteQuery query = default,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RoutedHttpClient));

            cancellationToken.ThrowIfCancellationRequested();

            // Steps 1–2: resolve, validate, and freeze. Nothing after this reads configuration.
            var resolution = Resolve(route, request, query);
            if (!resolution.Resolves)
                return UnresolvedOutcome(resolution);

            var prepared = Prepare(resolution, request, query);

            if (!TryValidateRequestSize(prepared, out string sizeError))
                return RejectedOutcome(prepared, NetworkErrorCategory.SecurityPolicy, sizeError);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            return await ExecuteAsync(prepared, linked.Token);
        }

        // ---- Steps 1–2: resolution and preparation

        private NetworkRouteResolution Resolve(NetworkRouteKey route, HttpRequest request, NetworkRouteQuery query)
        {
            // A raw send takes its relative path from the request's own url, unless the caller named an
            // endpoint or supplied an explicit path. A request flagged useFullUrl is not a routed send —
            // the legacy client owns that case.
            string relativePath = query.RelativePath;
            if (relativePath == null && string.IsNullOrEmpty(query.EndpointId))
                relativePath = request?.url ?? string.Empty;

            var effective = new NetworkRouteQuery(
                query.Protocol, query.EndpointId, relativePath, query.PolicyOverride);

            return Resolver.Resolve(route, effective);
        }

        private ResolvedHttpRequest Prepare(
            NetworkRouteResolution resolution,
            HttpRequest request,
            NetworkRouteQuery query)
        {
            var policy = resolution.Policy;
            var endpoint = resolution.Endpoint;

            // Step 3: service default headers underneath the caller's own, so an explicit request header
            // always wins over a service default.
            var headers = NetworkHeaderCollection.Merge(
                NetworkHeaderCollection.FromServiceDefaults(resolution.Service.DefaultHeaders),
                NetworkHeaderCollection.FromRequest(request));

            if (!string.IsNullOrEmpty(policy.IdempotencyKey))
            {
                var withKey = headers.ToDictionary();
                withKey[IdempotencyKeyHeader] = policy.IdempotencyKey;
                headers = NetworkHeaderCollection.From(withKey);
            }

            var method = endpoint != null && string.IsNullOrEmpty(query.RelativePath)
                ? endpoint.Method
                : request?.method ?? HttpMethod.GET;

            var transportRequest = BuildTransportRequest(request, resolution.ResolvedUri, method, headers, policy, endpoint);

            return new ResolvedHttpRequest(
                resolution.Route,
                endpoint,
                resolution.ResolvedUri,
                method,
                headers,
                policy,
                resolution.CredentialAppliesToHost ? resolution.Credential : null,
                resolution.AllowedHosts,
                Guid.NewGuid().ToString("N").Substring(0, 16),
                DateTime.UtcNow,
                resolution.IsProduction,
                transportRequest);
        }

        /// <summary>
        /// Builds the request handed to the transport.
        /// </summary>
        /// <remarks>
        /// Sets <c>useFullUrl</c> and clears query parameters so <see cref="HttpRequest.FullUrl"/> returns
        /// the resolved URI verbatim and never consults <c>GlobalSettings</c> for a base URL — the
        /// global-read that plan §2.1 item 4 identifies. Redirect following is disabled unconditionally;
        /// the pipeline follows them itself.
        /// </remarks>
        private static HttpRequest BuildTransportRequest(
            HttpRequest source,
            string uri,
            HttpMethod method,
            NetworkHeaderCollection headers,
            NetworkEffectivePolicy policy,
            NetworkEndpointDefinition endpoint)
        {
            var prepared = source?.Clone() ?? new HttpRequest();

            prepared.useFullUrl = true;
            prepared.url = uri;
            prepared.method = method;
            prepared.queryParams?.Clear();
            prepared.followRedirects = false;
            prepared.validateSSL = policy.ValidateTlsCertificate.Value;

            if (endpoint != null && source == null)
                prepared.expectedResponseType = endpoint.ExpectedResponseType;

            // The resolved set replaces the source's own headers outright: it is the source's headers
            // merged over the service defaults, so keeping the originals would duplicate every entry.
            prepared.headers ??= new List<HttpHeader>();
            prepared.headers.Clear();
            foreach (var header in headers)
                prepared.headers.Add(new HttpHeader(header.Key, header.Value));

            return prepared;
        }

        private static bool TryValidateRequestSize(ResolvedHttpRequest request, out string error)
        {
            long limit = request.Policy.MaxRequestBytes.Value;
            if (limit <= 0)
            {
                error = null;
                return true;
            }

            long size = EstimateBodyBytes(request.TransportRequest);
            if (size <= limit)
            {
                error = null;
                return true;
            }

            error = $"Request body is {size} bytes, over the {limit}-byte limit for this route.";
            return false;
        }

        private static long EstimateBodyBytes(HttpRequest request)
        {
            if (request == null || !request.HasBody) return 0;

            switch (request.bodyType)
            {
                case BodyType.Json:
                    return System.Text.Encoding.UTF8.GetByteCount(request.jsonBody ?? string.Empty);

                case BodyType.Form:
                case BodyType.Binary:
                    long total = 0;
                    if (request.formFields != null)
                    {
                        foreach (var field in request.formFields)
                        {
                            if (field == null || !field.isEnabled) continue;
                            total += System.Text.Encoding.UTF8.GetByteCount(field.key ?? string.Empty);
                            total += System.Text.Encoding.UTF8.GetByteCount(field.value ?? string.Empty);
                        }
                    }
                    if (request.binaryFields != null)
                    {
                        foreach (var field in request.binaryFields)
                        {
                            if (field?.data != null && field.isEnabled)
                                total += field.data.Length;
                        }
                    }
                    return total;

                default:
                    return 0;
            }
        }

        // ---- Steps 3–14: execution

        private async Awaitable<RoutedHttpOutcome> ExecuteAsync(
            ResolvedHttpRequest request,
            CancellationToken cancellationToken)
        {
            var started = DateTime.UtcNow;
            var attempts = new List<NetworkAttemptRecord>();
            var state = _routeStates.For(request.Route);

            TimeSpan queued = TimeSpan.Zero;
            TimeSpan authentication = TimeSpan.Zero;
            TimeSpan retryDelay = TimeSpan.Zero;
            TimeSpan wire = TimeSpan.Zero;

            // Step 5: cache lookup.
            if (_cache.TryGet(request, DateTime.UtcNow, out var cachedResponse))
            {
                var cachedOutcome = SuccessOutcome(request, cachedResponse, attempts, request.Uri, 0, false);
                cachedOutcome.ServedFromCache = true;
                cachedOutcome.Timings = new NetworkSendTimings(
                    TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTime.UtcNow - started);

                Finish(request, cachedOutcome, state, moveCircuit: false);
                return cachedOutcome;
            }

            // Step 12 (early): the circuit fails fast before anything is queued.
            if (!state.TryPassCircuit(request.Policy, DateTime.UtcNow, out string circuitReason))
                return RejectedOutcome(request, NetworkErrorCategory.Connectivity, circuitReason);

            // Step 6: per-route bulkhead and queue bound.
            if (!state.TryEnterQueue(request.Policy, out string queueReason))
                return RejectedOutcome(request, NetworkErrorCategory.Connectivity, queueReason);

            bool slotHeld = false;
            bool inQueue = true;
            try
            {
                var queueStarted = DateTime.UtcNow;

                // Step 7: cancellation and the overall deadline are both observed while queued, so a
                // cancelled request never reaches the transport.
                while (!state.TryOccupySlot(request.Policy))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (request.HasExpired(DateTime.UtcNow))
                    {
                        queued = DateTime.UtcNow - queueStarted;
                        var timedOut = RejectedOutcome(
                            request, NetworkErrorCategory.Timeout,
                            $"The overall {request.Policy.OverallTimeoutSeconds.Value}s budget elapsed while queued.");
                        timedOut.Timings = new NetworkSendTimings(
                            queued, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, DateTime.UtcNow - started);
                        return timedOut;
                    }

                    await Awaitable.NextFrameAsync(cancellationToken);
                }

                slotHeld = true;

                // Leave the queue the moment a slot is taken, so the queue bound measures requests
                // actually waiting rather than counting the in-flight one against it.
                state.LeaveQueue();
                inQueue = false;

                queued = DateTime.UtcNow - queueStarted;
                cancellationToken.ThrowIfCancellationRequested();

                _observers.NotifyStarted(request);

                var loop = await RunAttemptsAsync(request, attempts, cancellationToken);
                authentication = loop.Authentication;
                retryDelay = loop.RetryDelay;
                wire = loop.Wire;

                loop.Outcome.Timings = new NetworkSendTimings(
                    queued, authentication, retryDelay, wire, DateTime.UtcNow - started);

                Finish(request, loop.Outcome, state, moveCircuit: true);
                return loop.Outcome;
            }
            finally
            {
                if (slotHeld) state.ReleaseSlot();
                if (inQueue) state.LeaveQueue();
            }
        }

        /// <summary>Accumulated result of the attempt loop.</summary>
        private struct AttemptLoopResult
        {
            public RoutedHttpOutcome Outcome;
            public TimeSpan Authentication;
            public TimeSpan RetryDelay;
            public TimeSpan Wire;
        }

        private async Awaitable<AttemptLoopResult> RunAttemptsAsync(
            ResolvedHttpRequest request,
            List<NetworkAttemptRecord> attempts,
            CancellationToken cancellationToken)
        {
            var result = new AttemptLoopResult();

            string uri = request.Uri;
            string host = request.Host;
            bool credentialAllowed = request.IsAuthenticated;
            int redirects = 0;
            bool authRefreshUsed = false;
            TimeSpan pendingDelay = TimeSpan.Zero;

            for (int attemptNumber = 1; ; attemptNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (request.HasExpired(DateTime.UtcNow))
                {
                    result.Outcome = FailureOutcome(
                        request, attempts, uri, redirects, credentialAllowed,
                        NetworkErrorCategory.Timeout,
                        $"The overall {request.Policy.OverallTimeoutSeconds.Value}s budget elapsed after " +
                        $"{attempts.Count} attempt(s).", null, 0, null);
                    return result;
                }

                // Step 4: acquire the credential against *this* attempt's host. After a redirect that is
                // a different host, and scope is re-checked rather than assumed.
                var authStarted = DateTime.UtcNow;
                var credential = NetworkCredential.None;
                if (credentialAllowed)
                {
                    credential = await _credentials.AcquireForHostAsync(
                        request.Credential, request.Route.ServiceId, host, authRefreshUsed, cancellationToken);
                }
                result.Authentication += DateTime.UtcNow - authStarted;

                var attempt = new NetworkAttemptRecord
                {
                    Attempt = attemptNumber,
                    Uri = uri,
                    DelayBefore = pendingDelay,
                    Authenticated = credential.HasValue
                };
                pendingDelay = TimeSpan.Zero;

                // Step 8: execute one attempt, its timeout clamped to what is left of the overall budget.
                var wireStarted = DateTime.UtcNow;
                HttpResponse response = null;
                Exception failure = null;

                try
                {
                    var transportRequest = ComposeAttempt(request, uri, credential);
                    response = await _transport.SendAsync(transportRequest, null, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is not an error and is never retried.
                    throw;
                }
                catch (Exception e)
                {
                    failure = e;
                }

                attempt.Duration = DateTime.UtcNow - wireStarted;
                result.Wire += attempt.Duration;

                // Steps 9–10: normalize headers case-insensitively and classify.
                var headers = NetworkHeaderCollection.FromResponse(response);
                var category = Classify(response, failure);
                attempt.StatusCode = response?.statusCode ?? 0;
                attempt.Category = category;
                attempt.Message = category == NetworkErrorCategory.None
                    ? string.Empty
                    : failure?.Message ?? response?.errorMessage ?? $"HTTP {attempt.StatusCode}";

                attempts.Add(attempt);

                // Step 11a: redirects. Followed here so the target host can be revalidated and the
                // credential stripped if it is not authorized for it.
                if (response != null && IsRedirect(response.statusCode))
                {
                    var decision = EvaluateRedirect(request, response, headers, uri, redirects);

                    if (decision.Follow)
                    {
                        redirects++;
                        uri = decision.TargetUri;
                        host = decision.TargetHost;
                        credentialAllowed = decision.CredentialMayFollow;
                        continue;
                    }

                    if (decision.Reject)
                    {
                        result.Outcome = FailureOutcome(
                            request, attempts, uri, redirects, credential.HasValue,
                            NetworkErrorCategory.SecurityPolicy, decision.Reason, response, response.statusCode, headers);
                        return result;
                    }

                    // Redirects disallowed: the 3xx is the answer, returned as-is.
                }

                if (category == NetworkErrorCategory.None)
                {
                    if (!TryValidateResponseSize(request, response, out string sizeError))
                    {
                        result.Outcome = FailureOutcome(
                            request, attempts, uri, redirects, credential.HasValue,
                            NetworkErrorCategory.SecurityPolicy, sizeError, response, response.statusCode, headers);
                        return result;
                    }

                    result.Outcome = SuccessOutcome(request, response, attempts, uri, redirects, credential.HasValue);
                    result.Outcome.Headers = headers;
                    return result;
                }

                // Step 4 (recovery): one credential refresh on a 401, when the profile permits it.
                if (ShouldRefreshCredential(request, response, authRefreshUsed, credentialAllowed))
                {
                    authRefreshUsed = true;
                    continue;
                }

                // Step 11b: retry evaluation.
                if (!ShouldRetry(request, category, attempts.Count))
                {
                    result.Outcome = FailureOutcome(
                        request, attempts, uri, redirects, credential.HasValue,
                        category, attempt.Message, response, attempt.StatusCode, headers);
                    return result;
                }

                var delay = ComputeDelay(request, headers, attempts.Count);
                float remaining = request.RemainingBudgetSeconds(DateTime.UtcNow);

                if (delay.TotalSeconds >= remaining)
                {
                    // Waiting would consume the whole remaining budget, so the retry could never run.
                    // Report the failure now rather than sleeping to a guaranteed timeout.
                    result.Outcome = FailureOutcome(
                        request, attempts, uri, redirects, credential.HasValue,
                        category,
                        $"{attempt.Message} (retry skipped: the {delay.TotalSeconds:F1}s backoff exceeds the " +
                        $"{remaining:F1}s left in the overall budget)", response, attempt.StatusCode, headers);
                    return result;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Awaitable.WaitForSecondsAsync((float)delay.TotalSeconds, cancellationToken);
                    result.RetryDelay += delay;
                    pendingDelay = delay;
                }
            }
        }

        /// <summary>
        /// Builds the per-attempt transport request: the attempt's URI, the credential header when one is
        /// authorized, and an attempt timeout clamped to the remaining overall budget.
        /// </summary>
        private static HttpRequest ComposeAttempt(
            ResolvedHttpRequest request,
            string uri,
            NetworkCredential credential)
        {
            var attempt = request.TransportRequest.Clone();
            attempt.url = uri;
            attempt.useFullUrl = true;
            attempt.queryParams?.Clear();
            attempt.followRedirects = false;

            // The clone already carries exactly the resolved header set, because TransportRequest is never
            // mutated and Clone copies headers verbatim. Only the credential is per-attempt.
            if (credential.HasValue && request.Credential != null)
            {
                attempt.AddHeader(
                    request.Credential.HeaderName,
                    (request.Credential.Scheme ?? string.Empty) + credential.Value);
            }

            float attemptBudget = request.Policy.AttemptTimeoutSeconds.Value;
            float remaining = request.RemainingBudgetSeconds(DateTime.UtcNow);
            float effective = float.IsPositiveInfinity(remaining)
                ? attemptBudget
                : Mathf.Min(attemptBudget, remaining);

            // UnityWebRequest takes whole seconds and treats 0 as "no timeout", so never round down to 0
            // for a request that still has budget left.
            attempt.timeout = Mathf.Max(1, Mathf.CeilToInt(effective));
            return attempt;
        }

        // ---- Classification

        private static NetworkErrorCategory Classify(HttpResponse response, Exception failure)
        {
            if (failure is OperationCanceledException) return NetworkErrorCategory.Cancellation;
            if (failure is TimeoutException) return NetworkErrorCategory.Timeout;
            if (failure != null) return NetworkErrorCategory.Connectivity;
            if (response == null) return NetworkErrorCategory.Unknown;

            if (response.isSuccess) return NetworkErrorCategory.None;

            // The legacy classifier already distinguishes timeout from connectivity using the typed
            // exception the transport attaches; reuse it rather than re-sniffing error strings.
            return response.Error.Kind.ToCategory();
        }

        private static bool IsRedirect(int statusCode) =>
            statusCode is 301 or 302 or 303 or 307 or 308;

        private struct RedirectDecision
        {
            public bool Follow;
            public bool Reject;
            public string Reason;
            public string TargetUri;
            public string TargetHost;
            public bool CredentialMayFollow;
        }

        /// <summary>
        /// Decides whether to follow a 3xx, and whether the credential may travel with it.
        /// </summary>
        /// <remarks>
        /// A credential follows only to a host the profile itself authorizes. Under
        /// <see cref="NetworkRedirectMode.SameOrigin"/> a cross-origin target is refused outright; under
        /// <see cref="NetworkRedirectMode.AllowedHosts"/> the target must match the service's allowlist.
        /// </remarks>
        private static RedirectDecision EvaluateRedirect(
            ResolvedHttpRequest request,
            HttpResponse response,
            NetworkHeaderCollection headers,
            string currentUri,
            int redirectsSoFar)
        {
            var mode = request.Policy.RedirectMode.Value;
            if (mode == NetworkRedirectMode.Disallow)
                return default;

            if (redirectsSoFar >= request.Policy.MaxRedirects.Value)
            {
                return new RedirectDecision
                {
                    Reject = true,
                    Reason = $"Exceeded the {request.Policy.MaxRedirects.Value}-redirect limit for this route."
                };
            }

            string location = headers["Location"];
            if (string.IsNullOrWhiteSpace(location))
                return default;

            if (!Uri.TryCreate(new Uri(currentUri), location, out Uri target))
            {
                return new RedirectDecision
                {
                    Reject = true,
                    Reason = $"Redirect target '{location}' is not a resolvable URI."
                };
            }

            if (!NetworkOrigin.IsSchemeAllowed(target.Scheme, false))
            {
                return new RedirectDecision
                {
                    Reject = true,
                    Reason = $"Redirect target scheme '{target.Scheme}' is not permitted."
                };
            }

            if (request.Policy.RequireSecureTransport.Value && !NetworkOrigin.IsSecureScheme(target.Scheme))
            {
                return new RedirectDecision
                {
                    Reject = true,
                    Reason = $"Redirect to '{target.Scheme}' is refused because this route requires an encrypted scheme."
                };
            }

            var current = new Uri(currentUri);
            bool sameOrigin =
                string.Equals(current.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Host, target.Host, StringComparison.OrdinalIgnoreCase) &&
                current.Port == target.Port;

            if (mode == NetworkRedirectMode.SameOrigin && !sameOrigin)
            {
                return new RedirectDecision
                {
                    Reject = true,
                    Reason = $"Redirect to '{target.Host}' is cross-origin and this route only follows same-origin redirects."
                };
            }

            string targetHost = target.Host.ToLowerInvariant();

            if (mode == NetworkRedirectMode.AllowedHosts && !sameOrigin &&
                !NetworkHostRule.MatchesAny(request.AllowedHosts, targetHost))
            {
                return new RedirectDecision
                {
                    Reject = true,
                    Reason = $"Redirect target '{targetHost}' is not in this service's allowed hosts."
                };
            }

            // The credential follows only if its own scope names the new host. This is the check that
            // stops a token walking off-domain behind a 302.
            bool credentialMayFollow =
                request.IsAuthenticated &&
                NetworkRouteResolver.CredentialApplies(request.Credential, request.Route.ServiceId, targetHost);

            return new RedirectDecision
            {
                Follow = true,
                TargetUri = target.ToString(),
                TargetHost = targetHost,
                CredentialMayFollow = credentialMayFollow
            };
        }

        private static bool ShouldRefreshCredential(
            ResolvedHttpRequest request,
            HttpResponse response,
            bool alreadyRefreshed,
            bool credentialAllowed)
        {
            if (alreadyRefreshed || !credentialAllowed || response == null || response.statusCode != 401)
                return false;

            var mode = request.Credential?.RefreshMode ?? NetworkCredentialRefreshMode.None;
            return mode == NetworkCredentialRefreshMode.OnUnauthorized ||
                   mode == NetworkCredentialRefreshMode.OnExpiryAndUnauthorized;
        }

        private static bool ShouldRetry(ResolvedHttpRequest request, NetworkErrorCategory category, int attemptsMade)
        {
            // A cancelled request is never retried, and neither is one the pipeline itself rejected.
            switch (category)
            {
                case NetworkErrorCategory.Connectivity:
                case NetworkErrorCategory.Timeout:
                    break;

                case NetworkErrorCategory.HttpStatus:
                    break;

                default:
                    return false;
            }

            if (!request.AllowsRetry)
                return false;

            if (attemptsMade > request.Policy.MaxRetries.Value)
                return false;

            return true;
        }

        /// <summary>
        /// The delay before the next attempt: <c>Retry-After</c> when present and honoured, otherwise
        /// exponential backoff with optional full jitter, capped by the policy.
        /// </summary>
        private TimeSpan ComputeDelay(ResolvedHttpRequest request, NetworkHeaderCollection headers, int attemptsMade)
        {
            if (request.Policy.HonorRetryAfter.Value &&
                TryParseRetryAfter(headers["Retry-After"], out TimeSpan retryAfter))
            {
                return retryAfter;
            }

            double cap = request.Policy.RetryBaseDelaySeconds.Value * Math.Pow(2, Math.Max(0, attemptsMade - 1));
            cap = Math.Min(cap, request.Policy.RetryMaxDelaySeconds.Value);

            if (cap <= 0d)
                return TimeSpan.Zero;

            // Full jitter: a random point in [0, cap]. Spreading across the whole window is what stops
            // clients that failed together from retrying in a synchronized burst.
            double seconds = request.Policy.RetryJitter.Value ? _jitter.NextDouble() * cap : cap;
            return TimeSpan.FromSeconds(seconds);
        }

        private static bool TryParseRetryAfter(string value, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value)) return false;

            if (int.TryParse(value.Trim(), out int seconds) && seconds >= 0)
            {
                delay = TimeSpan.FromSeconds(seconds);
                return true;
            }

            // The header also permits an HTTP date.
            if (DateTime.TryParse(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTime when))
            {
                var remaining = when - DateTime.UtcNow;
                delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                return true;
            }

            return false;
        }

        private static bool TryValidateResponseSize(
            ResolvedHttpRequest request,
            HttpResponse response,
            out string error)
        {
            long limit = request.Policy.MaxResponseBytes.Value;
            if (limit <= 0 || response == null)
            {
                error = null;
                return true;
            }

            long size = response.rawData?.LongLength ?? response.contentLength;
            if (size <= limit)
            {
                error = null;
                return true;
            }

            error = $"Response body is {size} bytes, over the {limit}-byte limit for this route.";
            return false;
        }

        // ---- Outcome construction and finalization

        private static RoutedHttpOutcome UnresolvedOutcome(NetworkRouteResolution resolution) =>
            new RoutedHttpOutcome
            {
                Route = resolution.Route,
                EndpointId = resolution.Endpoint?.Id ?? string.Empty,
                Uri = resolution.ResolvedUri,
                IsSuccess = false,
                Category = resolution.FailureCategory,
                Message = resolution.FailureMessage,
                SecurityClamps = resolution.Policy?.SecurityClamps ?? Array.Empty<string>(),
                CorrelationId = string.Empty
            };

        private RoutedHttpOutcome RejectedOutcome(
            ResolvedHttpRequest request,
            NetworkErrorCategory category,
            string message)
        {
            var outcome = new RoutedHttpOutcome
            {
                Route = request.Route,
                EndpointId = request.EndpointId,
                Uri = request.Uri,
                IsSuccess = false,
                Category = category,
                Message = message,
                CorrelationId = request.CorrelationId,
                SecurityClamps = request.Policy.SecurityClamps
            };

            // A request the pipeline refused says nothing about the service's health, so the circuit does
            // not move — but it is still recorded, because "why did nothing go out?" has to be answerable.
            Finish(request, outcome, _routeStates.For(request.Route), moveCircuit: false);
            return outcome;
        }

        private static RoutedHttpOutcome SuccessOutcome(
            ResolvedHttpRequest request,
            HttpResponse response,
            List<NetworkAttemptRecord> attempts,
            string uri,
            int redirects,
            bool authenticated) =>
            new RoutedHttpOutcome
            {
                Route = request.Route,
                EndpointId = request.EndpointId,
                Uri = uri,
                IsSuccess = true,
                StatusCode = response?.statusCode ?? 0,
                Category = NetworkErrorCategory.None,
                Response = response,
                Headers = NetworkHeaderCollection.FromResponse(response),
                Attempts = attempts.ToArray(),
                CorrelationId = request.CorrelationId,
                RedirectCount = redirects,
                WasAuthenticated = authenticated,
                SecurityClamps = request.Policy.SecurityClamps
            };

        private static RoutedHttpOutcome FailureOutcome(
            ResolvedHttpRequest request,
            List<NetworkAttemptRecord> attempts,
            string uri,
            int redirects,
            bool authenticated,
            NetworkErrorCategory category,
            string message,
            HttpResponse response,
            int statusCode,
            NetworkHeaderCollection headers) =>
            new RoutedHttpOutcome
            {
                Route = request.Route,
                EndpointId = request.EndpointId,
                Uri = uri,
                IsSuccess = false,
                StatusCode = statusCode,
                Category = category,
                Message = message,
                Cause = response?.exception,
                Response = response,
                Headers = headers ?? NetworkHeaderCollection.Empty,
                Attempts = attempts.ToArray(),
                CorrelationId = request.CorrelationId,
                RedirectCount = redirects,
                WasAuthenticated = authenticated,
                SecurityClamps = request.Policy.SecurityClamps
            };

        /// <summary>
        /// Steps 12–14: update circuit state, store in cache, record diagnostics, notify observers.
        /// </summary>
        /// <param name="request">The resolved request.</param>
        /// <param name="outcome">The final outcome.</param>
        /// <param name="state">The route's pipeline state.</param>
        /// <param name="moveCircuit">
        /// Whether this outcome is evidence about the service's health. <c>false</c> for a cache hit or a
        /// pipeline rejection.
        /// </param>
        private void Finish(
            ResolvedHttpRequest request,
            RoutedHttpOutcome outcome,
            RoutePipelineState state,
            bool moveCircuit)
        {
            if (!moveCircuit)
                state.RecordNeutral();
            else if (outcome.IsSuccess)
                state.RecordSuccess();
            else
                state.RecordFailure(request.Policy, DateTime.UtcNow);

            if (outcome.IsSuccess && !outcome.ServedFromCache)
                _cache.Store(request, outcome.Response, DateTime.UtcNow);

            string bodyPreview = request.Policy.CaptureBodies.Value ? outcome.Response?.text : null;
            _diagnostics?.Record(request, outcome, bodyPreview);

            _observers.NotifyCompleted(request, outcome);
        }

        private static Awaitable<RoutedHttpOutcome> Completed(RoutedHttpOutcome outcome)
        {
            var completion = new AwaitableCompletionSource<RoutedHttpOutcome>();
            completion.SetResult(outcome);
            return completion.Awaitable;
        }

        /// <summary>
        /// Cancels every in-flight send and releases the client.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="NetworkRuntimeSubsystem"/> on teardown. Cancelling the lifetime token is
        /// what unwinds queued requests, backoff waits, and in-flight transport operations.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _lifetime.Cancel();
            _lifetime.Dispose();
            _observers.Clear();
            _routeStates.Clear();
            _cache.Clear();
        }
    }
}
