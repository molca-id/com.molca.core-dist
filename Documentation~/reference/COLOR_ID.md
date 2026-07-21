---
title: Color ID Theming
category: UI & Presentation
order: 510
---

# Color ID Theming

**Color ID** is the framework's semantic-color layer. Instead of hard-coding a `Color` on every
`Image`, `TMP_Text`, or `Renderer`, you name a color — a **swatch** plus a **color ID** — and the
value is resolved from a central palette. Swap the palette and every object that references it
re-themes at once, with no per-object edits.

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

- [Molca UI Tokens](UI_TOKENS.md)
- [Modals](MODALS.md)
- [Figma to uGUI](FIGMA_TO_UGUI.md)
- [Settings](SETTINGS.md)
- [Doctor Checks](DOCTOR_CHECKS.md)
