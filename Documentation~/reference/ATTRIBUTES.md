---
title: Attributes
category: Runtime & Core
order: 150
---

# Attributes

Molca Core ships a small set of C# attributes you apply to your own types, fields, and properties.
They fall into two groups: **runtime** attributes that drive the DI container and bootstrap ordering
(`Molca` namespace), and **Inspector** attributes that shape how a field draws in the Unity editor
(`Molca.Attributes` namespace). All of them live in
[`Runtime/Attributes/`](molca://asset/Packages/com.molca.core/Runtime/Attributes) inside the
package; you just add a `using` and annotate.

## At a glance

| Attribute | Namespace | Applies to | Purpose |
|---|---|---|---|
| `[Inject]` | `Molca` | field, property, constructor | Resolve a dependency from the container |
| `[DependsOn]` | `Molca` | class | Order a subsystem after the subsystems it uses |
| `[ShowIf]` | `Molca.Attributes` | serialized field | Draw the field only when a bool member is `true` |
| `[HideIf]` | `Molca.Attributes` | serialized field | Hide the field when a bool member is `true` |
| `[ReadOnly]` | `Molca.Attributes` | serialized field | Draw the field greyed-out but still serialize it |
| `[Expandable]` | `Molca.Attributes` | `ScriptableObject` field | Inline-edit the referenced asset in the Inspector |
| `[InfoBox]` | `Molca.Attributes` | field, property, class, struct | Show a help box above the member |
| `[RefId]` | `Molca.Attributes` | serialized `string` field | Mark the backing store for a `RefId`, with refresh tooling |

Runtime attributes affect actual behavior in a build. Inspector attributes are backed by editor-only
property drawers, so they change how a value is *authored* but never how it *runs*.

## Runtime attributes

### `[Inject]`

Marks a field, property, or constructor for dependency injection. After `RuntimeManager` initializes,
resolved services are assigned into the marked members. Use it instead of `FindObjectOfType<T>()` or a
static singleton for accessing services and subsystems.

| Property | Default | Meaning |
|---|---|---|
| `Required` | `true` | If the dependency cannot be resolved, injection fails with an error. Set `false` for optional dependencies — the member simply stays `null`. |
| `ForceInject` | `false` | If `true`, injects even when the member already holds a non-null value. |

A convenience constructor `InjectAttribute(bool required)` sets `Required` positionally. The attribute
targets `Field`, `Property`, or `Constructor` and does not allow multiple on one member.

```csharp
using Molca;

public class ScoreReporter : MonoBehaviour
{
    // Fails loudly if the service is not registered.
    [Inject] private IScoreService _scores;

    // Optional: stays null if no analytics subsystem is present.
    [Inject(required: false)] private AnalyticsSubsystem _analytics;
}
```

See [Dependency Injection](DEPENDENCY_INJECTION.md) for how members get injected (`InjectDependencies`
/ `CreateWithInjection`) and how services are registered.

### `[DependsOn]`

Declares that a `RuntimeSubsystem` requires other subsystems to be initialized first. The bootstrap
pipeline topologically sorts subsystems by these declarations before initializing them, so a
dependency is always ready when your `Initialize`/`InitializeAsync` runs.

Reach for it whenever your subsystem touches another subsystem during its own initialization. It is
more robust than leaning on `InitializationPriority` numbers, which have no compile-time relationship
to each other. When `[DependsOn]` is present, declared dependencies always come first and
`InitializationPriority` only breaks ties between otherwise-unrelated subsystems. The attribute may be
applied multiple times (and is inherited), and cycles are detected at bootstrap: `RuntimeManager` logs
the participating types and falls back to priority-only ordering so the app still boots.

```csharp
using Molca;

[DependsOn(typeof(LogManager))]
public class TelemetrySubsystem : RuntimeSubsystem
{
    // LogManager is guaranteed initialized before this runs.
}
```

The constructor takes `params Type[]` — pass one or more `RuntimeSubsystem` types. See
[Subsystems](SUBSYSTEMS.md) for the lifecycle these declarations order.

## Inspector attributes

These extend `UnityEngine.PropertyAttribute` and are rendered by editor-only drawers. Import them with
`using Molca.Attributes;`.

### `[ShowIf]` / `[HideIf]`

Conditionally draw a serialized field based on the value of a **bool member** on the same object.
`[ShowIf("flag")]` draws the field only when `flag` is `true`; `[HideIf("flag")]` draws it only when
`flag` is `false`. Each takes the member name as a string.

The named member may be a private or public **field or property** — computed bool properties work too,
so you can gate on derived state. If the name cannot be resolved the drawer logs a warning and shows
the field to be safe.

```csharp
using UnityEngine;
using Molca.Attributes;

public class HintConfig : MonoBehaviour
{
    [SerializeField] private bool _useCustomDelay;

    [ShowIf(nameof(_useCustomDelay))]
    [SerializeField] private float _delaySeconds = 2f;

    // Hidden while the object is in its locked state (computed property).
    [HideIf(nameof(IsLocked))]
    [SerializeField] private string _editableLabel;

    private bool IsLocked => Application.isPlaying;
}
```

### `[ReadOnly]`

Draws a serialized field greyed-out and non-editable in the Inspector while still serializing its
value. Useful for surfacing runtime-assigned or derived state that a designer should see but not edit.

```csharp
using UnityEngine;
using Molca.Attributes;

[ReadOnly]
[SerializeField] private string _generatedId;
```

### `[Expandable]`

Applied to a field that references a `ScriptableObject`, this lets you expand the referenced asset
inline and edit its properties directly in the host's Inspector — no need to select the asset
separately.

```csharp
using UnityEngine;
using Molca.Attributes;

[Expandable]
[SerializeField] private AudioLibrary _library;
```

### `[InfoBox]`

Displays a Unity help box with a message near the annotated member. It can decorate a field, property,
class, or struct, and multiple boxes may be applied to one target. Pick a severity with `InfoBoxType`
(`Info` / `Warning` / `Error`), which controls the icon and color.

```csharp
using Molca.Attributes;

[InfoBox("Set this before entering Play mode.", InfoBoxType.Warning)]
[SerializeField] private string _apiKey;
```

The primary constructor is `InfoBoxAttribute(string message, InfoBoxType type = InfoBoxType.Info)`.

### `[RefId]`

Marks a serialized `string` field as the backing store for an `IReferenceable.RefId`. The reference
type is resolved at edit time from the host's `RefType`, and the Inspector adds a refresh button plus
context-menu actions for the field. After regenerating an id, a dialog offers to redirect any
`SceneObjectReference` fields in loaded scenes that pointed at the old id; unloaded scenes must be
updated by a project scan.

```csharp
using UnityEngine;
using Molca.Attributes;

[RefId]
[SerializeField] private string _refId;
```

See [Reference System](REFERENCE_SYSTEM.md) for how `RefId` values connect scene objects across
scenes.

## See also

- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [Subsystems](SUBSYSTEMS.md)
- [Runtime Manager](RUNTIME_MANAGER.md)
- [Reference System](REFERENCE_SYSTEM.md)
