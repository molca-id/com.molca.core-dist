---
title: "Networking: HttpClient & Requests"
category: Data & Networking
order: 400
---

# Networking: HttpClient & Requests

The HTTP layer sends requests through a single queued subsystem (`IHttpClient`), authors endpoints as
read-only `HttpRequestAsset` ScriptableObjects instead of hardcoded URLs, injects auth via `AuthManager`,
and classifies every failure with a structured `HttpError` so retry and recovery are decided from *why* a
request failed — never a scraped error string. Live streaming feeds (SSE / WebSocket / Socket.IO) are a
separate concern; see [Data Providers](DATA_PROVIDERS.md).

## Sending a request

`HttpClient` is a `RuntimeSubsystem` that implements `IHttpClient`. Resolve the instance API — never the
obsolete static shims on `HttpClient` — and always `await RuntimeManager.WaitForInitialization()` before
using it:

```csharp
[Inject] private IHttpClient _http;                       // or RuntimeManager.GetService<IHttpClient>()

private async Awaitable LoadAsync(CancellationToken ct)
{
    var request = _http.CreateRequest()                   // fluent HttpRequestBuilder
        .Method(HttpMethod.GET)
        .Url("users/me")                                  // resolved against HttpModule.BaseUrl
        .Header("Accept", "application/json")
        .ResponseType(ResponseType.Json)
        .Build();

    HttpResponse response = await _http.SendAsync(request, ct);
    if (response.isSuccess)
        var user = response.GetJsonData<UserDto>();
}
```

`SendAsync` returns `Awaitable<HttpResponse>` (never `Task`, per the [Async Contract](ASYNC_CONTRACT.md))
and never returns null. A callback overload, `Send(request, ct, onSuccess, onError, onProgress)`, exists for
fire-and-forget call sites. Cancelling the token aborts an in-flight request or drops it from the queue and
completes the awaitable as cancelled — cancellation is not an error.

Requests are queued and processed up to `MaxConcurrentRequests` (default 4) at a time. Each send works on a
**clone** of your request with default headers merged in (request headers win) and the timeout resolved from
`HttpModule.DefaultTimeout` when unset — so neither the transport nor an interceptor can mutate the instance
you passed in.

### HttpResponse essentials

| Member | Meaning |
|---|---|
| `isSuccess` | `true` for a `2xx` status (`IsSuccessStatusCode`), independent of the transport error string. |
| `statusCode` / `statusMessage` | HTTP status; `0` when no HTTP exchange happened. |
| `Error` | Structured `HttpError` (see below); `HttpError.None` on success. |
| `GetJsonData<T>()` | Deserializes the body when `IsJson`; `null` otherwise. |
| `GetContentAsString()` | Body text (or UTF-8-decoded bytes). |
| `text` / `rawData` / `texture` / `audioClip` / `assetBundle` | Typed payloads by `ResponseType`. |

## Endpoints as assets — `HttpRequestAsset`

Do not hardcode URLs in scripts. Author each endpoint as an `HttpRequestAsset` ScriptableObject and
reference it where you need it.

- **Folder placement:** your project's `ScriptableObjects/` area (e.g. `Assets/YourProject/ScriptableObjects/`).
- **Base class:** `ScriptableObject` (do not subclass — it is a concrete container).
- **Create via:** *Create → Molca → Networking → HTTP Request*.

The asset is **read-only config**: never mutate it at play time (the mutating helpers such as `AddHeader`
are `[Obsolete]` for exactly this reason). Call `CreateRequest()` for an independent, mutable clone to
customize and send:

```csharp
[SerializeField] private HttpRequestAsset _getProfile;

private async Awaitable<HttpResponse> FetchAsync(string id, CancellationToken ct)
{
    HttpRequest req = _getProfile.CreateRequest();        // safe mutable clone
    req.url = $"users/{id}";
    return await RuntimeManager.GetService<IHttpClient>().SendAsync(req, ct);
}
```

`HttpRequestAsset` also offers convenience senders (`SendAsync()`, `Send(...)`) that clone-then-send for you,
and `CreateBuilder()` to seed an `HttpRequestBuilder` from the asset's configuration.

## Configuration — `HttpModule`

Client-wide settings live on an `HttpModule` (a `SettingModule`), read through `GlobalSettings` and paired
with a mutable `HttpState`. Create it via *Create → Molca → Settings → HTTP* and register it like any other
setting module (see [Settings](SETTINGS.md)). Authored `SerializeField`s are defaults; runtime changes and
persisted default headers live on the state, never written back to the asset.

| Setting | Purpose |
|---|---|
| `BaseUrl` | Prefix for non-`useFullUrl` request URLs. |
| `MaxConcurrentRequests` | In-flight cap (1–20, default 4). |
| `DefaultTimeout` | Seconds applied when a request leaves `timeout` unset. |
| `EnableRequestHistory` / `MaxHistorySize` | Bounded, redacted ring of completed requests for diagnostics. |
| `EnableRetry` / `MaxRetries` / `RetryBaseDelaySeconds` | Retry policy inputs (see below). |
| `FollowRedirects` / `ValidateSSL` / `EnableLogging` | Transport behavior. |

## Errors and retry — `HttpError` / `HttpErrorKind`

Every failed response exposes a structured `HttpError` via `response.Error`, so callers branch on the kind
rather than parsing a message:

| `HttpErrorKind` | Meaning | Retryable? |
|---|---|---|
| `None` | Success. | — |
| `Network` | Connection-level failure, no HTTP status (DNS/socket/TLS). | Yes |
| `Timeout` | Exceeded the request timeout. | Yes |
| `Http5xx` | Server error. | Yes |
| `Http4xx` | Client error. | Only `408` and `429` |
| `Canceled` | Request was cancelled. | No |
| `Serialization` | Body could not be (de)serialized. | No |
| `Auth` | Authentication/authorization failure. | No |

Retries apply only to **idempotent** methods (`GET`, `HEAD`, `OPTIONS`, `PUT`, `DELETE`) when
`EnableRetry` is on. `HttpRetryPolicy.IsRetryable(HttpError)` makes the call; backoff is full-jitter
exponential (`ComputeBackoffDelay`), and a server-sent `Retry-After` on a `429`/`503` overrides the computed
delay (capped at 60s). You can override the module-sourced policy at runtime with `SetRetryPolicy(policy)`
(pass `null` to restore module-driven config).

## Interceptors

Interceptors observe or adjust requests and responses without touching call sites. Register them with
`_http.AddInterceptor(interceptor)`; registration order is invocation order.

- **`IHttpRequestInterceptor`** — `OnRequestPrepared(HttpRequest)` runs on the per-send clone just before
  transport. Mutations never reach your original request or the source asset.
- **`IHttpContextAwareRequestInterceptor`** — the same, plus the `HttpRequestContext` so an interceptor can
  stash per-request state.
- **`IHttpResponseInterceptor`** — `OnResponseReceivedAsync(...)` returns `ResponseAction.Continue` or
  `RetryOnce`. An interceptor that also implements this interface is registered for response callbacks
  automatically (no separate API). At most one `RetryOnce` is honored per request, so there is no loop.

Place custom interceptors in your project's `Scripts/` area; implement the interface(s) and register during
subsystem or bootstrap initialization. An interceptor that throws is logged and skipped — it never breaks the
request pipeline.

## Authentication — `AuthManager`

`AuthManager` is a `RuntimeSubsystem` (`IAuthManager`) that manages login, logout, and token refresh, and
persists the auth payload encrypted at rest (`SecureStorage`). It is configured with `HttpRequestAsset`
references for its login/logout/validate/refresh endpoints — again, no hardcoded URLs.

```csharp
var auth = RuntimeManager.GetSubsystem<AuthManager>();
if (await auth.LoginAsync(username, password, ct))
{
    // auth.IsAuthenticated == true; token now injected on opted-in requests
}
```

| Member | Behavior |
|---|---|
| `LoginAsync(user, pass, ct)` | Single-flight (a login gate) so concurrent logins can't clobber the session. |
| `LogoutAsync(ct)` | Clears the session and dispatches the logout event even if the server call fails. |
| `RefreshAsync(ct)` | Renews the access token; concurrent callers coalesce onto one in-flight refresh. |
| `TryValidateCachedToken(ct)` | Probes a cached token; a cancelled probe leaves the cached user intact. |
| `IsAuthenticated` / `HasCachedToken` / `AuthToken` | Session state. |

Token injection is automatic via `AuthTokenInterceptor`, registered by `AuthManager` at init. It is
**opt-in per request**: a request receives the token only if it already declares a header with the
configured `authTokenKey`. On a `401` for such a request, the interceptor triggers a single-flight
`RefreshAsync`, retries once with the refreshed token, and — if refresh is impossible — dispatches
`AuthEvents.Expired` and lets the `401` surface. (`AuthManager.TryApplyToken` is `[Obsolete]`; do not
hand-inject tokens into shared/asset-backed requests.)

`AuthManager` raises typed events (see [Events](EVENTS.md)): `AuthEvents.LoggedIn`, `LoggedOut`, and
`Expired` — subscribe to `Expired` to route the user back to login when a session can no longer be refreshed.

## Redaction

Credentials must never reach a log or the request-history surface. `LogRedaction` masks them:

- **`RedactUrl`** — masks every query-string value; use `HttpRequestContext.RedactedUrl` when logging.
- **`RedactHeaderValue`** — masks known sensitive headers (`Authorization`, `X-Api-Key`, `Cookie`, …).
- **`RedactJsonBody`** — masks credential-shaped JSON fields (`password`, `token`, `secret`, …).

Request logging routes URLs through `RedactUrl`, and stored history keeps a `CreateRedactedSnapshot()` copy —
masked headers/params/body, binary payloads dropped, heavy Unity object references released — never the live
context with its plaintext tokens.

## See also

- [Data Providers](DATA_PROVIDERS.md)
- [Async Contract](ASYNC_CONTRACT.md)
- [Settings](SETTINGS.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Events](EVENTS.md)
