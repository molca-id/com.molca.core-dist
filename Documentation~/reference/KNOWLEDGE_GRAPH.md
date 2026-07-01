# Molca Knowledge Graph (graphify integration)

The Molca Assistant (and IDE MCP clients) can answer open-ended questions about the **whole
project** — code, asset wiring, and docs — by querying a [graphify](https://graphify.net)
knowledge graph. This is the *static knowledge* layer; it complements the **Framework Graph**
(`Molca ▸ Utilities ▸ Framework Graph`), which is the *live runtime* layer (subsystems, services,
resolved init order). Use the knowledge graph for "how does X work / what depends on Y / where is
Z configured"; use the Framework Graph for "what is wired at runtime right now".

Molca does **not** reimplement GraphRAG. It feeds graphify a Unity-aware corpus and drives
build / query as MCP tools. graphify is an **optional external dependency** — if it isn't
installed, the knowledge-graph tools simply report that and everything else keeps working.

## Prerequisites

1. Install graphify so the `graphify` command is on your PATH. See https://graphify.net for the
   installer (it runs on Python; a `uv`/`pipx` install works too). Verify with `graphify --help`.
2. graphify uses an LLM to extract entities and relationships, so configure its model/key per the
   graphify docs. **Indexing sends project content (code, docs, exported facts) to graphify's
   configured LLM provider** — see *Privacy & cost* below.

## Building the graph

From Unity: open **Project Settings ▸ Molca ▸ MCP** (the `McpSettings` inspector) → the
**Knowledge Graph (graphify)** section → **Build Graph**. The status dot turns green and shows the
last-built time when a graph exists. **Update Graph** runs an incremental re-index; **Full Rebuild**
re-extracts from scratch.

Or drive it as a tool (IDE / assistant): `molca_kg_build` (an allowlisted, confirmed Action).

Either path first refreshes the **Unity facts corpus** (below), then runs graphify over `Assets/`
plus that corpus, producing `graphify-out/graph.json`.

## What's in the corpus

graphify indexes the raw files under `Assets/` (C# source, markdown docs, `.claude/` knowledge
files) directly. On top of that, an exporter writes Unity structure that a raw-text scan
can't see into `graphify-corpus/`:

- **`molca-types.md`** — the `TypeCache` type graph: concrete `RuntimeSubsystem`s (with their
  `[DependsOn]` edges), `SettingModule`s, `Step`s, and `StepAuxiliary`s, each with base type and
  `[CreateAssetMenu]` provenance.
- **`molca-assets.md`** — the `AssetDatabase` dependency graph: each prefab / scene /
  ScriptableObject under `Assets/` and the project assets it references.

Both `graphify-out/` and `graphify-corpus/` are build artifacts — add them to `.gitignore`.

## Asking questions

- In the **Assistant chat** (`Molca ▸ Assistant Chat`): just ask. For project-wide questions the
  assistant checks `molca_kg_status` and answers from `molca_kg_query` instead of guessing.
- As tools (IDE / MCP):
  - `molca_kg_status` — is a graph built, where, when (read-only).
  - `molca_kg_query` — natural-language question (read-only). Optional `dfs` (trace one path) and
    `budget` (cap answer tokens).
  - `molca_kg_path` — shortest relationship path between two concepts (read-only).
  - `molca_kg_explain` — plain-language explanation of one node (read-only).
  - `molca_kg_build` — (re)build the graph (Action: allowlist + confirmation + audit).

## Extending the facts for an SDK fork

A fork that adds its own subsystems/steps gets them in the type graph automatically (the exporter
reflects over all derived types in the loaded assemblies). To contribute **extra** domain facts,
write additional markdown into the corpus folder (`graphify-corpus/`) before a build — e.g. from a
fork editor hook — using the same relationship-prose style (`"X depends on Y"`, `"A references B"`) so
graphify extracts typed edges. Do not modify Core to do this; write to the corpus folder.

## Privacy & cost

- **Privacy:** building the graph sends project content to graphify's configured LLM provider.
  Don't index a project whose source/docs you can't share with that provider. The corpus contains
  type names, asset paths, and your docs — review what graphify is configured to send.
- **Cost:** extraction over a large project costs LLM tokens and minutes. Prefer **Update Graph**
  (incremental) over **Full Rebuild** for day-to-day refreshes.
- **No hard dependency:** Core ships no graphify dependency. The tools degrade gracefully
  ("graphify CLI not found") when it isn't installed, so a project that never uses the knowledge
  graph is unaffected.
