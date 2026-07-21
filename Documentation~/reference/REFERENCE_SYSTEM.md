---
title: Scene Reference System
category: Scene & References
order: 200
---

# Scene Reference System

Scene objects find each other by a **string Ref Id** rather than a direct serialized Unity
reference. This decouples wiring across prefab and additive-scene boundaries, where a plain
`[SerializeField] MyComponent` link would break or serialize as null. You tag a target with a Ref
Id, store a `SceneObjectReference` on the referencing object, and call `Resolve<T>()` at runtime.

## Key types

All live in the `Molca.ReferenceSystem` namespace.

| Type | Kind | Role |
|---|---|---|
| `IReferenceable` | interface | Contract for anything resolvable by id: `RefId`, `RefType`, `DisplayName`. |
| `ReferenceableComponent` | `MonoBehaviour` | Drop-in component that makes any GameObject referenceable. |
| `SceneObjectReference` | serializable struct | Serialized field on the *referencing* object; holds a Ref Id + Ref Type and resolves it. |
| `SceneObjectReference<T>` | serializable struct | Type-constrained variant; the Inspector picker only shows `T`, and `Resolve()` needs no type argument. |
| `ReferenceManager` | `RuntimeSubsystem` | The runtime registry of every live `IReferenceable`. |
| `ReferenceId` | readonly struct | Value key pairing an id string with a Ref Type. |
| `ReferenceGenerator` | static | Generates collision-safe unique ids. |

## Setup in the Inspector

**On the target GameObject** — add a `ReferenceableComponent`
(*Add Component → Molca → Reference System → Referenceable*, folder
`Packages/com.molca.core/Runtime/ReferenceSystem/`). It exposes:

- **Ref Id** — read-only in the Inspector; auto-generated on `OnValidate` (a GUID-based id such as
  `ref_referenceable_a1b2…`). You can assign a stable, human-readable id in code or via tooling —
  the convention is **kebab-case**, e.g. `"main-valve"`, `"control-panel"`.
- **Ref Type** — a category string used for grouped lookups; defaults to `"Referenceable"`.
- **Display Name** — optional; falls back to the GameObject name.

**On the referencing object** — declare a `SceneObjectReference` (or `SceneObjectReference<T>`)
serialized field and pick the target in the Inspector:

```csharp
[SerializeField] private SceneObjectReference _valveRef;
[SerializeField] private SceneObjectReference<ValveInteraction> _typedValveRef;
```

Each prefab *placement* gets its own fresh id: when a prefab instance is detected still carrying its
source asset's id, `OnValidate` regenerates it, so placing a referenceable prefab N times never
shares one id.

## Resolving at runtime

`Resolve<T>()` looks the target up through `ReferenceManager`. `T` must be a reference type that
implements `IReferenceable`. Resolution is valid once `RuntimeManager` initialization has completed
and the target's scene/prefab is loaded and enabled.

```csharp
public class OpenValveStep : Step
{
    [SerializeField] private SceneObjectReference _valveRef;

    private ValveInteraction _valve;

    protected override void OnStepActivated()
    {
        // Returns null (and logs a warning) if not registered or the type doesn't match.
        _valve = _valveRef.Resolve<ValveInteraction>();
    }
}
```

### Optional, required, and typed variants

```csharp
// Optional — no log noise on a deliberately-empty reference; test the result.
if (_valveRef.TryResolve<ValveInteraction>(out var valve))
    valve.Open();

// Required — throws ReferenceResolutionException (carrying the call site) on failure.
var valve = _valveRef.Resolve<ValveInteraction>(required: true);

// Typed field — no type argument needed at the call site.
var valve = _typedValveRef.Resolve();
```

### Async resolution (recommended from `Awake`/`Start`)

`ResolveAsync` awaits `RuntimeManager` initialization and then waits for the target to register —
bounded by a timeout and cancellation token — instead of racing a single frame:

```csharp
private async void Awake()
{
    // Waits up to DefaultResolveTimeoutSeconds (5s) for the target to register.
    _valve = await _valveRef.ResolveAsync<ValveInteraction>(
        cancellationToken: destroyCancellationToken);
}
```

Pass `required: true` to throw on timeout instead of returning null, and supply a lifetime token so
the wait unwinds if the caller is destroyed.

## Direct `ReferenceManager` access

Resolve the subsystem via `[Inject]` or `RuntimeManager.GetSubsystem<ReferenceManager>()` — never a
static singleton. Its lookups return `IReferenceable`; cast to the concrete type yourself.

```csharp
[Inject] private ReferenceManager _references;

// By type + id.
var valve = _references.Get("Referenceable", "main-valve") as ValveInteraction;

// By ReferenceId value (note: ctor is (id, type)).
var valve = _references.Get(new ReferenceId("main-valve", "Referenceable")) as ValveInteraction;

// Try form — no null-cast dance.
if (_references.TryGet("Referenceable", "main-valve", out var referenceable))
    (referenceable as ValveInteraction)?.Open();
```

Useful queries: `GetAllOfType(type)`, `GetAllTypes()`, `IsRegistered(...)`, `Count`, and the
`Registered` / `Unregistered` events for reacting to late registration.

## Authoring your own referenceable

For most cases `ReferenceableComponent` is enough. When a component *is* the interactive object,
implement `IReferenceable` directly (folder `Assets/YourProject/Scripts/`, base class
`MonoBehaviour`) and register with the subsystem once `RuntimeManager` is ready:

```csharp
public class ValveInteraction : MonoBehaviour, IReferenceable
{
    [SerializeField, ReadOnly] private string refId;

    public string RefId { get => refId; set => refId = value; }
    public string RefType => "Referenceable";
    public string DisplayName => gameObject.name;

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(refId))
            refId = ReferenceGenerator.GenerateUniqueId(RefType);
    }

    private async void OnEnable()
    {
        await RuntimeManager.WaitForInitialization();
        if (this == null || !isActiveAndEnabled) return;   // Unity fake-null / disabled while awaiting
        RuntimeManager.GetSubsystem<ReferenceManager>()?.Register(this);
    }

    private void OnDisable() =>
        RuntimeManager.GetSubsystem<ReferenceManager>()?.Unregister(this);
}
```

`SequenceController` and `Step` are already referenceable, so their Ref Ids are resolvable the same
way. Only loaded scene MonoBehaviours live in the runtime registry — ScriptableObjects are not
runtime-resolvable through this system.

## Behavior worth knowing

- **Type-first with id fallback.** A resolve looks up `(RefType, RefId)` first, then falls back to
  id-only across all types — so a reference survives a Ref Type rename (it logs a nudge to re-save
  the field). An id that is ambiguous across multiple types fails rather than guessing.
- **Not found returns null.** `Resolve<T>()` and `TryResolve<T>()` never throw; only the
  `required: true` overloads raise `ReferenceResolutionException`. A wrong-type resolve logs an
  error.
- **Self-healing on destroy.** If a referenced object was destroyed without unregistering, the
  resolve path purges the dead entry and reports not-found rather than handing back a fake-null
  object.
- **Duplicate ids.** Two live objects sharing a `(RefType, RefId)` is a conflict: the first
  registration wins and the second is rejected with an error naming both — keep ids unique within a
  Ref Type.
- **Registration lifecycle.** `ReferenceableComponent` registers in `OnEnable` (after
  `RuntimeManager` initialization) and unregisters in `OnDisable`.

## See also

- [Sequences](SEQUENCES.md)
- [Subsystems](SUBSYSTEMS.md)
- [Events](EVENTS.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Async Contract](ASYNC_CONTRACT.md)
