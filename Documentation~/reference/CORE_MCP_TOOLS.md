---
title: Core MCP Tools
category: Tooling
order: 950
---

# Core MCP Tools

`CoreMcpToolProvider` owns the `molca` namespace: the introspection, content/settings authoring, networking, localization, knowledge-graph, and ClickUp tools that are specific to Molca Core. General-purpose Unity-editor actions live in [`UnityMcpToolProvider`](UNITY_MCP_TOOLS.md). The Figma tools (`molca_figma_*`) moved to the `com.molca.integration.figma` add-on's own `FigmaMcpToolProvider`, owning the `molca.figma` namespace — see [Figma → UI Intent Spec](FIGMA_TO_UI_SPEC.md). The sequence tools (`molca_sequence_*`, `molca_validate_sequence`, `molca_validate_all_sequences`) moved to the `com.molca.sequence` add-on's own `SequenceMcpToolProvider`, still under the `molca` namespace (unchanged tool names, for back-compat) — see [Sequence MCP Tools](molca://doc/SEQUENCE_MCP_TOOLS).

## Provider Layout

Tools are discovered by convention from the `Create*Tool()` factories across the provider partial files. There is no central registration list to keep in sync. SDK forks add tools by subclassing `McpToolProvider` under their own namespace, never by editing this provider; see [MCP_FORK_PROVIDERS.md](MCP_FORK_PROVIDERS.md).

- `CoreMcpToolProvider.cs`: provider asset metadata, `molca` namespace, and discovery.
- `CoreMcpToolProvider.Status.cs` / `.BuildInfo.cs`: editor/runtime status and build-profile/version info.
- `CoreMcpToolProvider.Subsystems.cs` / `.Services.cs` / `.Bootstrap.cs`: live subsystem graph, DI service registrations, and static bootstrap description.
- `CoreMcpToolProvider.FrameworkGraph.cs`: read-only project-wiring map.
- `CoreMcpToolProvider.CreateMcpTool.cs`: MCP-tool codegen. (Step/Auxiliary script scaffolding moved with the sequence tools — see below.)
- `CoreMcpToolProvider.RefIds.cs` / `.RefIdFix.cs`: Ref Id listing and repair.
- `CoreMcpToolProvider.ContentPackages.cs`: Play-mode content package listing, sizing, queue status, and install/update lifecycle.
- `CoreMcpToolProvider.ContentAuthoring.cs` / `.ContentBuild.cs`: content-package config authoring, build-config authoring, build verification, build, and deploy.
- `CoreMcpToolProvider.Settings.cs`: project settings and `SettingModule` read/write.
- `CoreMcpToolProvider.Networking.cs`: `HttpModule` config and `HttpRequestAsset` read/create/edit.
- `CoreMcpToolProvider.Localization.cs` / `.LocalizationEdit.cs`: DynamicLocalization coverage/readback, language authoring, and runtime language switching.
- `CoreMcpToolProvider.KnowledgeGraph.cs`: graphify knowledge-graph status/query/path/explain/build.
- `CoreMcpToolProvider.Docs.cs`: read-only reference-guide list/read/search over the Hub docs registry.
- `CoreMcpToolProvider.ReadSource.cs`: in-project source-file reads (single `path`, or a `paths` array to batch-read several files in one call).
- `CoreMcpToolProvider.ToolSchema.cs` / `.ListTools.cs`: the tiered-exposure meta-tools — `molca_tool_schema` (fetch a tool's input schema on demand) and `molca_list_tools` (expand a family to names + summaries). See [MCP_FORK_PROVIDERS.md](MCP_FORK_PROVIDERS.md) → "How the in-editor assistant exposes tools".
- `CoreMcpToolProvider.Doctor.cs` / `.Actions.cs`: Molca Doctor checks and Doctor-fix.
- `CoreMcpToolProvider.AskUser.cs`: interactive ask-user prompt.
- `CoreMcpToolProvider.ClickUp.cs`: ClickUp integration status, task/workspace listing, task creation, and status changes.

## Read-Only Tools

Status / runtime introspection:

- `molca_status`: editor and Molca runtime status.
- `molca_build_info`: build profiles, current version, and recent changelog entries.
- `molca_subsystems`: registered `RuntimeSubsystem`s with dependency/init-order information. Play mode.
- `molca_services`: `RuntimeManager` service-container registrations. Play mode.
- `molca_describe_bootstrap`: static bootstrap description.
- `molca_framework_graph`: read-only project-wiring map.

Reference system:

- `molca_references_audit`: the shared read-only reference audit — findings with `REFnnn` codes, provider
  and reference-site inventories, per-reference outcomes, and scan coverage. Reports `Clean`, `Warnings`,
  `Errors` or `Incomplete`; `Incomplete` means the scan could not see everything, so it is *not* clean.
- `molca_references_plan_fix`: builds a reviewable repair plan from a fresh audit and returns it — every
  object, property and before/after value — without changing anything. Findings whose repair needs a human
  decision come back under `choices` with their candidate targets.
- `molca_refids`: **deprecated** adapter over the same snapshot, kept for existing clients. Prefer
  `molca_references_audit`.

Content packages:

- `molca_content_list`: available/installed packages with live state, version, progress, and sizes. Play mode.
- `molca_content_download_size`: download size plus cache usage and available disk space. Play mode.
- `molca_content_queue_status`: queue state, item counts, pause state, and aggregate progress. Play mode.
- `molca_content_validate_config`: validates content-package config/package definitions before build.
- `molca_content_scan`: scans Addressables/project content for package-authoring candidates.
- `molca_content_verify`: verifies built package bundle output against configured package labels.

Settings:

- `molca_settings_project_info`: bootstrap `MolcaProjectSettings`.
- `molca_settings_list_modules`: `SettingModule` assets on `GlobalSettings`.
- `molca_settings_get_fields`: serialized field values for a registered `SettingModule`.

Networking:

- `molca_network_catalog`: the whole `NetworkCatalog` - environments, services and their per-environment
  bindings, policy profiles, credential profile *metadata*, endpoint collections, and a validation summary.
- `molca_network_validate`: catalog validation findings with their stable codes and workspace deep links, or
  one route resolved (origin, URI, effective policy, credential scope) when `service` is supplied.
- `molca_network_diagnostics`: the running game's redacted diagnostics - counts, per-route queue and circuit
  state, live streaming sessions, and retained request records. Play mode only.
- `molca_network_config`: `HttpModule` config with sensitive values redacted.
- `molca_network_list_requests`: every `HttpRequestAsset` with redacted request details.
- `molca_network_get_request`: one `HttpRequestAsset` in full with sensitive values masked.

The catalog tools **delegate**: reads project `NetworkCatalogValidator` and the shared resolver, and writes go
through `NetworkCatalogEditingService`. None of them holds a rule of its own, because a second copy of the
authoring rules is how automation and the Hub start disagreeing about what a valid catalog is. Credential
values never cross MCP - only profile names.

Localization:

- `molca_localization_status`: localization modules/languages and runtime language state.
- `molca_localization_pseudo_preview`: non-mutating accent/expansion, missing-key, or RTL text preview.
- `molca_localization_pseudo_catalog`: bounded read-only pseudo preview of catalog cells.
- `molca_localization_pseudo_overflow`: reports loaded localized UI that overflows under a pseudo profile.
- `molca_localization_list_texts`: localized text bindings found in loaded scenes.
- `molca_localization_coverage`: schema-aware LocalizedValue coverage, findings, and editable field paths.
- `molca_localization_migration_inventory`: stable legacy-value locators, source kinds, row counts,
  writability, and fingerprint.
- `molca_localization_plan_migrate_values`: read-only schema-v2 migration preview.
- `molca_localization_migrate_values`: stale-safe, post-verified Unity Undo migration action.
- `molca_localization_plan_add_language`: read-only add-or-repair preview bound to the current catalog
  fingerprint, with every module/Locale/Addressables/table mutation listed.
- `molca_localization_plan_archive_language`: read-only removal preview that lists every disable action
  and every Locale/table/inline asset intentionally preserved.
- `molca_localization_catalog`: stable StringTable collection/entry/locale cells with missing and
  ownership state.
- `molca_localization_plan_catalog_edit`: read-only cell/new-key preview with fingerprint, identity,
  locale, ownership, and placeholder validation.
- `molca_localization_export_csv`: deterministic stable-identity catalog CSV.
- `molca_localization_plan_import_csv`: all-or-nothing CSV import preview with per-cell changes and
  blocking conflicts.
- `molca_localization_remote_status`: remote trust/allowlist readiness and active overlay status.
- `molca_localization_sync_remote_allowlist`: guarded Unity Undo repair of the shipped stable-identity
  and placeholder allowlist.

Knowledge graph:

- `molca_kg_status`: graphify graph location/status.
- `molca_kg_query`: natural-language project query over the graph.
- `molca_kg_path`: shortest relationship path between two concepts/entities.
- `molca_kg_explain`: plain-language explanation of one concept/entity.

Documentation:

- `molca_docs_list`: list the reference guides available in the project (id, title, category), optionally filtered by category.
- `molca_docs_read`: return a guide's full Markdown body by id (front-matter stripped).
- `molca_docs_search`: case-insensitive substring search over guide titles and bodies, returning matches with a snippet.

Source / Doctor / ClickUp:

- `molca_read_source`: read a text/source file inside the project by path with optional line-range pagination.
- `molca_doctor`: run Molca Doctor convention checks.
- `molca_clickup_status`: ClickUp connection, target, push target, and token state.
- `molca_clickup_focus`: the task currently focused for this project, plus the pinned task ids.
- `molca_clickup_list_tasks`: tasks from the configured target folder, paginated, with priority, due date, tags,
  assignees, and pinned/focused flags.
- `molca_clickup_list_workspaces`: workspaces available to the stored token.

Interactive:

- `molca_ask_user`: ask the user a question and wait for their answer. It is `ReadOnly`; it changes no project state.

## Action Tools

Unity Undo-backed (`McpToolReversibility.UnityUndo`, Edit mode):

- Reference repair: `molca_references_apply_fix` — applies a plan from `molca_references_plan_fix` by
  `planId`, as one Undo group. Rejects the plan if the project changed since it was built, skips any
  mutation whose expected value moved, and reports the findings actually fixed, remaining, and *introduced*.
- Ref Id repair: `molca_fix_refids` — **deprecated** adapter over the same repair system, kept for existing
  clients. It applies without a review step, which is exactly what the plan/apply split exists to prevent;
  prefer `molca_references_plan_fix` + `molca_references_apply_fix`.
- Settings: `molca_settings_set_fields`
- Network catalog: `molca_network_edit` - one `operation` per call over the shared editing service
  (create/bind/rename/delete an environment, service, policy, credential, collection, or endpoint; set the
  defaults). IDs are normalized and de-duplicated by the same rules the Hub uses.
- OpenAPI import: `molca_network_import_openapi` - previews a reviewable diff (add / update / unchanged /
  conflict, plus orphans and the spec's declared servers) and takes `apply: true` to write it in one Undo
  group. It never overwrites a hand-authored endpoint, never binds a service to a server URL from the spec,
  and never creates a credential profile.
- Network migration: `molca_network_migrate` - previews the legacy scan by default; `apply: true` executes it
  in one Undo group, creating project-owned assets and deleting no legacy asset.
- Networking (legacy request assets): `molca_network_create_request`, `molca_network_set_request_fields`
- Localization authoring: `molca_localization_set_text`; `molca_localization_add_language` executes only
  a fresh `planId` from `molca_localization_plan_add_language`, applies the module, Unity Locale,
  Addressables, and all table collections as one verified transaction, and rolls back on failure;
  `molca_localization_archive_language` likewise requires a fresh
  `molca_localization_plan_archive_language` plan and disables the locale without deleting authored
  Locale, table, or inline assets; `molca_localization_catalog_edit` executes a fresh
  `molca_localization_plan_catalog_edit` plan; `molca_localization_import_csv` executes a fresh
  `molca_localization_plan_import_csv` plan atomically.
- Content-package config authoring: `molca_content_define_package`, `molca_content_update_package`, `molca_content_remove_package`, `molca_content_assign_labels`, `molca_content_set_build_config`

Irreversible (`McpToolReversibility.Irreversible`):

- `molca_network_send` - sends one request through the routed pipeline against a **catalog route**, never a
  raw URL. It refuses whatever preflight blocks, and refuses a production mutation outright rather than
  prompting: automation must not bypass a per-send human confirmation, and there is nobody at an MCP call to
  give one. Use `preflightOnly` to see the destination, effective policy, and credential decision without
  sending. Credentials come only from profiles marked usable from the request console.

File-snapshot reversible (`McpToolReversibility.FileSnapshot`, revert via `molca_undo_last_action`):

- `molca_run_doctor_fix`
- `molca_edit_source`: guarded, reversible in-place editing of a single project file — the write half of the
  file loop that pairs with the read-only `molca_read_source` (read the file first so an exact-string
  `replace` matches). Four discriminated `mode`s: `replace` (exact `oldString`→`newString`; must match
  exactly once unless `replaceAll`, otherwise it errors and writes nothing), `insert` (`content` after a
  1-based `afterLine`; `0` = top, line count = end-of-file), `create` (new file; errors if it exists), and
  `overwrite` (whole file; the file must exist). There is no `delete` mode. Guarantees: the path is resolved
  and **contained to the project root** (no `../` escape), and the **read-only protected zones**
  (`Packages/`, `Assets/_MolcaSDK/`) are refused with a "subclass / work in your own area" message — so the
  architecture's read-only layers hold even when an edit is requested directly. Every write to an existing
  file is snapshotted first and is byte-for-byte revertible (`undoId` in the result; a
  brand-new `create` has no backup — revert by deleting it). Editing a `.cs` file recompiles
  (`requiresDomainReload=true`). As an Action tool it ships off by default and is inert until added to the
  action allowlist, and each write is confirmed before it applies.

Play-mode runtime actions (irreversible):

- `molca_content_install`, `molca_content_uninstall`, `molca_content_update`, `molca_content_switch_version`, `molca_content_cancel`
- `molca_content_queue_pause`, `molca_content_queue_resume`, `molca_content_queue_cancel_all`
- `molca_localization_set_language`

Edit-mode irreversible actions:

- `molca_content_create_build_config`
- `molca_content_settings`
- `molca_content_settings_edit`
- `molca_content_build`
- `molca_content_bind_group`
- `molca_create_mcp_tool`
- `molca_trigger_build`
- `molca_kg_build`
- `molca_clickup_set_task_status`, `molca_clickup_create_task`, `molca_clickup_set_focus`
- `molca_undo_last_action`

## Usage Rules

- Use read tools before action tools to resolve targets: `molca_references_audit` before Ref Id fixes, settings reads before `molca_settings_set_fields`, networking reads before request edits, and localization coverage before localization edits.
- Settings, localization text/language-list, and most content-config edits route through Unity Undo.
- Doctor/validation fixes that touch scene files are snapshotted for `molca_undo_last_action`.
- Play-mode control, content lifecycle, codegen, ClickUp writes, builds, deploys, and graph generation are irreversible.
- Codegen tools write `.cs` files; new types are unavailable until after a domain reload.
- `molca_trigger_build` runs a Doctor gate first and refuses to build on blocking findings.
- Networking and request reads redact/mask sensitive headers and values.
- Knowledge-graph query tools require a built graph; check `molca_kg_status` and build with `molca_kg_build` first.
- ClickUp tools require a configured ClickUp integration.
- Action tools must remain allowlisted in `Assets/_Molca/Editor/MCP Settings.asset`.

## See Also

- [UNITY_MCP_TOOLS.md](UNITY_MCP_TOOLS.md): general-purpose `molca.unity` Unity-editor tools.
- [MCP_FORK_PROVIDERS.md](MCP_FORK_PROVIDERS.md): adding provider tools from an SDK fork.
- [Sequence MCP Tools](molca://doc/SEQUENCE_MCP_TOOLS) (`com.molca.sequence` add-on): the `molca_sequence_*`/`molca_validate_sequence` tools that moved out of this provider.
- [KNOWLEDGE_GRAPH.md](KNOWLEDGE_GRAPH.md): the graphify knowledge graph behind the `molca_kg_*` tools.
