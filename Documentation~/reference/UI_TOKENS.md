# Molca UI Tokens

A **design-token layer over uGUI** — the framework's "style sheet". uGUI has no cascading stylesheet
(no USS), so a token is a named handle onto the styling mechanisms the framework *already* has:
`ColorID` swatches, `LocalizedText` style presets, sprites, and reusable prefabs. Tokens let tooling
(the hand-authoring `MolcaStyleApplier`, and the Figma→uGUI materializer in a later sprint) refer to
**semantic names** — `color/primary`, `surface/panel-bg`, `text/title`, `control/button` — instead of
raw hex, sprite GUIDs, PPU multipliers, and ad-hoc prefab paths.

> **The cardinal rule: tokens *name* existing mechanisms; they never store raw appearance.** A
> `color/*` token resolves to a `ColorID` swatch+step (not a `Color`); a `text/*` token to a
> `LocalizedTextStyleInfo` preset; a `surface/*` token to a sprite + a PPU *rule*. Re-theming therefore
> keeps flowing through `ColorID`/style presets exactly as before — a token-built object is
> indistinguishable from a hand-built one and needs no registry at runtime.

## Token id grammar

`category/name` (lower-case category, kebab-case name). Categories:

| Category | Resolves to | Example |
|---|---|---|
| `color`   | a `ColorID` swatch + step | `color/primary`, `color/text-60` |
| `text`    | a `LocalizedTextStyleInfo` preset | `text/title`, `text/body` |
| `surface` | a sprite + `Image.Type` + a PPU reference | `surface/panel-bg` |
| `control` | a reusable prefab (or variant) | `control/button`, `control/list-item` |
| `spacing` | a layout scalar (UI units) | `spacing/gap` |

`MolcaUiTokenId.TryParse` / `IsValid` are the single source of the grammar (`Runtime/UI/Tokens/`).

## The Core-vs-SDK split

Core ships the **engine and contract**, but **no token values** — the same layer discipline as the
rest of the framework.

- **`MolcaUiToken`** — a flat serializable record: a `MolcaUiTokenCategory` discriminator plus the
  per-category fields. Build them with the `New*` factories (`NewColor`, `NewText`, `NewSurface`,
  `NewControl`, `NewSpacing`).
- **`MolcaUiTokenRegistry`** (abstract) — id-based lookup (`TryResolve`, `TokenIds`). The extension
  point for forks that want custom resolution.
- **`MolcaUiTokenCatalog`** (concrete, `[CreateAssetMenu]` → *Molca/UI/UI Token Catalog*) — the default
  container: a serialized list of tokens. **An SDK or project authors this asset; Core never does.**

These live in a dedicated **`Molca.UI`** assembly (it needs both `Molca` for `ColorID` and
`Molca.Localization` for the text style preset, so it sits downstream of both).

## Applying a token — `MolcaUiTokenResolver` (editor)

`MolcaUiTokenResolver.TryApply(catalog, tokenId, target, out error)` writes the concrete components at
edit time (registered with `Undo`):

- **color** → adds/updates a `ColorID` (swatch+step) and `Refresh()`es it onto the object's graphic(s).
- **text** → adds a `LocalizedText` (its `[RequireComponent]` auto-adds the TMP text), sets the
  `styleInfo` preset, and calls `ApplyStyle()`.
- **surface** → adds/updates an `Image`, sets `sprite` + `type`, and applies the **PPU rule**:
  `pixelsPerUnitMultiplier = ReferencePixels / min(rectWidth, rectHeight)` — so a 9-sliced corner
  radius stays visually constant across rect sizes.
- **control** / **spacing** → *not* applied in place (control tokens are instantiated by the
  materializer; spacing tokens are layout scalars consumed by the layout pass) — `TryApply` returns
  false with an explanatory error.

### Hand-authoring — `MolcaStyleApplier`

Drop the `MolcaStyleApplier` component on a UI object, assign a catalog + token id, and click
**Apply Token** in the inspector (one undo group). The component only *records* the token; it has no
runtime behavior, so there is zero per-frame cost.

## Seeding a catalog — `MolcaUiTokenMiner` (editor)

Rather than inventing a token taxonomy, derive it from real prefabs:

```csharp
// Engine in Core; running it against your UI folder is the SDK/project step.
MolcaUiTokenMiner.MineToCatalog(
    "Assets/_MolcaSDK/Level/Prefabs/UI",
    "Assets/_MolcaSDK/Level/Prefabs/UI/Tokens/MolcaVrUiTokenCatalog.asset");
```

`Mine` scans every prefab under the folder and harvests, de-duplicated by id:

- `control/<prefab-name>` — each prefab is a reusable control.
- `color/<swatch>-<step>` — every distinct `ColorID` in the prefab trees.
- `text/<preset-name>` — every distinct `LocalizedText.styleInfo` preset.
- `surface/<sprite-name>` — 9-sliced background `Image`s, with `ReferencePixels` *inferred* from the
  authored `pixelsPerUnitMultiplier × min(w, h)` (the PPU rule, inverted).

## How a fork extends the system

1. Mine your UI prefab folder into a catalog asset (above), or author one by hand
   (*Create → Molca → UI → UI Token Catalog*).
2. Bind/curate tokens: `control/*` → your real prefabs, `color/*` → the swatches/steps you use,
   `text/*` → your `LocalizedTextStyleInfo` presets, `surface/*` → your background sprite + the right
   `ReferencePixels`.
3. Reference that catalog from `MolcaStyleApplier` (and, later, the Figma materializer).

The catalog and its values live in the **SDK/project layer**; Core only defines the shape and the
apply/mine engine.
