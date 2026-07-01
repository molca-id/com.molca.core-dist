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

## Documentation layout

- **`reference/`** — evergreen reference docs describing how the framework works *now*:
  - [`EDITOR_DESIGN_LANGUAGE.md`](reference/EDITOR_DESIGN_LANGUAGE.md) — design reference for Molca custom
    editor windows and editor UI refactors.
  - [`SEQUENCE_AUTHORING.md`](reference/SEQUENCE_AUTHORING.md) /
    [`SEQUENCE_VALIDATION.md`](reference/SEQUENCE_VALIDATION.md) — `molca_sequence_author` and the
    `ISequenceValidator` gate.
  - [`MCP_FORK_PROVIDERS.md`](reference/MCP_FORK_PROVIDERS.md) /
    [`UNITY_MCP_TOOLS.md`](reference/UNITY_MCP_TOOLS.md) — MCP provider fork pattern and the Unity tool set.
  - [`KNOWLEDGE_GRAPH.md`](reference/KNOWLEDGE_GRAPH.md) — the generated type/asset knowledge graph.
- **`sprints/`** — the development process journal:
  - [`SPRINT_PLAN.md`](sprints/SPRINT_PLAN.md) — the active sprint plan (31+) and Cross-Sprint Rules.
  - [`SPRINT_PLAN_ARCHIVE.md`](sprints/SPRINT_PLAN_ARCHIVE.md) — the canonical history of shipped sprints,
    each with a self-contained completion summary.
  - [`REFERENCE_SYSTEM_SPRINT_PLAN.md`](sprints/REFERENCE_SYSTEM_SPRINT_PLAN.md) — reference-system plan.

Per-sprint completion notes are folded into the matching `SPRINT_PLAN`/`SPRINT_PLAN_ARCHIVE` entry; the
full blow-by-blow for any sprint lives in git history.
