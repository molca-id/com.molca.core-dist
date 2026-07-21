---
title: Onboarding Wizard
category: Tooling
order: 920
---

# Onboarding Wizard

The Onboarding Wizard (menu **Molca → Onboarding Wizard**) is the first-run setup surface for a project
that has just installed `com.molca.core` (and optionally `com.molca.sdk`). It walks through a handful of
**optional, idempotent** setup steps — each safe to run more than once — that seed project-space config
and coding-agent instructions. It never writes into `Packages/`; the read-only packages stay untouched.

> The wizard is **post-compile only**: a fresh project compiles first (that's the package's job via its
> declared dependencies and shipped content), *then* the wizard offers convenience setup. It is never
> the thing that makes the project compile.

## Steps

| Step | What it does |
|---|---|
| **Project Settings** | Clones the read-only Core `MolcaProjectSettings` default into consumer space at `Assets/_Molca/Settings/MolcaProjectSettings.asset` (keeps an existing one; offers *Repair* to overwrite). See [Settings](SETTINGS.md). |
| **SDK Starter Settings** | If `com.molca.sdk` is installed, seeds its held-back bootstrap config (GlobalSettings, input actions, lighting) into `Assets/_MolcaSDK/Settings/` via the SDK's Quick Setup installer — *Install (keep existing)* or *Repair (overwrite)*. |
| **Coding-Agent Instructions** | Generates a project-root `CLAUDE.md` that points at the installed packages' `Documentation~/reference/` guides and states that Core/SDK are read-only and consumers extend from project space. Only writes when absent. |

Each step reports its own status (created / already present / not yet run) so you can see at a glance
what a fresh project still needs. Because the steps are idempotent, re-running the wizard on an
already-configured project is harmless.

## When to use it

Run it once right after installing the packages into a new project. Everything it does is also
reachable individually later (Project Settings opens the same asset the Hub does; the Quick Setup
installer is available from the SDK), so the wizard is a convenience, not a gate.

## See also

- [The Molca Hub](HUB.md)
- [Getting Started](GETTING_STARTED.md)
- [Settings: Project, Global & Modules](SETTINGS.md)
- [Build System & Versioning](BUILD_SYSTEM.md)
