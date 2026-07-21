---
title: Runtime Subsystems
category: Runtime & Core
order: 110
---

# Runtime Subsystems

A **`RuntimeSubsystem`** is the framework's unit of application-wide service: a manager that exists
once, is brought up during bootstrap, stays available for the whole session, and is torn down in an
orderly way at shutdown. Reach for one whenever you'd otherwise be tempted by a static singleton or a
`FindObjectOfType` service locator — analytics, save/load, a domain state machine, a connection to an
external system.

## Writing a subsystem

Subclass `RuntimeSubsystem` (namespace `Molca`; it derives from `MonoBehaviour`). Custom subsystems
live in **`Assets/YourProject/Scripts/Subsystems/`** and are named `[Feature]Subsystem`.

```csharp
using System.Threading;
using Molca;
using UnityEngine;

/// <summary>Collects and forwards gameplay analytics for the session.</summary>
public class AnalyticsSubsystem : RuntimeSubsystem
{
    /// <summary>Records a named analytics event. Safe to call after bootstrap completes.</summary>
    public void Track(string name) { /* ... */ }
}
```

That's the whole minimum: no code registration, no base call in a constructor. The base class already
implements the full lifecycle contract; you override only the pieces you need.

## Registration — attach, don't instantiate

`RuntimeManager` discovers subsystems by scanning its own children at startup
(`GetComponentsInChildren<RuntimeSubsystem>()`, enabled components only). So you register a subsystem
by **adding its component as a child object under the RuntimeManager prefab** in the Editor — nothing
more. Never call `new MySubsystem()` or use `RegisterService<T>()`; those bypass the lifecycle.

For a subsystem that must live outside that hierarchy (spawned later, or owned by another object),
register it explicitly:

```csharp
// Awaits WaitForInitialization internally, then drives the subsystem's own init.
await RuntimeManager.RegisterSubsystem(mySubsystem);
// ... later:
RuntimeManager.DeregisterSubsystem(mySubsystem); // calls Shutdown() if the manager is ready
```

## Accessing a subsystem

Bootstrap must be finished before any subsystem is guaranteed active, so **await
`RuntimeManager.WaitForInitialization()` first** (typically in `Start`):

```csharp
private async void Start()
{
    try
    {
        await RuntimeManager.WaitForInitialization(destroyCancellationToken);
        var analytics = RuntimeManager.GetSubsystem<AnalyticsSubsystem>();
        analytics.Track("scene-entered");
    }
    catch (System.OperationCanceledException) { /* destroyed before ready — exit quietly */ }
}
```

Inside another subsystem or an injectable object, prefer `[Inject]` — registered subsystems resolve
through the DI container by concrete type and interfaces:

```csharp
[Inject] private AnalyticsSubsystem _analytics;
```

## Lifecycle

```
InitializeAsync(ct)  →  MarkActive()  →  [running]  →  Shutdown()  →  Teardown()
```

| Stage | What it is |
|---|---|
| `InitializeAsync(CancellationToken)` | The path `RuntimeManager` actually drives. One-time async setup. |
| `Initialize(Action<IRuntimeSubsystem>)` | Legacy callback form the base bridges to (see below). |
| `MarkActive()` | Called by the manager once **all** subsystems finish init; sets `IsActive`. Not overridable. |
| `Shutdown()` | Cancels `ShutdownToken`, then calls `Teardown()`. Invoked in reverse init order. |
| `Teardown()` | Your cleanup override — unregister listeners, release resources. |

### `InitializeAsync` vs legacy `Initialize`

Override **exactly one** of the two. New code should override the async form — it receives a
bootstrap-lifetime `CancellationToken` you thread through every awaited call:

```csharp
public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
{
    await LoadConfigAsync(cancellationToken);
}
```

The legacy callback form still works unchanged; the base `InitializeAsync` bridges to it, so existing
and SDK-fork subsystems keep compiling. You **must** invoke `finishCallback` — bootstrap blocks on it:

```csharp
public override void Initialize(System.Action<IRuntimeSubsystem> finishCallback)
{
    // synchronous setup...
    finishCallback?.Invoke(this);
}
```

Each subsystem's init is bounded by a timeout (20 seconds); if it elapses, the manager cancels the
token, marks that subsystem's init failed, and lets bootstrap continue so the app never soft-locks.

### `ShutdownToken` and teardown

Any background loop or pending await a subsystem starts should be keyed on `ShutdownToken`. `Shutdown()`
cancels it **before** `Teardown()` runs, so in-flight work unwinds cleanly. The token stays cancelled
afterward; it is never reset.

```csharp
public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
{
    _ = PumpLoopAsync(ShutdownToken); // fire-and-forget: owns its exceptions, honors the token
}

private async Awaitable PumpLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested) { /* ... */ await Awaitable.NextFrameAsync(ct); }
}

/// <inheritdoc/>
public override void Teardown()
{
    // release resources, unregister listeners
    base.Teardown(); // always call base — it clears IsActive
}
```

## Controlling init order

| Mechanism | Purpose |
|---|---|
| `[DependsOn(typeof(OtherSubsystem))]` | Declares a hard ordering — the dependency initializes first. Preferred when your `Initialize`/`InitializeAsync` touches another subsystem. Cycles are detected and logged. |
| `InitializationPriority` (Inspector, `int`) | Tiebreaker for subsystems unrelated in the dependency graph. **Higher number initializes earlier.** Core uses 0–100; use 200+ for project subsystems. |

## Runtime mode

The Inspector `RuntimeMode` flag (`Editor`, `Runtime`, or both) gates where a subsystem runs.
`IsActive` returns `false` when the current execution context doesn't match the authored mode, so a
subsystem can be present but inert in, say, standalone builds.

## See also

- [Runtime Manager](RUNTIME_MANAGER.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Async Contract](ASYNC_CONTRACT.md)
- [Settings](SETTINGS.md)
