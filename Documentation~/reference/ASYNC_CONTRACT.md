---
title: Async Contract
category: Runtime & Core
order: 130
---

# Async Contract

Molca Core and every SDK layer share **one** async convention. Follow it and your code composes
cleanly with subsystem initialization, teardown, and the framework's cancellation model. This guide
covers the rules, the lifetime tokens you bind work to, and the two pipelines that cross real
thread boundaries.

## Return types

Async APIs return Unity's **`Awaitable`** / **`Awaitable<T>`** — never `Task` (allocation and
scheduler mismatch with Unity's player loop) and never `async void`.

```csharp
// Right — awaitable, cancellable, name ends in "Async".
public async Awaitable<UserProfile> FetchProfileAsync(CancellationToken ct = default)
{
    var response = await _http.GetAsync(_profileRequest, ct);
    ct.ThrowIfCancellationRequested();
    return response.Deserialize<UserProfile>();
}
```

`async void` is allowed **only** as a Unity event-handler entry point — `Start`, `Awake`,
`OnEnable`, UI button callbacks, `[RuntimeInitializeOnLoadMethod]`. Such a method must be a thin
shim: wrap the awaited work in `try/catch` so an exception can never escape into Unity's
synchronization context unobserved.

```csharp
private async void Start() // event-handler entry point — the one place async void is OK
{
    try
    {
        await InitializeAsync(destroyCancellationToken);
    }
    catch (OperationCanceledException) { /* destroyed mid-init — exit quietly */ }
    catch (Exception e) { Debug.LogException(e); }
}
```

## Cancellation tokens

Long-running or cancellable work takes a **`CancellationToken` as the last parameter, defaulting to
`default`**. Thread the token through *every* `await` in the chain, and check it after every resume
point that may outlive the caller.

Bind work to a token whose lifetime matches the work's owner:

| Scope | Token to use | Cancelled when |
|---|---|---|
| MonoBehaviour-scoped | `destroyCancellationToken` | the component is destroyed |
| Subsystem-scoped | `RuntimeSubsystem.ShutdownToken` | `Shutdown()` / `Teardown()` runs |
| Bootstrap-scoped | the token passed into `InitializeAsync(CancellationToken)` | bootstrap is torn down, or the per-subsystem init timeout elapses |

`ShutdownToken` is exposed by `RuntimeSubsystem` (`Runtime/Runtime/RuntimeSubsystem.cs`). It stays
cancelled after shutdown and is never reset — key any background loop your subsystem starts on it so
teardown unwinds cleanly.

## Subsystem initialization

`RuntimeSubsystem` (base class in `Packages/com.molca.core/Runtime/Runtime/`, registered on a
GameObject the `RuntimeManager` owns) exposes two initialization paths. **Override exactly one.**

```csharp
// Assets/YourProject/Scripts/Subsystems/AnalyticsSubsystem.cs
public class AnalyticsSubsystem : RuntimeSubsystem
{
    /// <summary>Preferred: async overload with the bootstrap-lifetime token.</summary>
    public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
    {
        await LoadConfigAsync(cancellationToken);
        // Start a background loop keyed on the subsystem's own lifetime.
        _ = HeartbeatLoopAsync(ShutdownToken);
    }
}
```

The **legacy callback form** still works and is never removed (protected-zone rule) — existing
subsystems and SDK-fork subclasses compile unchanged:

```csharp
public override void Initialize(Action<IRuntimeSubsystem> finishCallback)
{
    finishCallback?.Invoke(this); // bootstrap blocks until this is called
}
```

The base `InitializeAsync` bridges to `Initialize(finishCallback)`, so `RuntimeManager` drives every
subsystem through the async path uniformly. A subsystem that overrides `InitializeAsync` must not
also depend on its `Initialize(finishCallback)` running — the runtime calls only `InitializeAsync`.

`Shutdown()` cancels `ShutdownToken` *before* `Teardown()` runs, so any loop or pending await keyed
on that token unwinds first.

## Fire-and-forget

Fire-and-forget is opt-in and **visible**. Discard explicitly with `_ =` only when the callee owns
its own exceptions (logs internally) and honors a lifetime token. Never discard an awaitable that can
fault silently.

```csharp
_ = HeartbeatLoopAsync(ShutdownToken); // explicit discard — loop logs its own errors, honors the token
```

A worker loop uses `async Awaitable` (not `async void`) and exits when its token cancels:

```csharp
private async Awaitable HeartbeatLoopAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            await SendHeartbeatAsync(ct);
            await Awaitable.WaitForSecondsAsync(30f, ct);
        }
    }
    catch (OperationCanceledException) { /* cancel is not an error */ }
    catch (Exception e) { Debug.LogException(e); }
}
```

## Post-await destroy checks

After any `await` in a MonoBehaviour, the object may have been destroyed while you were suspended.
Re-check `this == null` (Unity's fake-null) or — preferably — rely on `destroyCancellationToken`
before touching Unity objects.

```csharp
await FetchProfileAsync(destroyCancellationToken);
if (this == null) return;         // fake-null guard after the await
_label.text = "Welcome back";     // safe to touch Unity objects now
```

## Cancellation is not an error

`OperationCanceledException` means "the owner went away", not "something failed". Catch it separately
and exit quietly — never log it as an error.

```csharp
try
{
    await LoadDataAsync(ShutdownToken);
}
catch (OperationCanceledException) { return; }   // quiet exit
catch (Exception e) { Debug.LogException(e); }    // real failures only
```

## Quick reference — wrong → right

| Wrong | Right |
|---|---|
| `public async void FetchData()` | `public async Awaitable FetchDataAsync(CancellationToken ct = default)` |
| `async void` worker loop | `async Awaitable` loop on `ShutdownToken` / `destroyCancellationToken`, started with an explicit `_ =` discard |
| `while (!op.IsDone) await NextFrame();` with no exit | same loop with `ct.ThrowIfCancellationRequested()` or a token-aware wait |
| Logging `OperationCanceledException` as an error | catch it separately, return quietly |
| Touching Unity objects straight after an `await` | guard with `this == null` / `destroyCancellationToken` first |

## Threading contract

Two pipelines cross real OS-thread boundaries, not just async continuations. Each has one decided
contract, so you don't re-derive it per call site.

### Data-provider pipeline

**Providers marshal onto the main thread at their own boundary.** Every collaborator downstream of
that boundary — `DataPoolFlusher`, `DataCache`, `DataProviderRegistry`, `DataSubscriptionHub` — may
assume it is always called from the main thread.

- `SocketIODataProvider` registers mapped events via `_socket.OnUnityThread(...)`.
- `WebSocketDataProvider` dispatches messages only inside its `PumpLoopAsync` `Awaitable` loop on the
  main thread.
- `SSEProvider` / `HttpDataProvider` poll from an `Awaitable` loop — already main-thread by
  construction.

Collaborator locks (such as `DataPoolFlusher._lock`) remain as a defense against a future provider
that doesn't honor the boundary, but **no code downstream of the boundary may call a
main-thread-only Unity API (`Time.*`, most `UnityEngine.Object` members) while holding one of those
locks** — read the value once beforehand instead. If you write a custom data provider, marshal to
the main thread at your dispatch boundary and everything downstream stays simple.

### Log pipeline

**The log write path can be entered from any thread and must never call a main-thread-only Unity
API.** Unity invokes `ILogHandler.LogFormat` / `LogException` on whatever thread called `Debug.Log`,
including background and network threads (third-party libraries and worker threads elsewhere in a
project log directly). Consequences enforced in `LogHandler` / `LogManager`:

- `LogHandler`'s re-entrancy guard (`_isLogging`) is `[ThreadStatic]`, so one thread's guard never
  gates or releases another thread's unrelated log call.
- `LogManager`'s flush-interval clock uses a `System.Diagnostics.Stopwatch`, never
  `Time.realtimeSinceStartup` (the Unity API throws off the main thread).
- `File.AppendAllText` and friends are plain BCL I/O, safe from any thread.

If you extend logging, keep the write path free of Unity main-thread APIs and use a `Stopwatch` for
any timing.

## See also

- [Subsystems](SUBSYSTEMS.md)
- [Runtime Manager](RUNTIME_MANAGER.md)
- [Data Providers](DATA_PROVIDERS.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Events](EVENTS.md)
