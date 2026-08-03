---
title: Overview
category: Getting Started
order: 10
---

# Overview

The **Molca application layer** is common application scaffolding that sits above Core's foundational
assemblies and below domain-specific forks and project code. In 1.x it shipped separately as
`com.molca.sdk`; Core 2.0 folds it into `com.molca.core`, where its types use the `Molca.App` namespace.
It provides the pieces almost every Molca app needs: an app-level `GameManager`, auth UI, media loading,
a modal library, uGUI building blocks, a preload phase, and general utilities.

## Layer position

```
Project content        (your scenes, screens, scenario assets)
   ↓ subclass only
Molca.App assemblies   (app layer — auth, media, modals, home, preload, UI widgets)
   ↓ subclass only
Core assemblies        (RuntimeManager, DI, Events, Networking, Modals, Settings…)
```

The whole package is **read-only**: extend the app layer from project space by subclassing, never by
editing it. Import **Starter Project Content** from Package Manager for owned fonts, prefabs, input,
lighting, and localization assets; then run the starter rows under **Hub → Onboarding** for generated
settings. The retained [Quick Setup](SDK_QUICK_SETUP.md) menu exists only for 1.x layout compatibility.

## Feature areas

| Area | Guide | What it adds |
|---|---|---|
| App flow | [App Flow](SDK_APP_FLOW.md) | `GameManager`, the `Preload` phase, and `Home` screens. |
| Auth | [Auth](SDK_AUTH.md) | Login/guest UI on top of Core's `AuthManager`. |
| Media | [Media](SDK_MEDIA.md) | Cached async image/video/document loading. |
| Modals | [SDK Modals](SDK_MODALS.md) | A library of concrete modals over Core's `BaseModal`. |
| UI | [SDK UI](SDK_UI.md) | uGUI widgets bound to ColorID, Localization, and UI tokens. |
| Utilities | [SDK Utilities](SDK_UTILITIES.md) | Helpers including a ZXing-based QR scanner. |

## Forks build on this

Fork-specific documentation — VR interaction, digital-twin sync, and other domain layers — does **not**
live here. Each fork (`molca-sdk-vr`, `molca-sdk-dt`, …) is its own `com.molca.*` package and drops its
`Documentation~/reference/*.md` guides into itself; Core's docs provider scans every installed
`com.molca.*` package, so a fork's guides appear in the Hub docs browser automatically alongside these.

## See also

- [Molca Core Overview](OVERVIEW.md)
- [Quick Setup](SDK_QUICK_SETUP.md)
- [App Flow](SDK_APP_FLOW.md)
- [Getting Started](GETTING_STARTED.md)
