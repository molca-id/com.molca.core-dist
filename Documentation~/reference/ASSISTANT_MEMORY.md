# Assistant Cross-Session Project Memory (Sprint 77)

Sessions persist **transcripts**, and pinned context is **manual and per-session** — so every new chat
re-derives the same project facts, and nothing the assistant learns survives across chats. This sprint adds a
lightweight, assistant-maintained **project memory** (the same pattern this framework's own coding-agent
instructions use) that compounds with KG grounding: durable facts about this project/user, surfaced into
context automatically and editable by the user. It is **distinct from the session library** (Sprint 35), which
stays per-conversation.

## Where memory lives

File-backed, human-readable, **consumer-scoped**: one fact per Markdown file under
`Assets/_Molca/AssistantMemory/` (with a regenerated `INDEX.md`), inside the consumer project — **never** inside
the read-only Core package (the Sprint-35.5 write-path rule, guarded by a test). Each entry is YAML frontmatter
plus a body:

```markdown
---
name: build-target
description: The project ships to Quest 3.
---
Platform is Android / Quest 3; standalone build, not PCVR.
```

Because it's plain files under `Assets/`, memory is editable outside the assistant and versionable if the
project chooses. CRUD is capped (`MaxEntries` = 200, `MaxEntryChars` = 8000) so it can't grow without bound.

## The tools

| Tool | Kind | What it does |
|---|---|---|
| `molca_memory_recall` | ReadOnly | Returns durable facts relevant to a query (auto-consulted for grounding; also callable to list/look further). |
| `molca_memory_save` | Action | Saves/overwrites a fact by name. A **confirmed** action, not a silent write. |
| `molca_memory_delete` | Action | Deletes a fact by name. A confirmed action. |

Reading is free grounding; **writing memory is a confirmed action**, gated exactly like any other mutation.

## Recall is grounding, not a dump

At the start of a turn the controller recalls only the **relevant** entries — ranked by keyword overlap against
each entry's name/description/body — within the retrieval token budget (`RetrievalTokenBudget`), reusing the
Sprint-47 retrieval-injection path. The block is prefixed with a "Project memory" header, **deduped against
pinned context** (a fact already pinned isn't re-injected), and shown as the same pinnable retrieval notice.
Never the whole store, and never persisted into history.

## Scope discipline

A base-prompt rule (mirroring the framework's own memory guidance) tells the model what to remember:

- **Do** save durable project/user facts — conventions, decisions, environment, stable preferences.
- **Don't** save conversation minutiae, one-off task state, or anything the code / git history / knowledge graph
  already records.
- **Convert relative dates to absolute** (write `2026-07-03`, not "today") so a fact stays correct when recalled.
- Keep one fact per entry with a **stable kebab-case name** so re-saving updates it instead of duplicating.
- Treat memory as a lead, like retrieved context — confirm specifics with tools before relying on a detail.

## Distinct from sessions

Memory is cross-session and project-scoped. Deleting a session never touches memory, and `NewChat` preserves it
(guarded by a test). A small **Hub → Assistant → Advanced → Project Memory** panel lists entries and lets the
user delete any of them.

## Non-goals

- **Shared/team memory** — this is local, project-scoped files.
- **Automatic un-prompted saving of every turn** — saving is a deliberate, confirmed action.
- **Embedding-based semantic memory search** — recall is keyword overlap; the KG already covers project
  structure for deeper retrieval.
