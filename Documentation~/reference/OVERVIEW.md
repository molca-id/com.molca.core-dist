---
title: Molca Core Overview
category: Getting Started
order: 0
---

# Molca Core Overview

Molca Core is a general-purpose Unity application framework. It provides the foundational systems an
app needs — bootstrap, dependency injection, events, scene references, sequences, networking, modals,
settings, audio — as one coherent, platform-agnostic layer. Domain SDKs and your project build **on**
Core by subclassing; they never modify it. Start here for the shape of the framework, then follow
[Getting Started](GETTING_STARTED.md) to build something.

## The layer model

```
Your project content   (scenes, custom steps, scenario assets)
   ↓ extends via subclass only
SDK layer              (domain-specific: shared SDK, VR, digital-twin, …)
   ↓ extends via subclass only
Molca Core             (the systems below)
   ↓
Unity Engine
```

- **Core** is platform-agnostic and defines all foundational systems.
- **SDK layers** add domain content on top of Core (see the [SDK Overview](SDK_OVERVIEW.md)).
- **Project content** extends the SDK layer. You never edit Core or an SDK layer — you subclass from
  project space.

Core ships as the read-only UPM package `com.molca.core`.

## The pillars — start here

| System | What it does | Guide |
|---|---|---|
| Runtime Manager & bootstrap | Owns persistent objects, runs the startup sequence, resolves subsystems. | [Runtime Manager](RUNTIME_MANAGER.md) |
| Subsystems | Long-lived services with an async lifecycle. | [Subsystems](SUBSYSTEMS.md) |
| Dependency injection | `[Inject]` + a service container instead of singletons/`FindObjectOfType`. | [Dependency Injection](DEPENDENCY_INJECTION.md) |
| Async contract | One `Awaitable`/cancellation convention across the framework. | [Async Contract](ASYNC_CONTRACT.md) |
| Events | Decoupled `EventDispatcher` messaging with typed payloads. | [Events](EVENTS.md) |
| Scene references | Cross-scene wiring without serialized references. | [Scene Reference System](REFERENCE_SYSTEM.md) |
| Sequences | Step-based flows with auxiliaries. | [Sequences](SEQUENCES.md) |
| Networking & data | HTTP requests + live streaming data providers. | [Networking](NETWORKING.md) · [Data Providers](DATA_PROVIDERS.md) |
| UI & presentation | Modals, ColorID theming, UI tokens. | [Modals](MODALS.md) · [Color ID](COLOR_ID.md) · [UI Tokens](UI_TOKENS.md) |
| Content & assets | Addressable DLC packages. | [Content Packages](CONTENT_PACKAGES.md) |
| Localization & audio | Locale-aware text and audio. | [Localization](LOCALIZATION.md) · [Audio](AUDIO.md) |
| Settings | Project/global config + setting modules. | [Settings](SETTINGS.md) |
| Tooling | The Hub, build system, MCP, Doctor, docs. | [The Molca Hub](HUB.md) · [Build System](BUILD_SYSTEM.md) |

## Core design principles

- **No static singletons** — use a [subsystem](SUBSYSTEMS.md) or the [DI container](DEPENDENCY_INJECTION.md).
- **No `FindObjectOfType<T>()` for services** — resolve with `GetSubsystem<T>()` or `[Inject]`.
- **No cross-scene serialized references** — use the [scene reference system](REFERENCE_SYSTEM.md).
- **ScriptableObjects are read-only config** — mutable state lives in C# objects (e.g. `SettingState`).
- **RuntimeManager owns persistence** — never call `DontDestroyOnLoad` yourself.
- **One async convention** — return `Awaitable`, thread a `CancellationToken`; see the
  [Async Contract](ASYNC_CONTRACT.md).

## See also

- [Getting Started](GETTING_STARTED.md)
- [Runtime Manager & Bootstrap](RUNTIME_MANAGER.md)
- [Dependency Injection](DEPENDENCY_INJECTION.md)
- [SDK Overview](SDK_OVERVIEW.md)
