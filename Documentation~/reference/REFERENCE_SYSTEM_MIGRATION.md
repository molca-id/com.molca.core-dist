---
title: Migrating to Scoped References
category: Scene & References
order: 201
---

# Migrating to scoped references

For projects and SDK forks upgrading to the scoped reference system (v2).

**Nothing in your project has to change.** Existing `SceneObjectReference` data keeps serializing and
resolving exactly as before, through `ReferenceScopeKind.LegacyGlobal`. This document exists so you can
decide *when* to migrate and know what each step buys you — not because a deadline is arriving.

## What actually changed, and why it had to

A v1 id had to be unique across the whole project, because the runtime registry had one flat key space.
That single fact caused the symptom everybody hit: placing a referenceable prefab a second time was an
id conflict, so the editor gave each new placement a fresh id — and *that broke the prefab's internal
wiring*, because the references inside it still named the id the asset was authored with.

Regenerating ids was never a fix. It traded a loud conflict for a silent breakage. Scope is now part of
identity, so the conflict does not arise and nothing has to be rewritten.

## Do I need to do anything?

| Situation | Action |
|---|---|
| References between objects in one scene, ids unique | Nothing. Optionally narrow to `Scene` scope later. |
| A referenceable prefab placed once | Nothing. |
| **A referenceable prefab placed more than once** | Add a `ReferenceScopeRoot`. This is the case v1 could not express. |
| Cross-scene references with additive loading | Author load sets so validation can check them. |
| Fields that must always be wired | Move to `SceneObjectReferenceV2` and declare `Required`. |
| A fork that subclasses `ReferenceManager` or `Step` | Read [Fork compatibility](#fork-compatibility). |

## Step 1 — Run an audit and read the Coverage view

Open **Molca Hub → References**, run a full audit, and look at the Coverage view. It reports:

- **Scope migration** — how many legacy references could be re-homed unambiguously, and how many need a
  decision. Nothing is applied from here.
- **Scene load sets** — whether yours are authored or inferred.
- **Legacy cached id lists** — offered for removal once the audit is healthy.

Fix the errors first. Migration on top of a project with existing reference errors makes it very hard to
tell which change caused what.

## Step 2 — Scope your repeatable prefabs

For any prefab placed more than once whose internals reference each other:

1. Add a **`ReferenceScopeRoot`** to the prefab root. It generates a stable scope template id, shared by
   every instance and never regenerated.
2. Set the `ReferenceableComponent`s beneath it to scope **`Prefab Local`**.
3. Set the reference *fields* beneath it to `Prefab Local` too, if they are `SceneObjectReferenceV2`.

From then on the authored ids beneath the root are left alone — `OnValidate` stops regenerating an
inherited id once a scope root is present, because inheritance is the point rather than a collision.

**Existing placements keep their already-regenerated ids.** That is deliberate: rewriting them would be
a bulk redirect of serialized data, exactly the operation that used to corrupt projects. Re-point the
affected fields yourself, or delete and re-place the instances now that doing so is safe.

## Step 3 — Author load sets if you load scenes additively

Create `ProjectSettings/MolcaReferenceLoadSets.json`:

```json
{
  "schemaVersion": 1,
  "sets": [
    {
      "id": "main",
      "entryScene": "Assets/Scenes/Main.unity",
      "concurrentScenes": ["Assets/Scenes/Hud.unity"],
      "deferredScenes": ["Assets/Scenes/Level.unity"]
    }
  ]
}
```

Commit it. Load sets decide whether a build fails, so a per-user setting would make the same commit
pass on one machine and fail on another.

Without a file, one set is **inferred** from the enabled build scenes with everything after the first
treated as *deferred*. That is the honest reading of unknown load order: it never claims two scenes are
loaded together, so it never manufactures a `REF009`. It also cannot catch a genuinely unreachable
cross-scene reference — which is what authoring real sets buys you.

## Step 4 — Adopt `SceneObjectReferenceV2` where it earns its keep

```csharp
[SerializeField] private SceneObjectReferenceV2 _valve;

private async void Start()
{
    // The owning component is required: a prefab-local reference resolves relative to the live
    // instance, and only the context can say which instance that is.
    var result = await _valve.ResolveAsync<ValveInteraction>(this, cancellationToken: destroyCancellationToken);
    if (!result.IsResolved)
        Debug.LogWarning(result.Summary);   // says which of the ten failure modes it was
}
```

What you gain:

- **Declared requiredness.** A `Required` field with no target is now `REF006` — an editor and build
  error. In v1 a field somebody forgot to wire was indistinguishable from one deliberately left empty,
  so it surfaced as a null at runtime.
- **Typed outcomes.** `ReferenceResolveResult` distinguishes "never assigned" from "the scene isn't
  loaded yet" from "two providers claim this id". All three used to arrive as the same `null`.
- **Scope.** The reason for all of this.

Convert an existing value with `SceneObjectReferenceV2.FromLegacy(v1)`. It produces `LegacyGlobal` +
`Deferred`, because that is what v1 actually did — narrowing during a mechanical conversion would change
behavior. Narrow deliberately, with the migration proposal to hand.

## Step 5 — Move registrations onto handles

If you call `ReferenceManager.Register` yourself:

```csharp
// Before
_isRegistered = manager.Register(this);

// After
var result = manager.Register(this, key, out _registration);
if (!result.IsRegistered)
    Debug.LogError(result.Describe());   // names the provider already holding the key

// On disable
_registration?.Dispose();
_registration = null;
```

`Register(IReferenceable)` still works and still means `LegacyGlobal`. Two reasons to move anyway:

- The `bool` collapsed `AlreadyRegisteredSameKey` — harmless, and the common re-enable case — together
  with `DuplicateKey`, a real authoring defect. You could not react differently because you were not
  told anything different.
- Unregistering by object re-read the provider's *current* `RefId`. A provider whose id changed while
  registered therefore unregistered the wrong key, or none, and orphaned the real entry for the rest of
  the session. A handle cannot do that: it releases the key it was issued for.

`Step` and `SequenceController` already work this way.

## Fork compatibility

| Surface | Status | Notes |
|---|---|---|
| `SceneObjectReference` | **Supported** | Serialization and resolution unchanged. |
| `ReferenceManager.Register(IReferenceable)` | **Supported** | Maps to a `LegacyGlobal` key. |
| `TryGet(refType, refId)`, `TryGetByRefIdOnly`, `GetAllOfType`, `GetAllReferenceIds`, `GetReferenceId` | **Supported, narrowed** | Global-scope entries only — see below. |
| `ReferenceManager.Count` | **Changed** | Now counts the whole registry, scoped entries included. |
| `ReferenceManager.Teardown` | **Changed** | Now clears the registry and drops event subscriptions. |
| `ReferenceableComponent.OnValidate` | **Changed** | No longer regenerates an inherited id inside a scope root. |
| `ReferenceManagerSettings` id-list queries | **Obsolete** | `GetReferenceStats`, `GetReferenceTypes`, `GetReferenceIds`, `FindDuplicateIds`. |
| `ReferenceManagerSettings.ShowValidationResults` | **Obsolete** | No effect. |
| `ReferenceManagerSettings.AutoValidateOnScan`, `FixRefIdsOnSceneSave` | **Obsolete** | No effect. |
| `ReferenceManager.RegisterWithAutoId` | **Obsolete** | Cannot assign ids; returns false when generation is needed. |

### The narrowed lookups

The v1 lookups answer for **global-scope entries only**. A prefab-local id is not unique across the
project, so answering a bare `(RefType, RefId)` query with one would reach into a scope the caller had no
way to name — which is the exact collision scopes exist to prevent.

If you need the whole registry, the scoped equivalents are `TryGet(ReferenceRuntimeKey)`, `GetAllKeys`,
`GetAllInScope` and `TryGetKey`. This only affects code that registers scoped keys; a fork that never
adopts scopes sees no change.

### Custom `IReferenceable` providers

No interface change. To support scopes, register through the scope root:

```csharp
var root = ReferenceScopeRoot.FindNearest(this);
var key = root == null
    ? ReferenceRuntimeKey.Legacy(RefType, RefId)
    : root.KeyFor(RefType, RefId);

root?.EnsureOpen(manager);                        // before registering, not after
manager.Register(this, key, out _registration);
```

Falling back to `Legacy` when there is no root matters: a prefab-local registration naming a scope that
is not open is refused outright, and behaving as before is better than silently not registering. The
audit reports the missing root as `REF007`.

### Custom audit contributors

`ReferenceSiteRecord`'s scope parameters are optional, so an existing
`MolcaReferenceIndexContributor` keeps compiling and keeps meaning what it meant. Supply
`scopeKind`/`scopeId`/`requiredness`/`availability`/`scopeRootId` when you describe v2 sites.

`ReferenceCollectionContext.MarkScanned` **is** required for a run to be persistable — a contributor
that adds records without declaring which assets it read makes the index unverifiable, so that run stays
in memory only.

## New finding codes

| Code | Meaning | Severity |
|---|---|---|
| `REF006` | A `Required`/`DeferredRequired` reference has no target. | Error, not lowerable. |
| `REF007` | A prefab-local reference has no enclosing `ReferenceScopeRoot`. | Error, not lowerable. |
| `REF009` | The target's scene is never loaded with the owner's under any declared load set. | Error; warning in development builds. |

`REF006` and `REF007` are not lowerable because both describe an authored declaration that cannot work
as written — a `Required` field with no target throws, and a prefab-local key with no scope root has its
registration refused. Neither is hygiene an iteration build can defer.

`REF009` is lowerable because it describes *configuration*: an iteration build may legitimately not match
the declared load sets. It is only ever produced when load sets exist — asserting unavailability from an
inferred guess would be worse than staying silent.

## Scheduled for removal in the next major

Nothing below is removed yet. Everything is obsolete-with-replacement today.

- `ReferenceManager.RegisterWithAutoId`
- `ReferenceManager.Instance` (use `RuntimeManager.GetSubsystem<ReferenceManager>()` or `[Inject]`)
- `ReferenceManagerSettings.AutoValidateOnScan`, `FixRefIdsOnSceneSave`, `ShowValidationResults`
- `ReferenceManagerSettings.GetReferenceStats`, `GetReferenceTypes`, `GetReferenceIds`, `FindDuplicateIds`
- The serialized `assetKnownIds` / `sceneKnownIds` buckets, after the removal action has shipped for one
  release and migration is confirmed across forks

`SceneObjectReference` itself is **not** on this list. It has no removal date, and would only ever get
one after v2 has been the default long enough to be boring.

## See also

- [REFERENCE_SYSTEM.md](REFERENCE_SYSTEM.md) — full reference
- [HUB.md](HUB.md) — the References workspace
