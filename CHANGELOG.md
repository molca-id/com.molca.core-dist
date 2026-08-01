# Changelog

All notable changes to Molca Core will be documented here.

## [1.18.0] - 2026-08-01

> ### ⚠ This is a minor version that contains breaking changes. Read this before upgrading.
>
> The version number does not warn you, so this note has to. A major bump would currently hide every
> add-on from **Hub ▸ Add-ons**: each pack's Core range is derived from its own manifest at upload time
> and stored on an immutable version row, so admitting a new Core major means republishing every pack,
> not editing a setting. The major is being saved for the release that earns it. Nothing about this
> number reflects the size of the change.
>
> Two changes here can break an existing project, and both are visible immediately rather than at
> runtime:
>
> 1. **Core ships no assets.** Every `.asset` and `.prefab` that lived inside the package is gone, so
>    any reference to one now resolves to null. Most projects will see this as a missing setting module
>    in `GlobalSettings` and a missing modal prefab on `ModalManager`.
> 2. **The onboarding wizard no longer offers SDK Quick Setup.** The same setup runs from
>    **Molca ▸ SDK ▸ Quick Setup** while `com.molca.sdk` is installed.
>
> **The repair is a button.** Open **Molca ▸ Hub ▸ Remediation** and run the pass: it compacts the null
> module entries and regenerates the missing modules into project space, where you own them. **Molca ▸
> Hub ▸ Onboarding ▸ Project Starter** creates anything else that was never configured. Modal prefabs
> and the two utility singletons are content and must be re-authored — nothing can regenerate a
> prefab's layout.
>
> Everything a package asset used to provide now comes from code (`ScriptableObject.CreateInstance` plus
> the type's own defaults), so an upgrade can no longer overwrite configuration you have edited.

### Added
- **A first-class Localization workspace in Molca Hub.** The complete audit, locale, catalog, CSV,
  migration, pseudo-preview, and remote-publication authoring surface now registers as an Authoring
  workspace with the shipped localization icon and a stable `localization` id. It is scrollable, cached
  across tab switches, discoverable through the tabs menu/search, and no longer buried in Settings.
  Its project-wide scan is explicit, so restoring the active tab after a domain reload stays responsive;
  legacy migration reuses the audit engine's YAML prefilter before loading serialized objects.
- **Tab management from the Hub toolbar.** An always-visible tabs menu at the right end of the workspace
  strip lists every workspace the project could show, checked when it is in the toolbar, so a tab is shown
  or hidden in one click — previously a hidden tab could only be brought back from Settings ▸ Editor,
  because hiding it removed the only element there was to right-click. The menu also carries a Pin submenu
  and still deep-links to the fuller Workspace Tabs settings card, which is unchanged. Pinned tabs now
  render an accent dot, so what is holding a toolbar slot is visible without opening a menu.
- **Globalization readiness.** Locale presentation profiles now declare primary/fallback TMP fonts,
  required glyph coverage, writing direction, and line-breaking guidance. Authored locale fallback
  graphs are shared by inline text and localized audio, with cycle/unknown-target diagnostics.
  `LocalizedText` applies locale typography and direction, while `LocalizedLayoutDirectionAdapter`
  provides explicit opt-in horizontal-layout RTL behavior. Doctor and production builds expose missing
  profiles, fonts, glyphs, writing direction, plural/Smart String drift, and unsupported RTL layouts.
  Hub and read-only MCP tools provide accent/expansion, missing-key, RTL-stress, catalog, and loaded-UI
  overflow previews without mutating source content.
- **Versioned localized values and safe migration.** New fields use schema-v2 `LocalizedValue` with
  explicit `Catalog`, `Inline`, or `None` sources; `DynamicLocalization` remains a serialized and API
  compatibility subclass. A generation-safe disposable binding API supports reactive UI and Smart
  String arguments. Doctor, Hub, and MCP now share schema-aware discovery, report retained v1 payloads,
  and provide fingerprint-bound inventory/preview/migrate operations that preserve catalog references,
  inline locale identity, row order, and text in one verified Unity Undo transaction. Inspector and
  localized-audio authoring are non-destructive: missing locales are appended explicitly, while unknown
  and duplicate rows remain visible for author review.
- **Stable catalog authoring and CSV round trips.** The Localization Hub and MCP now browse every
  StringTable cell by stable collection/entry identity, preview single-cell edits and new keys, reject
  package-owned targets and placeholder mismatches, and execute as verified Unity Undo transactions.
  Deterministic RFC 4180 CSV export uses schema `molca.localization.catalog.v1`; import previews the
  entire file and rejects unknown or stale identities, locale/key conflicts, smart-metadata changes,
  conflicting duplicates, placeholder mismatches, and read-only targets before applying any row.
  Shared audit/Doctor now reports catalog placeholder drift and promotes missing catalog values to
  build errors when complete production translations are required. Export neutralizes spreadsheet
  formula cells without changing round-tripped text; bounded import rejects oversized files.
- **Previewed transactional locale authoring.** The Localization Hub and MCP now share
  `LocalizationAuthoringService`: `molca_localization_plan_add_language` returns a read-only plan bound
  to the current audit fingerprint, and `molca_localization_add_language` applies that plan across the
  Molca module, Unity Locale registry, Addressables, StringTable collections, and AssetTable
  collections. Stale plans are refused; partial failures roll back; successful changes form one Unity
  Undo group and return a fresh audit snapshot. Locale removal is archive-first through
  `molca_localization_plan_archive_language` / `molca_localization_archive_language`: policy,
  registration, table membership, and Addressables are disabled while Locale/table assets and inline
  rows are preserved for restore or separately confirmed deletion. The final configured locale cannot
  be archived, and previews warn when fallback behavior will change.
- **`IRoutedHttpClient` and the routed HTTP pipeline**, registered by the new `NetworkRuntimeSubsystem`
  alongside `INetworkDiagnostics`. Additive: `IHttpClient` and every existing call site are untouched, and
  the routed client is opt-in until the legacy adapter lands. Failures are **returned** as a typed
  `RoutedHttpOutcome` — status, case-insensitive headers, attempts, timing breakdown, route, and
  `NetworkErrorCategory` — rather than requiring string inspection; only cancellation still throws, because
  a cancelled send has no outcome to describe.
- **Every request is frozen into an immutable `ResolvedHttpRequest` before it is queued.** Past that point
  nothing reads `GlobalSettings`, the catalog, a mutable request asset, or the Hub's selection, so editing
  configuration cannot change what an in-flight request does. The catalog snapshot is captured once at
  subsystem init. The transport request carries `useFullUrl`, which is what keeps
  `HttpRequest.FullUrl` from consulting a global base URL on the routed path.
- **The overall timeout now means what it says.** It covers queueing, authentication, retry delays and
  transfer, and each attempt's timeout is clamped to whatever is left of it. A backoff that would consume
  the entire remaining budget is skipped and reported rather than slept through to a guaranteed timeout.
- **Cancellation is immediate.** A queued request observes the token each frame, leaves the queue, and
  never reaches the transport — where the legacy client only checked at dequeue. A cancelled attempt is
  never retried.
- **Redirects are followed by the pipeline, not the transport**, which always gets
  `followRedirects = false`. `UnityWebRequest`'s internal following offers no chance to inspect the target,
  and inspection is the entire point: scheme and encryption are checked, `SameOrigin` refuses a cross-origin
  target while `AllowedHosts` requires an allowlist match, the hop count is bounded, and **the credential is
  re-scoped against the new host** so a token cannot walk off-domain behind a 302. Credentials are acquired
  per attempt against that attempt's host, single-flighted per profile so a burst of 401s produces one
  refresh.
- **Bulkhead, queue bound and circuit breaker are per route**, so a failing service cannot starve an
  unrelated one. Queue overflow fails fast instead of growing without bound; the breaker admits exactly one
  trial request when its reset window elapses; and cache hits and pipeline rejections deliberately do not
  move it, since neither is evidence about the backend.
- **Response caching restricted to what is unambiguously safe to replay**: GET only, successes only,
  anonymous only unless the policy opts into body capture — a credentialed response may be user-specific.
  Keyed by route, URI *and* credential profile so two identities never share an entry, and bounded with LRU
  eviction.
- **Observer callbacks are isolated.** An observer that throws is counted in diagnostics and cannot change a
  request's outcome, because an observer is usually project code and a bug there must not turn a successful
  request into a failed one.
- **`INetworkDiagnostics`**, a stable interface for Hub, Doctor and MCP to read instead of discovering
  providers by reflection. Records are redacted by construction — request and response headers are absent
  entirely, and the credential *profile name* stands in for the value. Bodies are captured only under an
  explicit opt-in, and truncated. The ring buffer is bounded while the counters keep accumulating, so "how
  many failed?" stays answerable even when detail is not retained.
- **`INetworkCredentialProvider`** plus `EnvironmentVariableCredentialProvider`. The catalog says which
  provider supplies a secret; the provider supplies it. Core ships only the environment-variable one — the
  others need something Core does not own — and `NetworkCredential.ToString()` returns `[credential]` so an
  accidental interpolation cannot leak a value.
- **One resolver, not two.** `NetworkRouteResolver` is used by both the runtime pipeline and the Hub's
  authoring preview; `NetworkEffectiveConfigurationService` became a thin projection over it, so a preview
  and a live request cannot disagree about where a route goes.
- **Legacy compatibility adapter.** `LegacyRouteMapper` decides, from the catalog snapshot alone, which
  route a legacy `HttpRequest` belongs to — a relative URL goes to the migrated `molca-legacy-default`
  service, and a full URL to whichever service binds the longest matching origin in the *default*
  environment (a non-default one would silently retarget a call site that named no environment). It is
  pure, so the editor's preview and the runtime client cannot disagree. Setting
  `NetworkCatalog.RouteLegacyHttpThroughPipeline` then makes `IHttpClient` execute a mapped send on the
  routed pipeline through `RoutedLegacyHttpAdapter`. Only the transport-and-retry middle moves: the
  `IHttpClient` API, its events, the request history, and registered interceptors are untouched, so no call
  site changes. Off by default — adopting the catalog and switching execution are separate decisions.
- **Legacy scan, dry run, and migration.** `LegacyNetworkScanner` reads the `HttpModule`, every
  `HttpRequestAsset`, and every data provider — provider URLs from their serialized form, including the
  scheme they prepend at connect time, so the origin recorded is the origin they actually reached.
  `LegacyMigrationPlan` is a pure function of that scan, so the preview and the applied change come from one
  object and cannot describe different things. `LegacyMigrationExecutor` writes only through
  `NetworkCatalogEditingService`, collapses the run into one Undo step, and is cancellable and rerunnable —
  the idempotence key is the created endpoint's own provenance, so deleting a migrated endpoint correctly
  makes its source asset eligible again. No legacy asset is modified or deleted.
- **`LegacyCompatibilityAudit`** reports what remains as validation findings whose navigation target is the
  legacy artifact rather than the catalog. Kept separate from `NetworkCatalogValidator`, which stays a pure
  function of a catalog so it can run in a build gate and on an in-memory instance.
- **`IHttpCredentialInterceptor`**, letting an interceptor declare that it injects a credential so
  `HttpClient` can withhold it from an unauthorized destination. `AuthTokenInterceptor` implements it.
  Additive; existing interceptors are always run, as before.
- **Focused policy-profile setters on `NetworkCatalogEditingService`** — `SetPolicyTimeouts`,
  `SetPolicyRetry`, `SetPolicyConcurrency` — plus `RecordLegacySource`, and optional source provenance on
  `CreateHttpEndpoint`. Each is its own Undo step, so reverting a timeout change does not also revert a
  retry change.

- **The Hub's Network workspace**, in a new `Infrastructure` workspace group between Quality and
  Assistance. Seven views over the catalog — Overview, Environments, Services, Endpoints, Policies,
  Credentials, Diagnostics — behind a toolbar carrying the catalog identity, an authoring preview
  environment, and a validation badge. The preview environment is a preview: it changes what
  effective-value panes resolve under, never the runtime selection and never the asset. Previews call the
  production `NetworkRouteResolver` and `NetworkPolicyResolver` rather than a preview-only reimplementation,
  so what an author sees and what a request does cannot drift. Every write goes through
  `NetworkCatalogEditingService`, so Undo, dirty tracking, and validation behave identically whether a
  change came from the Hub, from MCP, or from migration.
- **The effective-policy inspector shows provenance per field** — the value and the layer that supplied
  it, with the whole `Library default → Catalog → Environment → Service → Endpoint → Send override` chain in
  the tooltip. Security-restricted fields say they are tighten-only, and any clamp that overruled a weaker
  authored value is listed rather than silently applied.
- **The service binding grid renders a missing binding as missing.** Copying an origin to another
  environment is an explicit action; nothing fills a gap from a neighbouring environment, because that is
  how a staging build ends up talking to production.
- **Deep links into the workspace are a value object**, `NetworkHubNavigationTarget`, with a round-tripping
  string form (`workspace=network&view=services&entity=identity&environment=staging`) that escapes its
  values and ignores keys it does not understand. Static pending fields would let two near-simultaneous
  navigations interleave and would give every future navigation source a field to remember to clear.
- **Structured search across the catalog** — environments, services, endpoints, policies, and credentials,
  including a service matched by its bound host. Results navigate straight to the detail they name rather
  than filtering the current list.
- **An empty-state flow for a project with no catalog**: create a catalog, run the read-only legacy scan, or
  open the guide. The scan shows what exists and what migration would do before anything changes.
- **A request console in the Hub**, executing through the production `RoutedHttpClient` rather than a
  console-specific sender — same resolver, policy pipeline, credential scoping, redirects, retry, bulkhead,
  and breaker a build runs. It cannot address a host the catalog did not bind: a draft names an environment
  and a service, and the origin comes from the binding, so there is no "full URL" field to type a
  destination into. Preflight shows the resolved destination with query values masked, the effective policy
  with per-field provenance, the credential *profile name*, and what is surprising about the send.
- **A production mutation is confirmed per send, and only when the catalog opted in** via
  `AllowProductionConsoleMutations`. There is no "don't ask again". A `POST` whose `MutationClass` was never
  reviewed still counts as a mutation, because `Safe` is the enum's zero value and an unset field must not
  read as a review.
- **`UsableFromRequestConsole` is enforced at credential acquisition**, not in the UI, so no console code
  path — including a future one — can reach a credential the catalog withheld. Only the environment-variable
  provider is registered in the editor. There is deliberately no TLS-validation toggle and no allowed-host
  override on a send: a lower-precedence layer may tighten a security rule but never weaken one.
- **A Live view** over `INetworkDiagnostics`: in-flight and queued counts per route, circuit state, the
  recent redacted timeline filtered by service, outcome, and correlation ID, and streaming session state.
  The running game's pipeline and the console's own client are shown separately — merging them would make
  "did my game send that, or did I?" unanswerable.
- **`INetworkStreamStatus`**, a stable two-member contract the SSE, WebSocket, and Socket.IO providers
  implement. It replaces a reflective `GetProperty("ConnectionStatus")` lookup that existed because the
  optional providers only compile under their own define symbols; an interface declared in the always-compiled
  assembly can be type-tested instead.
- **Hub activity chips for long networking operations** — legacy scan, migration, console send, diagnostics
  export — with progress and a ✕ that cancels. Short synchronous work registers nothing: validating a catalog
  is microseconds, and a chip that flickers is how a rail stops being read. The chips are not remote-safe,
  since a caption names a route and can name a host.

- **Streaming on a catalog route.** An SSE, WebSocket, or Socket.IO provider can name a service, an
  environment strategy, and a relative path instead of a URL; the origin comes from the service's binding for
  that protocol. Streams then get the same allowed-host list, production scheme rule, and credential scope an
  HTTP request gets, because `NetworkStreamBinding` is a projection of the same `NetworkRouteResolution` — not
  a second resolver. A route that fails to resolve stops the connection rather than falling back to the URL
  still on the asset, and a resolved route supplies the scheme, so a provider's own "use secure connection"
  toggle cannot downgrade it.
- **`NetworkStreamSession` and a subsystem-owned session registry.** Connection state, attempt count, last
  error, cancellation source, reconnect budget, and the protocol handle all live on the session. A provider
  asset holds none of it — recording connection status on a `ScriptableObject` was a runtime asset mutation,
  and two scenes sharing one provider were overwriting each other's status. The base class owns the
  connect/reconnect/authenticate loop, including one forced credential refresh on a rejection and the
  give-up rules; a protocol implements one method, and nothing in the base class models frames or acks.
  Teardown closes every session, and opening under a live id closes the previous one.
- **`INetworkStreamTransport`**, the seam that makes reconnect, backoff, credential refresh, and give-up
  rules testable without a socket — the role `IHttpTransport` plays for requests.
- **WebSocket and Socket.IO now run on sessions too**, alongside SSE. `WebSocketStreamSession` and
  `SocketIoStreamSession` each implement one method and inherit route resolution, the encrypted-transport
  check, scoped credential acquisition, bounded jittered backoff, the stable-connection window, and the
  give-up rules. No provider asset holds a socket, a reconnect counter, or a connection status any longer;
  the serialized fields survive for deserialization and are no longer written at runtime. `WebSocketDataProvider`
  went from 913 lines to 495 and `SocketIODataProvider` from 664 to 431, with the connection machinery
  replaced rather than duplicated.
- **Socket.IO's library-level reconnection is switched off.** It reuses the headers built when the socket was
  constructed, which is why the provider previously needed a hook that tore the socket down mid-reconnect
  whenever the auth token changed underneath it. Session-owned reconnection removes that: every attempt is a
  fresh connect with a freshly resolved route and a freshly acquired credential. The asset's
  `randomizationFactor` is superseded by the shared jitter and kept only for deserialization.
- **`NetworkStreamBinding.Direct`** lets a provider that still authors its own URL run on a session as well,
  so the mutable-state fix does not depend on adopting the catalog. A direct binding carries library-default
  policy, no credential profile, and no allowed-host list — adopting the catalog is what buys enforcement,
  and a test pins that so the trade stays visible.
- **`NetworkStreamSession.TryRefreshExternalCredentialAsync`**, the hook a session uses to refresh a
  credential it obtained outside the catalog. Called once per rejection episode and only when the binding
  carries no catalog credential; the default refuses, because without a refresh there is nothing new to try.
  The base class deliberately does not know `AuthManager` exists.
- **A Providers view in the Hub**, authoring provider routes through `SerializedObject` by property name
  rather than by type, so it builds in a project that has neither optional streaming package. It shows the
  resolved destination under the previewed environment, whether the credential covers that host, and the
  live session's state, attempts, and received count in Play mode.
- **`INetworkDiagnostics.StreamSessions()`**, defaulted so adding it broke no implementer. Live lists routed
  sessions and provider-owned connections separately, since only the first kind keeps its state off the asset.

- **Structured network-catalog MCP tools**: `molca_network_catalog`, `molca_network_validate`,
  `molca_network_edit`, `molca_network_migrate`, `molca_network_send`, and `molca_network_diagnostics`. They
  delegate to the same locator, editing service, resolver, validator, migration executor, and console
  preflight the Hub uses - no rule, no ID normalization, and no origin parsing lives in the MCP layer, because
  a second copy is how automation and the Hub start disagreeing about what a valid catalog is. Credential
  values never cross MCP; only profile names do. The legacy `molca_network_*_request` tools are unchanged.
- **`molca_network_send` refuses a production mutation rather than prompting.** Automation must not bypass a
  per-send confirmation, and there is nobody at an MCP call to give one, so the honest outcome is a refusal
  that points at the Hub's console.
- **Doctor checks `network-catalog` and `network-provider-route`.** The first projects
  `NetworkCatalogValidator` findings - the same codes the Hub and the build gate use - and reports an
  unregistered catalog or a pending schema migration. The second flags a streaming provider still on a raw
  URL while the project has a catalog, since a direct URL sits outside allowed-host, production-scheme, and
  credential-scope enforcement. Neither implements a networking rule of its own.
- **An audited, documented list of sanctioned direct-transport exceptions**
  (`NETWORKING_TRANSPORT_EXCEPTIONS.md`), enforced by `CoreTransportExceptionAuditTests`: a new direct
  `UnityWebRequest` anywhere in Core fails the suite unless it is both allowlisted and justified in the
  document, and a stale entry fails it too so the list cannot become a blanket exemption. The audit found the
  legacy `HttpClient` already goes through `IHttpTransport`, so it needs no exemption.
- **OpenAPI import**, preview first. `NetworkOpenApiImportService` parses an OpenAPI 3.x or Swagger 2.0 JSON
  document, diffs it against an endpoint collection, and applies the diff in one Undo group - reachable from
  Hub ▸ Network ▸ Endpoints ▸ Import OpenAPI… and from `molca_network_import_openapi`. The diff is a pure
  function of the document plus the collection, so the preview is exactly what apply performs, and identity
  is the spec's `operationId` recorded as the endpoint's `SourceReference`, so a re-import recognizes an
  endpoint an author renamed and applying the same spec twice writes nothing the second time.
- **Import will not overwrite a hand-authored endpoint, bind a service to a spec's `servers` URL, or create a
  credential profile.** A spec is written by someone who does not know how the project is configured: the
  first would discard an author's policy and mutation class, the second would point traffic wherever the spec
  author pointed, and the third is impossible to do safely because a spec cannot say where a secret comes from
  or which hosts may receive it. Parameters a security scheme names import as `Sensitive` with no default
  value. An operation that left a newer spec is reported as an orphan and left in place.
- **`NetworkCatalogEditingService.CreateImportedEndpoint` / `UpdateImportedEndpoint`**, which write a whole
  endpoint template transactionally. An update keeps the endpoint's ID, display name, policy profile, and
  idempotency requirement - decisions about the project - and replaces what the spec owns. `Update` also
  refuses a non-`OpenApi` source itself, so a caller that skipped the plan still cannot overwrite hand
  authoring.
- **Performance and bounds tests** for the resolver's warm-path allocations and shared snapshot index, the
  diagnostic ring buffer and body-preview limits, export size, Hub search over a 400-service catalog,
  queue-overflow fail-fast under concurrency, and that stream-session and route-state churn leaves nothing
  behind.

### Removed
- **The onboarding wizard's "SDK Quick Setup" card, and with it Core's last dependency on the SDK.**
  The card found `MolcaSDK.Editor.Setup.QuickSetupInstaller` by name across the loaded assemblies and
  invoked it, so Core could offer the SDK's setup without referencing the SDK assembly. Avoiding the
  reference was the wrong goal: the dependency was real either way, and expressing it reflectively only
  hid it from the compiler — `com.molca.sdk` could not be deleted without silently breaking a button in
  Core.

  Nothing replaces it. A layer that wants to contribute setup implements `IMolcaStarterStep`, which
  `MolcaStarter` discovers and the Project Starter card renders; contribution flows upward through an
  interface Core owns rather than Core reaching down for a type name. The settings graph the card
  copied is what the starter already generates. `QuickSetupInstaller` still works from its own
  **Molca ▸ SDK ▸ Quick Setup** menu — Core simply no longer advertises it.

- **Core ships no editable asset at all.** Seventeen `.asset`/`.prefab` files lived inside the package —
  `Data Configuration`, three per-platform `BudgetSettings` plus a `PanelSettings`, `ModalHelper`, the
  `Rect`/`Scene Utility` singletons, six localization style assets, two modal prefabs, and a stale
  `MolcaEditorSettings`. Every one of them was un-ownable by construction: a consumer cannot write to a
  file in an immutable package, and an upgrade replaces it, so an edit disappeared with no error anywhere.
  Several were already broken outside this repository — the localization styles referenced a font in
  `Assets/`, and `Data Configuration` was registered as a *live* module rather than a template to copy, so
  upgrading Core silently reverted a project's data settings.

  Nothing is lost. `DataConfig` is a `SettingModule`, so the project starter already generates it; the
  three tuned budget profiles moved into `BudgetSettings.Create(BudgetPreset)` and a new starter step
  materializes them into project space; the two zero-field utility singletons and the modal prefabs are
  content a project authors. Projects that referenced the deleted assets get a null entry in
  `GlobalSettings.modules`, which `bootstrap.module-entry-null` compacts and `bootstrap.module-missing`
  refills — the remediation pass repairs its own migration.

  `PackageSterilityTests` now fails the build if any editable asset reappears in the package. The rule was
  a convention before, and conventions do not survive a slow failure whose symptom is remote from its
  cause.

### Changed
- **Rebuilt the log pipeline. `LogManager` no longer suppresses anything.** The previous design installed
  itself as `Debug.unityLogger.logHandler` — *replacing* Unity's handler rather than wrapping it — and when
  its severity filter rejected a message it returned before forwarding. A field that read as "how verbose
  should my log file be" was therefore a global mute on `Debug`: the Console, the player log, and everything
  downstream of `Application.logMessageReceived` (the development-player bridge, crash reporting) lost every
  message below the threshold. That threshold was typed `LogType`, whose ordinals are
  `Error=0, Assert=1, Warning=2, Log=3, Exception=4`, so the shipped Runtime Manager prefab's unset `0` meant
  **Error** — and every `Debug.LogWarning` in the framework was discarded. It was found because
  `LogAssert.Expect(LogType.Warning, …)` can never match in a project using the prefab.
  - **Capture cannot suppress.** `MolcaLogPipeline` installs `LogCapture`, which calls Unity's handler
    first and unconditionally, outside any filter or guard. Filtering moved to the destination:
    `ILogSink.MinimumLevel`, applied per sink.
  - **`MolcaLogLevel` replaces `LogType` as the threshold type**, ascending, with `Verbose = 0` — so an
    unset serialized field is the *most* permissive value. A logger that fails open loses volume; one that
    fails closed loses the evidence for why. Only the first is recoverable.
  - **Capture starts before the subsystem that configures it.** The pipeline installs at
    `SubsystemRegistration` and buffers into a bounded `MemoryLogSink`; `LogManager` drains that into the
    file sink once it has a directory. Previously both handler methods returned early on `!IsActive`, and
    `MarkActive` runs only after the last dependency wave — so bootstrap, the window where a project
    actually breaks, was recorded by nothing.
  - **No `Debug.Log` call touches the disk.** `FileLogSink.Write` only enqueues; flushing happens on the
    main thread on a cadence, on pause, on focus loss and on teardown. The old sink called
    `File.AppendAllText` from inside the log handler every 64 messages, on whatever thread had logged —
    a frame hitch on the main thread, and a shared lock serialising worker threads behind disk latency.
  - **Rotation is collision-proof.** Filenames are second-precision, and the old code reused a colliding
    name while resetting its byte counter — merging two sessions into one file and disabling the size cap
    for it. A name is now claimed by creating the file with `FileMode.CreateNew`, the retention count is the
    total kept rather than that plus one, and the open file is never a deletion candidate.
  - **Entries are structured.** `MolcaLogEntry` carries level, Unity log type, message, stack trace, context
    name, UTC timestamp and thread. `LogManager.EntryLogged` and `ILogSink` replace the three
    pre-formatted-string callback *fields*, which a consumer could null wholesale and which made severity
    undiscoverable. `onLogInfo`/`onLogWarning`/`onLogError` still work and are `[Obsolete]`.
  - The context object's name is resolved at capture time on the main thread only — `Object.name` throws
    off it — and only the resolved string is carried, so a sink can never read a destroyed object.
  - `LogHandler` is `[Obsolete]` and inert; constructing it no longer installs anything, and its two
    setters are no-ops because honouring them would re-create the mute.
  - **Prefab defaults:** Verbose in the Editor, Info in a player. File logging stays off, as it already
    was. Consumer projects that never touched the field get warnings back on upgrade — which is the point,
    but it is a behaviour change.
  - `ModalManager` registers a sink instead of subscribing to the string callbacks, and now marshals
    off-thread entries through `Update` — `AddMessage` reaches `StartCoroutine`, which throws off the main
    thread. That hazard pre-dated this change and was unreachable only because warnings never arrived.
  - 30 EditMode tests and 7 PlayMode tests. The PlayMode ones assert against the prefab as shipped, and the
    `LogVisibilityScope` workaround the colour suites needed is deleted.
- **Retained request diagnostics now mask query values.** `NetworkRequestDiagnostic.Uri` is redacted at
  capture rather than preserved verbatim. A query string is a routine place for a signed URL or a one-time
  token to end up, and the record is displayed, copied, and exported — so "query values are not secrets" was
  a contract the caller could not keep on the record's behalf. Read `RoutedHttpOutcome.Uri` when the
  unmasked form is genuinely needed.
- **The Settings rail's Network leaf is a summary plus a deep link, not a second authoring surface.** It
  keeps its live runtime telemetry and gains a catalog health line. Keeping a lesser copy of the catalog
  editor next to the setting would guarantee the two eventually disagreed about what a route resolves to,
  and the leaf is the one a user is more likely to trust because it sits where they were already looking.
- **A catalogued project no longer sends process-wide credentials to hosts no service claims.** With a
  catalog present, a `useFullUrl` request to an unclaimed host is denied the `HttpModule` default headers
  whose name is a credential header (`Authorization`, `Proxy-Authorization`, `Cookie`, or any header a
  catalog credential profile names) and every registered `IHttpCredentialInterceptor`. A header the request
  itself authored is untouched — that was a deliberate decision about that host, and this correction is only
  about credentials that were never aimed anywhere in particular. A project with **no** catalog is
  unaffected, per the compatibility contract; it logs once when it sees a credential heading somewhere
  unexpected. `AllowLegacyGlobalAuthOnExternalUrls` restores the old behaviour for one transition window,
  warns wherever it is honoured, and is reported by validation until cleared.
- **Legacy migration creates its credential profile unscoped and never assigns it to a service.** An
  unscoped profile denies every host, so a freshly migrated catalog cannot send a credential anywhere until
  the author says where it may go. Scoping it automatically to every host that happened to declare an
  `Authorization` header would rebuild the leak inside the new model while looking like a finished
  migration.

### Added
- **Transactional content migration (revamp §16.6).** `ColorContentMigration` converts shipped `ColorID`
  components into `ColorThemeBinding` components as a previewed, fingerprint-bound transaction. Menu items
  preview and apply; a CLI (`PreviewFromCli` / `ApplyFromCli`, `-molcaMigrationBatch <n>`) exposes the
  plan's batches as named path filters, so a visual regression is attributable to one reviewable change.
  Seven refusals, each protecting a specific failure the previewer found in real content:
  - *Referenced by another component* — `ColorIDButton` and `ButtonState` drive a `ColorID` at runtime;
    removing it would leave a null reference and a button that never changes colour. Detected by walking
    serialized data, so a project's own component is protected without this code knowing it exists.
  - *Part of a nested prefab* — loading prefab contents materializes nested prefabs, so their components
    are reachable from an asset that only holds a reference and overrides. Migrating one would duplicate
    the change into every prefab nesting the same source.
  - *Nested prefab colour override* — separated from the above because it needs a human: the instance
    chose a colour its source does not have, and migrating the source silently drops that choice.
  - *Conflicting legacy components* — two `ColorID`s on one object naming different colours. V1 resolved
    this by execution order, which no migration can faithfully reproduce. Components naming the *same*
    colour (resolved through the alias map, so `Default.Text` and `Text.100` count as one) are merely
    redundant and merge into a single binding set.
  - Plus *no canonical token*, *asset not writable*, and *no colour targets*.

  Alpha is preserved per target rather than per component, and object locators carry sibling indices —
  identically named siblings are ordinary, and a name-only path would let the apply step address the wrong
  one, so the plan a reviewer approved would not be the plan that ran.

### Fixed
- **`Black.60` was rendering magenta under V2.** It is a real V1 key — black at 0.6 alpha, defined in both
  shipped palettes and used by `EnterPIN` — that the vocabulary had no alias for, so installing V2 broke
  it. Added `surface/scrim-medium` and aliased to it, restoring exactly what V1 rendered. The vocabulary
  is now 37 tokens and 23 aliases.

  It was missed because **the colour audit's scan cannot see a legacy pair carried as a prefab-instance
  override**: an override serializes as a `propertyPath`/`value` modification, not as the field pair the
  scan matches. Found by the content-migration previewer, which reaches content through loaded objects.
  The direction of that error is the dangerous one — usage is under-reported, so an alias can look unused
  while content still depends on it — so `ColorThemeDeprecationReport` now documents the gap and its
  `Removable` list is a candidate list to confirm against a migration preview, not proof on its own.

- **The Light variant's de-emphasised text failed WCAG, and now does not (revamp §7.1).** `text/muted`
  measured 3.80:1 against a 4.5:1 threshold on the Light canvas and `text/subtle` measured 2.28:1 against
  3.0:1. V1 could not detect this because nothing recorded that those colours were foregrounds; declaring
  usage in V2 made them checkable for the first time. The cause was V1 using a single alpha for both
  variants, which cannot work over a dark shell and a light one alike. The Light alphas are raised to 0.67
  and 0.53 — the smallest values clearing each threshold with margin, reaching 4.62:1 and 3.16:1 — and both
  requirements are promoted from `Warning` to `Error` so neither variant can regress. **Dark is
  byte-identical**, and Dark is the shipped default variant, so a project that has not switched to Light
  sees no change. `ColorThemeVocabularyTests` still pins every mapped legacy key to an exact colour; the
  two changed keys are listed in a named exception table with their measured ratios rather than the
  assertion being relaxed. `status/error/text` remains at `Warning` (2.88:1 in Light): fixing it means
  re-picking a brand hue rather than adjusting a neutral's opacity.

### Added
- **Compatibility hardening and deprecation (revamp Phase 6, §16.7).** The V1 surfaces that hide their
  dependencies are now deprecated with actionable messages, the legacy alias map carries a lifecycle, and
  removal is gated on evidence rather than on judgement.
  - **Deprecated, all still working and still tested, all scheduled for Core 2.0.0:** the implicit
    `ColorIDReference` → `Color` conversion; `ColorModule`'s palette-mutation APIs (`AddColor`,
    `RemoveColor`, `UpdateColor`, `AddSwatch`, `RemoveSwatch`); the whole `Molca.ColorID.ColorUtility`
    class; and `MolcaUiToken.NewColor(id, swatch, colorId)`. Deliberately *not* deprecated:
    `ColorIDReference.Color`, which is the explicit replacement for the implicit conversion, and the
    `ColorID`/`ColorIDReference` serialized types themselves — content is not deprecated, only the ways of
    resolving it that hide an initialization-order dependency.
  - **`ColorIDReference` gained its replacements** (§9.4): `TryResolve(IColorThemeService, out Color)`,
    which reports failure instead of returning magenta and makes the theme dependency visible, and
    `GetCanonicalTokenId(ColorThemeSet)` for inspection by migration tooling.
  - **Alias lifecycle policy.** Every shipped legacy alias now declares `DeprecatedSince` (1.18.0) and
    `RemovalVersion` (2.0.0), stamped from two constants so the policy cannot drift alias by alias. An
    alias with no declared removal version is never removable at any usage count — zero usage is not the
    same as permission, because nothing recorded what consumers were told to expect.
  - **`ColorThemeDeprecationReport`** (`Molca ▸ ColorID ▸ Report Compatibility Usage`, plus
    `ColorThemeDeprecationReportMenu.ReportFromCli` for CI) — a pure projection of the shared audit
    snapshot. Reports migration progress, per-alias usage split into project-owned and package-owned
    content, and legacy keys matching no alias. Two distinctions carry the gate: package content blocks
    removal outright because a consuming project cannot rewrite it, and an *incomplete* snapshot produces
    no removal evidence at all, because a floor cannot prove absence.
  - **Performance budgets locked** (§17.8). Runtime: steady-state lookup is allocation-free, ten variant
    switches instantiate no material, and a switch applies to all 200 bindings before `TrySetVariant`
    returns — no partially themed frame. Editor: per-variant stylesheet generation time, and generated
    stylesheet size stated per declared token. The whole-project audit budget is asserted by
    `ReportFromCli` rather than by an EditMode test, because a full audit opens every closed scene and the
    resulting engine asserts are attributed to whichever unrelated test is running when they are pumped.
  - **Upgrade guide** — `Documentation~/reference/COLOR_ID_MIGRATION.md`: the four-step path, the
    deprecation table with the reason for each entry, and what removal will require.

- **Colour-theme JSON interchange, catalog migration, and Figma variant selection (revamp Phase 5
  completion, §13.2/§14/§16.6 batch 4).**
  - **A documented JSON interchange format** (`molca.colortheme.interchange.v1`). DTCG-shaped where DTCG
    has an answer — `$type`, `$value`, `$description` — and everything it has no field for under
    `$extensions.molca`: per-variant values, token usage and kind, deprecation, the legacy alias map, and
    accessibility requirements. `$value` carries the default variant's resolved colour so a plain DTCG
    reader sees a usable palette; `$extensions.molca.modes` is the lossless half. Being explicit about
    which half is standard means a consumer can read the standard half and a future DTCG modes proposal
    can be adopted without changing what `$value` means.
    - Export is **deterministic** — authored order, invariant culture, no timestamp — so the file can be
      committed and diffed.
    - Aliases export as aliases, not as flattened literals. A flattening export would look correct while
      silently detaching every semantic token from the primitive it tracked, which is the entire value of
      the model.
    - **Import is always previewed**, and the preview builds the incoming set *in memory* and resolves it
      rather than diffing JSON — because the two ways an import goes wrong are invisible in the file: a
      token it drops may still be named by serialized content, and a colour it changes may push a contrast
      requirement below its threshold. The preview reports added/updated/removed tokens, variant and alias
      changes, **contrast regressions** (a pair that passes today and would fail after — pairs that already
      fail are not repeated, so the ones this import breaks are not buried), affected serialized sites, and
      any field the reader did not understand. Applying copies from that same candidate, so what lands is
      what was previewed.
    - No access tokens, file keys or private remote configuration are ever written.
  - **Catalog colour migration** (`Molca ▸ ColorID ▸ Preview / Migrate UI Token Catalog Colours`, plus
    batch-mode entry points). Only an **exact alias** migrates an entry: the runtime adapter's two
    fallbacks — treating a pair as a canonical id, and matching a token's last segment — are deliberately
    not used, because they are reasonable guesses for keeping a shipped component rendering but writing one
    into an asset would promote a guess to authored data with nothing to show it had been guessed. An entry
    whose canonical token does not resolve in *every* variant is blocked rather than migrated, since
    pinning it would convert a working legacy lookup into a per-variant hole. The legacy pair is **kept**,
    so a batch reverts by clearing one field.
  - **The SDK's UI Token Catalog is migrated** — 12 of 12 legacy colour entries, none blocked, no
    per-variant holes. The mapping confirms the vocabulary findings from the rebuild: `Text.80` becomes
    `surface/wash-strong` and `Default.Secondary` becomes `surface/panel`, neither of which its V1 name
    suggested.
  - **A category-aware catalog inspector.** `MolcaUiToken` is a flat record so it serializes without
    `[SerializeReference]`; the cost was a default inspector that showed a spacing token a sprite field and
    a prefab slot. It now shows only the fields the selected category uses, and validates: duplicate ids
    (lookup returns the first, so later entries were silently unreachable), id/category disagreement,
    missing sprites, presets and prefabs, canonical tokens the theme set does not declare, per-variant
    coverage, and legacy pairs no alias maps. Everything is reported; nothing is repaired by drawing.
  - **Figma import names the variant it was designed in** (§14.2). The colour source was
    `GlobalSettings.GetModule<ColorModule>()`, which returns whichever palette comes first in the module
    list — so a file authored in light mode was snapped against dark values, every match came back poor,
    and nothing in the output said why. `molca_figma_to_ui_spec` takes a `variant` argument, resolves it
    through the theme set, and reports what it actually snapped against as `colorSource`.
  - `ColorThemeAssetWriteAccess` decides writability from `PackageSource` rather than a `Packages/` path
    prefix. The audit keeps the coarser rule on purpose — it describes what a *consumer* project faces, and
    its rename planner reports content sites it does not itself rewrite — but a tool that actually writes
    needs the real answer, because in this repository the Molca packages are embedded and authored here.
  - 22 new EditMode tests.
- **The UI Token Catalog can name canonical colour tokens (revamp Phase 5, §13).** A `Color` catalog entry
  gains a `ColorTokenReference` beside the legacy `(swatch, colourId)` pair, and the apply path follows
  whichever the entry carries — per entry, not per project, because a half-migrated catalog is the normal
  state for the whole compatibility window.
  - `MolcaUiTokenResolver` writes a `ColorThemeBinding` for a canonical entry and a `ColorID` for a legacy
    one. An entry carrying *neither* is now an error rather than a silent success: a `Color` entry with no
    colour is an incomplete catalog, and applying nothing while reporting success is how a missing style
    survives review.
  - The legacy fields no longer default to `"Default"`/`"Primary"`. Those initializers gave every
    canonically-authored entry a legacy pair as well, which made "which one did the author mean"
    unanswerable and hid unmigrated entries from any audit that counts legacy pairs. Existing serialized
    catalogs are unaffected — their values are on disk.
  - New `ColorThemeBindingAuthoring` resolves a GameObject's writable components by asking
    `ColorTargetAdapterRegistry`, rather than hard-coding a type list, so a project that registered its own
    adapter gets its component types discovered too. Applying replaces the binding list instead of
    appending: two bindings on one target would fight over it in list order.
  - **`MolcaStyleApplier`'s token field is a searchable picker**, grouped by category, with colour entries
    annotated `(legacy)` so an author sees which generation they are about to get *before* applying. An id
    the catalog does not contain is shown as a disabled `(unresolved)` entry and kept verbatim — the free
    text field made a typo indistinguishable from an unmigrated value, and selecting the nearest match
    would destroy the authored value and hide that it was ever broken.
  - **The miner canonicalizes.** Mining a project that is on V2 translates each discovered legacy pair
    through the theme set's alias map and derives the catalog id from the canonical token, so two aliased
    pairs collapse into one entry instead of seeding a fresh catalog with an already-obsolete vocabulary.
    It also mines `ColorThemeBinding` components, which it previously could not see at all — a mined
    catalog would otherwise under-report the vocabulary in use exactly as a project finished moving onto it.
  - **Figma colour snapping no longer poisons its own palette.** A catalog entry that does not resolve is
    skipped rather than added as magenta. The palette is the target set for nearest-colour matching, so a
    magenta entry sat in a corner of the colour space and quietly attracted every unmatched Figma fill; a
    missing entry only costs a match, which the report surfaces as `_unmapped`. Letting an import name its
    source design mode (§14.2) is still outstanding, and until then confidence numbers are the signal that
    a light-mode file was snapped against a dark palette.

  17 new EditMode tests.
- **The V1 → V2 switch, and PlayMode coverage that proves it works.** Four phases of colour-theme work
  were verified only by EditMode tests of the model in isolation; nothing had ever run the subsystem.
  - `Molca ▸ ColorID ▸ Install Color Theme Settings (V1 → V2)` creates the `ColorThemeSettings` module and
    registers it with the project's `GlobalSettings`, which is the single piece of configuration
    `ColorSchemeManager` chooses its generation from. `Molca ▸ ColorID ▸ Report Colour Theme Installation`
    reports which path a project is on without changing anything. The installer is idempotent, refuses a
    theme set that does not validate (installing one would put the project into the degraded emergency
    fallback, which is worse than staying on V1), and deliberately leaves the now-inert `ColorModule`
    palettes in the module list so the switch reverts in one line.
  - 36 new PlayMode tests across three suites. `ColorThemeBootstrapPlayModeTests` drives the activation
    ladder against real `PlayerPrefs` and live `GlobalSettings` module ordering: authored default,
    persisted preference, a persisted variant a later build removed, a preference belonging to a
    *different* theme set, `SessionOnly` persistence, and a set no variant of which resolves reaching the
    degraded fallback. It also covers last-known-good preservation on a failed switch, refusal when
    runtime switching is disabled, subscriber-exception isolation, `Shutdown` clearing the static legacy
    provider override, and the legacy `IColorSchemeService` surface mapping onto variants.
    `ColorThemeBindingPlayModeTests` runs `ColorThemeBinding` through its real Unity lifecycle — `Start`
    applying an already-published snapshot, `OnDestroy` unsubscribing, an object activated after several
    switches landing on the current variant. `ColorThemeInstalledProjectPlayModeTests` asserts against the
    configuration this repository actually ships, and skips rather than fails on a legacy-path project.
  - **Every `Debug.LogWarning` is invisible at runtime.** `LogManager` installs Core's `LogHandler` and
    filters by a serialized `minimumLogLevel`; the shipped Runtime Manager prefab sets it to `0`, and
    `LogType`'s ordinals are not severity order — `0` is `Error`. So warnings and info messages are
    dropped before they reach Unity, in every project using the prefab. This surfaced because
    `LogAssert.Expect(LogType.Warning, ...)` can never match; the tests lower the level for their duration
    via `LogVisibilityScope` and restore it. The colour system's author-facing diagnostics are all
    warnings, so changing the prefab default is worth deciding on separately — it is not changed here.
- **Rebuilt canonical colour vocabulary.** `ColorThemeVocabulary` defines the token contract, its
  Dark/Light values, the legacy alias map and the accessibility contract in code;
  `Molca ▸ ColorID ▸ Create or Update Colour Vocabulary Asset` writes it to a project asset. Three tiers:
  `palette/*` primitives, semantic roles (`surface/*`, `text/*`, `border/*`, `action/*`, `status/*`,
  `focus/*`), and no component tier — the UI Token Catalog already covers that layer.
  - **It is a rebuild, not a remap, because the values contradict the V1 names.**
    `Default.Secondary`'s RGB is *identical* to `Default.Background` (and `Default.Disabled`'s is too), so
    "Secondary" was never a secondary brand colour — it is the surface base at full alpha.
    `Default.Accent` is a second chrome tone. The only genuine brand hue is `Default.Primary`. Separately,
    the `Text.*` family is not all text: `Text.20` has 8 uses and every one is an `Image` fill.
  - **Migrating content cannot change what renders.** All 22 aliases resolve to their V1 baseline RGBA at
    8-bit in both variants, asserted by `ColorThemeVocabularyTests` against the Phase 0 fixture rather
    than against the shipped palettes, so the guarantee outlives their deletion. `Default.Text` and
    `Text.100` collapse to one token because they are byte-identical at 8 bits.
  - The `White.*` family is dropped (0 of 5 keys referenced anywhere); the 15-key alpha ramps collapse to
    alias-with-alpha over one ink base; `status/success/fill` is declared a surface, not text, because its
    Light ratio is about 1.1:1.
  - **The contrast contract immediately found a real defect.** Declaring usage made two pairs checkable
    for the first time, and both fail in Light: `text/muted` on `surface/canvas` is 3.80:1 (4.5:1 needed)
    and `text/subtle` is 2.28:1 (3.0:1 needed). V1 could not detect this because nothing recorded that
    those colours were foregrounds. Raising the Light ink alpha from 0.60 to 0.67 reaches 4.62:1, but that
    changes what 23 live `Text.60` sites render — so both are recorded at `Warning` severity with their
    measured ratios and the fix left as a design decision.

  15 new EditMode tests (185 total). Decisions and remaining open items are recorded in
  `docs/internal/COLORID_LEGACY_KEY_USAGE_INVENTORY.md` §7.

### Added
- **Shared colour-theme audit, transaction engine and non-destructive drawer (revamp Phase 4).** Safe,
  complete theme authoring becomes the default.
  - `ColorThemeAuditService` produces one immutable snapshot that the Hub, Doctor, the build gate, MCP read
    tools and migration planning all consume, so those surfaces cannot disagree about whether a project is
    healthy. It is **strictly read-only**: it scans serialized YAML text rather than loading assets, so it
    covers closed scenes and package assets — which `AssetDatabase`-driven scanning cannot reach without
    opening them — and cannot mutate anything, because nothing is deserialized in the first place.
  - **Incomplete coverage can no longer report Clean.** The request *declares* which inputs it will cover;
    any declared input that was skipped or unreadable makes the result `Incomplete`, which outranks both
    `Clean` and `Findings`. A scan that could not read part of the project has not shown the project is
    clean, only that it does not know. V1 scanning was limited to `Assets/`, never opened closed scenes,
    and still reported a clean result — which is how package prefabs with broken references shipped.
  - References are checked **per selectable variant**, and failing variants are named. The older
    `ColorIDReferenceValidityCheck` unioned keys across every `ColorModule`, accepting any reference
    defined in *any* palette even though switching to a palette that lacked it rendered magenta.
  - `ColorThemeTransactionPlanner` / `ColorThemeTransactionExecutor`: a plan is a preview that changes
    nothing and is **bound to the audit fingerprint it was built from**. The executor re-audits and refuses
    a plan whose fingerprint has moved, so a preview reviewed against one state can never be applied to a
    different one. Package-owned sites are **reported, never written** — an installed package is read-only
    to the project that installs it, and shipped content is overwhelmingly package-owned, which is why a
    rename keeps a compatibility alias. Changes go through Unity Undo, and the result carries a
    post-apply rescan so a caller sees the state its change produced rather than the state it planned
    against.
  - `ColorThemeSetEditing` confines every theme-set mutation to one editor-only class. `ColorThemeSet`
    still exposes no mutators: it is read-only configuration, and adding internal ones for editor
    convenience would weaken that guarantee for runtime code too. A rename updates the definition, every
    variant value, every alias *targeting* it, every legacy mapping and every contrast requirement in one
    call, so it cannot be half-applied; adding a token seeds all variants at once, because a token present
    in one variant and absent from another is the exact defect the contract model prevents.
  - `ColorTokenReferenceDrawer`: searchable picker grouped by declared usage, primitives behind their own
    submenu, live swatch against the authored default variant. **Drawing never writes** — an unresolvable
    value is shown as a disabled `(unresolved)` entry and preserved verbatim until the author picks a
    replacement.
  - `ColorThemeAuditCheck` reports the shared snapshot through Doctor, including incomplete coverage as its
    own warning so a partial scan cannot present as a clean bill of health.

  20 new EditMode tests (170 across Phases 1-4). The Themes **Hub workspace view is deferred**: the exit
  criteria for this phase are behavioural — scans cannot mutate, incomplete coverage cannot report Clean,
  renames preview exactly and reject stale plans, packages stay read-only — and those live in the services
  above, which the view would only present.

### Added
- **Binding adapters, runtime UI Toolkit theming and a build gate (revamp Phase 3).** Every supported
  presentation surface now follows one active variant through one application path.
  - `IColorTargetAdapter` + `ColorTargetAdapterRegistry` replace the type-switch with a registry whose
    **resolution order is the contract**: `TMP_Text` before `Graphic` (TMP shadows `Graphic.color` with a
    setter that also flags the mesh dirty), and every specialised renderer before the generic material
    path. That ordering *is* the fix for the V1 defect where matching `is Renderer` first made the
    specialised branches unreachable. Built-ins always resolve before external adapters, so a fork can
    extend to new component types but cannot silently hijack a standard one — an override has to claim a
    distinct channel, which makes it visible in authored data instead of hidden in assembly load order.
    A throwing third-party adapter is caught and cannot abort the theme switch for the rest of the scene.
  - `ColorTargetApplier` — the V1 correctness-pass façade — now delegates entirely to the registry, so
    legacy `ColorID` components and V2 `ColorThemeBinding` components apply colour through the *same*
    adapters. A migrated object cannot start rendering differently from an unmigrated one, and a new
    adapter reaches both at once.
  - `ColorThemeBinding` + `ColorBinding`: many tokens on many targets from one component, each binding
    carrying its own target reference. No parallel cache (the V1 index-skew defect is structurally
    absent), no hierarchy scan on a theme change, and generation-gated so a duplicate publish is cheap
    rather than redundant work. Alpha policy is `UseTokenAlpha` / `PreserveTarget` / `Explicit`, resolved
    before the adapter runs so no adapter reimplements it.
  - Explicit material-property binding through a reused `MaterialPropertyBlock`:
    `Renderer.material` is never read, the existing block is read-modify-written so unrelated
    per-instance overrides survive, and an **explicitly named missing property is an error rather than a
    silent fall back** to the probe order — the author stated an intent, and writing a different property
    would put the colour on the wrong channel and look like it worked.
  - **Runtime UI Toolkit follows the same variant.** `ColorThemeUssGenerator` emits one USS stylesheet per
    variant plus a `ColorThemeManifest` recording the source fingerprint; `ColorThemeDocumentBinder` swaps
    the generated sheet on a `UIDocument` when the variant changes, leaving Unity's default control theme
    assigned through `PanelSettings`. This closes the fragmentation the plan identified: runtime UI
    Toolkit previously rendered a hardcoded dark theme with an unrelated accent and never switched at all.
    Generation is an explicit action, never an `OnValidate` side effect, and output is byte-deterministic
    (sorted tokens, invariant-culture colour formatting, no timestamp in the file) so regenerating
    unchanged data produces no diff.
  - `ColorThemeBuildValidator` (`IPreprocessBuildWithReport`) fails a build on an invalid theme set, an
    undeclared default variant, a variant that does not resolve, an author-declared `Error` contrast
    failure, or stale generated UI Toolkit output. It hooks Unity's pipeline as well as Molca's, so a
    developer building from the Build Profiles window hits the same gate as CI. A legacy-only project
    produces no findings — that is a supported configuration during the compatibility window — and
    deleting the manifest is the documented way to opt out of the generated-output requirement.

  38 new EditMode tests (150 across Phases 1-3).

### Added
- **Colour Theme Set model, immutable runtime snapshot and V2 theme service (revamp Phase 2).** The V2
  source of truth for colour, added alongside the V1 `ColorModule` path rather than replacing it.
  - `ColorThemeSet` owns **one token contract** (`ColorTokenDefinition`: canonical ID, primitive vs
    semantic, usage flags, required/deprecated) and the `ColorThemeVariant`s that supply values for it.
    A variant cannot introduce an undeclared token and validation rejects one that omits a required
    token, so cross-variant parity is structural rather than something to remember. This inverts V1,
    where each palette held its own independent list and a key could exist in Dark and silently not in
    Light.
  - `ColorExpression` supports literal, alias and alias-with-alpha values. The shipped palettes spend 15
    of their 31 keys on `Black.*`/`White.*`/`Text.*` ramps that are one base colour at five alpha levels;
    those collapse to 3 aliases plus alpha with no change to any resolved colour.
  - Canonical IDs are lower-case, slash-separated and require **at least two segments**, which makes a
    canonical token structurally impossible to confuse with a legacy bare colour ID.
  - `ColorThemeResolver` flattens every alias once at activation into an immutable `ResolvedColorTheme`,
    so steady-state lookup is one allocation-free dictionary hit that walks no graph. It rejects cycles
    (naming the path), chains deeper than 4 hops, missing alias targets and unresolved required tokens
    **before** publishing anything, which is what makes "failed activation preserves the last known good
    theme" a structural property instead of a promise. Snapshots carry a content-only deterministic
    fingerprint for later generated-artifact staleness checks.
  - `ColorContrast` implements WCAG relative luminance and contrast ratio with **explicit alpha
    compositing**, and refuses to guess: a translucent background with no declared under-surface is
    reported *incomplete*, never as a pass or a failure. That applies immediately — the shipped
    `Default.Background` has alpha 0.901961 in both variants, so every contrast claim against it needs an
    under-surface named.
  - `ColorThemeSettings`/`ColorThemeState` persist the active variant scoped by **stable set ID and
    schema version**, so a preference belonging to a different or newer theme set is ignored rather than
    misapplied. V1 keyed persistence off `typeof(ColorModule).FullName`, so every variant shared one key.
  - `IColorThemeService` exposes typed resolution and typed activation outcomes. `ColorTokenReference`
    deliberately has **no implicit `Color` conversion** and no static resolution path — the V1
    convenience that hid a bootstrap-ordering dependency at every call site.
  - **Compatibility.** `ColorSchemeManager` now implements both services and selects its generation from
    configuration alone. In V2, `LegacyColorProviderAdapter` translates legacy `(swatch, colorId)` pairs
    into canonical tokens through the theme set's alias map, so all 194 shipped `ColorID` components keep
    their serialized data and are translated at lookup time — never migrated in the assets, and never by
    fabricating a `ColorModule` at runtime. Each lookup records whether it used an authored alias, a
    direct canonical ID or an ambiguous bare-ID search, so migration progress is measurable rather than
    assumed. The legacy `IColorSchemeService` members are mapped onto variants, so existing
    scheme-switching content — including the shipped Color Scheme Dropdown prefab — drives the new model
    unchanged.

  76 new EditMode tests. Phase 2 intentionally ships **no theme-set asset**: the canonical semantic map
  depends on a usage review the plan requires be evidence-based, and that evidence now exists in
  `docs/internal/COLORID_LEGACY_KEY_USAGE_INVENTORY.md`.

### Fixed
- **ColorID V1 correctness pass (revamp Phase 1).** Nine defects, no serialized data rewritten and no
  public API removed:
  - *A fresh install had dead theme switching.* The packaged `Runtime Manager` prefab serializes
    `_availableSchemes` by GUID, but those GUIDs belonged to `Assets/_MolcaSDK/` palettes that ship with
    no package, while the Quick Setup templates carried different GUIDs. Unity deserializes an
    unresolvable object array at full length with null elements, and `Initialize` treated only
    `Length == 0` as unconfigured — so `[null, null]` reported a healthy subsystem with no active scheme.
    The palette templates now carry the GUIDs the prefab references (they cannot collide: an install
    skips files that already exist), and `ColorSchemeManager` drops unresolved entries, falls back to
    the palettes `GlobalSettings` owns, and says loudly which reference failed.
  - *Target configuration could land on the wrong component.* `ApplyColors` indexed `_colorTargets`
    and a parallel `_cachedTargets` together, but the cache was rebuilt skipping null components — so
    one removed component shifted every later target's configuration by one. The parallel cache is
    gone; each target uses its own component reference.
  - *"Apply To Children" now means the whole hierarchy*, matching its label and docs, instead of
    immediate children only.
  - *Theme application no longer instantiates materials.* `renderer.material.color` created a
    per-renderer material copy on every apply, assumed a colour property existed, and was simply wrong
    for `SpriteRenderer`. Application now runs through `ColorTargetApplier`, which matches the
    most-derived type first (sprite tint, line/trail gradients, particle start colour), probes
    `_BaseColor` then `_Color` on the **shared** material, and writes a reused
    `MaterialPropertyBlock` — reporting a typed outcome instead of failing silently. Adds a
    `SpriteRenderer` target type (appended to the enum; existing ordinals unchanged).
  - *`ColorUtility` honours its own parameters.* `CreateColorID` ignored `applyToChildren` and
    `autoDetectTargets`; its `is Renderer` test made the line/trail branches unreachable;
    `RemoveColorIDs` called `DestroyImmediate` even at runtime.
  - *Drawing an inspector cannot destroy data.* `ColorIDReferenceDrawer` silently repointed an
    unresolved pair at the first available colour just by rendering. Unresolved values now show as a
    marked `(unresolved)` entry and stay untouched until explicitly repaired.
  - *Editing one palette no longer has project-wide side effects.* `ColorModule.OnValidate` cleared
    every persisted override, ran the destructive legacy migration, and recoloured and dirtied every
    `ColorID` in all open scenes. It now only rebuilds that asset's own lookup cache.
  - *The reset dialog describes what reset actually does* — clear overrides, keep authored swatches —
    instead of promising a destructive reset that never happened.
  - *Bare-ID lookup honours the authored `IsDefault` swatch* rather than hardcoding the name
    `"Default"`, and one parser (`ColorID.TryParseComposite`) accepts both `Swatch/Color` and
    `Swatch.Color`. `ColorIDReference.SetFromComposite` makes round-tripping a `GetAllColorIds()`
    value correct; the shipped example, which assigned a dotted composite into the bare `ColorId`
    field and resolved to magenta, is fixed. `ColorID.Start` no longer lets exceptions escape
    `async void`, and treats shutdown cancellation as normal.

  Guarded by 36 new EditMode tests, including asset-level invariants (prefab→template GUID closure,
  single default swatch, no duplicate or blank keys, Default/Light key parity, template/active
  content parity) so the packaging defect cannot silently return. Pre-change behaviour and the full
  31-key numeric baseline are recorded in `docs/internal/COLORID_PHASE0_CHARACTERIZATION.md`.
- **No Doctor check completes its `Awaitable` on a ThreadPool thread any more.** The sixteen text-scanning
  checks hopped off the main thread with `Awaitable.BackgroundThreadAsync()` and returned from there, so the
  pooled `Awaitable` — a managed object with a native counterpart — was completed on the pool thread the
  scan ran on. That raises the native `Scripting object is not properly attached` assert. Nothing throws;
  the assert is logged asynchronously, and the test framework charges an unexpected log to whichever test
  is open when it lands, which is how it surfaced as intermittent failures in the unrelated OAuth loopback
  tests. Each check now ends with `try { return Scan(…); } finally { await Awaitable.MainThreadAsync(); }`,
  and their tests `await` the check instead of spin-waiting on `IsCompleted` with `Thread.Sleep` — that
  sleep loop blocked the very main-thread pump the hop back depends on.
- **`OAuthLoopbackListener` no longer hops threads to accept the redirect.** It awaits
  `HttpListener.GetContextAsync()` on the main thread rather than wrapping a blocking `GetContext()` in a
  `BackgroundThreadAsync`/`MainThreadAsync` round trip. Same semantics — a timeout or cancellation still
  stops the listener, which faults the pending accept — with no Unity `Awaitable` ever touched off the main
  thread.
- **`HttpRequest.Clone()` no longer duplicates the default headers or shadows an authored `Accept`.** It
  constructed through the public parameterless constructor — which seeds `Accept: */*` and
  `User-Agent: Unity/1.0` — and then appended the source's headers, so the seeded values landed *ahead* of
  an authored `Accept`. `GetHeaderValue` returns the first match, so it answered `*/*` instead of the
  authored value, and every clone doubled the two entries. Since `HttpClient` clones on each send, anything
  inspecting a cloned request (an `IHttpRequestInterceptor`, diagnostics, a test) read the stale value.
  Transmission was already correct — `UnityWebRequestTransport` calls `SetRequestHeader` in list order and
  later entries overwrite earlier ones — so this was a read-path bug only.
  `Clone()` now copies the source's headers verbatim through a private constructor that skips seeding. A
  request that never authored those headers still carries them, because the source has them; one that
  deliberately removed a default now keeps it removed, which a copy should do anyway.
- **A library-default policy value no longer acts as a ceiling no authored policy can raise.** Resolving
  `RedirectMode` and `MaxRedirects` tighten-only across *every* layer made the built-in `SameOrigin` an
  absolute maximum, so a service authoring `AllowedHosts` silently got `SameOrigin` and the looser mode was
  unreachable. The strictest-wins comparison now spans only the authored layers, falling back to the library
  default when nothing is authored. Tighten-only is a rule about what one authored layer may do to another,
  not a licence for the default to outvote all of them.
- **Route-based networking configuration: `NetworkCatalog` and `NetworkEndpointCollection`.** A request
  targets a route — `(environmentId, serviceId)` — instead of a process-wide `HttpModule.BaseUrl`, so one
  session can reach several services in several environments without mutating global state. The catalog is a
  `SettingModule` holding environments, services with a binding per environment, policy profiles, credential
  *metadata*, and endpoint collection references; endpoints do not get one asset each. Everything is
  read-only configuration — `CreateState()` returns `null` — and no field on the catalog or anything it
  serializes holds a credential value. **Purely additive:** `IHttpClient`, `HttpClient`, `HttpRequest`,
  `HttpRequestAsset`, `HttpModule`, the interceptor interfaces and `IHttpTransport` keep every member and
  serialized field, and existing request assets and providers behave exactly as before. See
  `Documentation~/reference/NETWORKING_CATALOG.md`.
- **A missing environment binding is a typed `RouteResolution` error, never a fallback.** A service may
  legitimately be absent from an environment, but resolution never substitutes another environment's origin —
  silent fallback is how a staging build ends up talking to production. Validation reports the holes in the
  environment × service matrix as warnings so they are visible rather than assumed.
- **Effective configuration resolves with per-field provenance.** `NetworkPolicyResolver` walks
  library default → catalog → environment → service → endpoint → per-send override, and every resolved value
  carries the layer that supplied it. Inheritable numerics treat `0` as "not authored here" and fall through,
  so an endpoint can override a retry count without restating a timeout. **Security-restricted fields resolve
  tighten-only** — allowed hosts, secure transport, redirect mode, redirect count and size limits may be made
  stricter by any layer and relaxed by none; TLS validation may be relaxed outside production and is clamped
  back on inside it. A rejected relaxation is recorded in `SecurityClamps` with its reason rather than
  silently having no effect, and `NetworkSendPolicyOverride` deliberately exposes no TLS or credential field
  at all, so a call site has no vocabulary in which to weaken a security rule.
- **Credential scope denies when empty.** `NetworkCredentialProfile` stores provider kind, non-secret lookup
  key, audience, scopes and attachment metadata — never a value — plus the services and hosts a credential may
  ever reach. A profile with no scope authored attaches to nothing: "no rules authored" must never read as
  "every host approved". Host patterns are an exact host or a single leading `*.` wildcard covering at least
  two labels; `*` and `*.com` are rejected, so "which hosts can see this token?" stays answerable by
  inspection. A `*.example.com` pattern does not match the apex.
- **`NetworkCatalogValidator` is the single networking contract**, shared by Hub Diagnostics, Doctor, the
  build gate, MCP tools and tests — there is no second set of rules. Pure and deterministic, so batch-mode
  gates and golden-file tests are stable. Findings carry a stable `Code` (matched by tooling, therefore added
  and never renamed), a shared `NetworkErrorCategory`, the owning entity, and a remedy. It covers identifier
  format and uniqueness, dangling references, missing and duplicate bindings, malformed or insecure origins,
  hosts outside a service allowlist or credential scope, credential sources that cannot exist in a player
  build, contradictory timeout/retry/circuit/cache values, endpoint paths and parameter placeholders,
  unsupported protocols, and fields that look like they hold a secret. `NetworkCatalogBuildValidator` runs it
  on build — warning-only unless the catalog opts into `Fail Build On Validation Error`.
- **`NetworkErrorCategory`**, one classification spanning runtime failures and authoring findings so both
  describe a problem with the same word. `HttpErrorKind` is unchanged; map between them with `ToCategory()` /
  `ToLegacyKind()`. Only `Connectivity` counts as a connection failure — an HTTP error status does not.
- **A shared authoring layer** (`Molca.Editor.Networking`): `NetworkCatalogLocator` finds the catalog by type
  rather than a hardcoded path and never creates one as a side effect of reading;
  `NetworkCatalogEditingService` is the one write path, editing through
  `SerializedObject`/`SerializedProperty` so Undo and dirty tracking behave normally, and collapsing
  multi-asset ID refactors into a single Undo step so a half-applied rename is not reachable;
  `NetworkEffectiveConfigurationService` previews what a route would actually do, including whether a
  credential really applies to the resolved host. Operations return a `NetworkAuthoringResult` instead of
  throwing, and a refused operation modifies nothing.
- **Versioned catalog schema with migration infrastructure.** `NetworkCatalogSchemaMigrator` upgrades older
  assets deterministically, previewably and rerunnably under one Undo group, records provenance, never reads
  secrets, and refuses to write to packages or other read-only locations. A catalog newer than the installed
  framework is refused rather than downgraded, since downgrading would silently drop fields.

### Changed
- **`Step` and `SequenceController` register through scoped keys and registration handles.** Inside a
  `ReferenceScopeRoot` the key is prefab-local, so several instances of one scoped prefab register the same
  authored id without conflicting; outside one it stays `LegacyGlobal`, which is what existing projects
  already mean. They release through the handle rather than by object, so a step whose Ref Id changed while
  registered now drops the entry it actually holds instead of the wrong one — or none.
- **The persisted index schema is version 2,** adding what a site declares: scope, requiredness,
  availability and enclosing scope root. A version 1 file is rejected rather than read with those fields
  defaulted, because findings are re-derived on load and a missing declaration would silently produce a
  more permissive result than the audit that wrote the file. Re-scanning once costs less than one wrong
  green result.
- **`ReferenceManager`'s authoritative registry is keyed by `ReferenceRuntimeKey`,** with the v1
  `(RefType, RefId)` index, the per-type multimap and the reverse lookup all derived from it through a single
  add/remove pair so they cannot drift apart. `Register(IReferenceable)` and every v1 lookup behave exactly as
  before by mapping onto `LegacyGlobal` keys, including the conflict error message. **Scoped entries are
  deliberately invisible to the v1 lookups** — `TryGet(refType, refId)`, `TryGetByRefIdOnly`, `GetAllOfType`,
  `GetAllReferenceIds` and `GetReferenceId` answer for global-scope entries only, because a prefab-local id is
  not project-unique and answering a bare `(RefType, RefId)` query with one would reach into a scope the caller
  had no way to name. `Count` covers the whole registry; `GetAllKeys`, `GetAllInScope` and
  `TryGet(ReferenceRuntimeKey)` are the scoped equivalents. A `Global` and a `LegacyGlobal` key making the same
  project-wide claim on one id now conflict, since the v1 index could otherwise name only one of them.
- **`ReferenceManager.Teardown` clears the registry and drops its subscriptions,** and refuses further
  registrations while shutting down. A provider whose `OnDisable` ran during teardown previously re-entered a
  half-cleared registry, and a stale handler on a torn-down subsystem kept its owner alive and fired against a
  registry that no longer meant anything.
- **`ReferenceableComponent` no longer regenerates an inherited prefab id inside a scope root.** Outside one
  the behavior is unchanged, because without a scope an inherited id really is a project-wide collision. Inside
  one the inheritance is the point, so the id is left alone and the prefab's internal references keep working.
  The component also gained a scope selector (defaulting to `LegacyGlobal`) and now releases its registration
  through its handle, so a component whose `RefId` changed while registered drops the entry it actually holds.
- **Reference validation is now one shared, read-only audit engine.** Molca Doctor, the build gate, the
  Inspector drawers, Sequence validation, Framework Graph and the MCP tools all project a single
  `ReferenceAuditSnapshot` instead of scanning for themselves. They had five independent scanners with rules
  that disagreed with the runtime and with each other: the build gate detected duplicates on the exact
  `(RefType, RefId)` key but tested resolvability on the Ref Id alone, so a reference whose Ref Type no longer
  matched any provider passed the gate and failed at runtime, while a reference matching two providers by id
  passed the gate even though the runtime refuses the ambiguity. Findings now carry stable `REFnnn` codes
  (`REF001` missing, `REF002` duplicate, `REF003` ambiguous fallback, `REF004` wrong type, `REF005` stale Ref
  Type, `REF008` provider with no id, `REF015` scan failure, `REF016` partial coverage) that read the same on
  every surface.
- **Scanning never modifies project data.** The project scan used to assign Ref Ids to providers that had
  none and reassign ones it considered duplicated — during an operation the user had asked to *scan* — and
  then offer to rewrite every serialized `refId` string in the loaded scenes from the old value to the new
  one. That blanket redirect matched any string property named `refId`, not only real reference fields, and
  could not know which of two duplicate providers a given reference had meant, so on a real duplicate it
  silently pointed references at the wrong object. Both behaviors are gone. Regenerating a Ref Id that has
  inbound references now lists the references it will break and asks first; `molca_fix_refids` assigns ids to
  providers that have none and re-keys a duplicate only when nothing references that id, reporting the rest
  for a human decision.
- **"Clean" now requires complete coverage.** An audit reports coverage alongside its findings, and zero
  findings with a skipped or failed input category is `Incomplete`, never `Clean`. This matters because the
  common configuration — no prefab scan paths, no enabled build scenes — previously produced a green result
  that asserted nothing about the project.
- **ScriptableObject-owned references are scanned.** An SO cannot be a runtime *target*, but it can hold an
  outbound reference that resolves a loaded scene object; conflating the two is why a real broken reference
  went unreported. Discovery walks serialized properties rather than reflecting over field values, so array
  elements, nested serializable structs and `[SerializeReference]` graphs are covered — and a class that
  merely has string fields named `refId`/`refType` is correctly not treated as a reference.
- **Every public `ReferenceManager` lookup rejects destroyed entries.** `Get`, `TryGet`,
  `TryGetByRefIdOnly`, `GetAllOfType` and `IsRegistered` now purge and reject a fake-null entry, which
  previously only `SceneObjectReference.Resolve` did — a target destroyed without unregistering was handed
  straight back to callers of the direct API. A destroyed incumbent also no longer blocks its own key, so a
  respawned object can register under it.
- **`Step` and `SequenceController` register on enable and unregister on disable**, matching
  `ReferenceableComponent`. They previously registered from `Start` and held the entry until destroy, so
  whether a *disabled* target resolved depended on which component type it was.
- **`SceneObjectReference.ResolveAsync` emits one diagnostic, for its terminal outcome only**, and threads
  its cancellation token through the `RuntimeManager` bootstrap wait as well as the registration wait. It
  used to log "could not resolve" for the entirely expected case of a target that had not registered yet, and
  its token did not cover the bootstrap wait at all. It also now re-checks the full key/type contract on each
  candidate registration instead of completing the wait on a Ref Id match alone.
- **The Inspector drawers show what the runtime would resolve, and no longer write during `OnGUI`.** They
  matched on Ref Id alone and took `FirstOrDefault`, so with two providers sharing an id they displayed — and
  their Select button jumped to — a different object than the runtime resolved. They also rewrote `refType`
  and `cachedDisplayName` from inside `OnGUI`, dirtying scenes and prefabs merely by looking at an Inspector.
  Metadata refresh is now an explicit button, the picker flags duplicated ids instead of offering them as if
  either would work, and a `SceneObjectReference<T>` field constrains its picker to assignable targets.
- **Framework Graph's reference layer includes every provider kind and every reference site.** `Step`,
  `SequenceController` and custom `IReferenceable` implementers were invisible because the layer looked only
  for `ReferenceableComponent`, and edges whose owner was not itself a provider were dropped entirely — so
  the graph could show a target that nothing appeared to reference.
- **The `ReferenceManagerSettings` Inspector is configuration plus a status line, not a second management
  UI.** Findings, filters, candidate navigation, severity policy and repair previews moved to the References
  workspace; the Inspector reports health in one line and links there. Keeping a lesser copy of the triage UI
  in an Inspector would have guaranteed the two surfaces eventually disagreed about what a finding means, and
  the Inspector is the one users are more likely to trust because it sits next to the setting.

### Added
- **`REF006` — a `Required` or `DeferredRequired` reference with no target is now an error.** This was
  undetectable before requiredness could be declared: every unset reference was equally legal, so a field
  somebody forgot to wire was indistinguishable from one deliberately left empty, and the mistake surfaced
  as a null at runtime rather than as an error in the editor. It is not lowerable — a `Required` field with
  no target throws, which is not hygiene an iteration build gets to defer. An unset **`Optional`** field
  stays silent exactly as before.
- **`REF007` — a prefab-local reference with no enclosing `ReferenceScopeRoot`.** Also not lowerable: the
  runtime refuses such a registration outright, and previously that surfaced only as a `WrongScope` resolve
  with no indication of what was actually missing. Reported even when the target resolves, because the two
  are independent — a prefab-local reference can name a perfectly real object and still be broken for want
  of a scope to resolve it in.
- **`REF009` — the target's scene is never loaded alongside the owner's under any declared load set.** Only
  produced when the project supplies load sets; without them nothing is known about concurrency and
  asserting unavailability from a guess would be worse than staying silent. Lowerable, unlike the two
  above, because it describes configuration an iteration build may legitimately not match. A site declaring
  `Conditional` availability opts out — the author already said it only resolves under a named condition.
- **The build gate audits the union of the enabled build scenes and every scene the load sets mention.** A
  load set routinely names a scene that is not enabled in Build Settings — an additively-loaded level
  usually is not — and scanning only the enabled list left that scene's providers undiscovered, so every
  reference into it was reported as missing rather than as deferred. The audit scans one combined set
  because a provider must be discovered before anything can be said about it; what load sets change is the
  *conclusion*, decided per-pairing inside the analyzer, so co-scanning no longer implies co-loading.
- **A Hub action to remove the legacy cached id lists** on `ReferenceManagerSettings`, gated on a healthy
  audit. Those lists were the original index — a hand-maintained snapshot written by a scan and read by
  validation — which made them a second source of truth able to disagree with the assets they described,
  and they routinely did: an id deleted from a scene stayed listed forever, so validation reported
  providers that no longer existed. Removal is refused until an audit has run with complete coverage and no
  errors, because dropping the old lists while the new index cannot answer would leave the project with
  neither and make the cleanup look like the cause of whatever broke next.
- **`Documentation~/reference/REFERENCE_SYSTEM_MIGRATION.md`** — the consumer and fork migration guide,
  including a per-surface compatibility matrix and the next-major removal list. `SceneObjectReference` is
  deliberately *not* on that list.
- **Scoped references, so an id only has to be unique where it actually means something.** A v1 id had to be
  unique project-wide, because the registry had one flat key space — which is why placing a referenceable
  prefab twice was a conflict, and why the editor gave each new placement a fresh id. That workaround *broke
  the prefab's internal wiring*, since the references inside it still named the id the asset was authored
  with. `ReferenceRuntimeKey` is now scope plus `(RefType, RefId)`, with scopes `Global`, `Scene`,
  `PrefabLocal` and `LegacyGlobal`. Add a `ReferenceScopeRoot` to a prefab root and its authored ids are left
  alone: every instance inherits the same scope *template* id, each live instance gets its own scope
  *instance* id, and two copies may carry identical local ids without colliding. Internal wiring survives
  duplication, prefab variants and runtime instantiation.
- **`ReferenceScopeKind.LegacyGlobal` is the zero value, deliberately.** A default-constructed key, or one
  deserialized from data written before scopes existed, lands on the compatibility path — which tolerates a
  missing scope and reports what it did — rather than silently claiming an exact `Global` identity it was
  never authored as. Equally, a scoped key with no scope id is *invalid* rather than being promoted to
  global: treating "prefab-local, scope unknown" as "global" is how a local id would escape its instance and
  collide with every copy.
- **`Register(IReferenceable, ReferenceRuntimeKey, out ReferenceRegistrationHandle)` reports why, not
  whether.** `ReferenceRegistrationOutcome` separates `AlreadyRegisteredSameKey` (harmless, the common
  re-enable case) from `DuplicateKey` (a real authoring defect), which the old `bool` collapsed into the same
  `false`, along with `RekeyRequired`, `InvalidProvider`, `InvalidKey`, `WrongScope` and
  `RegistryShuttingDown`. `DuplicateKey` carries the conflicting holder's *name* rather than the object, so a
  refused registration never extends the incumbent's lifetime.
- **`ReferenceRegistrationHandle` releases exactly the registration it was issued for.** It captures the key
  as it stood at registration time; v1 unregistered by re-reading the provider's current `RefId`, so a
  provider whose id changed while registered unregistered the wrong key — or none — and orphaned the real
  entry permanently. A stale handle cannot tear down a re-keyed live registration, and `ClearAll` and
  `Teardown` spend every outstanding handle so one held across a reset cannot remove whatever runs next.
- **`SceneObjectReferenceV2`,** carrying the target's scope, `ReferenceRequiredness`
  (`Optional`/`Required`/`DeferredRequired`) and `ReferenceAvailabilityPolicy`
  (`Immediate`/`Deferred`/`Conditional`). V1 had no requiredness declaration, so a field the author forgot to
  wire and one deliberately left empty were indistinguishable and neither could be validated before play.
  `TryResolve`/`Resolve`/`ResolveAsync` return a `ReferenceResolveResult` instead of an object-or-null, so
  "never assigned", "the scene isn't loaded yet" and "two providers claim this id" stop arriving at the caller
  as the same `null`. Resolving a prefab-local reference takes the owning component, because the serialized
  scope names the template every instance shares and only the context can say which live copy answers —
  without one the result is `WrongScope`, not a reach into whichever instance registered first.
- **`ReferenceManager.Diagnostics`,** a bounded record of registrations, conflicts, legacy fallbacks,
  ambiguous fallbacks, timeouts, cancellations, late successes and destroyed-entry purges, shown live in the
  Hub's Runtime view during Play Mode. A registration conflict is invisible in a steady-state listing — the
  losing registration simply is not there — so without the stream the most diagnostic events in the system
  left no trace. It retains strings and value types only: holding the `IReferenceable` would keep a destroyed
  object's wrapper alive for as long as the buffer held the entry and make the stream a leak proportional to
  churn.
- **Scene load sets in `ProjectSettings/MolcaReferenceLoadSets.json`,** describing which scenes are loaded
  together and which may arrive later. Cross-scene references cannot be validated without such a statement:
  assuming every enabled scene is simultaneously available reports nothing, and assuming only the owner's own
  scene is floods an additively-loaded project with false errors. The file is committed, unlike the Hub's
  severity overrides, because load sets must decide validation identically for every developer and for CI.
  With nothing authored, one set is inferred from the enabled build scenes with everything after the first
  treated as *deferred* — the honest reading of unknown load order — and every surface that uses it says it is
  inferred. When several sets mention the same owner scene the **worst** availability wins, since a reference
  that resolves in one configuration and cannot in another is broken in that second one.
- **A v1-to-v2 scope migration proposal in the Hub's Coverage view.** It narrows a scope only when the data
  forces one conclusion — the site and its single provider are inside the same prefab, or the same scene — and
  hands everything else back as a decision, because a wrong scope turns a working reference into one that
  cannot resolve, silently and across a whole project at once. It prefers the type-matched provider over the
  id-only set, so an exact reference is not called ambiguous merely because an unrelated type reused the id.
  Nothing is applied from the view: migration re-homes a reference into a scope and never re-points it at a
  different target, which would be a repair, and repairs stay a separate previewed action.
- **A scope-aware target picker.** A prefab-local field offers only targets inside its own prefab, and a
  scene-scoped field only targets in its own scene, because nothing else can satisfy those keys. The global
  scopes offer only runtime-registered providers — a prefab-asset or ScriptableObject provider is never
  registered, so offering one guarantees a reference that looks resolved in the Inspector and fails at
  runtime. When a scope leaves nothing to choose the picker explains why, since an empty list is otherwise
  indistinguishable from a broken picker.
- **A persisted reference index under `Library/Molca/References/`,** so a cold editor knows the project's
  reference health without first paying for a full scan. It is derived data and git-ignored: a committed index
  would be a second source of truth able to disagree with the assets it describes, which is what the authored
  id lists on `ReferenceManagerSettings` were. Three properties make restoring it safe. **It proves it is
  current** — every asset an audit read is fingerprinted with its dependency hash at scan time, and the index
  is adopted only if all of them still match, because the `AssetPostprocessor` and scene hooks the in-memory
  cache relies on do not run while Unity is closed. **Findings are re-derived, never replayed** — the file
  stores them so it is readable alone, but loading re-runs the analyzer under the current policy and rules, so
  a policy change or a fixed analyzer bug takes effect immediately instead of waiting for someone to run a
  full audit. **Unverifiable runs are not stored at all** — a scan that read an untitled scene, a dirty scene
  or a modified asset recorded fingerprints for file contents it never looked at, so it stays in memory and
  the Coverage view says why.
- **Incremental index updates.** When some fingerprints no longer match, only the changed assets are
  rescanned and merged. **Analysis still re-runs over everything**, deliberately: a reference in one scene
  resolves against providers in another, so a partial re-analysis could leave a finding the change already
  fixed, or miss one it just caused. Scanning is the expensive half and is the half this skips. The pass
  declines outright — asking for a full audit — when it cannot prove the result, such as a changed scene that
  is not currently open, since re-reading it would mean changing the user's scene setup.
- **`ReferenceCollectionContext.MarkScanned`**, through which a contributor declares the assets its records
  came from. A contributor that adds records without declaring their origin makes the index unverifiable, so
  that run is kept in memory rather than written to disk.
- **A References workspace in the Molca Hub** (group *Quality*, next to Doctor), so reference health is
  continuously visible and repairable instead of being something you go and ask for. It projects the shared
  audit snapshot and owns no scanning or resolution logic of its own. Six views: **Issues** (findings),
  **References** (every site and what it resolves to), **Providers** (every target, with how many references
  resolve to it *and* how many merely store its Ref Id — when those differ, something claims the id and does
  not get it), **Graph** (a bounded one-hop neighbourhood of the selection, where a solid arrow is what the
  runtime resolves and a dashed one is a match that does not win), **Runtime** (live registrations in Play
  Mode, split into *expected but not registered* and *registered but outside the audit scope* — the
  distinction a failed resolve alone cannot tell you), and **Coverage**. The header may print **Clean** only
  when no findings, complete required coverage and a current snapshot all hold at once, and otherwise names
  which of the three is missing. Filters cover severity, text, source kind, reference type, folder,
  requiredness, legacy fallback, read-only assets and repair availability; a filtered table reports what it is
  hiding rather than just showing fewer rows. Filters, selection and an in-flight scan survive a tab switch,
  and **opening the tab does not start a scan** — a scan can open scenes, so it stays an explicit action.
- **Severity policy authoring, in the workspace rather than an Inspector.** `REF002`, `REF003` and `REF004`
  are shown as fixed at error and cannot be authored down; everything else can, and the card says plainly that
  authored severities apply to **editor audits only** — they live in per-user editor prefs, so letting them
  gate a build would make the same commit pass on one machine and fail on another.
- **An "Open in References" action on every reference field**, available on healthy references too: "what
  else points at this target" is a question a working reference raises as often as a broken one.
- **A References activity chip.** Scanning shows progress; errors, incomplete coverage and staleness show a
  dismissible chip; **a clean project shows nothing**, because a permanent green pill is the noise that stops
  a rail being read. Captions are built from counts and states only, never an asset path, so they are safe on a
  remote-observed session.
- **`MolcaHubWindow.OpenWorkspace(string)`**, the id-based counterpart to the existing enum overload, for
  navigating to a workspace Core has no enum member for — including one contributed by another package.
- **`ReferenceBuildGate`, a global `IPreprocessBuildWithReport`.** Reference validation now runs for Molca
  Build Manager, **File → Build**, and batch-mode CI alike; previously it was only invoked from Molca's own
  build command, so a developer using Unity's Build button or a CI job calling `BuildPipeline.BuildPlayer`
  shipped with no reference validation at all. It fails **closed**: a coverage gap or scan failure aborts a
  production build instead of passing green, and a build that processes a scene the gate never validated
  fails rather than letting an explicit `BuildPlayerOptions.scenes` list bypass it.
- **A repair-plan transaction system.** `ReferenceRepairPlanner` builds an inert, fully previewable plan
  from one audit snapshot — every object, property and before/after value — and `ReferenceRepairExecutor`
  applies it as a single Undo group, then re-audits and reports what *measurably* changed, including any
  finding the repair introduced. A plan records the audit revision it came from, so applying it after the
  project moved is rejected rather than applied to data nobody reviewed; each mutation re-checks the value
  it expects to find and is skipped with a reason if that value moved. Reachable from the settings
  Inspector as **Preview Safe Repairs**, and over MCP as `molca_references_plan_fix` →
  `molca_references_apply_fix`. Only three repairs are automatic (assign a missing provider id, refresh
  stale cached metadata, re-key a duplicate nothing references); re-keying a *referenced* duplicate,
  clearing a broken reference, retargeting to an incompatible type, and editing a read-only asset are all
  refused and reported as decisions with their candidates. `molca_fix_refids` is now a deprecated adapter
  over this system rather than a second implementation of the rules — it had got two of them wrong,
  detecting duplicates on the Ref Id alone and re-keying without checking for inbound references.
- **`molca_references_audit`** — read-only projection of the audit snapshot: findings with codes, provider
  and reference-site inventories, per-reference outcomes, and coverage. `molca_refids` remains as a
  deprecated adapter, now derived from the same snapshot so it can no longer disagree with the rest of the
  tooling.
- **`ReferenceAuditEngine.RunAsync` / `ReferenceAuditService.RefreshAsync`** — the audit yields the main
  thread on a time budget, so Molca Doctor, the settings Inspector and the MCP tools no longer freeze the
  editor for the length of a scan and their Cancel is observable within about one asset. Both drivers step
  the same iterator, so the synchronous path the build gate needs cannot drift from the async one.
- **`MolcaReferenceIndexContributor`**, an editor-only seam through which a package outside Core adds
  providers, reference sites and coverage to the shared audit. A contributor that throws is isolated: the
  audit is downgraded to incomplete rather than reported as clean.
- **`ReferenceResolveOutcome`**, the shared vocabulary for what happened during a resolve
  (`ResolvedExact`, `ResolvedViaLegacyFallback`, `ProviderMissing`, `DuplicateProvider`,
  `AmbiguousFallback`, `WrongRuntimeType`, …), so no consumer reports resolution as a bare `bool` again.
- **`ReferenceSeverityPolicy`**, centralizing finding severity. A development build may lower coverage and
  scan-failure findings; duplicate providers, ambiguous fallbacks and wrong target types are runtime failures
  and can never be configured below error, even by an explicit override.

### Deprecated
- **The `ReferenceManagerSettings` id-list queries** — `GetReferenceStats`, `GetReferenceTypes`,
  `GetReferenceIds` and `FindDuplicateIds`. They read the authored lists, which are no longer the
  operational index and may be stale or empty. `FindDuplicateIds` in particular counted list *entries*,
  which is not the same question as "do two live providers claim one key" — the audit answers that one on
  the exact key the runtime registers under, and reports it as `REF002`. Project a
  `ReferenceAuditSnapshot` instead.
- **`ReferenceManagerSettings.ShowValidationResults`** no longer does anything: the References workspace
  and Doctor decide their own presentation.
- **`ReferenceManager.Instance`** now states its removal window alongside its replacement. Every obsolete
  member in the reference system names both, so an obsolete warning tells a fork what to do rather than
  only what to suppress — there is a test for it.
- **`ReferenceManagerSettings.FixRefIdsOnSceneSave`** no longer does anything. Reassigning a provider's Ref
  Id during a save silently broke every reference pointing at it, with no preview and no record of what
  moved. Saving a scene now reports its provider identity problems and changes nothing.
- **`ReferenceManagerSettings.AutoValidateOnScan`** no longer does anything: analysis is part of every scan,
  so there is nothing to switch off.
- The `assetKnownIds` / `sceneKnownIds` buckets on `ReferenceManagerSettings` are no longer written or
  consulted as authoritative data — the audit reads the objects that actually provide the references, so a
  stale cache can no longer produce a false finding or false confidence. They stay readable for one
  compatibility release; the Inspector offers **Remove Legacy Cached ID Lists**.

## [1.17.3] - 2026-07-28

### Removed
- **Sequence (`SequenceController`, `Step`, `StepAuxiliary`, and all Sequence editor tooling — authoring and
  graph views, the validator, its Doctor check, and its MCP tools) has moved out of Core into its own
  package, `com.molca.sequence`.** Core no longer declares a dependency on it — the two are decoupled, not
  merely relocated, so a project that doesn't use Sequence carries none of its code or tooling. Distributed
  through Hub → Add-ons as a signed UPM-shaped pack via the Molca control plane (protocol 3), not a git-URL
  dist repo like `com.molca.core`/`com.molca.sdk`. Existing projects add `com.molca.sequence` the same way as
  any other add-on; its own reference docs (`SEQUENCE_AUTHORING.md`, `SEQUENCE_VALIDATION.md`) moved with it.
  `FixReversibility`/`FieldEditResult` and the serialized-field editing helpers Sequence's tooling shares with
  the rest of Core's Doctor/editor systems stayed behind in Core, since other Core systems depend on them.

### Added
- **The Hub toolbar now survives any number of workspace tabs.** The strip measures itself and degrades in
  order: full `[icon] [label]` tabs, then icon-only (all-or-nothing, and only when every tab resolves an
  icon — a row of blanks reads worse than a menu), then a `» N` overflow menu. Tabs that do not fit are one
  click away in that menu, grouped and never silently dropped; the Settings tab and the active tab always
  keep their slot and their label. `.molca-hub-workspace-tab` also gained `flex-shrink: 0`, which on its own
  fixes labels being cut mid-word.
- **The Hub search box finds workspace tabs, not just settings sections.** Typing surfaces a *Workspaces*
  group above the section results, and Enter activates the first match. Note the box lives in the Settings
  rail panel, so it is a Settings-surface affordance — a global command palette is a separate proposal.
- **`MolcaHubWorkspaceItem.Group`** and **`MolcaHubWorkspaceGroups`**: a tab declares a semantic group
  (`quality`, `authoring`, `assistance`, `integrations`, `reference`, or the default `general`) instead of
  guessing an `Order` integer against a namespace it cannot observe. Tabs sort by group rank, then `Order`
  *within* the group — the only scope a provider can reason about honestly. Unknown groups are allowed and
  sort last. Existing providers compile untouched and land in `general`; Core's own tabs render in exactly
  the order they did before.
- **Pinning and recents.** `MolcaHubWorkspaceRegistry.PinnedIds()`/`SetPinned(id, pinned)`/`PinsChanged` keep
  chosen tabs in the toolbar when space runs out; everything else falls back to overflow, ordered by recent
  use. Pin from a tab's right-click menu or the Settings ▸ Editor ▸ Workspace Tabs card. Hidden still beats
  pinned, and the default is no pins — an existing project's toolbar is unchanged until someone pins something.
- **`MolcaHubSettingsLeafProvider`**: a lower-ceremony seam for a contribution that is really one settings
  panel rather than a full-window tool. Discovered via `TypeCache` with the same resolution contract as the
  workspace registry, placed into a named rail category, and namespaced as `ext:<id>` so a provider can never
  collide with a Core section name. Contribute a workspace tab when your surface is a full-window tool with
  its own toolbar and long-running work; contribute a leaf when it would look at home next to *Network* or *MCP*.
- **`MolcaHubWorkspaceItem.CacheContent`**: opt a workspace view into being hidden rather than rebuilt on tab
  switch, so scroll position and in-progress view state survive. Opt-in only, because it changes the view's
  lifecycle contract — an opted-in view keeps running while hidden and does not get a `DetachFromPanelEvent`
  between activations (detach still fires on eviction, and at most three views are kept). Core opts in Docs
  and Sequence; Doctor and Assistant deliberately stay uncached until their long-running work is reviewed
  against a hidden-but-live view.

### Changed
- `MolcaHubWorkspaceItem`'s constructor gained three optional trailing parameters (`group`, `cacheContent`,
  alongside the existing ones). This is source-compatible — existing provider code compiles unmodified — but
  not binary-compatible. Core ships as a UPM source package and consumers recompile, so this is theoretical;
  it is called out here for anyone shipping a precompiled DLL against the old signature.
- **A git install can now take a Core update from the Hub instead of being sent to `manifest.json`.**
  Repointing a git dependency is a `Client.Add` against the git URL — exactly the move the Package
  Manager's own `Manage ▸ Update` makes — so About > Updates offers the same one-click update it offers a
  registry install. Unity still writes the manifest entry; Core never edits it. The clipboard fallback
  remains for the two cases the client cannot apply faithfully: a release whose `upgradeSpec` is
  registry-shaped (adding it would silently move the project off git) and one whose git URL pins no
  revision (it would resolve the default branch's HEAD, not the version named on the card).
- An `upgradeSpec` published in npm's `git+https://…` spelling is now accepted. Neither `Client.Add` nor
  `Packages/manifest.json` takes the `git+` prefix, so it is stripped before either sees it — previously
  the copied manifest line carried a prefix that would not resolve.

### Fixed
- **A Remote session no longer dies the instant it connects.** `AssistantRemoteFacade.Changed` is not a
  field-like event but a custom accessor over `AssistantChatRuntime.Shared`, which on first touch builds the
  Assistant settings asset through the `AssetDatabase`. `RunSocketAsync` subscribed to it from the socket
  thread — the first thing it does after a successful connect — so Unity refused it, correctly, and the
  session was torn down on arrival on every attempt. The lazy initialiser never completes when it throws, so
  it threw again on every retry rather than failing once. Both the subscribe and the unsubscribe now go
  through the main-thread marshal (the unsubscribe folded into the existing teardown, so it costs no extra
  round-trip), and `Changed` documents the hazard at its declaration so the next caller does not repeat it.
  Toggling Remote off and on also clears the announced-reason memory, so a re-enable reports its failure in
  full instead of being suppressed as a duplicate of one whose warning has scrolled away.
- **A Remote connect/fail flap reports one line, and names its own throw site.** `"Connected"` clearing the
  last-logged-reason memory looked right — a recurrence after a working session is news — but a session that
  dies on arrival reaches `"Connected"` on every attempt, which restored the per-attempt spam by a second
  route. Only a session that holds for 30s now clears it, which is `RunLoopAsync`'s call to make and not
  something a status string can answer. A busy failure that no marshal reported also carries
  `DescribeThrowSite` — the exception's own innermost frames — because the async resumption chain Unity prints
  in the console does not name the throwing call, which is what made this class of bug so hard to place.
- **Molca Remote can connect again.** The connect response was parsed with `JsonUtility.FromJson` — a
  UnityEngine API — on the connection loop's thread-pool thread, where Unity refuses it with "can only be
  called from the main thread". Correctly, this time: that really is not the main thread. It threw on every
  attempt, so the session could never be established and the refusal was reported as the connection's failure
  reason. It worked only while the loop ran on the main thread and captured Unity's synchronization context,
  so moving the loop to `Task.Run` — the fix for Remote stalling the editor on an unreachable server — is what
  exposed it. Now mapped field by field with Newtonsoft, keeping the whole connect path free of Unity APIs.
- **One unchanging Remote failure is announced once, not once per retry.** `SetStatus` wrote the
  last-logged-reason memory *before* filtering out the quiet transitions, so the `"Connecting"` that precedes
  every attempt cleared it each time and a persistent reason logged on every attempt forever — the exact spam
  the de-duplication existed to prevent. The decision moved to `ShouldLogReason`, which filters first;
  connecting successfully clears the memory, so a recurrence after a working session is still reported. The
  `Editor busy` line now also names the operation and the editor's own first line, so a persistent refusal
  identifies its own call site without a stack trace, and a busy editor no longer escalates the retry backoff
  — it is transient by nature, and escalating pushed a Remote enabled during a scene load out to a minute.
- **A busy editor no longer reads as a broken Remote connection.** Unity refuses editor APIs during a domain
  reload or scene load with "can only be called from the main thread" — *even when the caller is the main
  thread* — and `McpMainThreadDispatcher` rethrows that onto the socket's thread, where the connection loop
  could not tell it from a transport fault. Three marshals were still unguarded: `GatherConnectInputs`, which
  runs on every connect attempt and reads both the Package Manager and the AssetDatabase, and the
  `assistant.*` / `automation.*` command handlers, which sat outside the `try` that protects `tool.invoke` —
  so one badly-timed request closed the socket. All three are now classified through `IsEditorBusy`: a connect
  attempt reports the status `Editor busy` and retries on the existing backoff, and a command answers the new
  retryable `editor_busy` wire code instead of dropping the session. `UserFacingStatus` also no longer passes
  an unrecognised exception message through verbatim — it truncates to the first line, which is what leaked
  three lines of Unity's script-authoring advice into the console under a `[Molca Remote]` prefix.
- **Hub activity discovery no longer warns about providers it was never meant to own.**
  `MolcaHubActivityRegistry.CreateProviders` now requires a public parameterless constructor (and skips open
  generics) before probing a type, instead of calling `Activator.CreateInstance` on everything derived from
  `MolcaHubActivityProvider` and reporting the inevitable failure. `TypeCache` sees test assemblies, so a
  provider built by its own caller — a test double, or one composed by the system it observes — produced a
  warning on every Hub open and every Remote session start. The warning is kept for the case that is a real
  fault: a default-constructible provider whose constructor throws.
- **Nested lists are indented instead of flattened.** Every list item's leading whitespace was discarded
  before the parser saw it, so a sub-list under a numbered step rendered at the same level as the step —
  losing the structure the doc was written with. `MolcaMarkdownBlock.Indent` now carries a 0-based nesting
  depth, which the renderer turns into a left margin and (for bullets) a disc → circle → square glyph cycle.
  Depth is read *relatively*, from a stack of open indent columns rather than a fixed spaces-per-level
  divisor, so a doc nesting by two spaces and one nesting by four both come out one level deep; a blank line
  keeps a list open, any other block kind ends it, and runaway nesting clamps at five levels.
- **`MolcaMarkdown` now renders GitHub alert callouts (`> [!NOTE|TIP|IMPORTANT|WARNING|CAUTION]`)** as a
  titled, type-colored variant of the blockquote instead of leaking the literal `[!TIP]` marker into the
  page. It also accepts the shape HTML→Markdown exporters actually emit — the marker on its own line,
  without the `>` prefix and with escaped brackets (`\[!TIP\]`) — because that is what real SDK docs
  contain. A marker with no quoted body after it renders nothing rather than a stray line.
- **`* * *` (spaced horizontal rule) is a rule again.** Only the unspaced `---`/`***`/`___` form was
  recognized, so the spaced form CommonMark allows — and exporters prefer — fell through to the bullet
  branch and rendered as a stray "• * *" list item between every section.
- **Backslash escapes are honored.** Only `\_` was unescaped; every CommonMark-escapable punctuation
  character is now rendered literally, in both the inline spans and `CleanInline`'s plain text. Windows
  paths are unaffected — a backslash before a non-punctuation character is left alone.

## [1.17.2] - 2026-07-27

### Fixed
- **A busy Editor no longer drops a working Remote session.** Reading the editor is not always possible:
  during a domain reload or a scene load Unity refuses Package Manager and other editor APIs with "can only
  be called from the main thread" *even on the main thread*, and the main-thread dispatcher faithfully
  rethrows that onto the socket thread, where it is indistinguishable from a transport error. A session
  would connect, read a busy editor, and tear itself down in a loop.
  Every main-thread read a session performs — activity-provider discovery, the state snapshot, the
  Assistant snapshot, maintenance and authorization-loss stops, and teardown — is now treated as skippable:
  it is reported once per session naming the operation, and the session continues, recovering on the next
  change or heartbeat. Nothing a remote session reads is worth dropping the session for.
- The Core-version lookups in the remote snapshot, the `molca.preflight` versions step, and the MCP status
  tool went through raw `PackageInfo.FindForAssembly`. They now use the same guarded helper the rest of Core
  uses, so an unreadable moment degrades to an empty or "unknown" version instead of failing the caller.

## [1.17.1] - 2026-07-27

### Fixed
- **A Molca Remote connection that cannot be established no longer stalls the Editor.** Three faults
  compounded. The retry loop was started from the main thread, so Unity's synchronization context was
  captured and every `await` in it — the HTTP request, the socket receive, the backoff delay — resumed on
  the main thread; it now runs on the thread pool. Each attempt also read Editor state directly from that
  loop's thread, including `MolcaProjectSettings.Instance`, which calls `AssetDatabase.LoadAssetAtPath` and
  can move an asset on its legacy-migration path; those reads are now gathered once per attempt through the
  main-thread dispatcher. Worst of all, this was heaviest in exactly the failing case: with no settings
  asset present nothing caches, so every retry re-ran the asset lookup and the migration probe.
- **Toggling Remote no longer leaves connection loops running.** Stopping disposed the cancellation source
  the running loop was still awaiting, so its parked delay threw `ObjectDisposedException` — not
  `OperationCanceledException` — which fell through to the generic handler and retried immediately, while
  the cleared task handle let a second loop start. Each toggle could add another loop. Loops are now
  retired by generation, and each owns its own token source.
- A failing connection reports each distinct reason once instead of logging a warning on every attempt, and
  the Hub status line now says how long the backoff is, so a polite retry is distinguishable from a hot loop.
- Also marshals the initial Assistant snapshot read off the socket thread onto the main thread — the same
  class of defect, in the connected path rather than the retry loop.

## [1.17.0] - 2026-07-27

### Added
- **Molca Remote is installable.** The dashboard ships a manifest, its own icon set, and a service worker
  scoped to `/dashboard/`, so it installs as a standalone app. A session is now four tabs — Overview,
  Workflows, Runs, Assistant — laid out as a bottom tab bar with safe-area insets on a phone and as a rail
  inside the session card on a desktop; no capability moves between the two. Only the app shell and its
  static assets are ever cached: Editor state is network-only, so an installed app that has lost its
  session shows sign-in rather than a stale view of the project.
- **Run automation from Molca Remote.** An authorized session can read the command catalog, preview a
  plan, start a run, follow it, cancel it, and revert it — through six new `automation.*` remote command
  types. Every one goes through the kernel's own policy, mode, confirmation, verification, and audit
  seams; `MolcaTransport.Remote` is recorded in the automation audit log. Under the **Observe** profile
  every action refuses remotely, with the policy's own message shown in the dashboard.
  - **Accept-fast.** A remote command row expires 60 s after creation, which bounds delivery and
    acceptance, not the work. `automation.invoke` returns as soon as the run is accepted and the run
    proceeds in an owned task the Editor's update loop drives, reporting through the activity and
    automation state blocks. A `molca.build` that runs for minutes is therefore not a protocol problem.
  - **Refusals that say why.** A headless Editor refuses `automation.batch_mode_refused` — a detached run
    would silently stall because fire-and-forget `Awaitable` chains do not advance without an update loop.
    A second concurrent remote run refuses `automation.run_in_flight` rather than queueing invisibly. A
    request authorized against a catalog the Editor no longer holds refuses `automation.catalog_stale`.
  - Actions additionally need **Allow remote actions** and a place on the remote action allowlist, which
    is a separate list from automation's — being on one never implies the other.
- **Molca Remote sees what the Editor is doing.** A remote session's `state.snapshot` now carries the Hub's
  activity rail and the automation kernel's live state, so a connected dashboard shows a Doctor scan
  advancing, an automation run's progress and step, the active policy profile, and recent run history
  instead of a static presence card. Additive within `remoteEditor` protocol 1 — an older control plane
  drops the new fields and stays connected.
- **`MolcaHubActivity.RemoteSafe`** (public API addition, default `false`). Only chips whose provider opts in
  are eligible to leave the Editor, because a chip's `Status` is author-controlled free text. Core opts in
  Doctor, automation runs, and the framework-update chip; a chip whose caption embeds a third-party
  command's own progress message does not opt in, and an add-on's activity provider exports nothing until
  someone reviews what its captions can contain. `OnClick` and `OnDismiss` are never serialized, and
  `WorkspaceId` travels only as a labelling hint.
- **`MolcaTransport.Remote`** so a run started from a remote session is distinguishable from one started at
  the keyboard in audit and policy. Remote authorization is additive to automation policy — it never raises
  the active profile, extends its allowlist, or implies confirmation.

### Changed
- Remote `state.snapshot` is change-driven and coalesced rather than sent once at connect: a 750 ms debounce,
  a 2 s floor, byte-identical payloads dropped, and one forced send per heartbeat. Observation has no local
  toggle — enabling Molca Remote for the project enables it, because a remote session that cannot say what
  the Editor is doing is the problem the feature exists to solve. Control keeps its own opt-ins.
- A command's own progress message is projected remotely only for the commands Core ships. A third-party
  command's run still reports status, progress, and step; its prose stays on the machine.
- `state.delta` is removed from the `remoteEditor` v1 message list. It was declared and never implemented by
  either peer, so no conforming peer could have sent or relied on it.

## [1.16.2] - 2026-07-27

### Fixed
- The package's `documentationUrl` and `changelogUrl` pointed at a repository that is neither the dev
  repository's actual name nor public, so the Package Manager's **Documentation** and **Changelog** links and
  the Hub About **Changelog** row all failed for consumers. Both now point at the public
  `com.molca.core-dist` repository the package is installed from.
- The install snippets in the documentation index and **Getting Started** named the wrong organization, used
  the dev repository's `?path=` sub-folder form that the flat distribution repository does not take, and
  pinned `1.0.0`. They now give the distribution repository's bare Git URL at the current tag.
- Hub toggle checkboxes now line up on one x. Unity lays a labelled `Toggle` out as caption-then-checkbox, so
  a stack of toggles with different caption lengths — About → Updates and Project → Remote Editor especially —
  showed a ragged checkbox column. Label-less toggles are unaffected.

## [1.16.1] - 2026-07-26

### Added
- **Molca Remote Editor** (`remoteEditor` protocol 1). A running Editor can connect to the Molca dashboard
  over an outbound encrypted link, without exposing Unity, the local MCP listener, or the local MCP token to
  the network. Enabled per project from **Hub → Project → Remote Editor**, private to the signed-in user, and
  rechecked against current license, project membership, and binding state. The presence snapshot is bounded
  (versions, edit/play mode, compilation health, project identity, scene/selection summary, console counts,
  Assistant activity); detail is fetched through the same allowlisted read-only tool registry and main-thread
  dispatcher as local MCP. **Allow remote Assistant** and **Allow remote actions** are separate opt-ins;
  remote actions additionally require the server feature, session ownership with project action access, the
  local action allowlist, an unchanged Editor/scene/selection context, and a free action lane. Remote
  Assistant never changes the configured action mode and cannot answer local confirmations from the web. See
  [`REMOTE_EDITOR.md`](Documentation~/reference/REMOTE_EDITOR.md).
- **Molca Free assistant provider.** A fourth Assistant provider that reuses the machine-bound developer
  entitlement to reach the control plane's OpenAI-compatible route. The client exposes only the stable
  `molca/free` alias and the server picks a currently available, tool-capable, zero-price model, keeping its
  upstream key private. Model and endpoint fields are locked so a consumer project cannot redirect the
  entitlement; **Check** forces a live availability refresh. No user-supplied key and no new secrets path.

### Changed
- **Assistant turns survive leaving the Assistant tab.** Turn execution moved out of the chat view into an
  editor-domain chat runtime, so switching Hub workspaces, docking the Hub, or rebuilding the editor layout
  detaches only the visible screen — the turn keeps running and reattaches to the same session and live
  transcript on return. The Hub and the Remote dashboard drive one shared turn; **Stop** in either cancels it.
- `MolcaDiagnostics` is now documented in the Telemetry & Diagnostics guide (API surface, sink registration,
  payload bounds, sink isolation, and why diagnostics stays separate from usage telemetry), and
  `DocsCoverageCheck` maps Runtime/Diagnostics to that guide instead of leaving a user-facing runtime system
  uncovered.

### Fixed
- The Hub project picker no longer closes while the project list is loading.
- The Hub project-section connection test pinned the disconnected copy and only passed on an unconnected
  project; it now accepts any state `ProjectConnectionText()` can return.

## [1.16.0] - 2026-07-26

### Added
- Project-bound add-on distribution protocol 3 with signed dependency graphs, project closure approval,
  reviewed external prerequisites, transactional graph installation, dependency-aware removal, and
  domain-reload resume.
- A public, bounded, vendor-neutral `MolcaDiagnostics` sink API with initial runtime-bootstrap breadcrumbs and
  explicit handled-exception capture. Core has no dependency on Sentry or another diagnostics vendor.
- Build provenance now stamps the non-secret Molca project ID and project code without embedding its signed
  binding receipt.

### Changed
- Add-on catalog and manifest operations require an active project binding and protocol 3.
- Project connection guidance now wraps below the Project ID controls instead of overflowing the Identity card.

## [1.15.0] - 2026-07-25

### Added
- **Hub → Settings → About.** A new last rail leaf reporting what a project is actually running: every
  installed `com.molca.*` package with its version and install source (registry / git / embedded / local),
  the editor version and scripting runtime, wire-schema versions, and the installed add-on count. **Copy
  diagnostics** puts the whole table on the clipboard as markdown for bug reports. The package list is
  enumerated rather than hardcoded, so an SDK layer or fork's own Molca packages report themselves without a
  Core edit. The card also mirrors the stored developer entitlement read-only and collects the repository,
  documentation, changelog, and support links.
- **Framework update check.** About reads the control plane's release feed
  (`GET /framework/releases/latest`, `frameworkUpdate` protocol 1) through the same trust shape as the add-on
  catalog — developer entitlement, pinned HTTPS host, machine header — and reports whether a newer Core
  exists, whether this editor's Unity can take it, and whether the installed version is still supported.
  Answers are cached for six hours; **Check now** bypasses the cache, the check never runs in batch mode, and
  it is opt-out per project.

  What the card offers depends on how Core is installed, because that is what decides whether an upgrade can
  be applied at all: a registry install gets a confirmed one-click Package Manager update, a git install gets
  the manifest dependency line copied, and an embedded or local install gets the upgrade spec copied. No code
  path edits `Packages/manifest.json` or files on disk. A release that raises the minimum Unity is reported
  but never offered — when the feed also names an older installable release, that one is offered and the
  blocked one stays visible with its requirement. Being offline or not signed in is reported inside the card,
  with no console error and no dialog. An optional activity-rail chip (off by default) surfaces an available
  update outside About.

### Changed
- **Developer OAuth code exchange moved to the control plane.** Core now sends the one-time Google
  authorization code, PKCE verifier, and loopback redirect URI to `/activate-dev`; it no longer ships a
  Google client secret or handles Google access tokens. The shared authorization-code client adds an
  authorize-only API while retaining the existing client-side exchange behavior for integrations.

## [1.14.0] - 2026-07-24

### Added
- **Revamped Add-ons Hub UI.** "Add-ons" is now its own Settings-rail root with **Browse** and **Installed**
  children, rebuilt on the shared editor design language (`MolcaSectionCard`/`MolcaSearchField`/`MolcaButtons`,
  tokenised USS) instead of the previous inline-styled single list. Cards show per-add-on status (installed /
  update available / source-drift / files-missing), an expandable details foldout (trust, compatibility,
  integrity), search, and an add-on icon (custom `package.json` `"icon"` with a generic fallback). Add-ons can
  also contribute their own `Documentation~/reference/*.md` guides, which surface automatically in the Hub
  **Docs** tab (any installed `com.molca.*` package is scanned).
- **Add-on license gate + load-time integrity check.** The Hub Add-ons panel withholds the online
  browse/install/update/remove surface unless a valid Molca developer license is present (offline signed-bundle
  import stays available by design). On editor load, installed add-ons are checked against the ownership
  ledger: a warning is logged when the license is invalid and when an installed add-on's `.cs` source has
  drifted from the signed content it was installed from (new `contentHash` recorded at install).

### Changed
- **Add-on activation defers its asset refresh** to a clean editor tick (`EditorApplication.delayCall`) instead
  of refreshing inline, fixing a domain-reload hang when updating an already-resolved add-on package.
- **Figma integration extracted to an add-on.** `FigmaIntegrationProvider` and its API client/translator,
  the Figma-specific UI Intent Spec producers (`FigmaFrameModel`, `FigmaColorSnap`, `FigmaTokenMapper`,
  `FigmaSpecComposer`), and the `molca_figma_*` / `molca_figma_to_ui_spec` MCP tools moved out of Core into
  the `com.molca.addon.figma` add-on package (`FigmaMcpToolProvider`, `molca.figma` namespace) — the first
  real capability distributed through the licensed add-on channel. `UiIntentSpec` and
  `UiIntentSpecValidator` stay in Core (relocated from `Editor/UI/Figma/` to `Editor/UI/Build/`, same
  namespace) as the generic contract the uGUI build pipeline and `molca_build_ugui` already depended on.

## [1.13.0] - 2026-07-23

### Added
- **Licensed Add-ons workspace.** The Hub can browse, install, update, remove, and import signed offline
  add-on packages using the current developer entitlement.
- **Defense-in-depth package verification.** Add-on acquisition validates the keyed RSA manifest, expected
  hash and size, Core/runtime compatibility, trusted hosts, tar confinement, package identity, and assembly
  policy before transactional activation.
- **Add-on ownership, recovery, audit, and telemetry.** Manager-owned installations have a durable ledger,
  recoverable removal, local audit history, and a retrying privacy-preserving event queue.
- **Editor tests and consumer guides** for the distribution contract and Add-ons workflow.

### Changed
- Internal licensing, signing, authoring, publishing, and deployment documentation now lives in the private
  `molca-unity-platform` integration repository rather than the consumer Unity package.
- The backend and dashboard were extracted from this repository into `molca-unity-control-plane`.

## [1.12.5] - 2026-07-22

### Added
- **`MolcaMarkdown` content-weighted table columns.** Rendered tables now size columns by content instead
  of splitting width evenly.
- **Mermaid flowcharts added to key reference guides** (Dependency Injection, Networking, Sequences,
  Sequence Validation, Subsystems, Figma→uGUI).
- **Bundled Poppins-Medium font** under `Runtime/UI/Fonts/Resources/Molca/` (with its OFL license) for use
  by UI tokens/components.

### Fixed
- **Cyclic Mermaid flowcharts no longer crash the renderer.** `MolcaMermaidView.Layout` bucketed nodes by
  layer assuming `AssignLayers`'s bounded longest-path relaxation always reaches a fixpoint; a flowchart
  with a cycle (e.g. a retry back-edge) never converges, producing out-of-range layer indices. Layers are
  now normalized/clamped before bucketing.
- **`BudgetMonitor` overlay renders its labels**, not just the bars.
- **Repaired four failing editor tests** (`DoctorCategoriesTests`, `MolcaHubTests`).

## [1.12.4] - 2026-07-21

### Changed
- **`MolcaHubWindow.OpenDoc(string)` is now public** (was `internal`), so an SDK layer or consumer project
  can programmatically open a specific Hub doc by id.

## [1.12.3] - 2026-07-07

### Added
- **Hub Docs system.** The Hub's Docs section is now a `TypeCache`-discovered docs-provider registry with
  front-matter-driven metadata, a nested rail `TreeView` (docs as a first-class branch alongside Sequence
  etc.), `molca://asset|doc` in-viewer link resolution (sibling-doc navigation, no full reload), and
  "See also" cross-links across every reference guide. Docs are now grouped by product with a switcher, and
  the Docs section is promoted to a right-anchored workspace tab. New "Authoring Hub Docs" guide.
- **Reusable `MolcaMarkdown` renderer.** Extracted from the Assistant into `Molca.Editor.UI.MolcaMarkdown`
  (`Render`/`Create` + `Variant`/`ActionScheme`/`OnAction`/`OnOpenFile`/`OnOpenUrl` options); the Assistant
  transcript is its first re-user. Renders H1–H6 (previously H1/H2 only), monospace code blocks, and now
  native **Mermaid flowchart** diagrams (` ```mermaid ` fences → a layered Painter2D-based renderer, with a
  plain-text fallback).
- **42 Core reference guides (Sprint 90)** covering Runtime/Core, Scene/Refs, Sequences, Data/UI, Settings,
  Data Providers, Content Packages, and Diagnostics/Tooling, plus a Getting Started guide and taxonomy
  pass. A new **`DocsCoverageCheck`** Doctor guardrail flags undocumented public systems.
- **`DocLinksCheck`** — a Doctor check that flags broken reference-doc links.
- **`molca_docs_list` / `molca_docs_read` / `molca_docs_search` MCP tools** for querying installed
  packages' reference docs directly.
- **Doctor checks grouped by category** with collapsible sections in the Hub.

### Changed
- Reconciled `.claude/` convention docs with the current code (event names, bootstrap waves).

## [1.12.2] - 2026-07-04

> Core level-up pass (Sprints 78–87): silent-failure scrubs, bootstrap/DI/event/sequence hardening,
> networking resilience & privacy, durability, async/threading contract enforcement, and expanded test
> coverage. Behavior-compatible hardening except where noted.

### Added
- **Bootstrap 2.0 (Sprint 80).** Wave-based subsystem initialization with observable failure and DI
  hardening — bootstrap surfaces per-wave/​per-subsystem failures instead of failing opaquely.
- **Doctor convention-enforcement checks (Sprint 86).** New checks that flag framework-convention
  violations (discovered via the Sprint-63 `DoctorCheckRegistry`).
- **PlayMode test assembly + threading contract (Sprint 87).** A new `Molca.Core.PlayModeTests` assembly
  plus the documented threading contract for the data-provider and log pipelines.

### Changed
- **EventDispatcher hardening & Sequence async contract (Sprint 81).** Listener/dispatch hardening and
  Sequence brought onto the `Awaitable` async contract.
- **Audio async sweep + read-only ScriptableObject enforcement (Sprint 85).** Audio APIs swept onto the
  async contract; runtime writes to config ScriptableObjects are now guarded/enforced read-only.

### Fixed
- **Silent-failure bug scrubs.** Sequence & kernel (Sprint 78) and networking, audio & data (Sprint 79)
  paths that previously swallowed failures now surface or log them.
- **Streaming resilience (Sprint 82).** SSE/WebSocket reconnect with backoff and auth-token refresh on
  reconnect; RFC-compliant SSE parsing.
- **Auth & HTTP privacy/correctness hardening (Sprint 83).** Credential/redaction and correctness fixes in
  the auth and HTTP client paths.
- **ContentPackage & telemetry durability (Sprint 84).** Hardened the ContentPackage download/storage and
  telemetry sink paths against interruption/corruption.

## [1.12.1] - 2026-07-04

### Added
- **Extensible Molca Doctor.** Doctor checks are now discovered via `DoctorCheckRegistry` (TypeCache),
  so an SDK layer or consumer project adds a check simply by implementing `IDoctorCheck` in an Editor
  assembly — no Core edit. Core's built-in checks keep their curated order; discovered checks follow,
  sorted by `Id`. Duplicate/empty ids are rejected loudly. See `Documentation~/reference/DOCTOR_CHECKS.md`.

## [1.12.0] - 2026-07-04

### Added
- **Assistant multimodal image input (Sprint 73).** The in-editor assistant accepts image attachments
  (screenshots/captures) alongside text on vision-capable models; input is gated by model capability and
  images are cost-accounted and redaction-marked.
- **Prompt caching (Sprint 74).** Anthropic `cache_control` breakpoints + OpenAI cached-token accounting,
  with a stable prompt prefix so the cache stays warm across turns; cache-aware cost + hit-rate reporting
  and an Auto/On/Off setting.
- **Web & documentation lookup (Sprint 75).** Read-only, egress-gated `molca_web_fetch` + `molca_web_search`
  tools (Brave/Tavily) with a host allowlist, HTML→text extraction, size caps, and redaction; disabled by
  default.
- **Reasoning effort (Sprint 76).** A neutral `ReasoningEffort` setting mapped to Anthropic thinking budget
  / OpenAI reasoning effort, with a collapsed "Thought" affordance and distinct reasoning-token billing.
- **Cross-session memory (Sprint 77).** File-backed assistant facts under `Assets/_Molca/AssistantMemory`
  with `molca_memory_recall`/`save`/`delete` and turn-start recall injection.
- **Scene-object tooling upgrades.** Tolerant GameObject resolution + batch targeting + asset duplication;
  `componentType`/`nameContains` filters on `molca_unity_select`; and `auxiliaryType` filter + auxiliary
  listing on `molca_unity_scene_objects`.
- **Auto-index installed Core/SDK packages** for knowledge-graph retrieval.

## [1.11.3] - 2026-07-01

### Added
- **Onboarding Wizard (`Molca > Onboarding Wizard`), implementing the Sprint 63.7 contract.** Standalone,
  post-compile-only window with independent, idempotent setup steps: seed/open the consumer-space
  `MolcaProjectSettings` clone; run the SDK's Quick Setup (via reflection — Core never references the SDK
  assembly, so the card only appears when an SDK layer is installed); generate a project-root `CLAUDE.md`
  stub pointing at installed packages' `Documentation~/reference/` (only when absent, never overwrites);
  build the MCP proxy (reuses `McpProxyBuilder`); optional Doctor smoke test and knowledge-graph build.
  A one-time `InitializeOnLoadMethod` offers to open it on a genuinely fresh install (no live
  `MolcaProjectSettings` asset yet), deferred past first compile and never shown again after either choice.

### Fixed
- **Core no longer depends on an SDK-layer asset.** `Confirmation Short.prefab` / `Confirmation
  Detailed.prefab` under `Runtime/Modals/` were Prefab Variants of `com.molca.sdk`'s
  `Runtime/Prefabs/UI/Button.prefab` — a reverse dependency the layer model forbids, and the cause of the
  "Problem detected while importing the Prefab file... Missing Prefab with guid 5addbc44..." error on a
  fresh install (an import-order race between the two packages). Both prefabs moved to
  `com.molca.sdk`'s `Runtime/Prefabs/Modals/` (guids unchanged, so the SDK's `Runtime Manager.prefab`
  reference resolves without changes); see `com.molca.sdk` 0.2.2.
- **`BootstrapAssetValidator` error messages now say what to do.** The `RuntimeManager`/`GlobalSettings`
  null-reference errors on a fresh install pointed at "assign an asset" with no indication that
  `Molca > SDK > Quick Setup > Install Starter Settings` seeds them; the messages now name that menu path
  (or the new Onboarding Wizard, which drives the same command).

## [1.11.2] - 2026-07-01

### Fixed
- **Dist-repo publish pipeline no longer strips `Tools~`/`Documentation~`/`Samples~`.** The
  `com.molca.core-dist` repo's `.gitignore` used a bare `*~` rule meant for OS/editor backup files, which
  also matched directory names ending in a tilde — so `Tools~/molca-mcp` (and `Documentation~`,
  `Samples~`) were silently dropped from every published release even though `PUBLISH_MANIFEST.txt`
  reported them as shipped. This broke "Build MCP Proxy" for any consumer installing via the Git-URL
  package (`Could not locate the proxy source in the package (Tools~/molca-mcp)`). The dist repo's
  `.gitignore` is fixed; this release republishes with the folders actually included.

## [1.11.1] - 2026-07-01

### Added
- **In-window model & provider switcher (Sprint 71).** The assistant window gains a provider/model picker
  backed by model discovery (Ollama `/api/tags`, curated cloud models, free-text); applying a selection
  re-resolves the transport.
- **Weak-model eval harness (Sprint 70).** A deterministic-replay + opt-in live (`MOLCA_EVAL_LIVE`) eval
  harness over both transports, exercising golden tool-execution and comprehension scenarios against the
  real send path.

### Changed
- **Assistant header shows the chat title**, not the model line; model-picker hint moved to its own line.
- **Collapsible "Advanced" group** in the assistant settings section.

### Fixed
- **Coherence fixes (Sprint 72):** blank-required-arg guard on both transports, a goal-persistence note in
  the request messages, and a `componentType` filter on `molca_unity_scene_objects`; plus expanded eval
  coverage.
- **Eval grounding mock + report-outcome rule** applied on both transports.
- **Guard against a stale `MolcaEditorSettings` asset** in the settings inspector.
- **`MediaLoader` HTTP-asset anti-pattern** call-site corrected.

## [1.10.7] - 2026-07-01

### Added
- **Text/XML tool-call protocol for weak local models (Sprint 69).** The in-editor assistant can drive tool
  calls via a text/XML protocol instead of native function-calling, so local/weak models that lack reliable
  structured tool-calling can still invoke tools.

## [1.10.6] - 2026-06-30

### Added
- **Assistant harness resilience + Local/Ollama provider (Sprint 68).** The in-editor assistant gains a
  local/Ollama LLM provider option and hardened turn-harness handling for weaker/local models.
- **Flat tool exposure for weak/local models (Sprint 68.9).** An alternative flat tool-exposure mode for
  models that handle the tiered catalog poorly, improving tool selection on local/weak models.

### Changed
- **Consumer-facing docs trimmed** of internal class names.
- **UI readability** improvements.

## [1.10.5] - 2026-06-30

### Changed
- **Doctor color-id validation prefilters prefabs.** `ColorIDReferenceValidityCheck` now prefilters prefabs
  before validating color-id references, avoiding work on prefabs that cannot carry a color-id reference.
- **MCP fork-provider docs:** documented the reserved tool/family namespaces in `MCP_FORK_PROVIDERS.md` so
  fork providers avoid colliding with Core-reserved names.

## [1.10.4] - 2026-06-29

### Changed
- **MCP tool surface optimization (Sprint 67).** The in-editor assistant no longer sends all ~184 tool
  schemas on every request. It now gets a compact catalog (`[family] (N): names`, no per-tool summaries)
  and fetches detail on demand via two new meta-tools — `molca_tool_schema(names[])` (a tool's input schema)
  and `molca_list_tools(family)` (a family's names + summaries) — so only the tools actually in use carry
  their full schema. Independent read-only tool calls in a round now execute in parallel (actions stay
  sequential), and `molca_read_source` accepts a `paths` array to batch-read several files in one call.
  Per-request tool payload drops by roughly an order of magnitude with no loss of tool-selection quality.
  The IDE MCP bridge still exposes the full registry; the two meta-tools are additive there.

## [1.10.3] - 2026-06-29

### Changed
- **Graphify indexes any installed Molca package.** Generalized the installed-package corpus export
  (`CorePackageCorpus` → `MolcaPackageCorpus.ExportInstalledPackages()`): a consumer's `molca_kg_build` now
  mirrors the docs/source of **every** non-embedded `com.molca.*` package (Core, SDK, and any future Molca
  package) into `graphify-corpus/<package>/`, so the graph is never silently project-only. Embedded packages
  are skipped (already swept from the project root).

## [1.10.2] - 2026-06-29

### Changed
- **Assistant usable while Play mode is paused (Sprint 65).** The in-editor assistant's LLM call moved off
  `UnityWebRequest` + `Awaitable.NextFrameAsync` (both player-loop driven, frozen by pause) to a background
  `HttpClient` pumped via `EditorApplication.update`. A turn now streams, answers, and runs read-only tools
  while Play mode is paused — handy for inspecting and asking about frozen scene state — and Stop still
  cancels promptly. Mutating actions remain user-gated exactly as before. New `EditorUpdateAwaiter` /
  `AssistantHttp` helpers; the obsolete `SseDownloadHandler` was removed.

## [1.10.1] - 2026-06-29

### Added
- **Richer Assistant transcript Markdown (Sprint 64).** Committed assistant turns now render blockquotes,
  task lists (☑/☐), simple tables, horizontal rules, and Markdown links (`[label](path-or-url)`) — file
  links open in-editor with `:line`, `http`/`https` links open on explicit click, unknown schemes stay
  plain text. The parser remains lightweight and dependency-free; malformed/partial Markdown degrades to
  plain text, streaming stays plain until commit, and copy/export output stays redacted and clean.

## [1.10.0] - 2026-06-29

### Added
- **Extensible Hub workspace tabs (Sprint 62).** The Molca Hub's top-bar tabs are now an id-keyed,
  `TypeCache`-discovered registry instead of a fixed enum. SDK/fork editor code adds a hosted-content tab by
  subclassing the new public `MolcaHubWorkspaceProvider` (returning `MolcaHubWorkspaceItem`s) — no Core edit
  — and hides a built-in (e.g. Sequence) per project via `MolcaHubWorkspaceRegistry.SetHidden`. Settings
  stays the anchored home tab; ordering is deterministic, duplicate/reserved ids are rejected, a throwing
  provider degrades gracefully, and selection persists by id with legacy enum-name migration.

## [1.9.8] - 2026-06-29

### Fixed
- **Standalone closure (built-in modules):** declare the toggleable `UnityEngine` modules Core uses
  directly — `com.unity.ugui` (`UnityEngine.UI`/`EventSystems`), `com.unity.modules.audio`
  (`AudioManager`/`AudioLibrary`), `com.unity.modules.unitywebrequest` (`HttpClient` via `UnityWebRequest`),
  and `com.unity.modules.uielements` (editor UI Toolkit). Previously relied on these being present by
  default or pulled transitively (Addressables → UnityWebRequest); a consumer with a trimmed module set
  could fail to compile. Declaring direct dependencies makes the package self-contained (Sprint 63.1).

## [1.9.7] - 2026-06-24

### Added
- **Molca UI token registry** — a new `Molca.UI` assembly providing a design-token "style sheet" layer over `ColorID`/`LocalizedText`/sprites/prefabs for uGUI. Tokens *name* those existing mechanisms; Core ships the engine + abstract registry but no token values (an SDK/project authors the catalog).
- **Figma frame → UI Intent Spec pipeline** — a UI Intent Spec contract with CIEDE2000 color snapping, Figma-frame mapping + tool, and `molca_build_ugui` which builds a VR-ready uGUI prefab from a UI Intent Spec.
- **`molca_build_ugui` canvasMode** — first-class non-VR (screen-space) output alongside the VR-ready (world-space) path.
- **`molca_edit_source` MCP tool** — guarded, reversible in-place source editing.
- **Assistant auto-all mode.**

### Fixed
- Drain `McpUndoStack` in `EditSourceToolTests` for test isolation.

### Packaging
- Declared `com.unity.nuget.newtonsoft-json` as a direct Core dependency for dist installs.
- Replaced the build changelog's YamlDotNet dependency with a JSON changelog format so the
  released package does not rely on dev-project `Assets/Plugins/*` assemblies.

## [1.9.6] - 2026-06-22

### Added
- **Assistant read-only research sub-agents ("swarm").** The assistant can fan out read-only research sub-agents to offload context-heavy exploration without mutating the project.
- **Doctor scene-audit closed loop.** Scene-audit findings can now apply safe automatic fixes, closing the loop from detection to remediation.

### Changed
- **Theme-aware editor UI.** The assistant, Hub, and sequence tree now render correctly in both light and dark editor themes.

### Fixed
- Added generated `.meta` files and USS tweaks for the Sprint 55–56 editor UI.

## [1.9.5] - 2026-06-22

### Added
- **Assistant structured Plan turn with a live checklist.** Plan mode proposes an ordered, reviewable plan (Approve/Edit/Cancel) and renders per-step status that updates live as execution advances, replacing the previous prose-only plan representation.
- **Assistant accurate token/cost telemetry + retrieval cache.** Token accounting prefers real vendor-reported counts, and proactive retrieval caches its result (keyed on message + graph mtime) so repeated turns don't each spawn a redundant graphify subprocess.
- **`BudgetMonitor` build-parity metrics + budget gate.** Adds build-parity metric collection and a budget gate so configured performance budgets can fail/flag at the appropriate point.

### Fixed
- **Plan approval now reads "Approved" rather than "Declined"** when a plan is accepted.
- **Multi-choice confirmation outcomes render neutrally** instead of as a rejection.
- **Graph-build feedback extended** in the MCP graph build path.

## [1.9.4] - 2026-06-22

### Added
- **Assistant Plan mode.** Approve a multi-step task once and let the assistant run undoable steps unprompted under a single whole-task undo bracket, with irreversible actions still re-confirmed.
- **Assistant proactive knowledge-graph retrieval.** The assistant grounds its answers by retrieving relevant project context from the graphify graph before responding.
- **Assistant tiered auto-compaction.** Conversation context is compacted automatically as it grows large — old tool results are digested first, and a turn summary is produced only when digesting alone does not bring the context back under the threshold.
- **Assistant session token/cost telemetry** plus a prompt-contract harness for the turn engine.
- **Scene performance audit** in Doctor (six scene-perf checks with a platform-aware budget resolver) and the `molca_scene_audit` MCP tool.

### Fixed
- **`WebSocketDataProvider` failed to compile** when the NativeWebSocket package is present — it referenced the renamed fields without the underscore prefix. Like the SocketIO sibling, the `Molca.Networking.WebSocket` assembly is gated behind the `MOLCA_WEBSOCKET` define, so this shipped undetected in projects without `com.endel.nativewebsocket`.
- **Sequence validator** now offers a fix action for the issues it reports.
- Hardened several Core MCP audit findings.

## [1.9.3] - 2026-06-21

### Fixed
- **`SocketIODataProvider` failed to compile (CS0103)** when the SocketIO package is present: two log statements referenced the renamed field as `serverUrl` instead of `_serverUrl`. The `Molca.Networking.SocketIO` assembly is gated behind the `MOLCA_SOCKETIO` define, so this never compiled in a project without `com.itisnajim.socketiounity` and shipped undetected. Consumers using SocketIO need this fix.

## [1.9.2] - 2026-06-21

### Fixed
- **`GlobalSettings.GetModule<T>()` no longer throws on an unconfigured project.** It now returns `null` when `GlobalSettings.main` is null (no GlobalSettings assigned) or `modules` is null (before `Initialize()` runs), instead of a `NullReferenceException`. Upstreamed from an SDK-layer fix.

## [1.9.1] - 2026-06-21

### Changed
- **Repository URL / Documentation URL are now editable** in **Project Settings → Molca** (slim settings provider). Previously they were only set on `MolcaEditorSettings` with no active UI. The Hub's Repository/Documentation links now refresh live when these values change.
- **DI-only subsystem access enforced internally.** Core no longer routes through legacy static singletons; `ReferenceManager.Instance` is now `[Obsolete]` — prefer `RuntimeManager.GetSubsystem<ReferenceManager>()` or `[Inject]`. The shim still works (compiles with a deprecation warning).

### Removed
- **Legacy `ColorSchemeManager` static shims** (`Instance`, `SetScheme`, `ToggleScheme`, `NextScheme`, `PreviousScheme`, `GetScheme`, `ActiveScheme`, `SchemeNames`, `SchemeCount`, `RefreshAllColorIDs`, `OnSchemeChanged`). Use `RuntimeManager.GetService<IColorSchemeService>()`. Breaking only for code that called these already-deprecated members.

### Fixed
- **`MolcaEditorSettings` fields rendered read-only** in the settings provider — `HideFlags.HideAndDontSave` bundles `NotEditable`, which disabled `SerializedObject`-bound fields. Now uses `HideInHierarchy | DontSave`.

### Packaging
- The distribution package now ships **`FORK_GUIDE.md`** (consumer/fork guide) and `.meta` files for the dist README and publish manifest, clearing Unity's "asset has no meta file in an immutable folder" import warnings.

## [1.9.0] - 2026-06-21

### Changed
- **`MolcaProjectSettings` relocated out of the Core package.** The live, editable settings instance now lives in consumer space (`Assets/_Molca/Settings/MolcaProjectSettings.asset`); the package ships a read-only default template that is cloned into the project on first access, and the editor never writes into the package. This lets Core be consumed as a read-only/binary UPM package. On upgrade the editor resolves (and migrates) the instance automatically — verify your `GlobalSettings` / `RuntimeManager` wiring afterward. Also resolves a prior split-brain where the editor and runtime could load different settings assets.
- **Editor HTTP client `IsSuccess` now accepts only 2xx status codes.** Responses outside 200–299 are no longer treated as successful; review call sites that relied on the previous broader behavior.

### Added
- **ClickUp `molca_clickup_*` MCP tool family** (status, list_tasks/workspaces, set_task_status, create_task), plus cascading Workspace/Folder/List dropdowns and resolved-name display in the ClickUp inspector.

### Removed
- **Project-specific sample assets** under `Runtime/Networking/Data` — the `Example/` data sets and `JsonPreProcessor/SO/` sample processor instances. The `DataManager` system and the reusable JSON processor classes are unchanged; only the sample `ScriptableObject` instances were removed so they no longer ship to consumers.

## [1.8.9] - 2026-06-20

### Changed
- **Assistant action confirmations** collapse to a single one-line outcome once answered (`✓ Approved · Run 18 actions` / `✕ Declined · …`) instead of a full collapsible question block, since the following "Worked through N steps" row already lists what ran and the audit log keeps the full record. Genuine `molca_ask_user` questions are unaffected — they keep the full header + question + answer. A new `ChatTurn.IsConfirmation` flag distinguishes the two, and prompt answers/flag now persist across reloads.

## [1.8.8] - 2026-06-20

### Fixed
- **Assistant chat NullReferenceException on Send**: the chat view tore itself down on `DetachFromPanelEvent` (which also fires on transient reparenting — docking, layout rebuilds, domain reloads), nulling its `CancellationTokenSource` and dropping its `Changed` subscription. It now re-arms on `AttachToPanelEvent` and null-guards the token at use sites, so Send works after a reload.

### Changed
- **Assistant "Assistant asks" prompts** collapse their body behind a disclosure once answered: a long confirmation question (e.g. "Run 18 actions?" plus the full action list) becomes a one-line summary headed by its first line, while still rendering expanded with the role header visible. Pending prompts stay expanded. Reuses the same disclosure as the Work rows.

## [1.8.7] - 2026-06-20

### Changed
- **Assistant chat theming**: the chat window now wears the shared Molca palette (`MolcaEditorTokens.uss`) — the assistant accent is the signature Molca lime, status/link/neutral roles inherit the Hub vocabulary, and the Send button is a branded lime primary.
- **Assistant "Worked" tool rows** collapse to a single line: a custom disclosure (▶/▼ header hosting the Copy/Undo buttons) replaces the Unity `Foldout`, so the editor's default foldout header background and focus highlight no longer wash the row pale. Raw tool payloads nest inside the disclosure content.

## [1.8.6] - 2026-06-20

### Fixed
- **Assistant Auto action mode** no longer prompts when the LLM emits a batch of consecutive allowlisted actions. The batch path previously surfaced the "Run all / Cancel" confirmation regardless of mode; Auto now runs the batch without prompting (each call audit-logged as `auto-approved`) while still executing it as a single undo group.

### Changed
- **Assistant action-confirmation prompt** caps its height and scrolls internally, so a large batch prompt no longer pushes the Run/Cancel buttons and composer off-screen.
- **Assistant "Worked" tool-activity rows** are more compact: the redundant "Worked" header is dropped (the foldout label carries it) and the raw tool payloads nest inside the same foldout, so a step collapses to a single line.

## [1.8.5] - 2026-06-20

### Added
- **Addressables MCP tool family**: read tools `molca_unity_addressable_settings` / `_entries` / `_resolve` for inspecting profiles, groups, entries, and labels; action tools `molca_unity_addressable_mark` / `_unmark` / `_set_address` / `_set_labels` / `_move` / `_create_group` / `_remove_group` for authoring entries, labels, and groups. Action tools are irreversible (a single Addressables edit spans the settings asset plus per-group assets, which the single-file snapshot stack cannot revert) and require Addressables to be initialized.

### Changed
- **`molca_unity_select`** accepts `paths` / `targets` / `instanceIds` arrays (combinable with the singular forms) to set a multi-object selection; the first resolved object becomes active, and any unresolved reference aborts the call rather than producing a partial selection.
- **Assistant scope enforcement** moved from a hardcoded keyword pre-filter into the system prompt, so in-context follow-ups (e.g. "yes") and non-English project questions are no longer wrongly refused. The system prompt was rewritten into labeled sections with the embedded fallback kept in sync.
- **Assistant tool activity** renders inline as one collapsible per same-kind tool run, in execution order, instead of a single summary bundled at the end of the turn.

## [1.8.4] - 2026-06-20

### Added
- **Figma integration provider** (Sprint 30): `FigmaIntegrationProvider` connects via a personal token validated over `EditorHttpClient`, lists files and a file's frames, and generates UI Toolkit `.uxml`/`.uss` (plus imported sprites) from a chosen frame. The frame builder targets UI Toolkit only and returns an explicit unsupported-node report so the fidelity ceiling is never silent. Surfaced through the data-driven Hub Integrations card.
- **ClickUp inbound task management** (Sprint 31): a dedicated **Tasks** section in the Molca Hub lists the token-user's tasks scoped to a configured project folder, with per-row status change that round-trips to ClickUp and row links that open the task in the browser. `targetFolderId` config is independent of the outbound `targetListId`.
- **Integration OAuth** (Sprint 32): GitHub authenticates via device flow and Figma via loopback + PKCE entirely through the editor (no embedded secret, no hosted callback), with PAT retained as fallback. Tokens (access/refresh/expiry) persist in a new `OAuthCredentialStore` backed by `EditorUserSettings` and auto-refresh before expiry; ClickUp/Discord retain their existing credential model.
- **MCP project settings authoring tools** (Sprint 33): four `molca_settings_*` tools let an agent read project settings and author a `SettingModule` asset's serialized fields with full coercion (read tools `Any`/read-only; the setter `Edit`/Action/`UnityUndo`), rejecting unknown/read-only fields with each write as one undoable group.
- **MCP convention-based tool discovery + codegen** (Sprint 34): the base `McpToolProvider.GetTools()` default now discovers a provider's own `Create<X>Tool()`/`Execute<X>` factory methods deterministically (cached per type), so a new tool is added by dropping a single partial file with zero edits to a shared list. `molca_create_mcp_tool` gains an extend-existing-provider mode that writes into a fork provider in place while still refusing to modify Core/SDK.
- **`molca_describe_bootstrap` MCP tool**: read-only introspection of the RuntimeManager bootstrap sequence.

### Changed
- **Core leaf modules** (`Audio`, `Modals`, `Networking`, `ContentPackage`, `Sequence`) set `autoReferenced: false`, so consumers opt in by asmdef reference instead of silently pulling every module into the predefined assembly.
- **Sprint plan docs** split: Sprints 1–30 moved to `Documentation~/SPRINT_PLAN_ARCHIVE.md`, leaving the active `SPRINT_PLAN.md` focused on current sprints and the Cross-Sprint Rules.

## [1.8.3] - 2026-06-20

### Added
- **Editor integration framework**: a shared `IntegrationProvider` base plus `IntegrationAssetValidator` and a project-scoped `IntegrationSettings` asset, giving editor tooling a uniform way to register and validate external-service integrations.
- **GitHub integration provider**: `GitHubIntegrationProvider` with a dedicated `GitHubApiClient`, typed `GitHubModels`, and a custom inspector for configuring the integration from the Hub.
- **Discord integration provider**: `DiscordIntegrationProvider` with its own inspector for webhook/activity configuration.
- **Shared activity router**: `IntegrationActivityRouter` and `IntegrationActivity` route build/release and other editor activities to all enabled providers through a single pipeline.
- **Hub Integrations section**: expanded `MolcaHubIntegrationsSection` to surface the GitHub, Discord, and ClickUp providers with status and configuration.
- **Integration tests**: EditMode coverage for the integration providers and activity router.

### Changed
- **ClickUp integration** migrated onto the shared provider/activity-router model; the standalone `ClickUpBuildReporter` and `ClickUpReleaseReporter` were folded into the provider and the routed activity pipeline.
- **Project Settings launcher** aligned with the shared editor design language (`MolcaSettingsProvider` slimmed to identity fields).

### Removed
- Obsolete Hub `MolcaHubSectionCard` / `MolcaHubStatusKind` aliases (superseded by the shared `Editor/UI/` components).

### Fixed
- Cleared remaining editor and serialization warnings, including dropping the redundant `[Serializable]` attribute from step auxiliaries.

## [1.8.2] - 2026-06-20

### Added
- **Shared editor design-language foundation**: promoted the design language into a reusable `Editor/UI/` foundation — `MolcaEditorTokens.uss` (single `--molca-*` token source with `--hub-*` back-compat aliases, skin-aware), `MolcaEditorUi.Apply`, a `MolcaEditorColors` C# palette for IMGUI/GraphView, and shared components (`MolcaSectionCard`, `MolcaStatusKind`, `MolcaRail`, `MolcaSearchField`, `MolcaLinkRow`, `MolcaButtons`).
- **Design-language conformance lint**: new `DesignLanguageCheck` Molca Doctor check (also surfaced via the `molca_doctor` MCP tool) flags raw hex, unscoped `EditorPrefs`, nested cards, and unscoped USS class names as warnings.
- **Design-language tests**: EditMode coverage for the token loader, shared components, and the conformance lint.

### Changed
- **Editor UI retrofit**: Sequence Visualizer, Sequence Graph, CSV Step Importer, Framework Graph, Auxiliary Migration windows and the Content Package / Notification / MCP inspectors now resolve colors from the shared design tokens instead of hardcoded hex; Hub `MolcaHubSectionCard`/`MolcaHubStatusKind` retained as `[Obsolete]` aliases.
- **Sequence Visualizer state**: window persistence moved from raw `EditorPrefs` to project-scoped `MolcaEditorPrefs`.

## [1.8.1] - 2026-06-20

### Added
- **Molca Hub editor settings redesign**: added a dockable `Molca/Hub` UI Toolkit shell with workspace tabs, persistent Settings rail, shared section-card language, and Hub pages for Project, Build & Version, Runtime & Global, Editor, MCP, Integrations, and Assistant.
- **Slim Project Settings launcher**: `Project Settings > Molca` now stays focused on identity fields and opens the full Hub for expanded settings workflows.
- **Hostable editor tools**: Doctor, Assistant, and Sequence Visualizer now expose reusable hostable views so they remain standalone windows while also being reachable inside the Hub.
- **Editor design language reference**: added `Documentation~/EDITOR_DESIGN_LANGUAGE.md` as the tracked guide for future Molca custom editor windows and editor UI refactors.
- **Hub regression tests**: added EditMode coverage for Hub state persistence, section/workspace registry coverage, settings provider wiring, key serialized binding paths, MCP token persistence, and hostable tool-view construction.

### Fixed
- **Assistant file links**: `.jsonl` paths now parse as full inline links instead of truncating at `.js`.
- **MCP undo fallback**: `molca_undo_last_action` no longer relies on Unity undo group numbers as a success detector after `Undo.PerformUndo()`.

## [1.8.0] - 2026-06-19

### Added
- **Assistant — interactive ask-user pause**: the model can ask the user a decision mid-turn via the new read-only `molca_ask_user` tool. The question is surfaced in a docked prompt bar above the composer with one button per option plus a free-text answer; answering resumes the same turn. Stop cancels a pending prompt cleanly.
- **Basic Unity GameObject MCP tools**: read-only `molca_unity_scene_objects` (hierarchy listing with path/active/instance id/components, name filter) and Edit-mode, Unity-Undo-revertible actions `molca_unity_gameobject_rename`, `_set_active`, `_set_transform`, `_create`, `_delete`, and `_add_component`. Routed through a dedicated `UnityMcpToolProvider` and `GameObjectEditingService` (one undo group per edit).
- **Unity MCP discovery tools**: `molca_unity_selection`, `molca_unity_scenes`, `molca_unity_component_types`, `molca_unity_gameobject_components`, and `molca_unity_component_fields` provide read-only editor/scene/component discovery before mutating Unity objects.
- **Unity MCP component/edit actions**: `molca_unity_gameobject_duplicate`, `molca_unity_gameobject_reparent`, `molca_unity_gameobject_remove_component`, and `molca_unity_component_set_fields` extend the Unity provider with Undo-backed safe authoring actions.
- **Unity MCP asset/prefab tools**: `molca_unity_assets`, `molca_unity_asset_dependencies`, and `molca_unity_prefab_contents` add AssetDatabase/prefab discovery; `molca_unity_prefab_instantiate` adds an Undo-backed prefab placement action.
- **Unity MCP scene workflow tools**: `molca_unity_build_scenes` lists EditorBuildSettings scenes, while `molca_unity_scene_set_active`, `molca_unity_scene_save`, and `molca_unity_scene_open` provide allowlisted scene workflow actions with dirty-scene guardrails.
- **`molca_sequence_get_step_fields`**: read-only counterpart to the field setters — returns the current serialized field values of a step and each of its auxiliaries, so the assistant can inspect before editing.
- **Assistant — last question pinned**: while a turn is running, the most recent user question stays pinned above the transcript.
- **Assistant — round-cap Continue**: hitting the tool-round limit now offers a one-click Continue instead of asking the user to type "continue".

### Changed
- **Assistant window modernized**: all cosmetic styling moved to a USS stylesheet + UXML layout; the window was split into focused `AssistantTranscriptView`, `AssistantComposer`, and `AssistantAssetPicker` collaborators. Long answers render without the previous per-word element explosion, and streaming updates only the in-flight row instead of rebuilding the whole transcript.
- **Assistant — Ask-mode action confirmation** now flows through the in-chat docked prompt bar (Run/Cancel) instead of a blocking modal dialog.
- **Assistant — system prompt** moved to a runtime-editable `AssistantSystemPrompt.txt` (tuning without a recompile); retry/edit now anchor to the conversation history precisely; the token estimate prefers the vendor-reported prompt size over the character heuristic.
- **Serialized-field helpers extracted**: `SerializedFieldCoercion` and `FieldNode` moved from the sequence editor into a general-purpose `Editor/Serialization/` home (now also reads values back, not just writes), reusable by any editor tooling.

### Fixed
- **Assistant header buttons**: hover tooltips work again (the icon image no longer intercepts the pointer), and the buttons use a flat, rounded, modern style with a hover highlight.

## [1.7.2] - 2026-06-19

### Fixed
- **Assistant settings**: the Project Settings → In-Editor Assistant section now exposes **Max Tool Rounds** (`maxToolRounds`) and **Stream Responses** (`streamResponses`). Both fields already existed on the asset but were only editable via the raw inspector.

## [1.7.1] - 2026-06-19

### Fixed
- **MCP bridge enable state**: the Start/Stop button in Project Settings now drives the persisted `Enabled` flag instead of calling `Start()`/`Stop()` directly. This keeps the button, the "Enable Bridge" checkbox, and the listener in sync, and a manual start now survives domain reloads (previously a bridge started while "Enable Bridge" was off ran until the next recompile, then silently died and contradicted the checkbox).

## [1.7.0] - 2026-06-19

### Added
- **Assistant chat — per-action Undo**: mutating MCP tool calls now appear as their own chat line with an Undo button. Undo reverts "back to this point", covering both `FileSnapshot` actions (via `McpUndoStack.UndoTo`) and `UnityUndo` actions (via `Undo.RevertAllDownToGroup`). The button greys out once a change is no longer revertible.

### Changed
- **Assistant chat — tool-call grouping**: consecutive same-kind tool calls coalesce into one line — a run of read-only calls collapses into a single grouped row, and a run of actions collapses into one row carrying a single Undo that reverts the whole run. A read↔action flip (or assistant text) starts a new group.

### Fixed
- **Assistant chat input**: Shift+Enter now inserts a newline at the caret instead of unfocusing the field; Enter still sends. Both Enter behaviours are handled explicitly so the editor navigation system can no longer blur the input.

## [1.6.0] - 2026-06-19

### Changed
- **MCP settings**: the action-tool allowlist editor now paginates (12 tools per page) so a growing tool list no longer dominates the inspector.

### Fixed
- **MCP Ref Id tools**: `molca_refids` and `molca_fix_refids` now scan every live `IReferenceable` (ReferenceableComponents, Steps, SequenceControllers, custom implementers) via a shared helper, instead of disagreeing on the "known" set. Fixes false "unresolved" reports for `SceneObjectReference`s targeting Steps and undetected empty/duplicate ids on non-`ReferenceableComponent` types.

## [1.5.0] - 2026-06-19

### Added
- **Framework Graph and Knowledge Graph**: editor windows, graph builders, persisted layout state, Graphify corpus export, and MCP/assistant tools for inspecting framework structure and source context.
- **Fork graph contract**: SDK layers can contribute graph nodes and edges through `IFrameworkGraphContributor` without modifying Core.
- **Assistant context UX** (Sprint 24): explicit editor context items, session persistence, improved transcript formatting, and richer OpenAI-compatible/Anthropic streaming support.
- **MCP sequence-authoring tools**: compound field editing, type discovery, code generation helpers, nested-field coercion, and a meta tool for scaffolding MCP tools.
- **Molca Doctor icon**: a dedicated medical-cross family icon.

### Changed
- The MCP assistant and registry now expose richer framework, knowledge graph, read-source, sequence, and configuration authoring workflows.
- Example sequence MCP coverage and project graph assets were refreshed for the current toolset.

## [1.4.0] - 2026-06-18

### Added
- **MCP bridge foundation** (Sprint 14): an in-editor Model Context Protocol bridge with a fork-extensible tool-provider contract, allowing external assistants/agents to inspect and drive the editor over a local port.
- **Read-only tool suite** (Sprint 15): the Core MCP provider exposes read-only inspection tools (project/sequence/reference queries) plus a fork extension point for SDK layers to register their own tools.
- **In-editor assistant** (Sprint 16): a non-coder chat window (`Molca Assistant`) backed by the MCP tool bridge, with an OpenAI-compatible provider (DeepSeek support) and editor-context injection so the assistant sees the current selection/scene.
- **Action tools & guardrails** (Sprint 17): the provider gains mutating action tools behind guardrails; file-snapshot undo makes action tools revertible.
- **Sequence-authoring action tools** (Sprints 19–20): a comprehensive suite for creating/editing sequences, steps, and step configuration through the bridge.
- **Content package tool family** (Sprint 21): MCP tools for inspecting and operating the ContentPackage system.
- **Molca family icons**: brand icons for ScriptableObjects and editor windows, with a per-window family icon that survives domain reloads.
- **Molca Doctor**: `ColorIDReference` validation checks.

### Changed
- The `molca-mcp` proxy now ships inside the package, so the bridge is UPM-installable with no external setup.
- Assistant chat workflow polish: send-on-enter, cleaner transcript markdown, compact toolbar, and improved formatting UX.
- Dropped the AzureOpenAI assistant provider in favor of the OpenAI-compatible provider.

### Fixed
- **Localization**: hardened `DynamicLocalization` init/locale handling and added corresponding Doctor checks.
- **Editor**: replaced obsolete `InstanceIDToObject` with `EditorUtility.EntityIdToObject`.
- **MCP**: the bridge now releases its port on domain reload and quiets the port-in-use warning; assistant/bridge warnings are quieted.

## [1.3.0] - 2026-06-18

### Added
- **Build/version lifecycle** now runs independently of the notification system: a dedicated build pre/post-processor appends the changelog and increments the build number for every build (Build Manager, `File > Build`, and CI), so these no longer require a `BuildNotificationProvider` asset to exist.
- **Platform version codes**: `VersionSettings.SyncPlatformVersionCode` sets `PlayerSettings.Android.bundleVersionCode` / `PlayerSettings.iOS.buildNumber` from the build number, so store uploads receive a fresh, monotonic code.
- **Build profiles**: per-profile Android App Bundle (`.aab`) output and target architectures; per-profile signing for Android (keystore; passwords sourced from environment variables, never stored in the asset) and iOS (team / automatic signing); and an opt-in "build Addressables content first" gate.
- **Async build gate**: `BuildManager.BuildAsync` runs the build-relevant Molca Doctor checks (scenes / version / profile / scene-references / content) and aborts on any error before building. Interactive *Build This Profile* / *Build All* use it; the synchronous `Build` is unchanged.
- **Release tool**: `ReleaseTool` cuts an app release from `VersionSettings` — syncs PlayerSettings, appends a release changelog entry, optionally creates a local `v{version}` git tag, and suggests the next bump from conventional commits. Surfaced as a *Release* section in the Version Settings inspector.
- **Conventional-commit changelog**: build/release changelog notes are grouped into Breaking / Features / Fixes / Other (`ConventionalCommits`).
- **Runtime build provenance**: `Molca.BuildInfo` exposes the version, build number, git commit/branch, and timestamp embedded at build time (a generated `Resources/MolcaBuildInfo` asset, removed after the build).
- **Build manifest**: each successful build writes a `build-info.json` sidecar (version, git commit/branch, target, options, scenes, size, timestamp) next to the output.
- **CLI**: `CommandLineBuild` accepts `-profile`, `-version`, and `-buildNumber` overrides so CI can inject the version / run number; a GameCI workflow template is included.
- **Build Settings inspector**: version/build header, *Build All (current target)*, and per-profile *Duplicate*.
- Tests for version math, changelog round-trip/trimming, build-profile lookup, and conventional-commit parsing.

### Changed
- `VersionSettings` version fields renamed to `major` / `minor` / `patch` (via `[FormerlySerializedAs]`; saved data migrates automatically).
- Pre-build gates (scene-reference validation, Addressables content build) now run **before** any `PlayerSettings` / `EditorUserBuildSettings` mutation, so an aborted build no longer leaves signing secrets, application id, scripting backend, or Android format applied.
- Deferred (target-switching) builds are stamped with a session token and discarded if left over from a previous editor session, so a stale build no longer fires unexpectedly on editor launch.
- `package.json` minimum Unity is now `6000.0` (the framework uses `Awaitable`).

### Fixed
- `ApplyProfile` and the Settings provider's *Sync to Player Settings* used a hardcoded company name instead of `MolcaProjectSettings.CompanyName`.
- Removed an unused build-failure notification method.

## [1.2.0] - 2026-06-17

### Added
- **Molca Doctor** build/version configuration checks: `build-scenes-valid` (missing/duplicate/empty build scenes), `version-settings-valid` (version/build-number range, SemVer pre-release/metadata, changelog path), `build-profile-valid` (unique names, output path, Android/iOS application id, define symbols), and `content-package-valid` (unique package ids, resolvable and acyclic dependencies). Each stays silent when its settings asset is absent.
- **Molca Doctor** window: per-check toggles now wrap across rows instead of overflowing, with `All` / `None` buttons to enable or disable every check at once.

### Fixed
- **Editor inspectors**: `FindProperty`/`FindPropertyRelative` literals were not updated after the underscore field-rename pass. `[FormerlySerializedAs]` migrates saved data but not the live `SerializedProperty` path, so lookups on the old names returned null — causing `NullReferenceException`s in some drawers and silently non-persisting inspectors elsewhere (Audio and ColorID drawers/editors).
- **ColorModuleEditor**: "Find References in Scene" iterated targets looking for a per-target `colorId` field that never existed, so it never matched. It now matches components on the ColorID's `swatchName` + `colorId` pair.

## [1.1.5] - 2026-06-17

### Fixed
- **Molca Doctor**: the `unresolvable-scene-reference` check no longer scans ScriptableObjects. A `SceneObjectReference` resolves only against scene-loaded objects via `ReferenceManager`, so one stored in an SO can never resolve at runtime (the "SOs-out" boundary documented on `ReferenceManagerSettings`) — validating it was meaningless, and the deep per-SO `SerializedObject` walk over every asset was the remaining bottleneck that made large-project runs appear stuck. The check now scans only prefabs (within `PrefabScanPaths`) and open scenes.

## [1.1.4] - 2026-06-16

### Fixed
- **Molca Doctor**: the `unresolvable-scene-reference` check no longer loads and scans every prefab in the project (which took many minutes on large projects). It now mirrors the reference-system scan — validating prefabs only within `ReferenceManagerSettings.PrefabScanPaths`, and skipping prefab scanning when that list is empty. Prefabs outside the list are never registered in the validation DB, so this also removes a class of false "unknown" findings. ScriptableObjects and open scenes are still scanned.
- **Molca Doctor**: Cancel is now responsive during the scene-reference scan. The check yields before each heavy prefab/scene (rather than every 25 assets), so `EditorApplication.update` can run and register the cancel request promptly.

### Added
- **Molca Doctor**: the progress bar shows live sub-check detail during long scans (e.g. `ScriptableObjects 1200/5000`, `Prefabs 3/12`, `Scene Main`) via an optional `DoctorContext.ReportStatus` channel. The detail leads the label so the narrow progress dialog does not clip it.

## [1.1.3] - 2026-06-15

### Fixed
- **Molca Doctor**: the `unresolvable-scene-reference` check hung in the editor Edit Mode. It yielded via `Awaitable.NextFrameAsync`, whose player loop does not advance outside Play Mode, so the await never resumed and the run stuck on the final check. It now yields via an `EditorApplication.update`-driven awaitable that fires in Edit Mode.
- **Molca Doctor**: the scene-reference check's prefab/ScriptableObject/open-scene scan now respects `DoctorContext.IsIgnored`, so third-party assets (vendor SDKs, imported samples) are skipped — previously the ignore globs only filtered `.cs` sources, leaving vendor assets to be loaded and scanned and making large-project runs crawl.

## [1.1.2] - 2026-06-15

### Fixed
- **Molca Doctor**: third-party / vendor code is no longer reported as Molca-convention violations. `DoctorContext` excludes it at the source-loading layer (so all checks benefit), combining built-in `DefaultIgnoreGlobs` (Plugins, TextMesh Pro, ThirdParty, Vendor, External, Standard Assets, Samples, AssetStoreTools), a project-root `.doctorignore` file (one glob per line, `#` comments), and an `extraIgnoreGlobs` constructor argument. Globs: `**` spans path segments, `*` within one; a no-wildcard pattern matches as a substring.

## [1.1.1] - 2026-06-15

### Changed
- **Molca Doctor**: checks now run asynchronously. `IDoctorCheck.Run` is replaced by `Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(DoctorContext, CancellationToken)`; CPU/reflection checks run on a background thread (editor stays responsive) while the scene-reference check stays on the main thread and yields per-frame. `MolcaDoctor.RunAll` is now `RunAllAsync`. The Doctor window shows a live, per-check progress bar with responsive cancellation.

### Fixed
- **Molca Doctor CI** (`RunCI`): drives the async run to completion and exits the editor itself. Invoke **without** `-quit` (e.g. `Unity -batchmode -executeMethod Molca.EditorTools.Doctor.MolcaDoctor.RunCI`) — `-quit` would quit before the run finishes and report a false pass.

## [1.1.0] - 2026-06-15

### Added
- **Async contract**: framework-wide `Awaitable`-based async convention; `RuntimeSubsystem.InitializeAsync(CancellationToken)` overload; cancellable `AwaitWithTimeout`; `CancellationToken` threaded through `HttpClient.SendAsync`/`Send` with a transport seam and `CancelAllRequests`.
- **Networking**: configurable retry-with-backoff for idempotent requests; request interceptors; encrypted token storage with token kept out of URL paths and log redaction.
- **Reference system**: Guid-based ids with prefab-instance uniqueness and length cap; hardened `SceneObjectReference` resolution and `ReferenceManager` registration; caller-info capture on `Resolve`.
- **Build**: pre-build scene-reference gate; CI now exits non-zero on failed builds.
- **Editor tooling**: GraphView sequence editor (Sprints 7–9); Molca Doctor convention validator with window + CI mode; batch auxiliary editing; `SequenceValidator` with event-driven refresh.
- **Sequence**: `Step.ForceComplete` to bypass the `CanComplete` gate.
- Foundation EditMode test suites for DI, topo sort, events, json, pool, networking, and reference system.

### Fixed
- Numerous lifetime/leak fixes across pool, log, events, modals, audio, and async Unity messages (destroy/enabled checks after awaits).
- Runtime: 20s subsystem init timeout to prevent boot soft-lock; activate and service-register externally registered subsystems; `try/finally` so faulted awaitables cannot deadlock `WaitForAll`.
- Events: per-subscriber exception isolation in `DispatchEvent`.
- DI: per-type `[Inject]` member caching; destroyed Unity objects treated as unset; silent optional injection.
- Networking: `HttpRequestAsset.CreateRequest()` clones (SO cardinal rule); `AuthManager` no longer mutates assets; `CacheManager` corrupt-index recovery.

### Changed
- De-staticed singletons behind instance APIs; migrated `_MolcaSDK` off legacy static singleton APIs.
- Private-field naming sweep to `_camelCase`; API-surface freeze tests for public members.
- `ReferenceTracker`/`ReferenceTrackers` marked `[Obsolete]`; `RegisterWithAutoId` deprecated.

## [1.0.0] - 2026-05-29

### Changed
- Migrated Core from `Assets/_Molca/_Core/` to a UPM package (`Packages/com.molca.core`)
- Asmdef references converted from GUIDs to assembly names for package compatibility
- `VersionSettings`: removed YAML, git, and process logic from the ScriptableObject into dedicated classes
- `ChangelogWriter`: new class owning all YAML changelog read/write and git commit note appending
- `GitLogReader`: new static utility in `Editor/BuildSystem/` for shelling out to git; reusable by other editor tools
- `lastBuildCommitHash` moved from a `[SerializeField]` on `VersionSettings` to `EditorPrefs` to prevent SO asset mutation during builds
- `VersionHistoryEntry` promoted to a top-level class (was a nested class on `VersionSettings`); `ChangelogEntryData` removed as a duplicate
- `GetBundleVersionString(BuildTarget)` no-op switch simplified to a direct return
- `SetVersion()` now throws `ArgumentOutOfRangeException` instead of silently returning on invalid input
- Exception handlers in changelog I/O now log full stack traces (`ex.ToString()`) instead of message-only
