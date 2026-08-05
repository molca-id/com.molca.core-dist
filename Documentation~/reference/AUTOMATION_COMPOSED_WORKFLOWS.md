---
title: Composed Workflows
category: Tooling
order: 941
---

# Composed Workflows

A **composed workflow** strings existing Molca capabilities — Doctor checks, remediation, build and content
commands, anything an add-on contributes — into one ordered, policy-gated run. Unlike the built-in workflows
(Preflight, Build, Runtime Smoke…), which are code-defined, a composed workflow is **data**: JSON that a
person, a saved file, or the in-editor assistant can author, and that compiles onto the same
`MolcaWorkflowRunner`.

## Shape

```json
{
  "id": "release-preflight",
  "displayName": "Release Preflight",
  "description": "Everything that must pass before tagging a release.",
  "failOnWarning": false,
  "steps": [
    { "commandId": "molca.preflight", "critical": true },
    { "commandId": "molca_scene_audit", "args": {}, "critical": false },
    { "commandId": "molca.build", "args": { "profile": "Release" }, "critical": true }
  ]
}
```

- `commandId` is any command in the automation registry — list them with `molca_workflow_commands` (or
  Hub → Automation). MCP tools are in the registry too, projected by the MCP adapter.
- `critical` (default `true`): a critical step's failure halts the workflow; a non-critical failure is
  recorded and the run continues. Either way the workflow's own verdict still fails.
- `id` doubles as the command id once saved, so it must be kebab-case (`a–z`, `0–9`, `-`, `.`).

## Facets are computed, never declared

The composition's policy metadata is derived by the kernel from its members. An author cannot claim a
workflow is safer than the commands inside it:

| Facet | Rule |
|---|---|
| `Kind` | `Action` if **any** member is an action, else `ReadOnly`. |
| `Mode` | The single mode all members are compatible with (`Any` is neutral). Mixing `Edit` and `Play` is a compile error. |
| `ResourceClaims` | The union of every member's claims. |
| `Reversibility` | The **weakest** member's revert path — one irreversible member makes the whole run irreversible. |
| `RequiresConfirmation` | True when any member requires confirmation or is an irreversible action. |

Execution enters through `MolcaWorkflowCommandAdapter` → the kernel executor, so authorization, mode
gating, resource leases, confirmation, and the audit log apply to the composition as a whole. Confirmation
is granted **once**, for the whole workflow — which is exactly why an irreversible member escalates it.

## Validate before you run

`MolcaComposedWorkflowCompiler.Validate` (tool: `molca_workflow_validate`) fails at propose time, with a
message per step, on:

- an unknown `commandId`,
- arguments that violate the command's input schema (missing required, unknown property when the schema
  forbids extras, wrong primitive type),
- duplicate step ids,
- a workflow invoking itself,
- an `Edit`/`Play` mode conflict,
- an empty composition.

Nothing runs on a plan that does not validate. This matters most with weaker models: a malformed plan fails
legibly at propose time instead of halfway through a mutating run.

## Saving

Saved workflows live as JSON under `Assets/_Molca/AutomationWorkflows/` — one file per workflow, hand-
editable and versionable. `SavedWorkflowCommandProvider` contributes each one to the registry, so a saved
workflow behaves like any other command: it appears in Hub → Automation, over the CLI/Pipeline, in MCP, and
in the assistant, with no extra wiring.

The file records the facets aggregated at save time (the registry needs policy metadata at build time). The
body **re-validates against the live registry at run time**, so if a member command changed or disappeared
since the save, the run fails legibly (`compose.unknown_command`, `compose.args_invalid`) rather than
executing a stale plan. Nested compositions are depth-capped to catch cycles.

## Saving does not authorize

A saved action workflow is **refused until its command id is on the automation action allowlist**
(`MolcaAutomationPolicySettings.ActionAllowlist`, or the legacy MCP allowlist during migration). This catches
people out, so be explicit about it:

- Raising the profile does **not** help. `UnattendedCi` uses an *exact* allowlist and skips interactive
  confirmation — an id that is not listed is refused there exactly as it is under `Develop`.
- Nothing in the save or run path allowlists a workflow for you. That is deliberate: an assistant that could
  authorize its own workflows would be widening its own permissions.
- Authorize it explicitly — the **Authorize** button on the workflow's row in Hub → Automation → Workflows,
  the **Authorize this workflow…** button on the canvas proposal or run panel, or the toggle in
  Hub → Automation → Permissions.

`molca_workflow_save` reports `authorizedToRun` so a caller can say this up front, and `molca_workflow_run`
refuses immediately (rather than starting a run that comes back `Refused`) when the id is not allowlisted.

### Revoking

Every surface that can grant authorization can also take it back — the Hub workflow row and the canvas
proposal panel show **Revoke** once a workflow is authorized, and the Permissions toggle works both ways. A
surface that could only widen permissions would be the wrong bias, so revocation is never harder to reach
than authorization. Granting asks for confirmation; revoking does not, because it narrows permissions, is the
safe direction, and is undone by the same button.

`molca_workflow_delete` also drops the workflow's allowlist entry. Otherwise the entry would linger
invisibly — the Permissions rail lists *registered commands*, and a deleted workflow no longer is one — and a
workflow later saved under the same id would inherit an authorization nobody granted it.

## Running (fire-and-poll)

A workflow can run for minutes, so it is never a single awaited call:

1. `molca_workflow_run` starts the run and returns a `runId` immediately.
2. `molca_workflow_status` polls it: status, progress, and the full result envelope once terminal. Pass
   `cancel: true` to request cancellation.

`molca_workflow_run` refuses in headless batch mode, where a detached awaitable never pumps — drive the
workflow over the Pipeline adapter there instead.

### Resume contract

**A domain reload mid-run does not resume the run.** The persisted journal reconciles it to `Interrupted`,
and status reports that truthfully — the run did not complete and its partial effect is of unknown state.
Any UI bound to a run must rebind from the journal, never from live editor objects.

## Observing steps

`MolcaWorkflowRunner.StepStarting` / `StepFinished` fire on the main thread around each step body
(workflow id, run id, step, and — on finish — the step result). They are for observers: telemetry, audit,
add-on instrumentation. A throwing hook is swallowed and logged, so an observer can never fail the workflow
it is watching.

## Tools

| Tool | Kind | Purpose |
|---|---|---|
| `molca_workflow_commands` | read | Registry commands available as steps, with schemas. |
| `molca_workflow_validate` | read | Validate a composition; returns issues + facets. |
| `molca_workflow_list` | read | Saved workflows, and whether each still validates. |
| `molca_workflow_save` | action | Validate and persist; registers the command. |
| `molca_workflow_delete` | action | Remove a saved workflow. |
| `molca_workflow_run` | action | Start a saved workflow; returns a `runId`. |
| `molca_workflow_status` | read | Poll a run; optionally request cancellation. |

The mutating three (`save`, `run`, `delete`) are withheld from models flagged as weak tool-callers — a model
that drops or malforms tool calls must not author and launch multi-step actions. The read-only tools stay
available.

## See also

- [Automation & the Unity CLI](AUTOMATION.md) — the kernel, the built-in workflows, and the CLI transport.
- [Assistant Canvas & Composer](ASSISTANT_CANVAS.md) — the proposal and run panels in the assistant canvas.
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md) — checks that make good first steps.
- [Core MCP Tools](CORE_MCP_TOOLS.md) — the wider tool surface.
