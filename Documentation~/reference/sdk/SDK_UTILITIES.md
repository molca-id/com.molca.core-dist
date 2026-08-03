---
title: Utilities
category: Utilities
order: 310
---

# Utilities

Scene-level helper components the SDK adds for common app behaviors — a QR scanner, timed events,
input helpers, and Transform/toggle conveniences. These complement Core's lower-level
[Utilities](UTILITIES.md); where Core's are static/data helpers, these are MonoBehaviours you attach in
a scene.

## QRScanner

`QRScanner` is a MonoBehaviour that decodes QR/barcodes from the camera feed using **ZXing**. It raises
a `UnityEvent<string>` when a code is read:

```csharp
[SerializeField] private QRScanner _scanner;

void OnEnable() => _scanner.onScanned.AddListener(HandleCode);
void HandleCode(string payload) { /* navigate, look up, etc. */ }
```

> Depends on the third-party **ZXing** library, bundled with the SDK. If you strip the SDK down, keep
> ZXing when you keep `QRScanner`.

## Component helpers

| Component | Purpose |
|---|---|
| `DelayedEvent` | Fire a `UnityEvent` after a delay. |
| `IntCounter` | Simple counter with change events. |
| `ProximityTrigger` | Fire events when a target enters/leaves range. |
| `SimpleToggle`, `ToggleGO`, `ToggleColor`, `BooleanColor` | Toggle GameObjects/colors from a bool. |
| `InputHelper` | Input convenience wrappers. |
| `TransformHelper`, `RectTransformHelper`, `RectTransformInterpolate`, `RotateUtil` | Transform/RectTransform manipulation and tweening. |
| `OutlineGroupHelper` | Manage a group of outline effects. |
| `VersionText` | Displays the app version (pairs with the [Build System](BUILD_SYSTEM.md)). |

Each is a small MonoBehaviour meant to be wired in the Inspector; subclass one to extend it rather than
editing the package.

## See also

- [Utilities](UTILITIES.md)
- [Media](SDK_MEDIA.md)
- [SDK Overview](SDK_OVERVIEW.md)
- [Build System & Versioning](BUILD_SYSTEM.md)
