# Molca Core

General-purpose Unity application framework.

## Installation

Add via Unity Package Manager using a Git URL:

```
https://github.com/molca-id/com.molca.core-dist.git#1.16.2
```

Or SSH:

```
ssh://git@github.com/molca-id/com.molca.core-dist.git#1.16.2
```

Replace `1.16.2` with the version tag you want.

## Requirements

- Unity 2022.3+
- Addressables 2.0+
- Localization 1.4+
- Input System 1.7+

## Documentation

The [`reference/`](reference/) folder holds evergreen guides describing how the framework works *now*.
Each guide carries YAML front-matter (`title`/`category`/`order`); the categories, in rail order, are:

- **Getting Started** — [`OVERVIEW.md`](reference/OVERVIEW.md), [`GETTING_STARTED.md`](reference/GETTING_STARTED.md).
- **Runtime & Core** — [`RUNTIME_MANAGER.md`](reference/RUNTIME_MANAGER.md),
  [`SUBSYSTEMS.md`](reference/SUBSYSTEMS.md), [`DEPENDENCY_INJECTION.md`](reference/DEPENDENCY_INJECTION.md),
  [`ASYNC_CONTRACT.md`](reference/ASYNC_CONTRACT.md), [`EVENTS.md`](reference/EVENTS.md),
  [`ATTRIBUTES.md`](reference/ATTRIBUTES.md).
- **Scene & References** — [`REFERENCE_SYSTEM.md`](reference/REFERENCE_SYSTEM.md), and
  [`REFERENCE_SYSTEM_MIGRATION.md`](reference/REFERENCE_SYSTEM_MIGRATION.md) for upgrading a project or
  fork to scoped references.
- **Sequences** — moved to the `com.molca.sequence` add-on: [`SEQUENCES.md`](../../com.molca.sequence/Documentation~/reference/SEQUENCES.md),
  [`SEQUENCE_AUTHORING.md`](../../com.molca.sequence/Documentation~/reference/SEQUENCE_AUTHORING.md),
  [`SEQUENCE_VALIDATION.md`](../../com.molca.sequence/Documentation~/reference/SEQUENCE_VALIDATION.md).
- **Data & Networking** — [`NETWORKING.md`](reference/NETWORKING.md),
  [`NETWORKING_CATALOG.md`](reference/NETWORKING_CATALOG.md),
  [`NETWORKING_MIGRATION.md`](reference/NETWORKING_MIGRATION.md),
  [`DATA_PROVIDERS.md`](reference/DATA_PROVIDERS.md).
- **UI & Presentation** — [`MODALS.md`](reference/MODALS.md), [`COLOR_ID.md`](reference/COLOR_ID.md),
  [`COLOR_ID_MIGRATION.md`](reference/COLOR_ID_MIGRATION.md),
  [`UI_TOKENS.md`](reference/UI_TOKENS.md), [`UI_INTENT_SPEC.md`](reference/UI_INTENT_SPEC.md).
- **Content & Assets** — [`CONTENT_PACKAGES.md`](reference/CONTENT_PACKAGES.md).
- **Localization & Audio** — [`LOCALIZATION.md`](reference/LOCALIZATION.md),
  [`LOCALIZATION_REMOTE_CATALOGS.md`](reference/LOCALIZATION_REMOTE_CATALOGS.md),
  [`AUDIO.md`](reference/AUDIO.md).
- **Settings** — [`SETTINGS.md`](reference/SETTINGS.md).
- **Tooling** — [`HUB.md`](reference/HUB.md), [`BUILD_SYSTEM.md`](reference/BUILD_SYSTEM.md),
  [`ONBOARDING.md`](reference/ONBOARDING.md), [`EDITOR_DESIGN_LANGUAGE.md`](reference/EDITOR_DESIGN_LANGUAGE.md),
  [`REMOTE_EDITOR.md`](reference/REMOTE_EDITOR.md),
  [`DOCS_AUTHORING.md`](reference/DOCS_AUTHORING.md), [`CORE_MCP_TOOLS.md`](reference/CORE_MCP_TOOLS.md),
  [`UNITY_MCP_TOOLS.md`](reference/UNITY_MCP_TOOLS.md), [`MCP_FORK_PROVIDERS.md`](reference/MCP_FORK_PROVIDERS.md),
  [`KNOWLEDGE_GRAPH.md`](reference/KNOWLEDGE_GRAPH.md).
- **Assistant** — the `ASSISTANT_*.md` guides (resilience, model switcher, reasoning, vision, web tools,
  memory, prompt caching, and the text tool protocol).
- **Diagnostics** — [`DOCTOR_CHECKS.md`](reference/DOCTOR_CHECKS.md),
  [`TELEMETRY.md`](reference/TELEMETRY.md), [`UTILITIES.md`](reference/UTILITIES.md).
- **SDK** — the shared SDK layer ships its own `reference/` guides in `com.molca.sdk` (auto-discovered).

All of these are browsable in-editor from **Molca → Hub → Docs**, rendered natively. Coverage is enforced
by the `docs-coverage` Doctor check (one reference guide per `Runtime/*` system). To add a guide, drop a
Markdown file with front-matter here — see [`DOCS_AUTHORING.md`](reference/DOCS_AUTHORING.md).
