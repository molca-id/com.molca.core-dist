---
title: Events
category: Runtime & Core
order: 140
---

# Events

Molca's publish/subscribe layer is the **`EventDispatcher`** — a Core subsystem that decouples the
code that raises an event from the code that reacts to it. Events are addressed by a **string name**
plus an optional **typed data payload**; there is no `EventBus.Raise()` and no `readonly struct` event
model — those do not exist in this framework.

## Getting the dispatcher

`EventDispatcher` (`Molca.Events`) is a [`RuntimeSubsystem`](SUBSYSTEMS.md) living on the RuntimeManager
prefab and discovered automatically — you never construct it. Obtain it by injection or service lookup:

```csharp
using Molca.Events;

[Inject] private EventDispatcher _events;                     // preferred
// or:
var events = RuntimeManager.GetService<EventDispatcher>();
```

Because it is a subsystem, only touch it after the runtime is up — `await RuntimeManager.WaitForInitialization()`
in `Start()` before your first dispatch. Calling the raw API on an instance that has not been resolved
logs an error and no-ops rather than throwing.

## Raw API — `EventDispatcher`

Three verbs, each in a parameterless and a generic `<T>` form:

| Method | Purpose |
|---|---|
| `RegisterEvent(name, Action)` / `RegisterEvent<T>(name, Action<T>)` | Subscribe a handler. |
| `UnregisterEvent(name, Action)` / `UnregisterEvent<T>(name, Action<T>)` | Remove a handler. |
| `DispatchEvent(name)` / `DispatchEvent<T>(name, data)` | Raise the event for all current subscribers. |
| `ClearEvent(name)` | Drop every handler for one event. |
| `ClearAllEvents()` | Drop every handler (also run on `Shutdown`). |

```csharp
// Parameterless
_events.RegisterEvent("GameStarted", OnGameStarted);
_events.DispatchEvent("GameStarted");
_events.UnregisterEvent("GameStarted", OnGameStarted);

// Typed payload — the handler's T must match the dispatched payload's type (or a base type)
_events.RegisterEvent<SceneLoadEventData>(EventConstants.Scene.LoadCompleted, OnSceneLoaded);
_events.DispatchEvent(EventConstants.Scene.LoadCompleted, new SceneLoadEventData("Main", isAdditive: false));

private void OnSceneLoaded(SceneLoadEventData data) { /* ... */ }
```

### Dispatch semantics

- **Handlers are isolated.** Each subscriber is invoked inside its own `try/catch`, so one throwing
  handler never blocks the rest; the exception is logged, not propagated to the dispatcher.
- **Snapshot iteration.** Dispatch walks a snapshot taken at registration time, so a handler may safely
  register or unregister other handlers (or itself) mid-dispatch.
- **Polymorphic delivery.** A `<T>` dispatch reaches handlers registered under `T` *or any base type of
  `T`* (including `object`, which acts as a catch-all). Registering under a *derived* type does **not**
  receive base-type dispatches.
- **Duplicate guard.** In editor and development builds, registering the same callback twice for one
  event logs a warning — the classic `OnEnable`-without-`OnDisable` double-subscribe bug.

## Typed API — `TypedEvents` (preferred)

`TypedEvents` wraps a name once in a `TypedEvents.Event` (parameterless) or `TypedEvents.Event<T>`
(typed) object exposing `Register` / `Unregister` / `Dispatch`. Prefer these over raw strings: the
name and payload type are bound in one place, so call sites get compile-time safety and no literals.

```csharp
// Core lifecycle events are predefined:
TypedEvents.ApplicationInitialized.Register(OnReady);   // Action
TypedEvents.ApplicationInitialized.Dispatch();
TypedEvents.ApplicationInitialized.Unregister(OnReady);

// Typed:
TypedEvents.SceneLoadCompleted.Register(OnSceneLoaded); // Action<SceneLoadEventData>
TypedEvents.SceneLoadCompleted.Dispatch(new SceneLoadEventData("Main", isAdditive: false));
```

Each wrapper resolves the dispatcher internally via `RuntimeManager.GetService<EventDispatcher>()`, so
it is safe to use once the runtime is initialized; if the dispatcher is not yet up the call logs and
no-ops. Predefined groups include `ApplicationInitialized` / `ApplicationPausing` /
`ApplicationResuming` / `ApplicationQuitting`, plus Scene, UI, Audio, Network, Input, and
ContentPackage events split across `TypedEvents.<Domain>.cs` partial files.

## Event names — `EventConstants`

`EventConstants` centralizes every name as a `const string` inside a nested static class, so a rename
is one edit and typos surface at compile time. The string literal mirrors the nested path:

```csharp
public static class Scene
{
    public const string LoadCompleted = "Scene.LoadCompleted";  // path == "Scene.LoadCompleted"
}
```

Reference the constant (`EventConstants.Scene.LoadCompleted`), never the raw literal — a string-keyed
dispatch has no "unknown event" diagnostic, so a mistyped name simply never fires. Groups shipped by
Core include `Application`, `Scene`, `Performance`, `UI`, `Network`, `Input`, `Sequence`, `Audio`, and
`ContentPackage`.

## Payloads — `EventData`

Typed payloads derive from the abstract `EventData` base, which stamps a `Timestamp` at construction.
Follow the `[Event]EventData` naming convention (e.g. `SceneLoadEventData`, `PackageProgressEventData`).
Payloads carry state through the event — never mutate a ScriptableObject to smuggle data between
publisher and subscriber.

```csharp
// Placement: your layer's event payloads live beside the code that dispatches them,
// e.g. Assets/YourProject/Scripts/Events/.
public sealed class ScorePostedEventData : EventData
{
    public int Score { get; }
    public ScorePostedEventData(int score) => Score = score;
}
```

Not every payload must be an `EventData` subclass — the generic API accepts any `T` (Core dispatches
`string`, `float`, and `Step` for some events) — but a dedicated `EventData` subclass is the convention
for anything carrying more than a single primitive.

## Auto-cleanup for listeners

A handler that outlives its owner is a leak (and, worse, fires against a destroyed object). For a
MonoBehaviour subscriber, subclass **`EventListenerBehaviour`** and register through the tracked
extension methods instead of hand-pairing every `Register` with an `Unregister`:

```csharp
// Placement: Assets/YourProject/Scripts/  ·  Base class: EventListenerBehaviour
public sealed class HudController : EventListenerBehaviour
{
    public override void RegisterEvents()
    {
        this.Register(TypedEvents.SceneLoadCompleted, OnSceneLoaded);   // tracked
    }

    public override void UnregisterEvents()
    {
        this.Unregister(TypedEvents.SceneLoadCompleted, OnSceneLoaded);
    }

    private void OnSceneLoaded(SceneLoadEventData data) { /* update HUD */ }
}
```

`EventListenerBehaviour` calls `RegisterEvents()` in `OnEnable`, `UnregisterEvents()` in `OnDisable`,
and `UnregisterAll()` in `OnDestroy`. The `this.Register(...)` / `this.Unregister(...)` extension
methods (`EventListenerExtensions`) track each subscription against the listener in a weak table, so
every registration is released automatically when the behaviour is destroyed — no leaked handlers even
if a matching unregister is missed.

## Defining your own events

Core's `TypedEvents` / `EventConstants` are a protected zone; define new project or SDK events in your
own layer. Mirror the Core pattern: add a `const string` name (path mirrors the literal) and a
`static readonly Event`/`Event<T>` field bound to it. You may declare them in your own static classes —
extending Core's `partial` types is unnecessary and only appropriate inside Core itself.

## Debugging an event flow

Trace it end to end: **`DispatchEvent(name[, data])` → the matching `RegisterEvent` handler → the state
change → the view update.** A handler that never fires is almost always a *name* mismatch (use
`EventConstants`, not a literal) or a *payload-type* mismatch — `DispatchEvent<T>` reaches a handler
only when its registered type is `T` or a base of `T`.

## See also

- [Subsystems](SUBSYSTEMS.md)
- [Runtime Manager](RUNTIME_MANAGER.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Sequences](SEQUENCES.md)
