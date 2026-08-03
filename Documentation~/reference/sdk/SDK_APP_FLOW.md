---
title: App Flow
category: Getting Started
order: 30
---

# App Flow

Core's application layer gives a Molca app its shared startup shape: a **preload** phase that runs checks and splash
screens, an app-level **`GameManager`** subsystem, and **home** screens. Each piece is a normal Core
citizen — subsystems on the RuntimeManager prefab, MonoBehaviours in scenes — so it slots into the
[bootstrap sequence](RUNTIME_MANAGER.md) without special casing.

## GameManager

`GameManager` is a `RuntimeSubsystem` in the `Molca.App` namespace. Wire it on the project's
`RuntimeManager` prefab and resolve it like any subsystem — it holds no static state:

```csharp
var game = RuntimeManager.GetSubsystem<GameManager>();
```

Its shipped responsibility is app-level connection handling: after
`RuntimeManager.WaitForInitialization()` it subscribes to `IHttpClient.ConnectionError` and, when
enabled, surfaces a localized confirmation modal on backend failure. It is the natural place to hang
your own app-wide coordination — subclass it in project space.

## Preload

`PreloadCheck` is a MonoBehaviour that runs during app entry. It executes a list of **custom checks**
and then plays **splash screens** (with fade in/out and per-splash hold durations) before handing off
to the first real screen.

- **Custom checks** implement `IPreloadCheck` (`Awaitable RunCheck()`); assign them in the
  `PreloadCheck` inspector list. Shipped examples: `FirstLaunchCheck` (first-run detection) and
  `ShaderWarmupCheck` (warms a `ShaderVariantManifest` to avoid first-use hitches).

```csharp
public class LicenseCheck : MonoBehaviour, IPreloadCheck
{
    public async Awaitable RunCheck()
    {
        await ValidateLicenseAsync();   // gate startup on your own condition
    }
}
```

Because checks return `Awaitable`, the preload phase awaits each in turn — a check can block startup
until its work completes.

## Home

The `Home` area holds the post-login shell screens — `ProfileUI` (user profile) and `AudioSettingUI`
(audio settings bound to the [Settings](SETTINGS.md) modules) — as MonoBehaviours you place in your home
scene and extend.

## See also

- [SDK Overview](SDK_OVERVIEW.md)
- [Auth](SDK_AUTH.md)
- [Runtime Manager & Bootstrap](RUNTIME_MANAGER.md)
- [Runtime Subsystems](SUBSYSTEMS.md)
