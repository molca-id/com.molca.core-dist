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

Scoped references (v2) add:

| Type | Kind | Role |
|---|---|---|
| `ReferenceScopeKind` | enum | The space an id must be unique in: `LegacyGlobal`, `Global`, `Scene`, `PrefabLocal`. |
| `ReferenceRuntimeKey` | readonly struct | Full identity: scope + `(RefType, RefId)`. The registry's authoritative key. |
| `ReferenceScopeRoot` | `MonoBehaviour` | Marks a prefab subtree as its own scope, so instances do not collide. |
| `SceneObjectReferenceV2` | serializable struct | Serialized reference carrying scope, requiredness and availability. |
| `ReferenceRegistrationHandle` | class | Owns exactly one registration; disposing it releases that entry and no other. |
| `ReferenceRegistrationResult` | readonly struct | Why a registration succeeded or was refused, instead of a `bool`. |
| `ReferenceResolveResult` | readonly struct | Why a resolve succeeded or failed, instead of an object-or-null. |
| `ReferenceRequiredness` | enum | `Optional`, `Required`, `DeferredRequired`. |
| `ReferenceAvailabilityPolicy` | enum | `Immediate`, `Deferred`, `Conditional`. |
| `ReferenceRuntimeDiagnostics` | class | Bounded record of registry events, shown in the Hub's Runtime view. |

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

Outside a scope root, each prefab *placement* gets its own fresh id: when a prefab instance is detected
still carrying its source asset's id, `OnValidate` regenerates it, so placing a referenceable prefab N
times never shares one id.

**Inside a `ReferenceScopeRoot` the id is left alone.** Regenerating it was only ever a workaround for
v1's flat key space, and it broke the prefab's internal wiring — the references inside still named the
authored id. Add a scope root to any prefab you place more than once; see
[Scoped references](#scoped-references).

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

This is the v1 shape and stays supported. For a provider inside a repeatable prefab, or one that wants to
know *why* a registration was refused, register under an explicit key and keep the handle — see
[Registration reports why, not whether](#registration-reports-why-not-whether).

`SequenceController` and `Step` are already referenceable, so their Ref Ids are resolvable the same
way. Only loaded scene MonoBehaviours live in the runtime registry — ScriptableObjects are not
runtime-resolvable through this system.

## Behavior worth knowing

- **Type-first with id fallback.** A resolve looks up `(RefType, RefId)` first, then falls back to
  id-only across all types — so a reference survives a Ref Type rename (it logs a nudge to re-save
  the field). An id that is ambiguous across multiple *live* types fails rather than guessing.
- **Not found returns null.** `Resolve<T>()` and `TryResolve<T>()` never throw; only the
  `required: true` overloads raise `ReferenceResolutionException`. A wrong-type resolve logs an
  error.
- **Self-healing on destroy.** If a referenced object was destroyed without unregistering, *every*
  public lookup — `Get`, `TryGet`, `TryGetByRefIdOnly`, `GetAllOfType`, `IsRegistered`, and the
  `SceneObjectReference` resolve path — purges the dead entry and reports not-found rather than
  handing back a fake-null object. A destroyed incumbent also stops blocking its own key, so a
  respawned object can register under it.
- **Duplicate ids.** Two live objects sharing a `(RefType, RefId)` *in the same scope* is a conflict:
  the incumbent keeps the key and the newcomer is rejected with an error naming both, so which one
  wins is decided by the key rather than by load order. The same id under *different* Ref Types is
  legal, and so is the same id in two different scopes — see [Scoped references](#scoped-references).
- **The v1 lookups never see scoped providers.** `TryGet(refType, refId)`, `TryGetByRefIdOnly`,
  `GetAllOfType` and `GetAllReferenceIds` answer for global-scope entries only. A prefab-local id is
  not project-unique, so answering a bare `(RefType, RefId)` query with one would reach into a scope
  the caller had no way to name. Use `TryGet(ReferenceRuntimeKey)` or `GetAllInScope` for those;
  `Count` covers the whole registry.
- **Registration lifecycle is uniform.** `ReferenceableComponent`, `Step` and `SequenceController`
  all register in `OnEnable` (after `RuntimeManager` initialization) and unregister in `OnDisable`,
  so whether a disabled target resolves no longer depends on which component type it is.
- **One diagnostic per async resolve.** `ResolveAsync` is silent while the target may still
  legitimately register; only its terminal outcome logs. Its cancellation token covers the whole
  operation, including the wait for `RuntimeManager` bootstrap.

## Scoped references

A v1 id had to be unique across the whole project, because the registry had one flat key space. That
is why placing a referenceable prefab twice was a conflict — and why the editor gave each new
placement a fresh id, which *broke the prefab's internal wiring*, since the references inside it still
named the id the asset was authored with.

Scoped references fix the cause instead of the symptom. `ReferenceRuntimeKey` is scope plus
`(RefType, RefId)`, so two instances of one prefab can hold identical authored ids without colliding.

| Scope | Uniqueness required | Resolved against |
|---|---|---|
| `LegacyGlobal` | Project-wide | The v1 compatibility path, including id-only fallback. |
| `Global` | Across every simultaneously loaded provider | The exact key. For true application singletons. |
| `Scene` | Within one authored scene | The exact key, scoped by scene path. |
| `PrefabLocal` | Within one runtime prefab instance | The nearest `ReferenceScopeRoot`'s live instance id. |

`LegacyGlobal` is the **zero value** on purpose. A default-constructed key, or one deserialized from
data written before scopes existed, lands on the compatibility path — which tolerates a missing scope
and reports what it did — rather than silently claiming to be an exact `Global` identity it was never
authored as.

### Prefab scopes

Add a `ReferenceScopeRoot` to a prefab root and set the `ReferenceableComponent`s beneath it to
`Prefab Local`:

- the prefab asset owns a stable **scope template id**, inherited by every instance;
- each live instance gets a unique **scope instance id**, never serialized;
- authored ids beneath the root are **left alone** — `OnValidate` stops regenerating them, because
  inheritance is now the point rather than a collision;
- two instances may carry identical local ids;
- internal wiring survives duplication, prefab variants and runtime instantiation.

A reference leaving the prefab must explicitly choose `Scene` or `Global`.

### Resolving a prefab-local reference needs a context

The serialized scope of a prefab-local reference names the *template*, which every instance shares.
The live key needs the instance's own scope id, so the resolve entry points take the owning component:

```csharp
var target = reference.Resolve<Step>(this);
var result = await reference.ResolveAsync<Step>(this, cancellationToken: destroyCancellationToken);
```

Without a context there is no correct answer, so the result is `WrongScope` rather than a reach into
whichever instance happened to register first.

### Registration reports why, not whether

```csharp
var result = manager.Register(this, key, out var handle);
if (!result.IsRegistered)
    Debug.LogError(result.Describe());   // names the conflicting holder
```

`ReferenceRegistrationOutcome` separates `AlreadyRegisteredSameKey` (harmless, the common re-enable
case) from `DuplicateKey` (a real authoring defect) — a `bool` collapsed both into `false`.

The **handle** captures the key as it was at registration time, and `Dispose` releases exactly that
entry. v1 unregistered by re-reading the provider's current `RefId`, so a provider whose id changed
while registered unregistered the wrong key — or none — and orphaned the real entry permanently.

### Requiredness and availability

`SceneObjectReferenceV2` declares both, and they are independent:

- **Requiredness** — `Optional` (unresolved is legal and silent), `Required` (an editor and build
  error; throws at runtime), `DeferredRequired` (may register later; a timeout is an error).
- **Availability** — `Immediate` (the provider must already exist), `Deferred` (it may arrive during a
  bounded wait), `Conditional` (only expected under a named load set).

V1 data migrates to `Optional` + `Deferred`, because that is what v1 actually did. The Hub labels it
inferred rather than authored, since nobody chose it.

### Scene load sets

Cross-scene references can only be validated against a statement of what is loaded when. Author
`ProjectSettings/MolcaReferenceLoadSets.json` — committed, so validation decides identically for every
developer and for CI:

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

With nothing authored, one set is **inferred** from the enabled build scenes: the first is the entry
scene and the rest are treated as deferred. Deferred, not concurrent — assuming every enabled scene
loads together is the assumption that made cross-scene validation useless, because it can never report
anything as unavailable. The Hub's Coverage view says when the set is inferred.

When several sets mention the same owner scene, the **worst** availability wins. A reference that works
in one configuration and cannot resolve in another is broken in that second one.

### Runtime diagnostics

`ReferenceManager.Diagnostics` keeps a bounded record of registrations, conflicts, fallbacks, timeouts
and purges. A conflict is invisible in a steady-state listing — the losing registration simply is not
there — so without the stream the most diagnostic events in the system leave no trace. The Hub's
Runtime view shows it live during Play Mode. It retains strings only, never objects, and is a
development aid: nothing leaves the process.

### Migrating v1 data

Nothing migrates on its own. The Hub's Coverage view shows a **scope migration proposal** that
narrows a scope only when the data forces one conclusion — the site and its single provider are inside
the same prefab, or the same scene. Everything else stays `LegacyGlobal` or is handed back as a
decision, because a wrong scope turns a working reference into one that cannot resolve, silently and
across a whole project at once.

Migration re-homes a reference into a scope; it never re-points it at a different target. That would
be a repair, and repairs are a separate previewed action.

## Validating references

Reference health comes from one shared, **read-only** audit engine. Molca Doctor, the build gate,
Sequence validation, the Inspector drawer, Framework Graph and MCP all project the same snapshot, so
they cannot disagree about what "broken" means.

Run it from **Molca Hub → References** (the surface built for it), from
**Molca → Reference System → Audit Project References**, from Molca Doctor, or over MCP with
`molca_references_audit`.

### What it checks

| Code | Meaning | Severity |
|---|---|---|
| `REF001` | No provider carries the stored Ref Id. Warning instead of error when the only match is a prefab template, which resolves once instantiated. | Error / Warning |
| `REF002` | Two or more providers claim the same `(RefType, RefId)`. Load order decides the winner. | Error |
| `REF003` | The stored Ref Type matches nothing and several providers carry the id — the runtime refuses an ambiguous fallback, so it resolves to nothing. | Error |
| `REF004` | The target is not assignable to the `T` a `SceneObjectReference<T>` field promised. | Error |
| `REF005` | The reference resolves through the compatibility fallback; the serialized Ref Type is stale. | Warning |
| `REF006` | A reference declared `Required` or `DeferredRequired` has no target. | Error |
| `REF007` | A prefab-local reference has no enclosing `ReferenceScopeRoot`, so there is no scope to resolve it in. | Error |
| `REF008` | A provider carries no Ref Id, so nothing can reference it. | Error |
| `REF009` | The target's scene is never loaded alongside the owner's under any declared load set. | Error |
| `REF015` | An asset could not be scanned — its state is unknown, not clean. | Error |
| `REF016` | Coverage was incomplete, so the result is not authoritative. | Warning |

`REF002`, `REF003`, `REF004`, `REF006` and `REF007` can never be configured below error: each describes
something that fails at runtime as written. A development build may lower `REF008`, `REF009`, `REF015`
and `REF016`.

`REF006` and `REF007` were undetectable before v2. Without authored requiredness, a field somebody forgot
to wire looked exactly like one deliberately left empty; without scopes there was nothing to be missing a
root for. `REF009` is only produced when the project supplies load sets — asserting unavailability from
an inferred guess would be worse than staying silent.

### Clean requires complete coverage

An audit reports **coverage** alongside its findings. Zero findings with a skipped or failed input
category is **Incomplete**, never **Clean** — a scan that could not look everywhere cannot certify
anything. Common gaps:

- **Prefab assets** — skipped entirely when *Prefab Scan Paths* is empty.
- **Scenes (declared)** — closed scenes are only opened when *Comprehensive Scene Scanning* is on
  *and* the caller allows it. The audit refuses to open scenes when any open scene has unsaved
  changes, since it will neither discard nor save your work.

### The derived index

A completed audit is written to `Library/Molca/References/index.json` and restored on the next editor
start, so a cold editor knows the project's reference health without paying for a full scan first.

It is **derived data and is never committed.** A committed index would be a second source of truth able
to disagree with the assets it describes — which is exactly what the authored id lists on
`ReferenceManagerSettings` were.

Three rules make a restored index trustworthy:

- **It must prove it is current.** Every asset the audit read is fingerprinted with its dependency hash
  at scan time. The index is adopted only if every one of those still matches. In-memory caching can
  rely on `AssetPostprocessor` and scene events, but those do not run while Unity is closed, so a
  file-backed cache has to carry its own evidence.
- **Findings are re-derived, never replayed.** The file stores them so it is readable on its own, but
  restoring re-runs the analyzer over the stored providers and sites under the *current* severity
  policy and *current* rules. Replaying stored findings would mean a policy change — or a fixed
  analyzer bug — silently failed to take effect until someone ran a full audit.
- **Unverifiable results are not stored at all.** A run that read an untitled scene, a dirty scene or a
  modified asset records fingerprints for file contents it never actually looked at, so it is kept in
  memory and refused by the index. The Coverage view says when this happens and why.

When some fingerprints do not match, the changed assets are rescanned and merged rather than triggering
a full audit. **Only scanning is incremental — analysis always re-runs over everything**, because a
reference in one scene resolves against providers in another. The incremental pass declines outright
when it cannot prove the result (a changed scene that is not open, unsaved state) and asks for a full
audit instead.

The Coverage view reports the index's location, size, origin and pending changes, and offers **Clear
index**.

### What is scanned

Scenes, prefab assets, and **ScriptableObject assets** — an SO cannot be a runtime *target*, but it
can absolutely hold an outbound reference that resolves a loaded scene object. Discovery walks
serialized properties, so scalar fields, arrays, lists, nested serializable structs and
`[SerializeReference]` graphs are all covered, and a class that merely happens to have string fields
named `refId`/`refType` is correctly *not* treated as a reference.

### Scanning never writes

**Scan, Refresh, Validate and Audit only read.** No id generation, no `SetDirty`, no asset save.
Saving a scene likewise reports its provider identity problems and changes nothing.

Repair is a separate, deliberate operation — see below.

## The References workspace

**Molca Hub → References** (group *Quality*, next to Doctor) is where reference health is read and
acted on. It projects the same snapshot as everything else; it has no scanning or resolution logic of
its own.

### Header

State, counts, coverage percentage, the last audit time, the current mode, and two actions:

- **Refresh affected** — re-audit what is already loaded. Opens no scene.
- **Full audit** — audit the configured project, opening closed scenes to read them and restoring
  your setup afterwards.

The header says **Clean** only when three things hold at once: no findings, complete required
coverage, and a snapshot the project has not moved past. Anything else names which one is missing —
`Errors`, `Incomplete`, `Stale`, `Warnings`, `Scanning`, or `Not audited`.

**Opening the tab does not start a scan.** A scan can open scenes and take real time; the header's
actions are the request for one.

### Views

| View | Shows |
|---|---|
| **Issues** | Findings, most severe first. The default. |
| **References** | Every reference site and what it resolves to. |
| **Providers** | Every target, with how many references resolve to it and how many merely store its Ref Id. When those two numbers differ, something claims the id and does not get it. |
| **Graph** | The neighbourhood of the selected row — one hop, bounded. A solid arrow is what the runtime resolves; a dashed one is a match that does not win. Not a project-wide graph. |
| **Runtime** | Live registrations in Play Mode, compared against the audit: *expected but not registered* (a disabled object, an unloaded scene, a lifecycle mistake) versus *registered but outside the audit scope*. |
| **Coverage** | What was scanned, skipped and failed, and why that decides whether `Clean` is available. |

Filters cover severity, free text, source kind, reference type, folder, requiredness, legacy
fallback, read-only assets and repair availability. A filtered table reports what it is hiding rather
than just showing fewer rows. Filters, the selected row and a scan in progress all survive switching
Hub tabs.

### Detail panel

The selected row's full locator and property path, its stored target and expected type, its
candidates, **why it has the severity it has**, and the repairs available for it — plus *Select
owner*, *Ping target*, *Open scene*, *Open prefab*, *Reveal asset* and *Copy diagnostic*.

Opening a closed scene is explicit and confirmed, and additive so your current setup survives. The
audit itself never disturbs your open scenes.

### Severity policy

The workspace owns severity authoring (the `ReferenceManagerSettings` Inspector does not — it reports
health in one line and links here). Two limits are worth knowing:

- `REF002`, `REF003` and `REF004` are **fixed at error**. They describe references that already fail
  at runtime, and lowering one would let a build pass over a project that is broken.
- Authored severities apply to **editor audits only**. They live in per-user editor prefs, so letting
  them decide whether a build fails would make the same commit pass on one machine and fail on
  another. The build gate always uses the production policy.

### From the Inspector

Every reference field has an **Open in References** action that opens the workspace focused on that
exact reference — useful on a healthy reference too, since "what else points at this target" is a
question a working reference raises as often as a broken one.

### Activity rail

A clean project shows **no chip**: a permanent green pill is noise, and noise is what stops a rail
being read. Scanning shows progress; errors, incomplete coverage and staleness show a dismissible
chip. Chip captions are built from counts and states only — never an asset path — so they are safe on
a remote-observed session.

## Repairing references

A repair is a **transaction built from a specific audit**: plan, review, apply, verify. There is no
"fix everything" button, because the fixes that matter are the ones where the data does not say what
was intended.

```text
Audit  →  ReferenceRepairPlanner  →  plan preview  →  your approval  →  ReferenceRepairExecutor  →  measured report
```

From **Molca Hub → References**: **Preview safe repairs** for the whole batch, or per row **Point
here…** (redirect to a chosen candidate) and **Clear reference…**. Over MCP:
`molca_references_plan_fix` then `molca_references_apply_fix` with the returned `planId`.

### What is repaired automatically

Only where the outcome is unambiguous:

| Repair | Why it is safe |
|---|---|
| Assign a Ref Id to a provider that has none | Nothing can reference an id that does not exist yet. |
| Refresh a reference's cached Ref Type and display name | The Ref Id — the identity — is untouched. |
| Re-key a duplicated `(RefType, RefId)` **that nothing references** | No inbound intent to preserve. |

### What is never repaired automatically

| Refused | Why |
|---|---|
| Re-keying a duplicate that something **does** reference | Nothing records which of the duplicates each reference meant, so re-keying one silently re-points them at the other. |
| A blanket `oldId → newId` rewrite | Same reason, at scale, plus it matched any string field named `refId` rather than actual references. |
| Clearing a broken reference | An unset reference passes validation, so this turns "broken" into "fine" without fixing anything, and destroys the record of what was intended. |
| Pointing a typed field at an incompatible object | The cast fails at runtime, so reporting success would be a lie. |
| Editing a read-only (package) asset | The write either fails or is lost on the next package resolve. |

Those land under `choices` in the plan output, each with its candidates and the question you need to
answer. Fix them from the Inspector of the referencing object, where the intended target is visible.

### Transaction guarantees

- A plan records the audit revision it came from. Applying it after the project changed is
  **rejected**, not applied to data you never reviewed.
- Each mutation records the value it expects to find and re-checks it at apply time. A value that
  moved is **skipped with a reason**, never overwritten.
- Everything applies in **one Undo group**, so Ctrl+Z takes back the repair rather than one object of
  it. Asset files that had to be saved are listed separately — Undo restores the in-memory value, but
  only version control restores the file.
- Afterwards the project is **re-audited** and the report states what measurably changed, including
  any finding the repair *introduced*.

### Build gate

`ReferenceBuildGate` is an `IPreprocessBuildWithReport`, so reference validation runs for Molca
Build Manager, **File → Build**, and batch-mode CI alike. It fails **closed**: a coverage gap or a
scan failure aborts a production build rather than passing green. A build that processes a scene the
gate never validated also fails, so an explicit `BuildPlayerOptions.scenes` list cannot bypass it.

### Extending the audit

A package outside Core contributes providers and reference sites by subclassing
`MolcaReferenceIndexContributor` in an editor assembly; the engine discovers implementations by
reflection. A contributor that throws is isolated — the audit is downgraded to incomplete rather
than reported as clean.

```csharp
public sealed class MyReferenceContributor : MolcaReferenceIndexContributor
{
    public override string Id => "mypackage.references";

    public override void Collect(ReferenceCollectionContext context)
    {
        foreach (var owner in MyOwnAssets())
        {
            // Declare where the records came from, so the persisted index can revalidate them later.
            context.MarkScanned(AssetDatabase.GetAssetPath(owner));
            context.AddProviderFor(owner);
            context.CollectSitesFrom(owner);
        }

        context.ReportCoverage("My assets", ReferenceCoverageStatus.Scanned, count: 12);
    }
}
```

A contributor that adds records without calling `MarkScanned` makes the whole index unverifiable —
nothing can later prove its inputs are unchanged — so that run is kept in memory and never written to
disk. Declaring an asset another phase already scanned is free; fingerprints are deduplicated.
Contributors may describe state, never mutate assets.

## See also

- [REFERENCE_SYSTEM_MIGRATION.md](REFERENCE_SYSTEM_MIGRATION.md) — upgrading a project or fork to scoped
  references, and the next-major removal list

- [Sequences](molca://doc/SEQUENCES)
- [Subsystems](SUBSYSTEMS.md)
- [Events](EVENTS.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Async Contract](ASYNC_CONTRACT.md)
