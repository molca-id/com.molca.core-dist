---
title: Unity MCP Tools
category: Tooling
order: 960
---

# Unity MCP Tools

`UnityMcpToolProvider` owns the general-purpose `molca.unity` namespace for Unity-native editor actions that are not specific to Molca sequences, content packages, or other Core framework domains.

> These tools group under `unity/<family>` in the in-editor assistant's tiered tool catalog; the model fetches a tool's schema on demand via `molca_tool_schema` (the IDE MCP bridge still sees them all directly). See [MCP_FORK_PROVIDERS.md](MCP_FORK_PROVIDERS.md) → "How the in-editor assistant exposes tools".

## Provider Layout

Tools are discovered by convention from the `Create*Tool()` factories across the provider's partial files (see `McpToolProvider.GetTools`). No manual registration is needed.

- `UnityMcpToolProvider.cs`: provider asset metadata, namespace, and discovery.
- `UnityMcpToolProvider.Common.cs`: provider-local JSON argument/result helpers.
- `UnityMcpToolProvider.Scene.cs`: scene, selection, hierarchy, build-scene, scene workflow, and build-settings tools.
- `UnityMcpToolProvider.Components.cs`: component type discovery, component listing, serialized field reading, and component field editing.
- `UnityMcpToolProvider.GameObjects.cs`: GameObject mutation tools.
- `UnityMcpToolProvider.Assets.cs`: AssetDatabase and prefab-asset tools.
- `UnityMcpToolProvider.Renderers.cs`: renderer discovery/inspection and renderer material actions.
- `UnityMcpToolProvider.Materials.cs`: material/shader inspection and material property/create actions.
- `UnityMcpToolProvider.Importers.cs`: asset importer inspection and import-setting actions.
- `UnityMcpToolProvider.Lighting.cs`: camera/light/probe discovery and camera/light actions.
- `UnityMcpToolProvider.Prefab.cs`: prefab instance status/override inspection and apply/revert/save/unpack actions.
- `UnityMcpToolProvider.UI.cs`: canvas/RectTransform inspection, UI diagnostics, and RectTransform layout actions.
- `UnityMcpToolProvider.UIToolkit.cs`: UI Toolkit PanelSettings/UIDocument authoring actions (`molca_unity_uitk_*`).
- `UnityMcpToolProvider.Physics.cs`: collider/rigidbody discovery, physics queries, and collider/rigidbody actions.
- `UnityMcpToolProvider.ProjectSettings.cs`: tags/layers/project-settings inspection and GameObject tag/layer actions.
- `UnityMcpToolProvider.Rendering.cs`: render pipeline, quality, and graphics-capability inspection.
- `UnityMcpToolProvider.Workflow.cs`: editor context snapshot and selection/navigation actions.
- `UnityMcpToolProvider.Addressables.cs`: Addressables settings/entry inspection and group/entry/label authoring actions.

## Read-Only Tools

Scene / hierarchy / components:

- `molca_unity_selection`: current editor selection.
- `molca_unity_scenes`: loaded scene state.
- `molca_unity_build_scenes`: `EditorBuildSettings.scenes`.
- `molca_unity_scene_objects`: loaded-scene hierarchy listing/filter.
- `molca_unity_component_types`: concrete `Component` type search.
- `molca_unity_gameobject_components`: component indexes for a target GameObject.
- `molca_unity_component_fields`: serialized fields and current values for a component.
- `molca_unity_context_snapshot`: one-call editor context (play state, loaded scenes, selection, open prefab stage).

Assets:

- `molca_unity_assets`: AssetDatabase search by query/type/folder.
- `molca_unity_asset_dependencies`: asset dependency list.
- `molca_unity_prefab_contents`: read-only prefab hierarchy/component inspection.
- `molca_unity_importer_inspect`: `AssetImporter` settings (texture/model importer summaries).

Renderers / materials:

- `molca_unity_renderers`: renderer listing across loaded scenes with shared-material slots.
- `molca_unity_renderer`: one renderer's material slots, shaders, and texture slots.
- `molca_unity_material`: material shader, color/float/vector/texture properties (incl. FBX/model sub-assets).

Lighting / cameras:

- `molca_unity_cameras`: camera listing with projection/clear-flags/culling.
- `molca_unity_lights`: light listing with type/intensity/color/shadows.
- `molca_unity_probes`: reflection probes and light probe groups.

Prefab inspection:

- `molca_unity_prefab_status`: prefab connection status, source asset, instance status, nearest root.
- `molca_unity_prefab_overrides`: modified properties, added/removed components, added GameObjects.

UI / canvas:

- `molca_unity_canvases`: canvas listing with render mode and CanvasScaler settings.
- `molca_unity_ui_tree`: RectTransform tree with layout and UI component types.
- `molca_unity_ui_diagnostics`: missing scripts, zero-size rects, negative scale.

UI Toolkit:

- `molca_unity_uitk_list_documents`: project UI Toolkit assets grouped by kind (UXML, USS, PanelSettings, ThemeStyleSheet), optional folder scope.
- `molca_unity_uitk_scene_uidocuments`: UIDocument components in loaded scene(s) with source UXML, PanelSettings, and sort order.
- `molca_unity_uitk_inspect_uxml`: a `.uxml`'s top-level elements, `<Style>`/`<Template>` references, and any style/template refs that do not resolve to a project asset.

Physics:

- `molca_unity_colliders`: collider listing with trigger/material/bounds/attached rigidbody.
- `molca_unity_rigidbodies`: rigidbody listing with mass/kinematic/gravity/constraints.
- `molca_unity_physics_query`: raycast / overlapSphere / overlapBox diagnostics.

Project / rendering:

- `molca_unity_tags_layers`: tags, populated layers, and sorting layers.
- `molca_unity_project_settings`: build target, scripting backend, color space, gravity, etc.
- `molca_unity_render_pipeline`: active SRP asset (URP/HDRP) or Built-in.
- `molca_unity_quality_settings`: quality levels and per-level settings.
- `molca_unity_graphics_capabilities`: device/shader capabilities for material authoring.

Addressables:

- `molca_unity_addressable_settings`: settings asset path, active/all profiles, labels, per-group summary (schemas, build/load path vars).
- `molca_unity_addressable_entries`: entry listing with address/asset path/GUID/group/labels/type, filterable by group/address/label.
- `molca_unity_addressable_resolve`: resolve an exact address or a label to the matching entries.

## Action Tools

Unity Undo-backed (`McpToolReversibility.UnityUndo`, Edit mode):

- `molca_unity_gameobject_rename`, `molca_unity_gameobject_set_active`, `molca_unity_gameobject_set_transform`
- `molca_unity_gameobject_create`, `molca_unity_gameobject_duplicate`, `molca_unity_gameobject_reparent`
- `molca_unity_gameobject_delete`, `molca_unity_gameobject_add_component`, `molca_unity_gameobject_remove_component`
- `molca_unity_component_set_fields`
- `molca_unity_gameobject_set_tag`, `molca_unity_gameobject_set_layer`
- `molca_unity_renderer_set_enabled`, `molca_unity_renderer_set_material`
- `molca_unity_material_set_color`, `molca_unity_material_set_property`
- `molca_unity_camera_set`, `molca_unity_light_set`
- `molca_unity_collider_set`, `molca_unity_rigidbody_set`
- `molca_unity_ui_set_rect`
- `molca_unity_uitk_create_uidocument` (creates a GameObject + `UIDocument`, wiring source UXML, PanelSettings, and sort order)
- `molca_unity_uitk_set_uidocument` (re-points an existing `UIDocument`'s UXML / PanelSettings / sort order)
- `molca_unity_prefab_instantiate`, `molca_unity_prefab_revert`, `molca_unity_prefab_unpack`

File-snapshot reversible (`McpToolReversibility.FileSnapshot`, revert via `molca_undo_last_action`):

- `molca_unity_texture_import_set`, `molca_unity_model_import_set` (snapshot the asset `.meta`)
- `molca_unity_prefab_apply` (snapshot the prefab asset)
- `molca_unity_build_scenes_set` (snapshot `ProjectSettings/EditorBuildSettings.asset`)
- `molca_unity_uitk_link_stylesheet` (adds a `<Style src=...>` to a `.uxml` root; snapshots the uxml file; no-op if already linked)

Irreversible (no automatic revert):

- `molca_unity_scene_set_active`, `molca_unity_scene_save`, `molca_unity_scene_open`
- `molca_unity_material_create`, `molca_unity_prefab_save` (creates a new asset; delete to revert)
- `molca_unity_uitk_create_panel_settings` (creates a `PanelSettings` asset, auto-assigning the shipped Molca theme `Packages/com.molca.core/Runtime/UIToolkit/MolcaDefaultTheme.tss`; falls back to any project `ThemeStyleSheet`)
- `molca_unity_reimport_asset`, `molca_unity_build_target_switch`
- `molca_unity_addressable_mark`, `molca_unity_addressable_unmark`, `molca_unity_addressable_set_address`
- `molca_unity_addressable_set_labels`, `molca_unity_addressable_move`
- `molca_unity_addressable_create_group`, `molca_unity_addressable_remove_group`

  Addressables edits span the settings asset plus per-group assets, which the single-file snapshot stack cannot capture — hence irreversible. They require Addressables to be initialized in the project.

Editor navigation (Action-kind for UI-state changes, but data-safe; run in any mode):

- `molca_unity_select`, `molca_unity_ping_asset`, `molca_unity_frame_selected`
- `molca_unity_open` (Edit mode; opening a scene may prompt to save dirty scenes)

`molca_unity_select` selects one or many objects: pass `path`/`paths`, `target`/`targets`, or `instanceId`/`instanceIds` (singular and plural forms combine). Resolved objects fill `Selection.objects`; the first becomes the active selection. Any unresolved reference aborts the call rather than producing a partial selection.

## Usage Rules

- Use read tools before action tools when resolving target names, component indexes, material/prefab/scene paths, tags, or layers.
- Most object edits route through Unity Undo. Asset-file edits that Unity cannot Undo are snapshotted for `molca_undo_last_action`; asset-creation and reimport/build-target actions are irreversible.
- Resolve materials/renderers/components/GameObjects by `instanceId` / `globalObjectId` / `name` / `path` / hierarchy `target` as documented per tool.
- `molca_unity_gameobject_set_tag` / `_set_layer` require the tag/layer to already exist (`molca_unity_tags_layers`).
- `molca_unity_scene_open` defaults to additive mode and refuses `Single` mode when loaded scenes are dirty unless `saveDirtyScenes=true`.
- The UI tools avoid a hard UGUI (`UnityEngine.UI`) assembly dependency; CanvasScaler data is read via reflection and is absent if UGUI is not installed.
- UI Toolkit (`molca_unity_uitk_*`) is the runtime-UI counterpart to the UGUI `molca_unity_ui_*` tools. They complete the Figma pipeline: `molca_figma_build_frame` (UXML/USS) → `molca_unity_uitk_create_panel_settings` (once) → `molca_unity_uitk_create_uidocument`. New panels are themed by the shipped Molca theme automatically; the result reports `themeSource` (`molca`/`override`/`project`/`none`).
- Action tools must remain allowlisted in `Assets/_Molca/Editor/MCP Settings.asset`.

## Deferred / Not Yet Implemented

- `molca_unity_prefab_replace` (replace a scene object with a prefab instance preserving transform/refs).
- Global project-settings mutation (tags/layers/graphics asset assignment) — only per-GameObject tag/layer assignment is implemented; global edits are not per-object Undo-able and need stronger gating.

## See also

- [Core MCP Tools](CORE_MCP_TOOLS.md)
- [Extending MCP from a Fork](MCP_FORK_PROVIDERS.md)
