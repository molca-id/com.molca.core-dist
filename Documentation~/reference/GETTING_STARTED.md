---
title: Getting Started
category: Getting Started
order: 10
---

# Getting Started

This guide takes you from an empty project to a running Molca app with your first subsystem. It assumes
you've skimmed the [Overview](OVERVIEW.md) for the layer model.

## 1. Install the package

Add Core via the Unity Package Manager using a Git URL (**Add package from git URL…**):

```
https://github.com/molca/framework-unity.git?path=Packages/com.molca.core#1.0.0
```

Replace `1.0.0` with the tag or branch you want. To also use the shared SDK layer, add
`com.molca.sdk` the same way (it declares `com.molca.core` as a dependency). Everything under
`Packages/com.molca.*` is **read-only** — you extend it from your own `Assets/` folder, never by
editing the package.

## 2. Configure the project

Core boots from a `MolcaProjectSettings` asset (which points at your `RuntimeManager` prefab and
`GlobalSettings`). Run **Molca → Onboarding Wizard** to seed these into consumer space — it clones the
read-only Core defaults into `Assets/_Molca/Settings/` (idempotent; see [Onboarding](ONBOARDING.md) and
[Settings](SETTINGS.md)). The wizard is post-compile convenience only; the project compiles from the
package alone.

At runtime, `RuntimeManager` instantiates itself from that settings asset, runs the bootstrap sequence,
and initializes every subsystem on its prefab. You don't call `DontDestroyOnLoad` or manage
persistence — the manager owns it.

## 3. Write your first subsystem

A subsystem is a long-lived service with an async lifecycle. Put custom subsystems in
`Assets/YourProject/Scripts/Subsystems/`, derive from `RuntimeSubsystem`, and place the component on the
RuntimeManager prefab so it's discovered at bootstrap.

```csharp
using System.Threading;
using UnityEngine;

namespace YourProject
{
    /// <summary>Tracks the player's score for the current session.</summary>
    public class ScoreSubsystem : RuntimeSubsystem
    {
        public int Score { get; private set; }

        // Preferred: async init with a bootstrap-lifetime token.
        public override async Awaitable InitializeAsync(CancellationToken cancellationToken)
        {
            await LoadHighScoresAsync(cancellationToken);   // your setup work
        }

        public void Add(int points) => Score += points;
    }
}
```

Override **one** initialization path — the async `InitializeAsync` (new code) or the legacy
`Initialize(finishCallback)` shim. See the [Async Contract](ASYNC_CONTRACT.md) and
[Subsystems](SUBSYSTEMS.md) for the full lifecycle and cancellation rules.

## 4. Use it — after initialization

Never touch a subsystem before bootstrap finishes. In a MonoBehaviour, await
`RuntimeManager.WaitForInitialization()` first, then resolve with `GetSubsystem<T>()`:

```csharp
public class ScoreHud : MonoBehaviour
{
    private async void Start()   // async void is allowed only as a Unity entry-point shim
    {
        try
        {
            await RuntimeManager.WaitForInitialization();
            if (this == null) return;                       // post-await destroy check

            var score = RuntimeManager.GetSubsystem<ScoreSubsystem>();
            _label.text = score.Score.ToString();
        }
        catch (System.Exception e) { Debug.LogError(e); }   // shim must not leak exceptions
    }
}
```

Or let the container inject it for you with `[Inject]` — see [Dependency Injection](DEPENDENCY_INJECTION.md).

## Next steps

- Build a step-based flow: [Sequences](SEQUENCES.md).
- Wire objects across scenes: [Scene Reference System](REFERENCE_SYSTEM.md).
- Talk to a backend: [Networking](NETWORKING.md).
- Check your project: run **Molca → Hub → Doctor** ([Doctor](DOCTOR_CHECKS.md)).

## See also

- [Molca Core Overview](OVERVIEW.md)
- [Runtime Manager & Bootstrap](RUNTIME_MANAGER.md)
- [Runtime Subsystems](SUBSYSTEMS.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [SDK Overview](SDK_OVERVIEW.md)
