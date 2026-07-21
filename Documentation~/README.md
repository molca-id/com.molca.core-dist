# Molca Core

General-purpose Unity application framework.

## Installation

Add via Unity Package Manager using a Git URL:

```
https://github.com/molca/framework-unity.git?path=Packages/com.molca.core#1.0.0
```

Or SSH:

```
ssh://git@github.com/molca/framework-unity.git?path=Packages/com.molca.core#1.0.0
```

Replace `1.0.0` with a tag or branch name.

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
- **Scene & References** — [`REFERENCE_SYSTEM.md`](reference/REFERENCE_SYSTEM.md).
- **Sequences** — [`SEQUENCES.md`](reference/SEQUENCES.md),
  [`SEQUENCE_AUTHORING.md`](reference/SEQUENCE_AUTHORING.md),
  [`SEQUENCE_VALIDATION.md`](reference/SEQUENCE_VALIDATION.md).
- **Data & Networking** — [`NETWORKING.md`](reference/NETWORKING.md),
  [`DATA_PROVIDERS.md`](reference/DATA_PROVIDERS.md).
- **UI & Presentation** — [`MODALS.md`](reference/MODALS.md), [`COLOR_ID.md`](reference/COLOR_ID.md),
  [`UI_TOKENS.md`](reference/UI_TOKENS.md), [`FIGMA_TO_UGUI.md`](reference/FIGMA_TO_UGUI.md).
- **Content & Assets** — [`CONTENT_PACKAGES.md`](reference/CONTENT_PACKAGES.md).
- **Localization & Audio** — [`LOCALIZATION.md`](reference/LOCALIZATION.md), [`AUDIO.md`](reference/AUDIO.md).
- **Settings** — [`SETTINGS.md`](reference/SETTINGS.md).
- **Tooling** — [`HUB.md`](reference/HUB.md), [`BUILD_SYSTEM.md`](reference/BUILD_SYSTEM.md),
  [`ONBOARDING.md`](reference/ONBOARDING.md), [`EDITOR_DESIGN_LANGUAGE.md`](reference/EDITOR_DESIGN_LANGUAGE.md),
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
