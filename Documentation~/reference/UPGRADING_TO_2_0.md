---
title: Upgrading Core 1.x to 2.0
category: Getting Started
order: 20
---

# Upgrading Molca Core 1.x to 2.0

Core 2.0 removes retired runtime APIs, but it deliberately keeps their editor-only migrators. You can
upgrade directly from any supported 1.x release; you do not need to install an intermediate version first.

Before changing the package reference, commit or back up the project. The migrators use Unity Undo where
the asset API supports it, but a repository checkpoint is the reliable rollback boundary for a project-wide
upgrade.

## Upgrade workflow

1. Install `com.molca.core` 2.0.0 and let Unity finish importing and compiling.
2. Core runs the read-only 1.x → 2.x audit once. If it finds anything, choose **Review Upgrade** to open
   **Molca Hub → Remediation**. You can reopen the report at any time from **Molca → Upgrade → Report 1.x →
   2.x Readiness**.
3. Resolve any colour-theme prerequisite reported by the audit. A project with legacy colour data must have
   exactly one validated `ColorThemeSet`, referenced by one installed `ColorThemeSettings` module. Review
   the 1.x palette values in **Molca Hub → Themes** before creating or choosing that contract; Core will not
   silently replace a customized palette with its stock vocabulary.
4. Preview and apply the Upgrade remediation domain. It migrates legacy colour components and references,
   localized values, UI token catalogs, and network catalog state only where the rewrite is locally
   decidable and verifiable. Re-run the report after resolving a prerequisite to enable its colour fixes.
5. Resolve findings that deliberately have no button: update consumer C# using the table below, review any
   serialized `ColorID.SetColorId` UnityEvent at its reported file/line and callback context, and decide whether
   the retired `com.molca.sdk` package dependency should be removed from the project manifest. Core does not
   guess a replacement token for an authored callback.
6. Run the report again. The project is upgraded only when it is clean and conclusive, then run the project's
   own EditMode, PlayMode, and build validation.

Legacy colour components can still be migrated after their C# classes have been deleted. Unity serializes a
`MonoBehaviour` reference by script GUID, so Core locates the missing component by that durable identity,
reads its serialized payload, translates it through the project's reviewed alias map, writes canonical 2.x
bindings, verifies the result, and only then removes the legacy component.

## Consumer API replacements

This table is generated from `RetiredApiUsageDetector.Retired`, the same source used by the live source-code
report. A package test fails if this checked-in table and the detector ever differ.

<!-- BEGIN GENERATED RETIRED API TABLE -->
| Retired or changed 1.x API | 2.x replacement |
| --- | --- |
| `ColorID` | ColorThemeBinding — one component holding several tokens, each naming its own target |
| `ColorIDReference` | ColorTokenReference — holds a canonical token id instead of a (swatch, colorId) pair |
| `ColorModule` | ColorThemeSet plus a ColorThemeSettings module; palettes are authored as theme variants |
| `IColorProvider` | IColorThemeService, resolved with RuntimeManager.GetService&lt;IColorThemeService&gt;() |
| `IColorSchemeService` | IColorThemeService — SetVariant replaces the scheme calls |
| `ColorSchemeManager` | IColorThemeService; the subsystem still exists but is consumed through the interface |
| `ColorTargetApplier` | ColorTargetAdapterRegistry.Apply(component, channel, colour) |
| `ColorUtility` | ColorThemeBinding for persistent tokens, or explicit IColorThemeService resolution for one-off colour |
| `BooleanColor` | no replacement — it had no users; hold two ColorTokenReference fields |
| `MolcaSDK` | Molca.App — the namespace and assembly were renamed |
<!-- END GENERATED RETIRED API TABLE -->

## Content-specific guidance

- Colour themes: see [Colour Theme Migration](COLOR_ID_MIGRATION.md) for alias resolution, component
  conversion, and validation details.
- Localization: retained schema-v1 payloads are inventoried before conversion; unknown or duplicate locale
  rows stay visible for a person instead of being guessed away.
- UI tokens and networking: the report upgrades UI-token catalogs, legacy routed-network state, and network
  catalog schemas before content domains consume them. Legacy network assets remain in place; the migration
  authors catalog entries alongside them and leaves credential scope for review.
- Consumer source: Core reports every matching `Assets/**/*.cs` location as `file:line`; it never rewrites
  code owned by the project.

If the project does not compile after the package change, use the replacement table to remove retired API
references first, allow Unity to compile, and then run the unified report for serialized content.
