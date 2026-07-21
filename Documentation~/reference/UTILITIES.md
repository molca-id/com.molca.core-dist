---
title: Utilities
category: Diagnostics
order: 1120
---

# Utilities

Small, dependency-light helpers under `Runtime/Utilities/` that the rest of the framework — and your
project — can lean on: object pooling, JSON, scene loading, byte/string formatting, and a runtime
performance HUD. Everything here is verified against the shipped source; use them rather than
re-implementing the same helper in project space.

## ObjectPool&lt;T&gt;

A reuse pool for `Component`-typed prefabs — avoids per-spawn `Instantiate`/`Destroy` churn.

```csharp
var pool = new ObjectPool<Projectile>(prefab, initialPoolSize: 20, root: poolRoot);
Projectile p = pool.Get();        // grows the pool if empty
// ...
pool.Return(p);                   // back into the pool (also fires OnObjectReturned)
```

| Member | Purpose |
|---|---|
| `Get()` / `Return(obj)` / `ReturnAll()` | Acquire, release, release-all. |
| `IncreaseSize(n)` / `Clear()` | Pre-warm / tear down. |
| `ActiveCount` / `PooledCount` / `TotalObjects` | Live counts. |
| `OnObjectReturned` | Callback per returned instance (e.g. reset state). |

## JsonHelper

Static JSON helpers built on Unity's `JsonUtility`, plus key-level access `JsonUtility` lacks:

| Method | Purpose |
|---|---|
| `FromJson<T>(json)` / `ToJson(obj, prettyPrint = false)` | Object ↔ JSON. |
| `TryGetValue<T>(json, key, out value)` / `GetValue<T>(json, key)` | Read a single field. |
| `ExtractBlock(json, blockName)` | Pull a nested object as a raw string. |
| `IsValidJson(s)` | Cheap validity check. |
| `MergeJsonObjects(params json[])` / `UpdateValue(json, key, newValue)` | Compose / patch. |

## SceneUtility

A ScriptableObject that wraps scene loading (by name, `SharedString`, `AssetReference`, or Addressable
address), so a scene transition can be wired from the Inspector or a [Sequence step](SEQUENCES.md)
without bespoke code:

```csharp
sceneUtility.LoadScene("MainMenu");
sceneUtility.LoadSceneAdditive(areaSceneRef);
sceneUtility.LoadNextScene();
```

## BudgetMonitor

A `RuntimeSubsystem` that shows a runtime performance HUD (FPS, memory, counts, percentages) and raises
threshold events when a metric enters warning/critical range. Budgets come from platform-specific
`BudgetSettings` assets (shipped for **PC**, **Mobile**, and **Quest**):

```csharp
var monitor = RuntimeManager.GetSubsystem<BudgetMonitor>();
monitor.BudgetThresholdCrossed += e => Debug.LogWarning($"budget: {e}");
monitor.ToggleVisibility();
bool overBudget = monitor.HasCriticalBreach;
```

`SetVisible(bool)`, `ToggleVisibility()`, `GetMetricsSnapshot()`, and `HasWarningsOrErrors()` round out
the surface. It pairs with the scene-performance [Doctor checks](DOCTOR_CHECKS.md) — the monitor is the
runtime view, the checks are the static audit.

## Formatting & string helpers

| Helper | Purpose |
|---|---|
| `ByteSizeFormatter.Format(bytes)` | Human-readable sizes (e.g. `1.5 MB`). |
| `RandomStringGenerator.Generate/GenerateSecure/GenerateGuid` | Random ids and tokens. |
| `StringUtility.IsFilenameSafe / EnsureFilenameSafe` | Validate / sanitize file names. |
| `TopologicalSort` | Dependency ordering (used by `[DependsOn]` resolution). |
| `SharedString`, `SceneReference`, `BuildInfo` | Serializable value types for Inspector wiring. |

## See also

- [Telemetry & Diagnostics](TELEMETRY.md)
- [Async Contract](ASYNC_CONTRACT.md)
- [Sequences: Controller, Steps & Auxiliaries](SEQUENCES.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
