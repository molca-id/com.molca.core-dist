# Molca Editor Design Language

This document is the source of truth for custom Molca editor windows and future refactors of
existing editor tools. The Molca Hub established the shared visual language: quiet, dense,
tool-oriented Unity editor UI with clear hierarchy, compact spacing, and strong settings workflows.

Use this guide when building or refactoring editor-only UI in `Packages/com.molca.core/Editor/`.
Runtime UI and in-game UX are out of scope.

## Shared Foundation (`Editor/UI/`)

Sprint 27 promoted the design language into a single reusable foundation. Do not copy token hex or
re-implement these components per window:

- **`Editor/UI/MolcaEditorTokens.uss`** — the one definition of every `--molca-*` color token (with
  `--hub-*` kept as back-compat aliases) plus a `molca-light` skin override.
- **`Editor/UI/MolcaEditorUi.cs`** — `MolcaEditorUi.Apply(root)` loads the token + component
  stylesheets and tags the root with `molca-editor` (and `molca-light` under the light skin). Call it
  once per window/hosted-view root.
- **`Editor/UI/MolcaEditorColors.cs`** — the same palette as C# `Color` for IMGUI (`OnGUI`/
  `OnInspectorGUI`) and GraphView code, which cannot read USS `var()`. Use a token here instead of a
  hex literal.
- **`Editor/UI/Components/`** — reusable `VisualElement` components styled by
  `MolcaEditorComponents.uss`: `MolcaSectionCard` (+ `MolcaStatusKind`), `MolcaRail`,
  `MolcaSearchField`, `MolcaLinkRow`, and the `MolcaButtons` factory. The Hub's `MolcaHubSectionCard`/
  `MolcaHubStatusKind` remain as `[Obsolete]` aliases over these.

Consume it from a new editor window:

```csharp
public void CreateGUI()
{
    var root = rootVisualElement;
    MolcaEditorUi.Apply(root);                       // tokens + molca-editor class

    var card = new MolcaSectionCard("Settings", status: MolcaStatusKind.Ok, statusText: "Ready");
    card.Body.Add(new MolcaSearchField("Filter"));   // in-field placeholder, no external label
    root.Add(card);
}
```

The `design-language` Molca Doctor check (also surfaced via the `molca_doctor` MCP tool) flags raw hex in
C#, **raw `rgb()`/`#hex` color in USS** (translucent `rgba()` washes exempt — see "Theming Rule" below),
unscoped `EditorPrefs`, nested cards, hardcoded settings-asset paths, and unscoped USS class names;
suppress an intentional case with a `doctor:ignore` comment.

## Principles

- Build tools, not landing pages. The first view should be the working surface.
- Prefer UI Toolkit for new custom editor windows. Use IMGUI only when reusing an existing mature
  inspector/body would reduce risk.
- Keep views dense but readable: compact rows, clear cards, stable rails, and no ornamental panels.
- Route data changes through `SerializedObject` / `SerializedProperty` or existing editor services.
- Reuse hostable `VisualElement` views when a tool must appear both standalone and inside the Hub.
- Preserve standalone menu windows when hosting a tool in the Hub.
- Avoid nested cards. A section can contain cards; a card should not contain another card.

## Core Layouts

### Hub-Style Workspace

Use this layout for multi-section tools:

- Top workspace toolbar: compact tabs, optional right-aligned action.
- Left rail: fixed 188px width for primary navigation.
- Detail area: scrollable content with 14px padding.
- Section pages: one header band or title area, then full-width cards.

Canonical dimensions:

| Surface | Value |
|---|---:|
| Detail padding | 14px |
| Left rail width | 188px |
| Rail vertical padding | 8px |
| Rail search horizontal margin | 9px |
| Rail row min height | 29px |
| Rail row horizontal padding | 12px |
| Rail selected border | 2px left |
| Card margin bottom | 14px |
| Card header min height | 29px |
| Card header padding | 7px vertical, 10px horizontal |
| Card body padding | 11px vertical, 12px horizontal |
| Compact field row min height | 24px |
| Compact field row bottom margin | 8px |
| Compact label column | 96px for slim settings, 118-120px for Hub pages |

### Hub Workspace Tabs (extensible)

The Hub's top-bar tabs are an **id-keyed registry**, not a fixed Core list. Settings is the anchored home
tab (always first, Core-owned); every other tab — Core's Doctor/Assistant/Sequence and any SDK/fork tab — is
contributed through a provider and discovered via `TypeCache`.

- **Add a tab** by subclassing `MolcaHubWorkspaceProvider` and returning `MolcaHubWorkspaceItem`s
  (`Id` stable kebab-case, `Label`, `Order`, `CreateContent`, optional `IsAvailable`). No Core edit, no
  registration call. The `"settings"` id is reserved.
- **Host content, don't nest controllers.** `CreateContent` builds a `VisualElement` into the workspace host
  (like Doctor/Assistant/Sequence). It is rebuilt on each selection and must tolerate teardown (the host is
  cleared on tab switch, firing `DetachFromPanelEvent` cleanup). A tab may also open a standalone window, but
  it must not stand up a second long-running hosted tool controller behind the host.
- **Hide a built-in by config, never by editing Core.** `MolcaHubWorkspaceRegistry.SetHidden(id, true)`
  drops a tab (e.g. a project that doesn't use Sequence) per project; Settings cannot be hidden.
- **Deterministic + safe.** Tabs sort by `Order` then `Id`; duplicate ids and the reserved id are rejected;
  an unavailable/throwing provider or content factory degrades to a skipped tab / compact error, never a
  broken Hub. Selection persists by id (legacy enum names migrate; a missing/hidden id falls back to
  Settings).

### Slim Project Settings Page

`Project Settings > Molca` is intentionally not a second full settings UI. Keep it as:

- identity header with Molca logo, title, subtitle, and version pill;
- single outlined `IDENTITY` card;
- Company Name and Project Name fields;
- full-width `Open Molca Hub` primary button;
- centered muted helper text.

Do not reintroduce a tabbed Project Settings provider for full Molca settings. Full workflows belong
in the Hub.

### Hostable Tool View

For existing tools such as Doctor, Assistant, and Sequence Visualizer:

- Extract the actual tool body into a reusable `VisualElement`.
- Let the standalone `EditorWindow` only own title, min size, notifications, and root hosting.
- Let the Hub host the same `VisualElement`.
- Cleanup must be tied to `DetachFromPanelEvent`, not only `EditorWindow.OnDisable`.
- Avoid duplicate controllers, polling loops, or long-running background work when a hosted view is
  replaced.

## Color Tokens

Prefer USS variables under `.molca-hub-root` or equivalent root classes. Use these token meanings
for any new editor UI:

| Token | RGB | Hex | Usage |
|---|---:|---:|---|
| `--hub-bg` | `56, 56, 56` | `#383838` | Main background |
| `--hub-panel` | `48, 48, 48` | `#303030` | Panels / rail-adjacent surfaces |
| `--hub-card` | `47, 47, 47` | `#2f2f2f` | Card base |
| `--hub-card-header` | `58, 58, 58` | `#3a3a3a` | Card header band |
| `--hub-input` | `43, 43, 43` | `#2b2b2b` | Text fields / readonly boxes |
| `--hub-border` | `31, 31, 31` | `#1f1f1f` | Strong borders |
| `--hub-border-soft` | `37, 37, 37` | `#252525` | Card/header outlines |
| `--hub-text` | `194, 194, 194` | `#c2c2c2` | Body text |
| `--hub-heading` | `220, 220, 220` | `#dcdcdc` | Titles |
| `--hub-muted` | `138, 138, 138` | `#8a8a8a` | Helper text |
| `--hub-label` | `182, 182, 182` | `#b6b6b6` | Field labels |
| `--hub-primary` | `59, 103, 150` | `#3b6796` | Primary actions / active workspace |
| `--hub-row-selected` | `59, 94, 128` | `#3b5e80` | Selected rail/profile row |
| `--hub-link` | `91, 155, 213` | `#5b9bd5` | Read-only URL/link text |
| `--hub-accent` | `198, 242, 58` | `#c6f23a` | Molca selection accent |
| `--molca-control` | `77, 77, 77` | `#4d4d4d` | Neutral (non-primary) button/segment/chip fill |
| `--molca-on-row-selected` | `245, 245, 245` | `#f5f5f5` | Text on a `--molca-row-selected` row |

Status colors:

- OK: `#57c84a`
- Idle/neutral: `#6b6b6b`
- Warning: `#d8b24a`
- Error: `#e0703a`

### Theming Rule: USS Must Reference Tokens, Not Raw Color

Every `.uss` under `Editor/` must express color through the `--molca-*` / `--hub-*` tokens, never a raw
`rgb()` or `#hex`. Tokens carry the `.molca-editor.molca-light` skin override, so a sheet that uses them
flips correctly under the light skin for free; a sheet that bakes a dark `rgb()` literal stays dark on a
light background. (This is exactly how the Hub sheet silently drifted dark-only — it half-used tokens and
half-baked grays. Sprint 55.x re-tokenized it.)

Two sanctioned exceptions, both enforced by the `design-language` Doctor check:

- **Translucent role/status tints** — use a low-alpha `rgba()` of the accent/status hue layered over a
  themed surface (e.g. `rgba(198, 242, 58, 0.09)` for an assistant-row wash, or a status-tinted badge).
  Composited over a token-driven background it tracks the skin, so `rgba()` is exempt from the check.
  Restate the alpha in a `.molca-light` block when the lighter surface needs a touch more.
- **A fixed-accent foreground** — near-black text on the lime/warn accent (whose hue is the same in both
  skins) is a legitimate opaque literal. Mark the line `/* doctor:ignore — … */`.

When `--molca-light` and a component class sit on the **same** root element (as with the Assistant view),
the light override must be a compound selector (`.molca-light.chat-root`, no space) — the descendant form
silently never matches.

## Components

### Section Card

Use `MolcaHubSectionCard` for grouped settings sections.

Card anatomy:

- 1px outline.
- Header band with title, optional subtitle, optional status dot/label, optional actions.
- Body containing compact field rows, lists, or tool-specific content.
- Optional disabled body state via opacity, not by replacing content with prose.

Do not place cards inside cards. If a sub-group is needed, use a divider, subheading, or row group
inside the existing body.

### Rails And Profile Lists

Rails are for switching the primary detail context. They should be stable-width and scannable:

- selected row uses `--hub-row-selected`;
- left selected border uses `--hub-accent`;
- no rounded card treatment for each row;
- no descriptive paragraphs inside row buttons unless the design explicitly needs a two-line row.

### Search Fields

Search labels must be placeholders inside the field, not external labels. Use a non-pickable overlay
placeholder if the Unity version does not support native placeholders.

### Buttons

- Primary full-width action: `--hub-primary`, bold centered text, 1px dark border.
- Mini/action buttons: compact height around 20px, small font, 2px radius.
- Toolbar buttons: grey, compact, restrained.
- Use icon buttons where a known Unity/lucide-style icon is available and the action is familiar.

### Links

Read-only URLs should render as link-colored text with an adjacent `Open` mini button when useful.
Opening links must use `Application.OpenURL` and be explicit; do not hide network side effects behind
passive row selection.

## Typography

- Compact editor labels: 10-11px.
- Card titles: around 12px bold.
- Detail/page titles: 15-17px bold.
- Avoid viewport-scaled font sizes.
- Letter spacing should remain default.
- Truncate only when the user still has a tooltip or adjacent full value.

## Implementation Rules

- New reusable editor views should document placement, base class, and registration in XML remarks.
- Public APIs require XML docs.
- Persist editor UI state with `MolcaEditorPrefs`, not raw unscoped `EditorPrefs`.
- Create editor-only settings ScriptableObjects (MCP, Assistant, Notification, Integration, …) through
  `MolcaEditorSettingsAsset.GetOrCreate<T>(fileName)` (`Editor/Settings/`), which finds the asset by type
  and otherwise creates it in the canonical `Assets/_Molca/Editor/` folder. Do not hardcode a per-class
  asset path — that is what drifted between `_Molca/Editor/` and `_Molca/_Core/Settings/`. Keep secrets
  (tokens, auth) off these assets. (Full rationale: `.claude/settings-system.md` → "Editor-Only Settings
  Assets".)
- Do not write runtime ScriptableObject assets at play time.
- Do not introduce static singletons for UI controllers; use view-owned instances and detach cleanup.
- Prefer package-relative asset loading through `AssetDatabase.LoadAssetAtPath`.
- Keep USS class names domain-specific (`molca-hub-*` or a similarly scoped prefix).

## Verification Checklist

Before finishing an editor UI refactor:

- Narrow docked view: no clipped labels, rail rows, or action buttons.
- Wide floating view: no awkward fixed-width islands unless the design calls for a fixed panel.
- Dark skin: primary buttons, selected rows, and outlines match the tokens.
- Light skin: controls remain legible, even when exact colors need skin-aware adjustment.
- Keyboard/mouse: buttons have clear hit targets; readonly links and copy/open actions work.
- State: navigation choices survive a domain reload where applicable.
- Tests: add EditMode coverage for state keys, registry/routing, binding paths, and non-duplicated
  hosted tool state where practical.
