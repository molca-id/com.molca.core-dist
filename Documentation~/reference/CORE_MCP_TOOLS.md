---
title: Core MCP Tools
category: Tooling
order: 950
---

# Core MCP Tools

`CoreMcpToolProvider` owns the `molca` namespace: the introspection, sequence/content/settings authoring, networking, localization, knowledge-graph, ClickUp, and Figma tools that are specific to Molca Core. General-purpose Unity-editor actions live in [`UnityMcpToolProvider`](UNITY_MCP_TOOLS.md).

## Provider Layout

Tools are discovered by convention from the `Create*Tool()` factories across the provider partial files. There is no central registration list to keep in sync. SDK forks add tools by subclassing `McpToolProvider` under their own namespace, never by editing this provider; see [MCP_FORK_PROVIDERS.md](MCP_FORK_PROVIDERS.md).

- `CoreMcpToolProvider.cs`: provider asset metadata, `molca` namespace, and discovery.
- `CoreMcpToolProvider.Status.cs` / `.BuildInfo.cs`: editor/runtime status and build-profile/version info.
- `CoreMcpToolProvider.Subsystems.cs` / `.Services.cs` / `.Bootstrap.cs`: live subsystem graph, DI service registrations, and static bootstrap description.
- `CoreMcpToolProvider.FrameworkGraph.cs`: read-only project-wiring map.
- `CoreMcpToolProvider.Sequence*.cs` / `.Author.cs` / `.Remediate.cs`: sequence validation, field reads/edits, structural edits, playback, whole-graph authoring, and remediation.
- `CoreMcpToolProvider.Codegen.cs` / `.CreateMcpTool.cs`: Step/Auxiliary script scaffolding and MCP-tool codegen.
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
- `CoreMcpToolProvider.Figma.cs`: Figma file/frame listing and UI Toolkit scaffolding.
- `CoreMcpToolProvider.ClickUp.cs`: ClickUp integration status, task/workspace listing, task creation, and status changes.

## Read-Only Tools

Status / runtime introspection:

- `molca_status`: editor and Molca runtime status.
- `molca_build_info`: build profiles, current version, and recent changelog entries.
- `molca_subsystems`: registered `RuntimeSubsystem`s with dependency/init-order information. Play mode.
- `molca_services`: `RuntimeManager` service-container registrations. Play mode.
- `molca_describe_bootstrap`: static bootstrap description.
- `molca_framework_graph`: read-only project-wiring map.

Sequences:

- `molca_validate_sequence`: validate one `SequenceController`.
- `molca_validate_all_sequences`: validate every `SequenceController` across loaded scenes.
- `molca_sequence_list_types`: concrete `Step` / `StepAuxiliary` types and writable fields.
- `molca_sequence_get_step_fields`: current serialized field values for a step and auxiliaries.

Reference system:

- `molca_refids`: Ref Ids exposed by `IReferenceable` components in loaded scenes.

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

- `molca_network_config`: `HttpModule` config with sensitive values redacted.
- `molca_network_list_requests`: every `HttpRequestAsset` with redacted request details.
- `molca_network_get_request`: one `HttpRequestAsset` in full with sensitive values masked.

Localization:

- `molca_localization_status`: localization modules/languages and runtime language state.
- `molca_localization_list_texts`: DynamicLocalization texts found in loaded scenes.
- `molca_localization_coverage`: DynamicLocalization coverage, missing translations, and editable field paths.

Knowledge graph:

- `molca_kg_status`: graphify graph location/status.
- `molca_kg_query`: natural-language project query over the graph.
- `molca_kg_path`: shortest relationship path between two concepts/entities.
- `molca_kg_explain`: plain-language explanation of one concept/entity.

Documentation:

- `molca_docs_list`: list the reference guides available in the project (id, title, category), optionally filtered by category.
- `molca_docs_read`: return a guide's full Markdown body by id (front-matter stripped).
- `molca_docs_search`: case-insensitive substring search over guide titles and bodies, returning matches with a snippet.

Source / Doctor / Figma / ClickUp:

- `molca_read_source`: read a text/source file inside the project by path with optional line-range pagination.
- `molca_doctor`: run Molca Doctor convention checks.
- `molca_figma_list_files`: Figma files for the configured team or a given project.
- `molca_figma_list_frames`: frames in a Figma file.
- `molca_clickup_status`: ClickUp connection, target, and token state.
- `molca_clickup_list_tasks`: tasks from the configured target folder/list.
- `molca_clickup_list_workspaces`: workspaces available to the stored token.

Interactive:

- `molca_ask_user`: ask the user a question and wait for their answer. It is `ReadOnly`; it changes no project state.

## Action Tools

Unity Undo-backed (`McpToolReversibility.UnityUndo`, Edit mode):

- Sequence field edits: `molca_sequence_set_step_fields`, `molca_sequence_add_auxiliary`, `molca_sequence_remove_auxiliary`, `molca_sequence_set_auxiliary_fields`
- Sequence structure: `molca_sequence_add_steps`, `molca_sequence_remove_steps`, `molca_sequence_duplicate_steps`, `molca_sequence_change_type`, `molca_sequence_reparent`
- Whole-graph authoring: `molca_sequence_author`
- Sequence remediation: `molca_sequence_remediate` for Unity-Undo-safe remediation. File-snapshot fixes such as `BrokenAuxiliary` are intentionally delegated to `molca_sequence_fix`.
- Ref Id repair: `molca_fix_refids`
- Settings: `molca_settings_set_fields`
- Networking: `molca_network_create_request`, `molca_network_set_request_fields`
- Localization authoring: `molca_localization_set_text`, `molca_localization_add_language`
- Content-package config authoring: `molca_content_define_package`, `molca_content_update_package`, `molca_content_remove_package`, `molca_content_assign_labels`, `molca_content_set_build_config`

File-snapshot reversible (`McpToolReversibility.FileSnapshot`, revert via `molca_undo_last_action`):

- `molca_run_doctor_fix`
- `molca_sequence_fix`
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

- `molca_sequence_start`, `molca_sequence_complete_step`
- `molca_content_install`, `molca_content_uninstall`, `molca_content_update`, `molca_content_switch_version`, `molca_content_cancel`
- `molca_content_queue_pause`, `molca_content_queue_resume`, `molca_content_queue_cancel_all`
- `molca_localization_set_language`

Edit-mode irreversible actions:

- `molca_content_create_build_config`
- `molca_content_build`
- `molca_content_deploy`
- `molca_content_bind_group`
- `molca_sequence_create_step_script`, `molca_sequence_create_auxiliary_script`
- `molca_create_mcp_tool`
- `molca_trigger_build`
- `molca_kg_build`
- `molca_figma_build_frame`, `molca_figma_build_panel`
- `molca_clickup_set_task_status`, `molca_clickup_create_task`
- `molca_undo_last_action`

## Usage Rules

- Use read tools before action tools to resolve targets: sequence type/field reads before sequence edits, `molca_refids` before Ref Id fixes, settings reads before `molca_settings_set_fields`, networking reads before request edits, and localization coverage before localization edits.
- Sequence, settings, localization text/language-list, and most content-config edits route through Unity Undo.
- Doctor/validation fixes that touch scene files are snapshotted for `molca_undo_last_action`.
- Play-mode control, content lifecycle, codegen, ClickUp writes, builds, deploys, and graph/Figma generation are irreversible.
- Codegen tools write `.cs` files; new types are unavailable until after a domain reload.
- `molca_trigger_build` runs a Doctor gate first and refuses to build on blocking findings.
- Networking and request reads redact/mask sensitive headers and values.
- Knowledge-graph query tools require a built graph; check `molca_kg_status` and build with `molca_kg_build` first.
- Figma tools require a configured Figma integration.
- ClickUp tools require a configured ClickUp integration.
- Action tools must remain allowlisted in `Assets/_Molca/Editor/MCP Settings.asset`.

## See Also

- [UNITY_MCP_TOOLS.md](UNITY_MCP_TOOLS.md): general-purpose `molca.unity` Unity-editor tools.
- [MCP_FORK_PROVIDERS.md](MCP_FORK_PROVIDERS.md): adding provider tools from an SDK fork.
- [SEQUENCE_AUTHORING.md](SEQUENCE_AUTHORING.md) / [SEQUENCE_VALIDATION.md](SEQUENCE_VALIDATION.md): the sequence model the sequence tools operate on.
- [KNOWLEDGE_GRAPH.md](KNOWLEDGE_GRAPH.md): the graphify knowledge graph behind the `molca_kg_*` tools.
