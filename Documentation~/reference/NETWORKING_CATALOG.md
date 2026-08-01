---
title: "Networking: Routes & Catalog"
category: Data & Networking
order: 401
---

# Networking: Routes & Catalog

A request targets a **route** — an environment paired with a service — instead of resolving against one
process-wide base URL. One session can talk to several services in several environments at the same time
without mutating global state.

```text
NetworkRouteKey = EnvironmentId + ServiceId

(development, identity)
(production, licensing)
(staging-eu, content)
```

This guide covers the configuration model. The existing request/response API is unchanged — see
[HttpClient & Requests](NETWORKING.md) — and streaming providers are in
[Data Providers](DATA_PROVIDERS.md).

> **Status.** The catalog, validation, authoring services, and the routed runtime client
> (`IRoutedHttpClient`) all ship. The Hub **Network** workspace and the legacy-adapter migration follow in
> later phases, so today the routed client is opt-in: existing `IHttpClient` / `HttpRequestAsset` call
> sites are untouched and keep using the legacy path.

## The two identifiers

| | What it names | Examples |
|---|---|---|
| **Environment** | A deployment context, plus its safety posture | `local`, `development`, `staging-eu`, `production` |
| **Service** | A logical backend | `identity`, `content`, `licensing`, `vendor-analytics` |

An environment holds **no origins**. Origins live on each service's binding *for* that environment. So an
environment is identity and posture; a service is what you talk to; the binding is the address.

## Assets

| Asset | Type | Holds |
|---|---|---|
| `NetworkCatalog` | `SettingModule` | Environments, services + bindings, policy profiles, credential metadata, collection references, build gates |
| `NetworkEndpointCollection` | `ScriptableObject` | A coherent group of endpoint templates |

One catalog is the project default, created under `Assets/_Molca/Settings/` and registered on
`GlobalSettings.modules` like any other setting module. Endpoint collections are the merge-conflict and
ownership boundary — an endpoint does **not** get its own asset.

Both are read-only configuration. `NetworkCatalog.CreateState()` returns `null` by design: nothing here is
mutated at runtime, and anything that changes during play (active selection, sessions, queues) belongs to
the network subsystem.

Create them from **Molca → Networking → Network Catalog** / **Endpoint Collection**, or let the Hub create
the catalog for you.

## Identifiers

Every entity is keyed by a stable kebab-case ID, validated on author and again on load:

- lowercase ASCII letters, digits, and single interior hyphens;
- starts with a letter, does not end with a hyphen;
- 1–64 characters, unique within its kind;
- `molca-` is reserved for framework-generated entries.

An empty ID is invalid input, never a default — the catalog's default environment is an explicit field.
Display names are free text and safe to rename any time. Changing an **ID** is a refactor operation that
rewrites every reference under one Undo step (`NetworkCatalogEditingService.RenameEnvironmentId` /
`RenameServiceId`).

## Bindings and the matrix

A service declares which protocols it speaks (`Http`, `ServerSentEvents`, `WebSocket`, `SocketIO`) and
supplies one binding per environment it exists in. Each binding carries an absolute origin per protocol:

```text
identity
  development  →  https://dev.example.com/v1
  staging      →  https://staging.example.com/v1
  production   →  https://api.example.com/v1
```

Origins are explicit absolute URIs, normalized and validated when authored. There is no `${variable}`
template language in the first release: host allowlisting, credential scope, and production checks all
need a concrete host at author time.

**A missing binding is meaningful.** It means the service does not exist in that environment, and the
resolver returns a typed `RouteResolution` error rather than falling back to another environment's
origin — silent fallback is how a staging build ends up talking to production. Copying a binding across
environments is always an explicit action.

Relative paths join onto the origin by concatenation, not `Uri`'s relative resolution — so an origin of
`https://host/v1` plus `users` gives `https://host/v1/users`, and the API version is not silently
discarded. A relative path that is absolute or protocol-relative is rejected: escaping the service origin
is a route decision, not a path decision.

## Endpoints

An endpoint template carries a method or stream kind, a relative path, its parameter shape, an optional
policy override, and a mutation classification — but **no origin**. That is what makes one template usable
across every environment.

Runtime callers are never required to use templates; code can issue a route plus a relative path. Templates
exist for Hub authoring, the request console, validation, and later client generation.

Endpoint IDs are unique **catalog-wide**, not per collection, so the console and deep links can address one
by ID alone.

`MutationClass` (`Safe` / `Mutating` / `Destructive`) drives two things: whether a failed call may be
retried, and whether the request console demands a per-send confirmation against production.

## Policy precedence

Effective configuration resolves through six layers:

```text
library default  <  catalog  <  environment  <  service  <  endpoint  <  per-send override
```

Two kinds of field behave differently.

**Inheritable numerics** treat `0` as "not authored here" and fall through to the layer below. That is how
an endpoint overrides a retry count without restating a timeout. Applies to the overall timeout, the
attempt timeout, and max concurrent requests.

**Security-restricted fields** resolve *tighten-only*: any layer may make the rule stricter, none may relax
it.

| Field | Rule |
|---|---|
| `RequireSecureTransport` | `true` at any layer wins. Forced on by a production environment. |
| `RedirectMode` | Strictest layer wins (`Disallow` < `SameOrigin` < `AllowedHosts`). |
| `MaxRedirects` | Smallest layer wins. |
| `MaxRequestBytes` / `MaxResponseBytes` | Smallest non-zero bound wins; `0` means unlimited and never beats an authored bound. |
| `ValidateTlsCertificate` | Topmost layer decides — relaxing it against a local mock server is legitimate — **except** in a production environment, where it is clamped back on. |

A per-send override (`NetworkSendPolicyOverride`) exposes only operational knobs plus a tighten-only
`RedirectMode`. It has no TLS or credential field at all, so a call site has no vocabulary in which to
weaken a security rule.

Every resolved value carries the layer that supplied it:

```csharp
NetworkEffectivePolicy policy = NetworkPolicyResolver.Resolve(
    catalog, environment, service, endpoint, sendOverride);

policy.OverallTimeoutSeconds.Value    // 30
policy.OverallTimeoutSeconds.Source   // NetworkConfigurationLayer.Service
policy.SecurityClamps                 // why an override did nothing
```

`SecurityClamps` is how a rejected relaxation stays visible instead of mysteriously having no effect.

## Credentials

`NetworkCredentialProfile` holds **metadata only** — provider kind, non-secret lookup key, audience,
scopes, header and scheme, refresh mode, and the scope that bounds where the credential may travel. It has
no secret-valued field and must never gain one. Secret material comes from an
`INetworkCredentialProvider` implementation at execution time.

Hard rules:

- No credential value in a catalog, an endpoint collection, `EditorPrefs`, `PlayerPrefs`, request history,
  or an exported diagnostic. The validator additionally flags fields that *look* like they hold one.
- **Scope denies when empty.** A profile with no allowed services or hosts attaches to nothing. "No rules
  authored" must never read as "every host approved".
- No credential header until the **final** host passes scope validation. Redirect targets are revalidated,
  and credentials are stripped unless policy names the target.

Host patterns are deliberately narrow: an exact host, or a single leading `*.` wildcard covering at least
two labels. `*` and `*.com` are rejected. Anything richer would make "which hosts can see this token?"
unanswerable by inspection.

`*.example.com` matches `api.example.com` and `a.b.example.com` — but not the apex `example.com`, which is
a separate, explicit decision.

## Validation

`NetworkCatalogValidator.Validate(catalog)` is the single contract. Hub Diagnostics, Doctor, the build
gate, MCP tools, and tests all read from it; there is no second set of networking rules. It is pure and
deterministic — same findings in the same order for the same catalog, so batch-mode gates and tests are
stable.

Each finding carries a severity, a stable `Code`, a shared `NetworkErrorCategory`, the entity it belongs
to, a message, and a remedy. **Codes are API** — matched by Doctor, MCP, and tests — so they are added,
never renamed. Messages are free to be reworded.

What it checks: identifier format and uniqueness; dangling policy, credential, service, and collection
references; missing or duplicate bindings; malformed origins; insecure schemes in environments that require
encryption; hosts outside a service allowlist or credential scope; credential sources that cannot exist in
a player build; contradictory timeout, retry, circuit, and cache values; endpoint paths and parameter
placeholders; protocols an endpoint needs but its service does not declare; and suspected secrets in
serialized fields.

Severity is chosen for what it costs to be wrong. A missing binding is a **warning** — a service may
legitimately be absent from an environment, but never silently. A credential scope that excludes the
service using it is an **error**: the request would go out anonymous, which is confusing to debug from the
call site.

A build runs the validator through `NetworkCatalogBuildValidator`. It is warning-only unless the catalog
sets `Fail Build On Validation Error` — turning a project's build red on the framework's schedule would be
the wrong default while the routed pipeline is rolling out.

## Error categories

One `NetworkErrorCategory` spans runtime failures and authoring findings, so a validation finding and a
runtime failure describe the same problem with the same word:

`Configuration`, `RouteResolution`, `SecurityPolicy`, `Authentication`, `Connectivity`, `Timeout`,
`Cancellation`, `HttpStatus`, `Serialization`, `Cache`, `Observer`, `Unknown`.

The legacy `HttpErrorKind` is unchanged and keeps working. Map between them with
`kind.ToCategory()` / `category.ToLegacyKind(statusCode)`. Note that only `Connectivity` counts as a
connection failure — an HTTP error status never raises a connection error.

## Authoring from code

The shared authoring layer lives in `Molca.Editor.Networking`. Hub views, MCP tools, migration, and tests
all go through it; nothing writes catalog fields directly.

```csharp
var catalog = NetworkCatalogLocator.GetOrCreateCatalog();
var editing = new NetworkCatalogEditingService(catalog);

editing.CreateEnvironment("development");                       // first one becomes the default
editing.CreateEnvironment("production", classification: NetworkEnvironmentClassification.Production);
editing.CreateService("identity");
editing.SetHttpBinding("identity", "development", "https://dev.example.com/v1");
editing.SetHttpBinding("identity", "production",  "https://api.example.com/v1");
```

Every mutation goes through `SerializedObject`/`SerializedProperty`, so Undo and dirty tracking behave the
way the rest of the editor does. Operations spanning several assets — an ID refactor rewriting references
across collections — collapse into one Undo step, so a half-applied rename is not a reachable state.

Operations return a `NetworkAuthoringResult` rather than throwing, and a refused operation modifies
nothing:

```csharp
var result = editing.SetHttpBinding("identity", "development", "not-a-uri");
result.Success   // false
result.Message   // why
```

`NetworkCatalogLocator` finds the catalog **by type**, never by a hardcoded path, so moving or renaming the
asset never orphans it. `FindCatalog()` is safe on read paths — opening the Hub or running Doctor must
never create an asset as a side effect of looking.

## Effective-configuration preview

`NetworkEffectiveConfigurationService` answers "what would this route actually do?" — the origin, the
resolved URI, the policy with provenance, the credential profile *name*, and whether that credential
actually applies to the resolved host:

```csharp
var configuration = new NetworkEffectiveConfigurationService(catalog);
var resolved = configuration.Resolve(new NetworkRouteKey("production", "identity"));

resolved.Resolves                   // false when the route cannot resolve
resolved.FailureCategory            // RouteResolution / SecurityPolicy / Configuration
resolved.ResolvedUri
resolved.CredentialAppliesToHost    // false ⇒ the request would go out anonymous
```

The policy resolves even when the route does not, so an unbound cell in the Hub's binding grid is
understandable rather than blank.

## Sending a routed request

`IRoutedHttpClient` is registered by `NetworkRuntimeSubsystem`. Resolve it like any other service, and
`await RuntimeManager.WaitForInitialization()` first:

```csharp
[Inject] private IRoutedHttpClient _http;   // or RuntimeManager.GetService<IRoutedHttpClient>()

private async Awaitable LoadAsync(CancellationToken ct)
{
    var request = new HttpRequest { method = HttpMethod.GET, url = "users/me" };

    RoutedHttpOutcome outcome = await _http.SendAsync(
        new NetworkRouteKey("production", "identity"), request, default, ct);

    if (outcome.IsSuccess)
        var user = outcome.Json<UserDto>();
    else if (outcome.Category == NetworkErrorCategory.Authentication)
        // ...
}
```

The request's `url` is a path **relative to the service origin**. To use an authored template instead,
pass `NetworkRouteQuery.ForEndpoint("get-me")` — the endpoint supplies the path, method, and any policy
override. `SendToServiceAsync(serviceId, …)` targets the catalog's default environment.

**Failures are returned, not thrown.** Branch on `outcome.Category`; there is no error string to parse.
Only cancellation surfaces as an `OperationCanceledException`, because a cancelled send has no outcome to
describe. `outcome.LegacyError` maps onto `HttpErrorKind` for code that already branches on it.

Read response headers through `outcome.Headers`, which is case-insensitive — unlike
`HttpResponse.headers`, where `GetHeaderValue("content-type")` misses a server-sent `Content-Type`.

### What the pipeline guarantees

Every request is frozen into an immutable `ResolvedHttpRequest` **before it is queued**. After that point
nothing in the pipeline reads `GlobalSettings`, the catalog, a mutable request asset, or the Hub's current
selection — so editing configuration cannot change what an in-flight request does. The catalog snapshot is
captured once, when the subsystem initializes.

| Concern | Behaviour |
|---|---|
| Overall timeout | Covers queueing, authentication, retry delays, and transfer. The per-attempt timeout is clamped to whatever is left of it. |
| Attempt timeout | Bounds one transport attempt only. |
| Cancellation | A queued request leaves the queue within a frame and never reaches the transport. A cancelled attempt is never retried. |
| Retry | Opt-in by method and mutation class. A mutating call is not retried unless the caller supplies an `IdempotencyKey`. `Retry-After` is honoured within the remaining budget; a backoff that would consume the whole remaining budget is skipped rather than slept through. |
| Concurrency | Bulkhead and queue bound are **per route**, so a failing service cannot starve an unrelated one. Queue overflow fails fast instead of queueing without bound. |
| Circuit breaker | Opens after N consecutive failures, admits exactly one trial request when the reset window elapses. Cache hits and pipeline rejections do not move it — neither is evidence about the backend. |
| Cache | GET only, successes only, anonymous only unless the policy opts into body capture. Keyed by route, URI, and credential profile. Bounded with LRU eviction. |
| Observers | Each callback is isolated. An observer that throws is counted in diagnostics and cannot change the request's outcome. |
| Connection errors | Only a genuine `Connectivity` failure counts as one. An HTTP error status never raises a connection error. |

### Redirects and credentials

Redirects are followed **by the pipeline, not by the transport** — the transport request always carries
`followRedirects = false`. `UnityWebRequest`'s internal following gives no opportunity to inspect the
target, and the whole point is to inspect it:

- the target scheme must be permitted, and encrypted when the route requires encryption;
- `SameOrigin` refuses a cross-origin target outright; `AllowedHosts` requires it to match the service's
  allowlist;
- the redirect count is bounded;
- **the credential is re-scoped against the new host.** It follows only to a host the credential profile
  itself authorizes. This is what stops a token walking off-domain behind a 302.

The credential is acquired per attempt against that attempt's host, never once up front. Acquisition is
single-flighted per profile, so a burst of 401s produces one refresh rather than many.

### Diagnostics

`INetworkDiagnostics` is a stable interface — Hub, Doctor, and MCP read it instead of discovering providers
by reflection. Records are redacted by construction: request and response headers are absent entirely
(an `Authorization` header being the likeliest leak), and the credential *profile name* stands in for the
value. Bodies are captured only under an explicit `CaptureBodies` opt-in, and truncated.

The buffer is bounded; counters (`TotalCompleted`, `TotalFailed`, `ObserverFailureCount`) keep accumulating
even while recording is paused, so "how many failed?" stays answerable.

### Credential providers

The catalog says *which* provider supplies a secret; an `INetworkCredentialProvider` supplies it. Core
ships only `EnvironmentVariableCredentialProvider` — the others need something Core does not own (a live
auth session, editor secure storage, a platform key store), so the SDK, the project, or the editor layer
registers those:

```csharp
[DependsOn(typeof(NetworkRuntimeSubsystem))]
public class MyAuthSubsystem : RuntimeSubsystem
{
    public override async Awaitable InitializeAsync(CancellationToken ct)
    {
        RuntimeManager.GetSubsystem<NetworkRuntimeSubsystem>()
            .RegisterCredentialProvider(new MyTokenProvider());

        await base.InitializeAsync(ct);
    }
}
```

A provider must never write a credential to a `ScriptableObject`, `PlayerPrefs`, `EditorPrefs`, or a log.
`NetworkCredential.ToString()` returns `[credential]` so an accidental interpolation is harmless.

## The Network workspace

Authoring lives in **Molca Hub → Network**, under the Infrastructure group. The catalog is a
`ScriptableObject`, so the Inspector still works, but the workspace is where the multi-environment shape is
actually visible.

| View | What it answers |
|---|---|
| **Overview** | Is this project ready to communicate safely? Counts, the binding matrix, credential readiness, and a prioritized action list |
| **Environments** | Identity, safety posture, build-target gating, and which services are bound here |
| **Services** | The per-environment binding grid, allowed hosts, and the resolved-origin preview |
| **Endpoints** | Collections and their templates, parameters, body/response, and the resolved URI |
| **Policies** | Authored profiles, and the effective-policy inspector with per-field provenance |
| **Credentials** | Profile metadata and scope. Never a value |
| **Providers** | SSE, WebSocket, and Socket.IO assets, authored against the same service/environment model |
| **Console** | Compose a request against a route, read what it will actually do, send it, read the redacted result |
| **Live** | In-flight requests, per-route queue and circuit state, the recent timeline, and streaming sessions |
| **Diagnostics** | The validation tree by severity, plus the legacy compatibility audit |

Three things about it are load-bearing rather than cosmetic:

- **The preview environment is a preview.** The toolbar selector changes what effective-value previews
  resolve under. It never writes to the catalog and never changes the runtime environment selection, so
  looking at how a route resolves in production cannot change what a build does.
- **Previews use the production resolver.** The resolved-origin and effective-policy panes call
  `NetworkRouteResolver` and `NetworkPolicyResolver` — the same code a request runs — so a preview and a
  live request cannot disagree. A second, preview-only resolver is exactly how the two drift.
- **A missing binding is rendered as missing.** The matrix shows an empty cell and the service detail says
  so. Copying an origin from one environment to another is an explicit action, because implicit fallback is
  how a staging build ends up talking to production.

### Streaming on a route

An SSE, WebSocket, or Socket.IO provider asset can name a **route** — a service, an environment strategy
(catalog default or one explicit environment), and a relative path — instead of a URL. The origin then comes
from the service's binding for that protocol, so a stream gets the same allowed-host list, the same
production scheme rule, and the same credential scope an HTTP request to that service gets.

```csharp
// Opened by the subsystem, not by the asset. Returned unstarted so the caller owns the lifetime.
var session = network.OpenSseSession("telemetry-stream",
    NetworkStreamRoute.Create("telemetry", "events"));
await session.RunAsync(cancellationToken);
```

Two rules are worth stating outright:

- **A route that fails to resolve stops the connection.** It never falls back to the URL still authored on
  the asset. Falling back would mean a provider whose binding was deleted quietly resumes connecting to a
  stale destination, which is the drift the catalog exists to remove.
- **A resolved route supplies the scheme.** The provider's own "use secure connection" toggle does not apply
  and cannot downgrade it — a production environment forces encryption regardless.

Leaving the route empty keeps the old behaviour exactly: the asset's URL is used, and none of the catalog's
rules apply to it. The Providers view marks that state amber for exactly that reason.

#### Sessions own the mutable state

`NetworkStreamSession` holds connection state, attempt count, last error, the cancellation source, the
reconnect budget, and the protocol handle. The provider asset holds none of it. That matters beyond
tidiness: a `ScriptableObject` is project data, so a provider recording its own connection status was
mutating an asset at runtime, and two scenes sharing one provider were overwriting each other's status.

The session base class owns the connect/reconnect/authenticate loop — route resolution per attempt, the
encrypted-transport check, scoped credential acquisition with one forced refresh on rejection, bounded
jittered backoff, redaction, and the give-up rules. A protocol implements one method. §6.7 warns against
flattening the transports into a lowest-common-denominator abstraction, so nothing in the base class models
frames, events, or acknowledgements.

`NetworkStreamSessionRegistry` lives on the subsystem, so teardown and domain reload close the sessions
rather than leaving sockets open and reconnect loops running. Opening a second session under a live id
closes the first, so a re-activated provider cannot end up with two connections.

**All three protocols run on sessions.** `SseStreamSession`, `WebSocketStreamSession`, and
`SocketIoStreamSession` each implement one method — connect and pump — and inherit route resolution, the
encrypted-transport check, scoped credential acquisition, bounded jittered backoff, the stable-connection
window, and the give-up rules from the base class. No provider asset holds a socket, a reconnect counter, or
a connection status any more. The serialized `connectionStatus` and `reconnectAttemptCount` fields survive so
existing assets deserialize, and are no longer written while the game runs; read `ConnectionStatus`, which
reads through to the session.

Two consequences worth knowing:

- **Socket.IO's own reconnection is switched off.** The library reuses the headers built when the socket was
  constructed, which is why the provider used to carry a hook that tore the socket down mid-reconnect
  whenever the auth token had changed underneath it. That is gone — every attempt is a fresh connect with a
  freshly resolved route and a freshly acquired credential. `Randomization Factor` on the asset is superseded
  by the shared policy's jitter and is kept only for deserialization.
- **A direct URL still works, and still buys none of the rules.** A provider with no route connects through a
  session too — so the state fix does not depend on adopting the catalog — but the binding carries
  library-default policy, no credential profile, and no allowed-host list. Adopting the catalog is what buys
  enforcement.

A provider's own token (from `AuthManager`, not from a credential profile) refreshes through the session's
`TryRefreshExternalCredentialAsync` hook: once per rejection episode, then the session faults and raises
`AuthEvents.Expired`. The base class does not know `AuthManager` exists.

### The request console

The console executes through the production `RoutedHttpClient` — the same resolver, policy pipeline,
credential scoping, redirect handling, retry, bulkhead, and circuit breaker a build runs. It is a separate
client instance from the running game's, so it follows catalog edits made seconds ago and its failures
cannot open a circuit that play mode then trips over.

Four properties are what make it safe to put a send button in the editor:

- **It cannot address a host the catalog did not bind.** A draft names an environment and a service; the
  origin comes from the binding. There is no "full URL" field, because a control that sends wherever you
  type, carrying whatever headers are in the panel, is the leak the catalog exists to close.
- **A production mutation is confirmed per send, and only if the catalog opted in at all.** Turn on
  `AllowProductionConsoleMutations` to permit them; each one still prompts. There is no "don't ask again".
  A `POST` whose `MutationClass` was never reviewed counts as a mutation, because `Safe` is the enum's zero
  value and an unset field must not read as a review.
- **Credentials are withheld unless the profile opted in.** `UsableFromRequestConsole` is enforced at
  acquisition, not in the UI, so no console code path can reach a credential the catalog withheld. Only the
  environment-variable provider is registered in the editor; the console names the profile and never a value.
- **Transport safety is not overridable from here.** There is no TLS-validation toggle and no allowed-host
  override, because a lower-precedence layer may tighten a security rule but never weaken one.

Headers and bodies live in memory for the life of the workspace and are never written to preferences, an
asset, or an export. History is bounded and redacted: request headers are not retained at all, query values
are masked, response bodies are recorded only when you opt in and are masked for credential-shaped fields.

### Live

`INetworkDiagnostics` is the stable read interface the network subsystem registers, and `Live` reads it
rather than reaching into the client. Streaming state comes through `INetworkStreamStatus`, which replaced a
reflective `ConnectionStatus` lookup — the WebSocket and Socket.IO providers only compile under their own
define symbols, so the editor cannot name their types, but it can test for an interface that is always
compiled.

The runtime and console sections are kept apart on purpose. Merging them would make "did my game send that,
or did I?" unanswerable, and they have separate circuit breakers for the same reason.

Long operations — a legacy scan, a migration, a console send, a diagnostics export — report a chip in the
Hub's bottom activity rail with a ✕ that cancels. Short synchronous work does not: validating a catalog is
microseconds, and a chip that flickers is how a rail stops being read. The chips are not remote-safe, since
a caption names a route and can name a host.

### Deep links

Other surfaces navigate in by value, not by reaching into the view:

```csharp
NetworkHubWorkspace.Open(NetworkHubNavigationTarget.Service("identity", "staging"));
NetworkHubWorkspace.OpenLink("workspace=network&view=services&entity=identity&environment=staging");
```

The string form round-trips, escapes its values, and ignores keys it does not understand, so a link written
by a newer version still navigates to the part an older one can reach. Doctor findings and MCP results carry
this form.

The Settings rail's **Network** leaf keeps its live runtime telemetry and gains a catalog health line plus a
link here. It is deliberately a summary and not a second authoring surface: a lesser copy of the catalog
editor would eventually disagree with this one about what a route resolves to.

## Importing an OpenAPI document

**Hub → Network → Endpoints → Import OpenAPI…** reads an OpenAPI 3.x or Swagger 2.0 **JSON** document and
turns its operations into endpoint templates. YAML is refused rather than half-parsed — Unity ships no YAML
parser and Molca will not add a dependency for one, so convert first
(`npx @redocly/cli bundle spec.yaml -o spec.json`).

Import is always **preview then apply**. The preview is a diff, computed purely from the document plus the
collection, so what you approve is what happens:

| Marker | Meaning |
|---|---|
| `+` | No endpoint matches this operation; one will be created |
| `~` | An imported endpoint matches and the spec changed; it will be rewritten, with the field-level changes listed |
| (blank) | Already up to date — the recorded content hash matches |
| `!` | **Conflict.** An endpoint authored by hand holds the ID. Import will not overwrite it |

Identity is the spec's `operationId`, recorded on the endpoint as its `SourceReference`. That means you can
rename an imported endpoint freely and a re-import still recognizes it — and that applying the same spec
twice writes nothing the second time.

Three things import will not do, all for the same reason: a spec is written by someone who does not know how
your project is configured.

- **It never overwrites a hand-authored endpoint.** Replacing one would discard the policy profile, mutation
  class, or idempotency requirement an author attached to it.
- **It never binds a service to a `servers` URL.** The servers are shown in the diff. Binding is a separate,
  deliberate act — the same reason routes never fall back between environments.
- **It never creates a credential profile.** A spec can say an operation needs authentication; it cannot say
  where the secret comes from or which hosts may receive it. Parameters a security scheme names import as
  `Sensitive` (so their values are redacted) with no default value, and that is where import stops.

What an update *keeps*: the endpoint's ID, display name, policy profile, and idempotency requirement — those
are decisions about your project. What it *replaces*: method, path, parameters, body kind, example, tags, and
description. Mutation class is inferred from the method, erring toward "this changes something" — `DELETE`
imports as `Destructive` — because the console's production confirmation keys off it and a wrong guess in the
safe direction only costs an extra confirmation.

An operation that has left a newer spec is reported as an **orphan** and left in place, never deleted.

Automation uses the same three calls: `molca_network_import_openapi` previews by default and takes
`apply: true`.

## Automation and Doctor

Everything the Hub does is reachable from MCP, through the same services: `molca_network_catalog`,
`molca_network_validate`, `molca_network_edit`, `molca_network_migrate`, `molca_network_send`, and
`molca_network_diagnostics` (see [Core MCP Tools](CORE_MCP_TOOLS.md)). Automation gets no privileged path -
it cannot weaken a security rule, bypass Undo, or send a production mutation, which is *refused* rather than
prompted because there is no user at an MCP call to confirm one.

Doctor consumes the same validator rather than implementing networking rules of its own:

| Check | Reports |
|---|---|
| `network-catalog` | Catalog validation findings, plus an unregistered catalog or a pending schema migration |
| `network-provider-route` | A streaming provider still on a raw URL while the project has a catalog |
| `http-hardcoded-url` | Endpoint literals in project runtime code |

A finding's code, its message, and its remedy read the same in the Hub, in Doctor, in an MCP payload, and in
a build failure. That is the point of one validator: a catalog that Doctor calls clean and the build gate
rejects would leave people trusting whichever they saw first.

Turn on `FailBuildOnValidationError` once the catalog is clean and `NetworkCatalogBuildValidator` fails the
build on an error rather than warning.

Core's own direct-transport uses are audited and documented separately - see
[Sanctioned Direct-Transport Exceptions](NETWORKING_TRANSPORT_EXCEPTIONS.md).

## Schema versioning

`NetworkCatalog.SchemaVersion` is an explicit integer; version 1 is the initial shape.
`NetworkCatalogSchemaMigrator` upgrades older assets deterministically. Migrations are previewable and
rerunnable, run under one Undo group, record provenance on the asset, never read secrets, and refuse to
write to packages or other read-only locations. A catalog *newer* than the installed framework is refused
rather than downgraded, since downgrading would silently drop fields.

Future schema changes are additive: new fields default to reproducing the previous version's behaviour,
fields are never repurposed, and removal happens only through the package versioning policy.

## Compatibility

Nothing in the existing networking API changed. `IHttpClient`, `HttpClient`, `HttpRequest`,
`HttpRequestAsset`, `HttpModule`, the interceptor interfaces, and `IHttpTransport` keep every member and
serialized field. Existing request assets and providers keep working exactly as before.

The one transition switch to know about is `AllowLegacyGlobalAuthOnExternalUrls`. Enabling it keeps
applying global `HttpModule` authentication to unrelated full URLs — the credential-leak boundary this
model exists to close. It is never set for a new catalog, and the validator warns wherever it is on.

### Bringing a legacy project across

Two things happen independently, and both are opt-in per project:

- **Adopting the catalog.** `LegacyMigrationExecutor` scans, previews, and authors the catalog alongside
  the existing assets. Nothing legacy is deleted or rewritten, and the run is cancellable and rerunnable.
- **Switching execution.** `RouteLegacyHttpThroughPipeline` makes `IHttpClient` execute a mapped legacy
  send on this pipeline. Only the transport-and-retry middle moves; the API, events, history, and
  interceptors are untouched, so no call site changes.

The credential correction is *not* gated on either switch — once a catalog exists, process-wide credentials
stop travelling to hosts no service claims. `LegacyRouteMapper` is the single place that decision is made,
and it is pure, so the editor's preview and the runtime client cannot disagree.

See [`NETWORKING_MIGRATION.md`](NETWORKING_MIGRATION.md) for the step-by-step guide.
