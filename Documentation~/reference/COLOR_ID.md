---
title: Color ID Theming
category: UI & Presentation
order: 510
---

# Color ID Theming

Molca's semantic-colour layer. Instead of hard-coding a `Color` on every `Image`, `TMP_Text` or
`Renderer`, you name a colour and the value is resolved from a central contract. Change the contract and
every object referencing it re-themes at once.

> **There are two generations, and which one a project runs is pure configuration.** With a
> `ColorThemeSettings` module installed, `ColorSchemeManager` resolves canonical **colour tokens** from a
> **Color Theme Set** (V2). Without one, it serves the legacy `ColorModule` palette array (V1). V1 content
> keeps working under V2 through the theme set's alias map — nothing has to be rewritten to switch.
>
> **New authoring uses V2.** This page documents it first; the V1 sections below are the compatibility
> reference for content that already exists.

## V2 at a glance

| Concept | Type | Notes |
|---|---|---|
| The contract | `ColorThemeSet` | One token list, plus variants that supply values for it |
| A token | `ColorTokenDefinition` | Canonical id (`text/primary`), kind, usage, required |
| A variant's value | `ColorExpression` | A literal, an alias, or an alias with an alpha multiplier |
| Runtime service | `IColorThemeService` | `RuntimeManager.GetService<IColorThemeService>()` |
| Active snapshot | `ResolvedColorTheme` | Immutable, fully flattened, one dictionary hit per lookup |
| Binding a component | `ColorThemeBinding` | Carries its own target; no hierarchy rescan |
| Referencing from script | `ColorTokenReference` | Resolves against a service or a snapshot — never a global |

The structural inversion versus V1: token *definitions* live once on the set, and a variant supplies
values for them. V1 gave every `ColorModule` its own independent list, which is why a key could exist in
Dark and silently not in Light, and switching theme turned it magenta.

### Installing V2

`Molca ▸ ColorID ▸ Install Color Theme Settings (V1 → V2)` generates the vocabulary asset, adds a
`ColorThemeSettings` module to `GlobalSettings`, and points it at that asset.
`Molca ▸ ColorID ▸ Report Colour Theme Installation` says which path a project is on.

The `ColorModule` palettes stay in the module list and are inert under V2, so the switch reverts in one
line.

### Reading a token at runtime

```csharp
await RuntimeManager.WaitForInitialization();
var theme = RuntimeManager.GetService<IColorThemeService>();

if (theme.TryResolve("text/primary", out Color color)) _label.color = color;

theme.ThemeChanged += change => Repaint(change.Theme);   // unsubscribe on destroy
theme.SetVariant("light");
```

`TryResolve` is allocation-free and silent — absence is a `false` return, not a log line and not magenta.
Capture `ActiveTheme` once when applying many tokens together; re-reading the property mid-loop can pick
up a newer activation and leave you half-themed.

### The UI Token Catalog

A `Color` catalog entry names a canonical token through a `ColorTokenReference`. Applying it writes a
`ColorThemeBinding`. An entry still carrying only a V1 `(swatch, colourId)` pair writes a `ColorID`
instead, so a half-migrated catalog works per entry.

`Molca ▸ ColorID ▸ Preview UI Token Catalog Colour Migration` reports what migrating a catalog would do;
`Migrate UI Token Catalog Colours` applies it, adding the canonical token and **keeping** the legacy pair
so the batch stays revertible. Clearing the pair is a separate second pass.

See [UI Tokens](UI_TOKENS.md).

### Interchange

`Molca ▸ ColorID ▸ Export Colour Theme (JSON)…` writes a design-token document: DTCG-shaped `$type`,
`$value` and `$description`, with everything DTCG has no field for — per-variant values, usage, the legacy
alias map, accessibility requirements — under `$extensions.molca`. `$value` carries the default variant's
resolved colour so a plain DTCG reader sees a usable palette; `$extensions.molca.modes` is the lossless
representation.

Export is deterministic, so the file can be committed and diffed. Import is always previewed
(`Preview Colour Theme Import (JSON)…`) and reports added/updated/removed tokens, alias changes, variant
coverage, **contrast regressions**, serialized sites naming a token the import removes, and any field the
reader did not understand. No access tokens or private remote configuration are ever written.

### Accessibility

A token declares what it *colours* (`ColorTokenUsage`), which is what makes contrast checkable at all — a
raw ratio against the background cannot be judged without knowing whether a colour is a surface or a
foreground. `ColorContrastRequirement` entries on the set are measured per variant by
`ColorThemeResolver.EvaluateContrast`, surfaced by the `color-theme-audit` Doctor check, and enforced at
build time by `ColorThemeBuildValidator`.

---

# V1 compatibility reference

Everything below describes the legacy path. It remains supported for the compatibility window: under V2
these APIs resolve against theme-set data through `LegacyColorProviderAdapter`, so existing content needs
no changes.

## The palette — `ColorModule`

A palette is a `ColorModule` ScriptableObject.

| | |
|---|---|
| Base class | `SettingModule` (also implements `IColorProvider`) |
| Create | *Create → Molca → Settings → Color Settings* (`[CreateAssetMenu]`) |
| Registered via | added to `GlobalSettings` as a setting module (`GlobalSettings.GetModule<ColorModule>()`) |
| Namespace | `Molca.ColorID` |

A `ColorModule` holds one or more **swatches** (`ColorSwatch`). Each swatch has a name and a list of
color entries (`ColorDefinition` — a `colorId`, a `Color`, and an optional description). The entries
within a swatch are the individual *steps* you reference by ID. One swatch is flagged
`IsDefault` and is named `"Default"`; it is always searched first.

Colors are keyed internally by the composite `"SwatchName.ColorId"`. A lookup by bare ID checks the
`Default` swatch first, then the remaining swatches in list order, so `GetColor("Primary")` resolves
deterministically. A missing color resolves to `Color.magenta` and logs a warning — treat magenta in
the scene as "this ID isn't in the active palette".

A freshly created module seeds a `Default` swatch with these IDs:

| Color ID | Meaning |
|---|---|
| `Primary` | Primary brand color |
| `Secondary` | Secondary brand color |
| `Accent` | Accent color |
| `Success` | Positive / success state |
| `Warning` | Warning state |
| `Error` | Negative / error state |
| `Text` | Default text color |
| `Background` | Default background color |
| `Disabled` | Disabled state |
| `Clear` | Transparent |

> **Palettes are read-only config at runtime.** `AddSwatch`/`RemoveSwatch` are edit-time authoring
> operations and are refused (with a logged error) in play mode. `AddColor`/`RemoveColor` at runtime
> touch only the in-memory lookup cache — the serialized swatch data is never rewritten. Author
> palettes in the editor; change *which* palette is live at runtime by switching schemes (below).

## Referencing a color from a component — `ColorID`

Drop the `ColorID` component (`Molca/Utilities/Color ID` in the Add Component menu) on a GameObject.
It carries a swatch name + color ID and applies the resolved color to the graphics it finds on the
object.

- It auto-detects supported targets (`Renderer`, `Image`, `RawImage`, `Text`, `TMP_Text`,
  `LineRenderer`, `TrailRenderer`, `ParticleSystem`) via its `ColorTarget` list; enable *Apply To
  Children* to include child objects.
- Each target can override alpha (`UseAlpha` / `CustomAlpha`) so the same ID can drive a solid fill
  and a translucent tint.
- In `Start()` it awaits `RuntimeManager.WaitForInitialization()`, subscribes to scheme changes, and
  applies colors — so a `ColorID` object re-themes automatically when the palette is swapped.

Useful members for driving it from code:

| Member | Purpose |
|---|---|
| `SwatchName` / `ColorId` | The currently referenced swatch + ID (read-only). |
| `SetColor(swatch, colorId)` | Point at a specific swatch + ID and reapply. |
| `SetColorId(colorId)` | Change the ID (keeps the swatch); accepts composite `"Swatch/ColorId"`. |
| `Refresh()` | Re-detect targets and reapply (after adding graphics at runtime). |
| `ApplyColors()` | Reapply the current color to known targets. |
| `GetAvailableColorIds()` | All IDs in the active palette. |

## Referencing a color in your own scripts — `ColorIDReference`

For a serialized field that a designer picks in the Inspector and your code reads, use
`ColorIDReference` (a `[Serializable]` field type, not a component):

```csharp
using Molca.ColorID;
using UnityEngine;
using UnityEngine.UI;

// Assets/YourProject/Scripts/UI/PanelTint.cs
public class PanelTint : MonoBehaviour
{
    /// <summary>Palette color the panel background paints with.</summary>
    [SerializeField] private ColorIDReference _background = new ColorIDReference("Background");

    [SerializeField] private Image _panel;

    private async void Start()
    {
        // Contract: resolve the color only AFTER bootstrap — see below.
        await RuntimeManager.WaitForInitialization();
        if (this == null) return;

        _panel.color = _background.Color;                 // resolved Color
        _panel.color = _background.GetColorWithAlpha(0.5f); // with explicit alpha
    }
}
```

`ColorIDReference` exposes `.Color`, `.GetColorWithAlpha(alpha)`, `.IsValid()`, and implicit
conversions both from a `string` ID (`ColorIDReference r = "Warning";`) and to `Color`.

## Looking colors up directly — `IColorProvider`

When you need the palette API in code, resolve the **active** provider through the scheme service
rather than a static — the static `ColorModule.GetColor(...)` surface is obsolete:

```csharp
var scheme = RuntimeManager.GetService<IColorSchemeService>();
IColorProvider palette = scheme.ActiveScheme;

Color warn = palette.GetColor("Warning");
Color deep = palette.GetColor("Brand", "Primary");   // swatch + id
bool has   = palette.HasColor("Accent");
string[] ids = palette.GetAllColorIds();              // "Swatch.ColorId" form
```

## Re-theming — `ColorSchemeManager` / `IColorSchemeService`

A **scheme** is just a `ColorModule`; switching schemes is how you re-theme the whole app (e.g.
Light/Dark). The `ColorSchemeManager` is a `RuntimeSubsystem` that holds an ordered array of
`ColorModule` schemes and makes one active.

| | |
|---|---|
| Base class | `RuntimeSubsystem`, implements `IColorSchemeService` |
| Resolve via | `RuntimeManager.GetService<IColorSchemeService>()` or `[Inject] IColorSchemeService` |
| Configure | assign the `ColorModule[]` schemes + default index on the subsystem |

Switching a scheme sets it as the active `ColorModule` and raises `SchemeChanged`, which every live
`ColorID` component listens for — so the swap propagates without you touching individual objects:

```csharp
public class ThemeToggle : MonoBehaviour
{
    [SerializeField] private Button _button;

    private async void Start()
    {
        await RuntimeManager.WaitForInitialization();
        if (this == null) return;

        var schemes = RuntimeManager.GetService<IColorSchemeService>();
        _button.onClick.AddListener(() => schemes.ToggleScheme()); // cycle Light/Dark
    }
}
```

`IColorSchemeService` also offers `SetScheme(index/name, save)`, `NextScheme`/`PreviousScheme`,
`ActiveScheme`, `SchemeNames`, `SchemeCount`, and `RefreshAllColorIDs()` (force every `ColorID` to
reapply, e.g. after a scene load). The `save` flag persists the choice across sessions.

## The initialization-order rule

Resolving a Color ID reaches through `ColorModule` to the active palette, which is only reliable
once `GlobalSettings` and the runtime have bootstrapped. **Do not read a resolved color before
initialization.** Concretely, never read `ColorIDReference.Color` or `GetColorWithAlpha(...)` inside
`Awake`, `OnEnable`, or `OnValidate` — those run before bootstrap and may resolve against an
uninitialized (or fallback) palette. Read them after `await RuntimeManager.WaitForInitialization()`
(as in the examples above), and re-check `this == null` after the await since the object may have
been destroyed meanwhile.

The Doctor check **`color-id-reference-early-access`** enforces this: it scans runtime scripts and
raises a *Warning* when a `ColorIDReference` color is read inside `Awake`/`OnEnable`/`OnValidate`.
See [Doctor Checks](DOCTOR_CHECKS.md).

## See also

- [Color ID Migration Guide (V1 → V2)](COLOR_ID_MIGRATION.md) — the upgrade path, what is deprecated and
  what replaces it, and the evidence the alias-map removal gate requires.
- [Molca UI Tokens](UI_TOKENS.md)
- [Modals](MODALS.md)
- [UI Intent Spec → uGUI](UI_INTENT_SPEC.md)
- [Settings](SETTINGS.md)
- [Doctor Checks](DOCTOR_CHECKS.md)
