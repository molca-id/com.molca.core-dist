# Molca Reference System

Resolve a **loaded scene `IReferenceable` MonoBehaviour by a string id**. That is the one
capability this system provides. The `ReferenceManager` is a `RuntimeSubsystem` holding a
registry of live scene objects; `SceneObjectReference` is the serializable handle you put on
a field to point at one of them across scenes.

## Scope & Boundaries

- **Runtime registry = live scene MonoBehaviours only.** Only objects that register
  themselves at runtime (`ReferenceableComponent`, `Step`, `SequenceController`, and your own
  `IReferenceable` MonoBehaviours) are resolvable through `ReferenceManager`.
- **ScriptableObjects are OUT.** `DataModel` / `DataMapping` / `DataProvider` (and any SO) are
  **not** runtime-resolvable through this system. Nothing registers a ScriptableObject into the
  runtime `ReferenceManager`. Their ids are data-identity only, not reference-system handles.
- **`ReferenceManagerSettings` id lists are an editor-time validation database, not a
  registry.** They record known ids per type/scene so editor tooling can flag missing or
  duplicate ids. They are never consulted to resolve a reference at runtime.
- **IDs are auto-generated, manually validated.** The implementing component generates its own
  id via `ReferenceGenerator.GenerateUniqueId` (in `OnValidate`/`Awake`); the settings asset
  and its editor tooling provide manual scan/validation over those ids.

## Core Components

### IReferenceable

The contract a scene object implements to be registerable and resolvable.

```csharp
public interface IReferenceable
{
    string RefId { get; set; }   // settable so id-generation/validation can assign it
    string RefType { get; }      // category for type-scoped lookups
    string DisplayName { get; }  // debugging / UI only
}
```

`IReferenceable<T>` is a thin generic wrapper exposing a typed `Self`. It carries no extra
contract beyond the non-generic interface.

### ReferenceManager

The `RuntimeSubsystem` that owns the runtime registry. Access it through `RuntimeManager` or
the `Instance` accessor (which resolves through `RuntimeManager`).

```csharp
var refManager = RuntimeManager.GetSubsystem<ReferenceManager>();
// or
var refManager = ReferenceManager.Instance;

// Registration (usually called by the object itself in OnEnable/Awake)
refManager.Register(myReferenceable);    // false on null/invalid id/conflict
refManager.Unregister(myReferenceable);

// Lookup
IReferenceable obj = refManager.Get(new ReferenceId("hero_001", "Player"));
IReferenceable obj = refManager.Get("Player", "hero_001");
bool ok = refManager.TryGet("Player", "hero_001", out var found);
bool ok = refManager.TryGetByRefIdOnly("hero_001", out var found); // when refType may be stale

// Queries
List<IReferenceable> all = refManager.GetAllOfType("Player");
List<string> types       = refManager.GetAllTypes();
int count                = refManager.Count;
```

`RegisterWithAutoId` is **`[Obsolete]`** — it cannot assign an id to the target and returns
`false` whenever generation would be needed. Assign the id yourself with
`ReferenceGenerator.GenerateUniqueId` and call `Register`.

### SceneObjectReference

A serializable struct you place on a field to reference a scene object by id. This is the
intended replacement for direct cross-scene serialized references.

```csharp
[SerializeField] private SceneObjectReference _targetRef;

async void Start()
{
    await RuntimeManager.WaitForInitialization();

    // Synchronous — the target must already be registered.
    var target = _targetRef.Resolve<ReferenceableComponent>();

    // Async — waits for RuntimeManager init (and one frame) before resolving.
    var target2 = await _targetRef.ResolveAsync<ReferenceableComponent>();

    if (_targetRef.TryResolve<ReferenceableComponent>(out var t)) { /* ... */ }
}
```

Resolve calls capture the synchronous call site (`[CallerMemberName]`/`FilePath`/`LineNumber`)
so resolve warnings name who initiated the resolve. Resolution returns `null` and logs a
warning when the manager is unavailable, the reference is unset, or the object is not
registered. `SceneObjectReference<T>` is a type-constrained variant whose Inspector picker only
shows objects of `T`; use its parameterless `Resolve()` / `ResolveAsync()` / `TryResolve(out T)`.

### ReferenceableComponent

A drop-in MonoBehaviour that makes any GameObject referenceable without a custom type — for
spawn points, checkpoints, triggers, etc. Auto-generates its id in `OnValidate`, registers in
`OnEnable` (after `WaitForInitialization`), and unregisters in `OnDisable`.

### ReferenceGenerator

Static, stateless id generator.

```csharp
string id      = ReferenceGenerator.GenerateUniqueId("Player");
string id2     = ReferenceGenerator.GenerateUniqueIdWithPrefix("custom_", "Player");
string shortId = ReferenceGenerator.GenerateShortUniqueId("Player");
ReferenceId rid = ReferenceGenerator.GenerateReferenceId("Player");
```

### ReferenceManagerSettings

A `SettingModule` (added to `GlobalSettings`) that holds the **editor-time validation
database** and a debug-logging toggle. It does not register or resolve runtime references.

```csharp
var settings = GlobalSettings.GetModule<ReferenceManagerSettings>();
Dictionary<string,int> stats          = settings.GetReferenceStats();
List<string> types                    = settings.GetReferenceTypes();
List<string> ids                      = settings.GetReferenceIds("Player");
Dictionary<string,List<string>> dupes = settings.FindDuplicateIds();
```

The id collections (`assetKnownIds`, `sceneKnownIds`) are populated by editor scans. Asset ids
are recorded for validation/visibility only and are **not resolvable at runtime** (SOs-out).

### ReferenceTracker&lt;T&gt; / ReferenceTrackers

**`[Obsolete]`** — a parallel per-type registry that mirrors `ReferenceManager`. Use
`ReferenceManager` (with `GetAllOfType`) directly. Removed next major.

## Implementing a Referenceable MonoBehaviour

```csharp
using Molca;
using Molca.Attributes;
using Molca.ReferenceSystem;
using UnityEngine;

public class SpawnPoint : MonoBehaviour, IReferenceable
{
    [SerializeField, ReadOnly] private string refId;

    public string RefId      { get => refId; set => refId = value; }
    public string RefType    => "SpawnPoint";
    public string DisplayName => gameObject.name;

    private void OnValidate()
    {
        if (string.IsNullOrEmpty(refId))
            refId = ReferenceGenerator.GenerateUniqueId(RefType);
    }

    private async void OnEnable()
    {
        await RuntimeManager.WaitForInitialization();
        if (this == null || !isActiveAndEnabled) return;
        ReferenceManager.Instance?.Register(this);
    }

    private void OnDisable() => ReferenceManager.Instance?.Unregister(this);
}
```

For the common case, just add the built-in `ReferenceableComponent` instead of writing this.

## Setup

1. **RuntimeManager prefab** — add the `ReferenceManager` component as a child of the
   RuntimeManager prefab. It is auto-discovered and initialized during bootstrap (no code
   registration). Set its `RuntimeMode` and `InitializationPriority` in the Inspector.
2. **GlobalSettings** — create a `ReferenceManagerSettings` asset
   (`Assets > Create > Molca > Reference System > Reference Manager Settings`) and add it to
   the `GlobalSettings` modules list. Used for the editor validation database and the debug
   toggle.

## Notes

- Always `await RuntimeManager.WaitForInitialization()` before resolving.
- A `SceneObjectReference` resolves only while the target's scene is loaded and the object is
  registered (active and enabled). Unloaded or destroyed targets resolve to `null`.
- `RefId` is settable specifically so generation and validation tooling can assign ids; treat
  it as read-only in normal gameplay code.
</content>
</invoke>
