---
title: Automation & the Unity CLI
category: Tooling
order: 940
---

# Automation & the Unity CLI

Molca Automation is a **transport-neutral command kernel**: one place that runs every automatable
operation — diagnostics, validation, builds, runtime smoke — behind one safety model and one
machine-readable result. The Hub, the Unity CLI (via `com.unity.pipeline`), MCP, the Assistant, and
headless batch/CI all route through the same kernel, so **no caller ever scrapes the Editor log to learn
whether something worked**.

It is **Editor-only**. Nothing here ships in a runtime Player, except the opt-in development-player
bridge described below, which is compiled only into development builds.

## The shape of every result

Every command and workflow returns the same versioned envelope:

```jsonc
{
  "schemaVersion": 1,
  "runId": "…",
  "command": "molca.preflight",
  "status": "succeeded",          // queued|running|succeeded|failed|cancelled|refused|needs_confirmation|blocked|interrupted
  "success": true,
  "durationMs": 126,
  "data": { … },                  // command-specific payload
  "diagnostics": [ { "code": "…", "severity": "…", "message": "…" } ],
  "artifacts": [ … ],
  "verification": { "performed": true, "passed": true, "evidence": [ … ] },
  "revert": { "kind": "none", "id": null }
}
```

Callers branch on `status` and diagnostic `code`s — stable strings, never log text.

## The CLI surface

The Unity CLI (`unity command <name>`) exposes a curated surface through the
`com.molca.automation.pipeline` adapter. Discovery and expert access:

| Command | Purpose |
| --- | --- |
| `molca-status` | Kernel status: command count, active profile, policy source, active/history counts. |
| `molca-capabilities` | List every command with its metadata. |
| `molca-describe --command <id>` | One command's schema, mode, kind, claims, retry class. |
| `molca-plan --command <id>` | **Preview** whether a command would run now (mode/policy gates, confirmation, retry class) — without running it. |
| `molca-invoke --command <id> [--args <json>] [--confirm]` | Run any command by id. Same policy/mode/audit as every path — not a bypass. |
| `molca-run-status --run_id <id>` | Reconnect to a long-running run. |
| `molca-cancel --run_id <id>` | Request cancellation. |
| `molca-revert --run_id <id>` | Undo a successful reversible run. |
| `molca-history [--limit N]` | Recent runs (durable across domain reloads), including interrupted ones. |

A curated wrapper exists for the common flow — `molca-doctor`. Every other command (including builds via
`molca.build`) and any custom command is reachable through the generic `molca-invoke <id>`.

## Built-in workflows

A workflow is one authorization unit composed of ordered, individually-diagnosed steps. Core ships:

- **`molca.preflight`** — read-only: versions, compilation settled, Molca Doctor.
- **`molca.content-verify`** — read-only: content-package config, dependency graph, Addressables wiring.
- **`molca.runtime-smoke`** — Play-mode: RuntimeManager init, subsystem health, Sequence discovery.
- **`molca.build`** — action (irreversible, requires confirmation): the pre-build Doctor gate, then
  `BuildManager.BuildAsync`, returning a structured build report.
- **`molca.dev-player-smoke`** — read-only: probes a connected development build (see below).

Workflows appear automatically in the Hub **Automation** tab with **Run** and **Preview** buttons.

## Policy and profiles

Standing authorization for **non-interactive** callers (CLI, CI, agents) is a named profile plus an
action allowlist, authored in the Hub **Automation → Permissions** panel:

- **Observe** — read-only commands only; every action refused. The safe default.
- **Develop** — allowlisted undoable/snapshot actions run; irreversible actions confirm.
- **Release** — curated validation/build/deploy actions; irreversible actions confirm.
- **Unattended CI** — exact allowlist, no confirmation prompts; credentials from the environment.

Running a workflow yourself in the Hub is already consent — the profile governs what runs *without a
human present*. Irreversible actions always confirm interactively (or take an explicit `--confirm` in
batch mode).

## Closed-loop autonomy

- **Postconditions.** A command may declare a verifier; a run whose delegate "returned" still **fails**
  if the postcondition does not hold.
- **Retry classification.** Every command is `retryable`, `retryable_after_rollback`, or `not_retryable`
  (derived from kind + reversibility), surfaced in `describe`/`plan` — so an autonomous caller never
  silently retries an irreversible action.
- **Rollback / compensation.** A command registers how to undo itself
  (`context.RegisterCompensation(kind, work)`). On failure it rolls back automatically; on success the
  revert is stored and offered (`molca-revert`, or the Hub **History** panel's Revert button).
- **Durable history & recovery.** Runs are journaled under `Library/Molca/Automation/Runs/`
  (per-machine, git-ignored). History survives domain reloads, and a run left in flight by a crash/reload
  is recovered as **`interrupted`**.

## Development Player diagnostics

An opt-in bridge lets a running **development build** be observed and smoke-tested from the Editor over
`PlayerConnection`. It is compiled **only** under `DEVELOPMENT_BUILD || UNITY_EDITOR`, so no runtime-dev
component can enter a production Player (a build preprocessor rejects a forced `DEVELOPMENT_BUILD` on a
production build as belt-and-braces). The bridge answers with a read-only snapshot — RuntimeManager
state, subsystem health, a recent-error tally — never log text, with no action or eval surface. Drive it
with `molca.dev-player-smoke`.

## Extending automation (no Core edit)

Automation follows the framework's layer model (`architecture.md`): **Core defines the foundation; an
SDK layer or consumer project extends by adding providers, never by editing the kernel.** Both extension
points are discovered by `TypeCache` — ship the type in an Editor assembly that references
`Molca.Editor`; no registration call.

### Add commands and workflows

Subclass `MolcaCommandProvider`. Your namespace must be unique (not `molca`); command ids must be
unique. Collisions fail loudly into the registry's `Errors` and are dropped — a fork can never shadow a
Core command.

```csharp
public sealed class AcmeCommands : MolcaCommandProvider
{
    public override string Namespace => "acme";

    public override IEnumerable<MolcaCommandDefinition> GetCommands() => new[]
    {
        new MolcaCommandDefinition(
            "acme.reindex", "Reindex", "Rebuilds the search index",
            executeAsync: async ctx =>
            {
                var snapshotId = TakeSnapshot();
                ctx.RegisterCompensation(MolcaCommandReversibility.FileSnapshot,
                    _ => RestoreSnapshotAsync(snapshotId));   // undo path, auto-run on failure
                await ReindexAsync(ctx.CancellationToken);
                return MolcaCommandResult.Succeeded("acme.reindex");
            },
            kind: MolcaCommandKind.Action,
            reversibility: MolcaCommandReversibility.FileSnapshot,
            requiresConfirmation: true,
            resourceClaims: new[] { MolcaResourceClaim.AssetDatabaseWrite }),

        // A workflow is projected to a command the same way Core's built-ins are:
        MolcaWorkflowCommandAdapter.ToCommand(AcmeVerifyWorkflow.Create()),
    };
}
```

Once registered, the command inherits **everything** — CLI (`molca-invoke acme.reindex`, `-describe`,
`-plan`, `-history`, `-revert`), Hub surfacing, policy/mode gating, resource coordination, confirmation,
audit, progress, verification, rollback, and retry classification.

### Replace the authorization policy

The default policy is the profile/allowlist above. A fork that needs its own authorization logic —
role-based, an external approval service, a stricter CI gate — ships a
`MolcaAutomationPolicyProvider`. The highest-`Priority` enabled provider wins; the built-in policy is the
fallback. A provider that throws is skipped, so a broken policy can never leave the kernel unguarded.

```csharp
public sealed class AcmePolicyProvider : MolcaAutomationPolicyProvider
{
    public override int Priority => 100;                 // beats the built-in default
    public override string Describe() => "acme-rbac";

    public override IMolcaAutomationPolicy CreatePolicy() => new AcmeRolePolicy(
        // compose on top of the built-in profile policy if you like:
        fallback: MolcaAutomationPolicy.FromSettings());
}
```

`molca-status` reports the active `policySource` so you can confirm which policy is in force.

## What is visible over Molca Remote

A connected [Molca Remote](REMOTE_EDITOR.md) session observes the kernel: the active profile and its
source, a digest of the command catalog, the active runs with their status/progress/step, and a bounded
recent-run history with duration, diagnostic count, verification verdict, and whether a revert is
registered. Observation needs no extra opt-in beyond enabling Remote for the project.

Two things do not travel. A command's `InputSchemaJson` never leaves the Editor — the remote surface
receives only a derived `none`/`simple`/`advanced` argument tier. And a run's own progress *message* is
projected only for the commands Core ships; a third-party command's run reports status, progress, and step
but keeps its prose local, because Core cannot review text it does not author.

A remote session can also *run* commands — preview, invoke, cancel, and revert — but only through this
kernel. Remote is **additive to** the policy on this page, never a substitute for it.
`MolcaTransport.Remote` records that a run came from a browser session, and a remote caller passes the
control plane's gates, then the Editor's remote opt-in and remote action allowlist, and only then arrives
here. Remote can never raise the active profile, extend the action allowlist, or mark a command confirmed
on the user's behalf — under **Observe**, a remote action is refused exactly as a local one is, and a
command absent from the action allowlist refuses even if the *remote* allowlist happens to name it.

Two remote-only restrictions exist because of how runs are driven:

- **A headless Editor refuses to host a remote run.** Fire-and-forget `Awaitable` chains do not advance
  without an update loop — the finding that removed `Kernel.StartRun` in favour of await-in-request — so a
  detached run in batch mode would silently stall rather than fail. Headless automation uses the CLI entry
  points, which await in-request.
- **One remote-initiated run at a time.** The coordinator already serializes mutating runs, but a remote
  queue with no visible owner is worse than an explicit refusal: the caller cannot see why nothing is
  happening.

See [Molca Remote Editor](REMOTE_EDITOR.md) for the accept-fast run model and the full refusal table.

## Safety notes

- Config only — **never store credentials** on the policy asset or a provider; read them from the
  environment/secret store at run time.
- The kernel exposes no source-editing, add-on install/removal, credential mutation, deployment, or
  arbitrary `eval` command under the default policy.
- Long-running work (builds, full Doctor sweeps, Play-mode entry) is batch-mode: the interactive request
  path caps at ~30 s.

## See also

- [Composed Workflows](AUTOMATION_COMPOSED_WORKFLOWS.md) — data-driven workflows that string registry commands together.
- [Extending Molca MCP from an SDK Fork](MCP_FORK_PROVIDERS.md) — the sibling provider pattern for MCP tools.
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md) — checks that automation workflows compose.
- [The Molca Hub](HUB.md) — where the Automation workspace lives.
