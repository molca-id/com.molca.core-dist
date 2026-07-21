---
title: Sequence Authoring (Spec→Sequence)
category: Sequences
order: 310
---

# Sequence Authoring (Spec→Sequence)

`molca_sequence_author` applies a **declarative whole-graph plan** to a `SequenceController`
transactionally, then converges it to a clean validation state — the Core half of the
"Spec→Sequence generator".

> **Layer note.** Core does **plan → validated graph**. The agent (the Assistant LLM) does
> **spec → plan**: it reads a ClickUp/Figma/Google spec via the existing read tools (`molca_figma_*`,
> ClickUp Hub, etc.) and emits a plan in Core's vocabulary. Core never learns what a "spec" is, never
> calls an LLM, never knows a connector. Spec-coverage / backend-contract checks are **SDK-layer**
> `ISequenceValidator`s (see [`SEQUENCE_VALIDATION.md`](./SEQUENCE_VALIDATION.md)). The "generator" is an
> emergent composition: **Assistant + spec-read tools + `molca_sequence_author` + the remediation loop**.

## The plan

```jsonc
{
  "controller": "ctrl-1",          // Ref Id or GameObject name
  "mode": "create",                // "create" (fail on existing refId) | "merge" (update + add)
  "remediate": true,               // run validate→safe-fix→re-validate after apply (default true)
  "steps": [
    {
      "refId": "phase-1",          // optional; auto-generated if omitted (then can't be a parent target)
      "type": "Step",              // step type name — discover via molca_sequence_list_types
      "name": "Phase 1"            // optional GameObject name
    },
    {
      "refId": "open-valve",
      "type": "PressButtonStep",
      "parentRefId": "phase-1",    // a planned refId or an existing scene step
      "fields": { "holdDuration": "2" },
      "auxiliaries": [ { "type": "HintAuxiliary", "fields": { "text": "Turn the valve" } } ]
    }
  ]
}
```

`fields` values are scalar strings coerced to the serialized field type (int/float/bool/enum/vector/etc.);
complex/object fields can be set afterward with `molca_sequence_set_step_fields`.

## Guarantees

- **Validate before mutate.** All step/auxiliary types resolve, no duplicate planned Ref Ids, every
  `parentRefId` resolves, and `create`-mode Ref Ids don't already exist — checked up front. Any issue →
  the tool returns `{ "applied": false, "planIssues": [...] }` and **the scene is untouched**.
- **Transactional.** The whole apply runs in one collapsed Unity `Undo` group; a mid-apply failure
  reverts everything (`Undo.RevertAllDownToGroup`) — never a partial graph. Revert with
  `molca_undo_last_action` / Ctrl+Z.
- **`create` vs `merge`.** `create` fails on an existing Ref Id. `merge` updates an existing step's
  fields/parent and creates missing ones; it does **not** touch the auxiliaries of pre-existing steps
  (avoids silent duplication) — new steps get their planned auxiliaries.
- **Converge, don't just apply.** After applying, the tool runs the validation registry → `SafeOnly` fix
  pass → re-validate, and returns `before`/`after`/`valid`/`reverts[]`/`residual` (suggestion-enriched),
  so the agent loops on the residual (e.g. rebind unresolved references) until `valid`.

Built on the same mutation logic as the granular `molca_sequence_*` edit tools — nothing reinvented, and
those edit tools remain available unchanged.

## Worked loop (agent side)

1. Agent reads a procedure spec (Figma frame / ClickUp task) via existing read tools.
2. Agent emits a plan and calls `molca_sequence_author`.
3. Tool authors transactionally and returns residual findings with Ref-Id suggestions.
4. Agent acts on residual (rebind references via `molca_sequence_set_step_fields`, etc.) and re-runs
   `molca_sequence_author` (merge) or `molca_sequence_remediate` until `after.valid` is true.

## See also

- [Sequence Validation](SEQUENCE_VALIDATION.md)
- [Core MCP Tools](CORE_MCP_TOOLS.md)
