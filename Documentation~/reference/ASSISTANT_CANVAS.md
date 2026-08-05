---
title: Assistant Canvas & Composer
category: Assistant
order: 1035
---

# Assistant Canvas & Composer

The assistant chat window is a **split workspace**: the conversation on the left, an **artifact canvas** on
the right. The transcript stays the conversation of record; the canvas is where structured things the
assistant produces — diagrams, audit findings, workflow proposals and runs — are shown and operated.

## The canvas

- Toggle the pane with the **canvas button** in the chat header, or drag the splitter to resize it. The
  open/closed state and width persist per user.
- The canvas **derives entirely from the transcript**. Every artifact is a typed fenced block inside an
  assistant reply; the pane re-scans committed turns and shows one tab per artifact. Because the transcript
  is the single source of truth, a domain reload or session switch rebinds the canvas by re-scanning —
  there is no separate canvas state to lose or migrate.
- The pane opens automatically when a new artifact arrives; closing it is remembered.

## Artifact kinds (closed vocabulary)

Artifacts are fenced blocks whose info string names a registered kind. The vocabulary is deliberately
closed — an info string with no registered renderer degrades to a plain code block, never an error.

| Fence | Rendered as | Notes |
|---|---|---|
| ` ```mermaid ` | Flowchart diagram | Rendered natively (flowchart subset); shows inline and in the canvas. |
| ` ```molca-findings ` | Findings list | JSON payload; rows show severity, code, path, message, the registered `IMolcaFix` for the code (id + facets), and an **Ask assistant to fix** button. |
| ` ```molca-workflow ` | Workflow proposal | Interactive: validation state, aggregated facets, per-step criticality toggles, **Save** / **Save & Run** / **Revise in chat**. |
| ` ```molca-run ` | Workflow run | Live binding to a run id: status, progress, per-step outcomes, **Cancel**, and **Diagnose & fix** on failure. |

The findings payload:

```json
{
  "title": "Scene audit",
  "findings": [
    { "code": "network.catalog.schema-migration-required", "severity": "error",
      "path": "Assets/Config/Catalog.asset", "message": "…" }
  ]
}
```

Finding `code` values must be the real namespaced codes from tool results — the panel uses them to look up
registered fixes in `MolcaFixRegistry`.

Renderers register through `MolcaMarkdown.RegisterFenceRenderer(infoString, factory)`. Adding a kind is a
deliberate decision shipped together with its renderer, tests, and a system-prompt update — never an ad-hoc
fence name.

## Workflows in the canvas

A **workflow proposal** is a composed workflow (see `AUTOMATION_COMPOSED_WORKFLOWS.md`) the assistant
authored and validated. The panel shows what running it would mean before anything runs:

- The kernel-aggregated facets on one line — read-only vs. action, required editor mode, revert path, and
  whether it will ask for confirmation. Resource claims are on the tooltip.
- One row per step with the command's kind badge (irreversible actions are marked), and a **critical**
  toggle: a critical step's failure halts the run, a non-critical one is recorded and the run continues.
- Validation issues inline. **Save** and **Save & Run** stay disabled until the composition validates.

Editing toggles in the panel changes a local copy — the transcript artifact remains the immutable record of
what was proposed. **Save & Run** saves, asks for one confirmation when the aggregated facets require it,
then swaps the panel for the run binding.

A **run panel** polls the kernel's run store by run id. It shows live status and progress with **Cancel**
while active, then per-step pass/fail. Terminal states are reported as they are — including `Refused` when
policy declined and `Interrupted` when the editor reloaded mid-run (the run did **not** resume; see the
resume contract in the automation guide).

## The remediation loop

Neither the findings panel nor the run panel mutates the project. Both re-enter the conversation instead:

1. A failed run offers **Diagnose & fix**, which prefills the composer with the failed steps and their
   stable diagnostic codes and asks for a dry-run remediation plan first.
2. A findings row with a registered fix offers **Ask assistant to fix** for that one code and path.
3. The assistant proposes; applying goes through the remediation pass and the normal action policy
   (`Ask` / `Auto` / `Plan` / `Auto All`), so a destructive fix is always explicit.

This is deliberate: a panel button never becomes a second write path into the project.

## The composer

- **Type while the assistant works.** The input never locks: pressing Enter (or **Queue**) during a turn
  stages the message in a queued row; it sends automatically when the turn finishes. Cancel restores it to
  the input.
- **`@` mentions** — typing `@` opens a picker over the context sources (selection, active scene, framework
  graph, KG status) and a project-asset search; selecting pins the item as a context chip.
- **`/` commands** — `/` at the start of the input opens a palette of chat verbs: `/new`, `/sessions`,
  `/copy`, `/transcript`, `/clear`.
- **Drag-and-drop** — drop project assets or scene objects to pin them as context; drop textures or image
  files to stage them as attachments (vision-capable models).
- **History** — Up/Down in an empty input recalls previous prompts.
- **Action mode** — the segmented Ask · Auto · Plan · All control replaces the old dropdown; each segment's
  tooltip explains its authorization behavior.
- **Session stats** — tokens, estimated cost, cache hit-rate, and reasoning tokens are separate tooltipped
  segments; click the row for the detail.

## Transcript behavior

- Streaming replies render **markdown as they arrive**: completed blocks format immediately; only the
  growing tail shows as plain text.
- The view scrolls to the bottom only on your own send or when already at the bottom; otherwise a floating
  **↓ New messages** chip offers the jump.
- Code blocks and tables carry copy buttons (tables copy as tab-separated text); each call in a tool chain
  copies its redacted raw result.

## See also

- [ASSISTANT_VISION.md](ASSISTANT_VISION.md) — image attachments the composer stages.
- [ASSISTANT_MEMORY.md](ASSISTANT_MEMORY.md) — cross-session memory.
- [CORE_MCP_TOOLS.md](CORE_MCP_TOOLS.md) — the tool surface the assistant grounds in.
