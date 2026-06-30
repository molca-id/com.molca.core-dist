# Changelog

All notable changes to Molca Core will be documented here.

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
