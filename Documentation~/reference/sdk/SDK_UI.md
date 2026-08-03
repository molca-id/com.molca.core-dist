---
title: UI
category: UI
order: 220
---

# UI

The SDK ships a library of reusable uGUI widgets — the everyday building blocks of an app screen —
bound to Core's presentation systems: [ColorID theming](COLOR_ID.md), [Localization](LOCALIZATION.md),
and the [UI tokens](UI_TOKENS.md). Drop them onto Canvas objects and wire them in the Inspector; they
re-theme and re-localize with the rest of the app because they resolve through those Core mechanisms
rather than storing raw appearance.

## Widget catalog

| Widget(s) | Purpose |
|---|---|
| `ColorIDButton`, `ColorIDButtonGroup` | Buttons whose appearance is driven by a `ColorID` swatch. |
| `ButtonState`, `ButtonStateGroup` | Multi-state button visuals / exclusive groups. |
| `ColorSchemeDropdown`, `ColorSchemeToggle` | Let users switch the active color scheme. |
| `LanguageDropdown` | Locale selection wired to the `LocalizationManager`. |
| `ProgressBarUI` | Determinate progress display. |
| `PasswordField`, `CodeInputPanel` | Masked entry and PIN/code input. |
| `MediaPreviewUI`, `MediaInfoCycle` | Show/cycle media items (see [Media](SDK_MEDIA.md)). |
| `PanelCycle` | Step through a set of panels (wizards, carousels). |
| `ProjectLogoUI`, `VersionText`, `TimeText`, `BatteryStatus` | Chrome bound to project settings / device state. |
| `CanvasScaleListener` | Reacts to the canvas-scale setting module. |
| `BillboardUI` | Keeps world-space UI facing the camera. |
| `UIReference` | Inspector-friendly handle for referencing UI elements. |

## Extending

These are ordinary MonoBehaviours — subclass one to specialize it, or compose several on a prefab. Keep
appearance flowing through the Core systems: reference a `ColorID` for color, a `LocalizedText` style
for type, and [UI tokens](UI_TOKENS.md) for semantic naming, rather than hardcoding hex or strings, so
your screens stay themeable and localizable.

## See also

- [Molca UI Tokens](UI_TOKENS.md)
- [Color ID Theming](COLOR_ID.md)
- [Localization](LOCALIZATION.md)
- [SDK Modals](SDK_MODALS.md)
- [Media](SDK_MEDIA.md)
