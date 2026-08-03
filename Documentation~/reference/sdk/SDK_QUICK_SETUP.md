---
title: Legacy Quick Setup
category: Getting Started
order: 20
---

# Legacy Quick Setup

Core 2.0 retains the 1.x Quick Setup menu as a compatibility path for projects that still use the
`Assets/_MolcaSDK/Settings/` layout. New projects should instead import **Starter Project Content** from
Package Manager and run the starter rows under **Hub → Onboarding**. The sample supplies owned
content; the starter generates editable settings directly in project space.

## Running it

From the menu:

- **Molca → SDK → Quick Setup → Install Starter Settings** — copies the starter settings, **keeping**
  any files you already have (idempotent; safe to run repeatedly).
- **Molca → SDK → Quick Setup → Repair (Overwrite) Starter Settings** — re-copies, **overwriting**
  existing files. Use this to reset to the shipped defaults.

Both land the settings under `Assets/_MolcaSDK/Settings/` to preserve 1.x paths and GUIDs. They are no
longer offered by [Onboarding](ONBOARDING.md). The compatibility installer now lives inside
Core as `Molca.App.Editor.Setup.QuickSetupInstaller`; no reflective cross-package call remains.

## What it installs

The legacy starter settings are the former SDK bootstrap scaffolding — the shared `GlobalSettings` module list,
input actions, and lighting configuration a fresh SDK app expects. They are seeded into project space
so you can edit them without touching the package; the package itself stays immutable.

After running it, open **Project Settings → Molca Settings** (or the [Hub](HUB.md)) to review the
seeded configuration, and see [Settings](SETTINGS.md) for how the modules work.

## See also

- [SDK Overview](SDK_OVERVIEW.md)
- [Getting Started](GETTING_STARTED.md)
- [App Flow](SDK_APP_FLOW.md)
- [Onboarding](ONBOARDING.md)
