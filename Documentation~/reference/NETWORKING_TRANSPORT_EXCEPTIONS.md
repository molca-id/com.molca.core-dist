---
title: Sanctioned Direct-Transport Exceptions
category: Data & Networking
order: 403
---

# Sanctioned Direct-Transport Exceptions

Molca's rule is that HTTP traffic goes through the governed pipeline — `IRoutedHttpClient`, resolved
against the [network catalog](NETWORKING_CATALOG.md), so it inherits allowed hosts, production scheme
enforcement, credential scoping, timeouts, retry, redirect revalidation, and redacted diagnostics.

A handful of places in Core construct a `UnityWebRequest` directly. Each one is listed here with the reason,
and the list is enforced: `CoreTransportExceptionAuditTests` fails the build if a new direct-transport site
appears without being added to both the test's allowlist and this document. It also fails if an entry here
goes stale, so the list cannot quietly grow into a blanket exemption.

**None of these carry project traffic.** They are the pipeline's own floor, Molca's framework
infrastructure, or a single URL the project names with no route to resolve.

## The pipeline floor

These *are* the governed pipeline, or are kept verbatim by the compatibility contract.

| File | Why |
|---|---|
| `Runtime/Networking/Http/Transport/UnityWebRequestTransport.cs` | The HTTP transport the pipeline executes attempts on. Something has to touch the engine API. |

| `Runtime/Networking/Streaming/UnityWebRequestStreamTransport.cs` | The chunked-stream transport, behind `INetworkStreamTransport`. |

Neither the legacy `HttpClient` nor any streaming provider is on this list. `HttpClient` executes every
attempt through `IHttpTransport`, and the SSE, WebSocket, and Socket.IO providers all connect through a
subsystem-owned session — so they are inside the governed floor even when they connect to a directly
authored URL.

Reviewed as: unavoidable. Both transports validate nothing themselves by design — validation belongs to the
pipeline above them, which is what makes them substitutable in tests.

## Framework infrastructure

Clients that talk to Molca's own services. The catalog describes what the **project** communicates
with; framework infrastructure is not in it, and adding it would mean every project's catalog carried
entries for endpoints the project does not own.

Most are editor-only. The one runtime entry is marked as such, because a shipped build reaching the
control plane is a higher bar than an editor tool doing it and deserves to be visible in this table.

| File | Destination | Why |
|---|---|---|
| `Editor/About/FrameworkUpdateClient.cs` | Control plane release feed | Framework version checks, authenticated by the developer entitlement. Editor-only, cached, never runs in batch mode. |
| `Editor/Addons/AddonCatalogClient.cs` | Control plane add-on catalog | Lists purchasable/installed add-ons. Editor-only. |
| `Editor/Addons/AddonInstaller.cs` | Control plane package downloads | Fetches an add-on tarball. Editor-only, user-initiated. |
| `Editor/Licensing/DevLicenseClient.cs` | Control plane licensing | Exchanges and verifies the developer entitlement. Handles a credential, so it must not go through a project-configurable pipeline. |
| `Editor/Localization/LocalizationRemoteCatalogEditorClient.cs` | Control plane localization publication | Previews and publishes a localization overlay bundle. Editor-only, user-initiated, and it carries the developer entitlement — same reasoning as `DevLicenseClient`. Pins the endpoint to `DevLicenseConfig.ServerBaseUrl` and re-checks it against `AddonDistributionConfig.IsTrustedDownloadHost` before sending. |
| `Runtime/Localization/LocalizationRemoteCatalogClient.cs` | Control plane localization catalog (**runtime**) | Fetches the signed overlay manifest and bundle for a shipped build, authenticated by the project-scoped build token from the license stamp. Defaults to the stamp's own `serverBaseUrl`. Enforces its transport policy inline: HTTPS or loopback only, an allowed-download-host list for a cross-origin bundle, the bearer token attached **only** when the bundle host matches the manifest origin, and a size-bounded download handler. |
| `Editor/ContentPackage/ContentAuthoringClient.cs` | Control plane content releases, and the presigned storage URLs it hands back | Publishes a content release: creates the draft, PUTs each object to a URL the server signed, finalizes, promotes. Editor-only and user-initiated. Carries the developer entitlement, so the `DevLicenseClient` reasoning applies — routing it through a project-editable catalog would let a project's configuration redirect a publishing credential. The object PUTs additionally *must not* be routed: they go to a storage host, the presigned URL is its own credential, and attaching a Molca header would hand a developer session to that host. Enforces its transport policy inline: HTTPS or loopback for both the control plane and every upload destination, no Molca credential on a storage request, and failures that never echo the signed URL. |
| `Editor/Projects/MolcaProjectApiClient.cs` | Control plane project API | Project registration and metadata. Editor-only. |
| `Editor/Telemetry/MolcaEditorTelemetry.cs` | Control plane telemetry | Editor usage telemetry. Editor-only, opt-in. |
| `Editor/Licensing/ControlPlaneBuildRecorder.cs` | Control plane build ledger | Reports a shipped player's provenance against the build token minted for it (`POST /builds/:buildId/record`). Editor-only, and it carries the developer entitlement — the `DevLicenseClient` reasoning applies unchanged: routing a credential-bearing call to a fixed Molca endpoint through a project-editable catalog would turn a project's configuration into a redirect primitive for that credential. Enforces its transport policy inline, the same three checks as its neighbours: the endpoint is pinned to `DevLicenseConfig.ServerBaseUrl`, HTTPS is required, and the host is re-checked against `AddonDistributionConfig.IsTrustedDownloadHost` before sending. Sends no output path and no identity. |
| `Editor/Networking/EditorHttpClient.cs` | Whatever the caller's `HttpRequest` names | The edit-mode sender for `HttpRequest` objects, used by editor tooling that has no running `RuntimeManager`. Superseded for catalog work by `NetworkConsoleRunner`, which does use the routed pipeline. |

Reviewed as: sanctioned. `DevLicenseClient` deserves the specific note that routing it would be *worse*,
not better: it carries a credential to a fixed Molca endpoint, and putting that exchange behind a
project-editable catalog would make a project's configuration able to redirect a licensing credential.
The same argument covers both localization catalog clients — each carries a Molca-issued token, so a
project-editable route would be a redirect primitive for that token, not a control on it.

What would change this for `Runtime/Localization/LocalizationRemoteCatalogClient.cs`: it is the one
runtime credential-bearing entry, and it is sanctioned *because* it enforces scheme, host, credential
scope, and download size itself. If any of those inline checks is removed, or the settings grow a way to
point the manifest at an arbitrary host without the allowed-host list also governing it, this stops being
an exception and becomes a catalog route with a scoped credential profile. Revisit it too if the routed
pipeline gains bounded-download support, since that is the remaining capability gap.

## Destinations the project names as a single URL

No route to resolve — the project supplies one absolute URL and nothing else.

| File | Why |
|---|---|
| `Editor/Settings/Notification/WebhookService.cs` | A webhook URL the project pastes in. There is no service, no environment matrix, and no credential profile behind it. |
| `Runtime/Telemetry/HttpBatchTelemetrySink.cs` | The telemetry endpoint is one configured URL. Routing it would change a published configuration contract for no security gain: the POST carries no credential, and its failures are already spooled and logged without the URL. |
| `Runtime/Telemetry/ControlPlaneTelemetrySink.cs` | Runtime telemetry to Molca's control plane. Framework infrastructure, as above. |
| `Runtime/ContentPackage/Services/PackageService.cs` | Addressables catalog reachability probes. Addressables owns those URLs; Molca only checks whether one answers. |

Reviewed as: sanctioned, and reconsider if any of them grows a credential. A credential is the line — the
moment one of these needs to authenticate to a project-configured host, it belongs on a catalog route with a
scoped credential profile, because that is the only place scoping is enforced.

## Adding one

Don't, if a route will do. If it genuinely won't:

1. Add the package-relative path to `Sanctioned` in `CoreTransportExceptionAuditTests`.
2. Add a row here, in the category it belongs to, with the destination and the reason.
3. Say what would change your mind — the conditions under which it should be migrated.

The test asserts (1) and (2) agree. (3) is what a future reader needs and no test can check.

## What this does not cover

Project and SDK code is not audited here. Doctor's consumer-facing
[networking checks](DOCTOR_CHECKS.md) cover that side: `http-hardcoded-url` flags endpoint literals,
`network-catalog` reports catalog validation findings, and `network-provider-route` flags a streaming
provider still on a raw URL while a catalog exists.

## See also

- [Routes & Catalog](NETWORKING_CATALOG.md)
- [Migrating to the Network Catalog](NETWORKING_MIGRATION.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
