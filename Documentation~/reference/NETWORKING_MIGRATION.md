---
title: Migrating to the Network Catalog
category: Data & Networking
order: 402
---

# Migrating from the global base URL to network routes

For projects upgrading to the routed networking model described in
[`NETWORKING_CATALOG.md`](NETWORKING_CATALOG.md).

**Nothing in your project has to change today.** `IHttpClient`, `HttpRequest`, `HttpRequestAsset`, and
every data provider keep working exactly as they do now, and migration never deletes or rewrites one of
them. This document exists so you can decide *when* to adopt the catalog and know what each step buys
you.

One correction does take effect as soon as a catalog exists, and it is deliberate: a request to a host no
catalog service claims no longer receives your process-wide credentials. [Read why](#the-one-behaviour-change),
and note the transition flag if you need the old behaviour for a window.

## What actually changed, and why it had to

`HttpModule.BaseUrl` assumes the application talks to one backend. The moment a project needs two — an
identity service and a content CDN, or staging and production side by side — the base URL stops being
configuration and becomes something call sites work around by passing full URLs.

That workaround is where the security problem lives. Global default headers and a global auth interceptor
are applied to *every* outgoing request, including a full URL to a third-party host, because neither the
header nor the interceptor can tell your backend from someone else's API. A route can: it names an
environment and a service, so the credential's scope is checked against the host the request is actually
about to reach.

## Do I need to do anything?

| Situation | Action |
|---|---|
| One backend, relative URLs only, no auth | Nothing. Adopt the catalog when you want per-route policy. |
| One backend, but you want timeouts and retry per service | Migrate. Policy profiles are the payoff. |
| **Full URLs to third-party hosts, and global auth configured** | Migrate, or read [the one behaviour change](#the-one-behaviour-change) first. |
| Two or more environments selected by editing the base URL | Migrate. This is the case the old model could not express. |
| Streaming providers (SSE, WebSocket, Socket.IO) | Migrate to get services bound; provider convergence lands in a later phase. |

## Step 1 — Scan, and read the dry run

Scanning is read-only. It reports what exists; it proposes nothing until you ask.

```csharp
var plan = LegacyMigrationExecutor.DryRun();
Debug.Log(plan.Report.Describe());   // what exists today
Debug.Log(plan.Describe());          // what migration would create, and what it would leave alone
```

The scan finds your `HttpModule`, every `HttpRequestAsset`, and every data provider, and describes each in
catalog terms — which host it reaches, which protocol it speaks, and whether it opts into
authentication. Provider URLs are read from their serialized form, including the `wss://` or `https://`
scheme they prepend at connect time, so the origin the plan writes is the origin the provider was actually
reaching.

Both descriptions are deterministic for a given project state, so you can diff them across runs.

## Step 2 — Apply

```csharp
var result = LegacyMigrationExecutor.Apply(plan);
Debug.Log(result.Summarize());
```

Every write goes through `NetworkCatalogEditingService`, the same path Hub authoring uses, and the whole
run collapses into one Undo step. Applying creates:

- an **environment** — reusing your catalog's existing default if it has one, rather than inventing a
  second;
- a **policy profile** carrying the `HttpModule`'s timeout, retry, and concurrency settings, set as the
  catalog default;
- a **service** for `HttpModule.BaseUrl`, named `molca-legacy-default`, bound to that origin;
- **one service per distinct foreign host**, declaring the union of the protocols the artifacts on that
  host speak;
- an **endpoint collection** holding one endpoint per `HttpRequestAsset`, each recording the asset's GUID
  as its source;
- a **credential profile**, if anything opts into authentication — see the next section.

It is safe to cancel and safe to re-run. Cancelling stops after the current step and keeps what landed;
re-running recomputes the plan from a fresh scan, which yields only the steps that remain. The idempotence
key is the endpoint's own provenance: an endpoint recording a request asset's GUID is never migrated a
second time, and deleting that endpoint correctly makes the asset eligible again.

### Why the timeout number changes

The legacy `DefaultTimeout` governed a single transport attempt. The routed overall budget also covers
queueing, credential acquisition, and retry backoff, so migration sets the *attempt* timeout to your
authored value and computes an overall budget large enough for the worst-case attempt count. Copying the
number into both would time out sends the legacy client completed.

## Step 3 — Scope the credential yourself

Migration creates the credential profile **unscoped**, and never assigns it to a service. That is not an
omission.

An unscoped profile denies every host, so a freshly migrated catalog cannot send a credential anywhere
until you say where it may go. The alternative — scoping the profile to every host that happened to
declare an `Authorization` header — would rebuild the exact leak the routed model exists to close, inside
the new model, while looking like a completed migration.

So, deliberately by hand, in **Molca Hub → Network → Credentials**:

1. Set the profile's provider kind to whatever supplies your token.
2. List the **services** that may use it.
3. List the **host patterns** it may reach. Exact hosts, or a single leading `*.` covering at least two
   labels.
4. On each service that needs it, set the credential profile ID.

Then run validation. A service naming a credential whose scope excludes it, or a bound origin outside the
credential's host scope, is reported rather than silently sending anonymously.

## Step 4 — Switch execution over, when you are ready

Adopting the catalog does not by itself change which pipeline runs your requests. `IHttpClient` keeps
using the legacy transport-and-retry loop until you set **Route legacy HTTP through pipeline** on the
catalog.

When you do, a legacy send that maps to a route executes on the routed pipeline instead: per-route
bulkhead and circuit breaker, scoped credentials, redirect revalidation, and redacted diagnostics. Only
the transport-and-retry middle moves — the `IHttpClient` API, the events it raises, the request history,
and your interceptors all stay where they are, so no call site changes.

A request that does not map to a route (an unclaimed host, or a path under no bound origin) stays on the
legacy path. Nothing has to be fully migrated before you can turn this on.

### How a request maps to a route

| Request | Result |
|---|---|
| Relative URL, `molca-legacy-default` exists | Routed to it, path preserved |
| Full URL under a bound service origin | Routed to that service; the longest matching origin wins |
| Full URL to a bound host, but under no bound origin's path | Not routed. Bind the origin that covers the path |
| Full URL to a host no service binds | Not routed, and credentials are withheld |

Only the **default environment's** bindings are candidates. A legacy call site names no environment, so
routing it to a non-default one would silently retarget it.

## The one behaviour change

Once a catalog exists, a `useFullUrl` request to a host no catalog service claims no longer receives:

- `HttpModule` default headers whose name is a credential header — `Authorization`,
  `Proxy-Authorization`, `Cookie`, or any header a catalog credential profile names;
- any registered `IHttpCredentialInterceptor`, which includes `AuthTokenInterceptor`.

A header the *request itself* authored is untouched. That was a deliberate decision about that host, and
the correction is only about process-wide credentials that were never aimed anywhere in particular.

A project with **no** catalog is unaffected — behaviour is exactly what it was, though the client logs
once when it sees a credential heading for a host that is not the base URL's.

If you need the old behaviour while you finish authoring external hosts as services, set
**Allow legacy global auth on external URLs** on the catalog. It is never enabled for a new catalog, it
logs a warning wherever it is honoured, and catalog validation reports it as a security finding until you
clear it.

## What migration leaves alone

- **`HttpRequestAsset` files.** Kept and still sendable. The migrated endpoint is an additional, routable
  way to reach the same thing.
- **Data providers.** Kept with their own URLs, still connecting. What migration does is ensure a service
  exists for each provider's host, so a later phase can move them onto routed sessions without you
  re-authoring URLs.
- **`HttpModule`.** Read, never written. Its base URL still resolves relative URLs on the legacy path.

## Finding what is left

`LegacyCompatibilityAudit.Audit()` reports the remaining work as validation findings that navigate to the
artifact needing attention, not to the catalog:

| Code | Meaning |
|---|---|
| `network.legacy.catalog-not-adopted` | Legacy configuration exists and there is no catalog |
| `network.legacy.base-url-not-bound` | A base URL is set but no service is bound to it |
| `network.legacy.full-url-with-credential` | A request reaches a full URL and declares a credential header |
| `network.legacy.unclaimed-host` | A request's host is bound by no service |
| `network.legacy.request-asset-unmigrated` | A request asset has no migrated endpoint yet |
| `network.legacy.provider-not-bound` | A stream provider's host is bound by no service |

These codes are API. They are added to, never renamed.

## See also

- [`NETWORKING_CATALOG.md`](NETWORKING_CATALOG.md) — the catalog model, policy precedence, and the routed
  pipeline.
- [`NETWORKING.md`](NETWORKING.md) — the legacy HTTP subsystem, which remains supported.
- [`DATA_PROVIDERS.md`](DATA_PROVIDERS.md) — provider authoring.
