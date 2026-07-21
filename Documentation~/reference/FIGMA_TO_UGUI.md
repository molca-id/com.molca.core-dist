---
title: Figma → uGUI (UI Intent Spec)
category: UI & Presentation
order: 530
---

# Figma → uGUI (UI Intent Spec)

The pipeline that turns a Figma frame into Unity **uGUI** — the VR-in-game UI target (world-space
canvas + `TrackedDeviceGraphicRaycaster`), distinct from the editor/2D **Figma→UITK** pipeline. It runs
in three stages:

```
Figma frame  ──►  UI Intent Spec  ──►  uGUI prefab
                                    (molca_build_ugui)
```

The first stage turns a frame into a validated, token-referential **UI Intent Spec**; the second builds
the prefab from that spec.

## Relationship to Figma→UITK

The existing `molca_figma_*` / `build_panel` tools target **UI Toolkit** and remain the right choice for
editor/2D panels. This pipeline targets **uGUI** because VR world-space UI needs it. Both read Figma node
JSON with the same field semantics, but the UITK translator emits UXML/USS while this emits the token
spec — **shared extraction idiom, divergent output**. The UITK path is unchanged; `FigmaFrameModel` is an
independent normalized parse used only here.

## The UI Intent Spec

A small, **token-referential, Unity-internal-free** JSON tree (`UiIntentSpec` / `UiIntentNode`). Every
visual choice is a UI token id; there are **no anchors, sizeDeltas, PPU values, sprite GUIDs, or
hex colors**.

- **Header:** `sourceFrame`, `worldScale` (panel width in metres), `minHitCm`, `catalogId`.
- **Node:** `type` (`panel`/`group`/`text`/`button`/`list`/`image`), `token`, optional `color`/`text`
  token overrides, `locKey`, `layout` (`vertical`/`horizontal`/`none`), `gap`, `padding`, `sizeHint`,
  `bind` (lists), `children`.

`UiIntentSpecValidator` gates it: known `type`/`layout`, and **every token id resolves in the catalog**.
The `…/_unmapped` sentinel is *permitted* — it flags an item for human review and is never a raw value.

> **VR inputs are declared, not inferred.** `worldScale` and `minHitCm` are supplied by the caller —
> Figma has no physical size. Defaults: 0.5 m / 4 cm.

## The mapping (two passes)

**1. Deterministic pre-pass (always runs, fully testable).**
- **Color:** each fill is snapped to the nearest catalog color via **CIEDE2000** (perceptual ΔE in
  CIE-Lab). Past a distance threshold it becomes `color/_unmapped` — never a guessed hex.
- **Text:** each run is snapped to the nearest `text/*` preset by font size.
- **Recognition:** rounded filled rects with a text child (or named "button") → `button`/`control/button`;
  ≥3 structurally-similar siblings → a `list`/`control/list-item` keeping one row template.
- **Layout:** auto-layout → `vertical`/`horizontal` + `gap`; padding carried through.

**2. Model pass (optional refinement).**
Feeds the draft + the catalog token vocabulary to the model to
*confirm/override semantics* (primary CTA color, list vs. group) and fill `locKey`s. The result is
**re-validated against the catalog** — a model that invents a token or type loses to the deterministic
draft. No provider / failure / invalid output ⇒ the draft is returned unchanged. The model can never emit
a raw value (the spec has no field for one) or an out-of-catalog token (validation rejects it).

## The tool — `molca_figma_to_ui_spec`

Read-only; reads Figma + the catalog and computes, **builds nothing**.

```
molca_figma_to_ui_spec(figmaUrlOrNode, fileKey?, catalog?, worldScale?=0.5, minHitCm?=4)
  → { spec, mapping, unmapped, catalog }
```

- `catalog` selects a `MolcaUiTokenCatalog` by asset name (omit for the only/first one).
- `mapping` is the per-node report (token, confidence, unmapped flag); `unmapped` lists the paths left for
  review.
- Colors resolve against the active `ColorModule` palette; if none is configured, color tokens simply go
  unmapped (safe, visible).

The output spec is the input to `molca_build_ugui` (below).

## Stage 2 — Spec → uGUI prefab (`molca_build_ugui`)

Deterministically materializes a validated spec into a **VR-ready uGUI prefab** — a strong first draft, not
a finished screen. **No model judgement runs here**, so the same spec + catalog always produce the same
tree. Three passes:

1. **Materializer** — builds the GameObject tree. `button` nodes instantiate the
   catalog's real control prefab (`ColorIDButton` and all); a `list` is a container with one instantiated
   row template; `panel`/`image`/`text`/`group` are primitives. **All appearance comes from the UI token
   resolver** — the materializer sets no raw color/sprite/PPU. The one sanctioned raw value is the
   **magenta `TODO_…` placeholder** an `_unmapped` token produces, so gaps are visible, not silently wrong.
2. **Layout pass** — `vertical`/`horizontal` → a `LayoutGroup` with the spec's
   `gap`/`padding` (+ `ContentSizeFitter` when hugging); `none` + `sizeHint:stretch` → 0–1 fill anchors;
   a `list` stacks its rows. (ScrollRect rigging is left to the human polish pass.)
3. **VR pass** — the root becomes a **world-space `Canvas`** scaled so its width equals
   `worldScale` metres; interactive rects grow to at least `minHitCm`; lists get a nested canvas to isolate
   their dynamic redraws. The `GraphicRaycaster` is the **catalog-declared type** (`VrRaycasterTypeName`,
   e.g. XRI's `TrackedDeviceGraphicRaycaster`) when set, else the built-in one — **Core never references
   XR Interaction Toolkit**; the SDK catalog supplies the type by name.

```
molca_build_ugui(spec, outputPath, overwrite?=false, catalog?, canvasMode?='world')
  → { prefab, undoId, nodesBuilt, prefabsInstantiated, unmappedPlaceholders, notes }
```

**Non-VR / flat-screen UI.** The pipeline is general uGUI — only the VR pass is VR-specific, and it's
gated by `canvasMode`: `world` (default) builds a VR/diegetic world-space canvas scaled to `worldScale`
metres with `minHitCm` hit targets; **`overlay`** builds a standard `ScreenSpaceOverlay` canvas and
**`camera`** a `ScreenSpaceCamera` one (assign `canvas.worldCamera` after build), both with a
`CanvasScaler` set to scale-with-screen at the design resolution. In screen-space modes the metre scaling
and VR hit-target growth are skipped (hit sizing follows the design px), and the raycaster is the built-in
`GraphicRaycaster`. Everything else — tokens, mapping, materializer, layout — is identical, so the same
spec builds either a VR panel or a flat screen.

Action tool: gated by the allowlist + confirmation; the write is snapshotted for revert via
`molca_undo_last_action` (byte-for-byte revert on overwrite; a new prefab → revert by deleting it).
Refuses Play mode and non-`Assets/` paths.

### The honest ceiling

This produces a **strong first draft a developer polishes** — not a black box. The VR physical and
performance decisions Figma cannot encode (`worldScale`, `minHitCm`, the raycaster type, canvas-split
policy) live as **caller inputs + catalog rules**, applied mechanically — never guessed by a model. Review
the layout/VR sizing, wire `locKey`s and any ScrollRect, and resolve every `TODO_` placeholder before use.

### Regen / overrides

Regenerating overwrites the whole generated prefab. Keep hand-tweaks in a **sibling object or a prefab
variant** rather than editing the generated asset in place, so a re-run doesn't clobber them.

## See also

- [UI Tokens](UI_TOKENS.md)
- [Editor Design Language](EDITOR_DESIGN_LANGUAGE.md)
