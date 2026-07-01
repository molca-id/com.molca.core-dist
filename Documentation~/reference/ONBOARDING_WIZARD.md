# Onboarding Wizard — Contract

Defines the first-interaction setup surface for a project that has just installed `com.molca.core`
(and optionally `com.molca.sdk`) from a dist Git-URL. This is a **contract**, not the implementation —
it locks what the wizard may and may not do so the standalone-closure guarantees from Sprint 63 are never
undermined by setup convenience. (Sprint 63.7.)

## The one hard rule

**The wizard is post-compile only.** A fresh project must compile *before* the wizard is available or
runs. The wizard may never be the mechanism that satisfies a compile-time dependency — that is the
package's job (declared `dependencies` + shipped package content). If something is required for Core/SDK
to compile, it belongs in `package.json` or in the package, not in a wizard step.

Corollary: the wizard's entry point must not be code that only compiles once setup has run. It hangs off
already-compiling editor surfaces (Hub, a menu item, an `InitializeOnLoad` check that *offers* setup).

## What the wizard MAY do (all post-compile, all opt-in)

- **Create/repair project settings in consumer space.** Clone the read-only package default
  `MolcaProjectSettings` into `Assets/_Molca/Settings/` (idempotent: keep existing, offer repair). Editor-
  only settings go through `MolcaEditorSettingsAsset.GetOrCreate<T>` → `Assets/_Molca/Editor/`. For the SDK
  layer, seed the held-back bootstrap config via `QuickSetupInstaller` (templates → `Assets/_MolcaSDK/
  Settings/`). Never write into `Packages/`.
- **Generate coding-agent instruction stubs** (`AGENTS.md` / `CLAUDE.md` / editor rules) that point at the
  **resolved installed-package docs** (e.g. `Documentation~/reference/EDITOR_DESIGN_LANGUAGE.md`), and state
  that Core/SDK are read-only and consumers extend from project space. Must not clobber unrelated user
  content (merge/append, or write only when absent).
- **Offer to build the MCP proxy** from the installed package's `Tools~/molca-mcp` source — copied into a
  writable `<project>/molca-mcp/` first (package is immutable), then `npm install && npm run build`.
- **Offer optional tooling checks** — Graphify presence/build, sample import, a Doctor smoke run.

## What the wizard MUST NOT do

- Install or substitute for a **compile-time dependency** (package dep or shipped content).
- Write into `Packages/com.molca.core` or `Packages/com.molca.sdk` (immutable).
- Mutate `Packages/manifest.json` **without explicit confirmation**.
- Run automatically in a way that blocks or precedes first compile.
- Create a second long-running hosted tool controller (per `EDITOR_DESIGN_LANGUAGE.md`) — it opens a
  workspace/section or a standalone window, then exits.

## Acceptance (ties to CORE_DIST_CONSUMER_VALIDATION.md §2–§3)

1. Fresh project + dist package → **compiles with zero errors before the wizard is touched**.
2. Running the wizard creates settings only under `Assets/`, never in `Packages/`.
3. Skipping the wizard entirely still leaves a compiling, runnable project (settings auto-seed lazily —
   Core's `MolcaProjectSettings.Instance` already clones-on-first-access).
4. Re-running the wizard is idempotent (keep-existing default; explicit repair/overwrite).

## Status

Contract locked (63.7). Implementation is a follow-up (onboarding sprint); the lazy auto-seed paths that
already exist (`MolcaProjectSettings`, `QuickSetupInstaller`) satisfy the "skippable" acceptance today, so
the wizard is additive polish, not a gate.
