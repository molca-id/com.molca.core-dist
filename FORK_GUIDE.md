# Molca Core — Consumer & Fork Guide

This guide is for developers building an SDK layer or project on top of Molca Core while
consuming it as a **read-only UPM package** (not editing the framework source). It covers
installing, where things live, how to extend Core the supported way, and how to migrate an
existing fork onto the package.

---

## 1. Installing

Add to your project's `Packages/manifest.json`:

```json
"com.molca.core": "https://github.com/molca-id/com.molca.core-dist.git#1.9.0"
```

**Always pin a version tag** (`#1.9.0`). A bare URL tracks the dist repo's default branch and
will float to whatever is published next — non-reproducible builds. Available versions are
the git tags on the dist repo.

Core's registry dependencies (Addressables, Localization, Input System) resolve
automatically; see `dependencies` in `package.json`. Use the Unity version in `package.json`
(`unity` / `unityRelease`) or newer.

### Upgrading

Bump the tag in `manifest.json` (`#1.9.0` → `#1.10.0`) and let Package Manager refetch. Read
`CHANGELOG.md` for behavior changes first. Nothing is pushed to you — you adopt new Core
versions on your own schedule.

---

## 2. Where things live (important)

Core is immutable in your project (it lives under `Packages/`, read-only). Your editable
state lives in **your** `Assets/` and `ProjectSettings/`:

| Thing | Location | Notes |
|---|---|---|
| Project settings (`MolcaProjectSettings`) | `Assets/_Molca/Settings/MolcaProjectSettings.asset` | Cloned from the package's read-only default on first access. Set `projectName`, `runtimeManager`, etc. here. |
| Editor settings (`MolcaEditorSettings`) | `ProjectSettings/MolcaEditorSettings.asset` | Outside the AssetDatabase so it's writable even with an immutable package. Set `repositoryUrl` etc. via the Molca Hub. |
| Other editor settings (Integration/Notification/etc.) | `Assets/_Molca/Editor/` | Created on demand via `MolcaEditorSettingsAsset.GetOrCreate<T>`. |
| Your code, scenes, prefabs, scenario assets | `Assets/...` (e.g. `Assets/_MolcaSDK/[Layer]/`) | Never under `Packages/com.molca.core`. |

You should never need to edit anything inside `Packages/com.molca.core`. If you think you
do, see §5.

---

## 3. Extending Core (the supported way)

Core is extended by **subclassing and registration**, never by editing the package.

### A runtime subsystem

```csharp
using System.Threading;
using UnityEngine;
using Molca;

/// <summary>Example layer subsystem.</summary>
public class MyLayerSubsystem : RuntimeSubsystem   // RuntimeSubsystem : MonoBehaviour
{
    public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }
}
```

- Resolve it after bootstrap: `await RuntimeManager.WaitForInitialization();` then
  `RuntimeManager.GetSubsystem<MyLayerSubsystem>()`.
- Register services for DI with `RuntimeManager.RegisterService<IMyService>(instance)` and
  consume with `[Inject]` or `RuntimeManager.GetService<IMyService>()`.
- Long-running/cancellable work takes a `CancellationToken`; key background loops on
  `ShutdownToken`. Async APIs return `Awaitable`/`Awaitable<T>`, never `Task` or `async void`
  (except thin Unity event-handler shims).

### Layer bootstrap hooks

To run layer setup during bootstrap without subclassing the settings asset, add a
`BootstrapExtension` to `MolcaProjectSettings.bootstrapExtensions` (invoked in order, each
awaited, after the RuntimeManager prefab is instantiated and before `GlobalSettings`
initializes).

### Anti-patterns (Core rejects these by convention)

- `FindObjectOfType<T>()` for services → `RuntimeManager.GetSubsystem<T>()` / `[Inject]`
- Static singletons → `RuntimeSubsystem` or `RegisterService<T>()`
- `DontDestroyOnLoad` yourself → RuntimeManager owns persistence
- Cross-scene serialized references → `SceneObjectReference` + `ReferenceableComponent`
- Writing to ScriptableObjects at runtime → SOs are read-only config

---

## 4. Migrating an existing fork onto the package

If your layer is currently a *fork* that embeds `Packages/com.molca.core`:

1. **Audit divergence.** Confirm you never modified Core *source* (forks should only add
   subclasses/content). `git diff` your embedded Core against the canonical version. Any real
   code edits must be upstreamed into Core or reimplemented as subclasses first — they are
   lost on swap.
2. **Preserve in-package config.** If you edited settings assets inside Core (e.g.
   `projectName`, a custom `runtimeManager` prefab), move the live `MolcaProjectSettings`
   asset to `Assets/_Molca/Settings/` **with its `.meta`** so its GUID (and any Addressables
   entry) survives.
3. **Swap.** `git rm -r Packages/com.molca.core`, then add the pinned dependency (§1).
4. **Validate.** Compile, rebuild Addressables, enter Play mode, confirm `RuntimeManager`
   boots and your settings load from `Assets/`.
5. **Cut the fork.** Remove the `upstream` git remote; request fork-detach from your Git host
   so the repo becomes standalone (it now *depends on* Core rather than forking it).

---

## 5. Requesting a Core change

You cannot edit the package. If you need a new extension point, a `protected`/`virtual`
member, a new event, or a public API:

- Open a request with the Molca framework team describing the use case and the minimal
  surface you need (a hook, an overridable method, an exposed property).
- Core adds the extension point, publishes a new version, and you bump your tag.

This keeps every layer on the same Core and avoids the fork drift this package model exists
to prevent.

---

## 6. Debugging without Core source

- Public APIs ship with XML docs (IntelliSense shows them).
- To step into Core temporarily, you can clone the dist repo and reference it as a local
  path package, or embed it under `Packages/` for a debug session — but do not commit edits
  to it; treat it as read-only and revert to the pinned tag when done.
- Report reproducible Core bugs to the framework team with your Core version (the tag) and a
  minimal repro.
