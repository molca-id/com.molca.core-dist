---
title: Molca Remote Editor
category: Tooling
order: 85
---

# Molca Remote Editor

Molca Remote connects a running Unity Editor to the Molca dashboard using an outbound encrypted connection.
It does not expose Unity, the local MCP listener, or the local MCP token to the network.

## Enable or revoke access

1. Sign in to Molca Hub and connect the repository to a Molca project.
2. Open **Hub → Project → Remote Editor**.
3. Enable **Remote access**. Enable **Allow remote Assistant** separately if needed.
4. To permit mutations, enable **Allow remote actions** and acknowledge its warning.
5. Open the Remote dashboard from the same card.

The dashboard is installable. On a phone or tablet, use the browser's **Add to Home Screen** to install
**Molca Remote**; it opens as a standalone app scoped to the dashboard and lays the session out as a bottom
tab bar. Nothing is cached except the app shell and its static assets — never Editor state, so an installed
app that has lost its session shows sign-in rather than a stale view of your project.

A session is four tabs, mirroring the Hub's own Automation workspace:

| Tab | What it shows |
| --- | --- |
| **Overview** | Editor identity and health, mode, scene, console counts, policy profile, disconnect |
| **Workflows** | The command catalog by category, with preview before run |
| **Runs** | Active runs with progress and cancel, then history with diagnostics and revert |
| **Assistant** | The chat, unchanged |

Turning Remote access off closes the connection. The dashboard can also disconnect a process session or
revoke the Editor installation. Access is private to the signed-in user and is rechecked against current
license, project membership, project, and binding state.

## Shared information

The presence snapshot is limited to Editor/Core versions, edit or play mode, compilation health, Molca
project identity, active-scene summary, selected-object display metadata, bounded console counts, and
Assistant activity. Detailed state is requested through allowlisted read-only tools.

Remote Assistant observation contains a bounded transcript and pending ordinary questions. Provider keys,
the local MCP token, source files, asset contents, environment variables, absolute home paths, and raw tool
arguments/results are not included.

## Live activity and automation

A connected session also reports what the Editor is doing, so the dashboard shows a Doctor scan advancing
and an automation run's progress rather than a static presence card. Both blocks travel on the same bounded
snapshot, which is sent on connect, on each heartbeat, and whenever the projected state changes — coalesced
so a burst of updates becomes one message and an unchanged projection sends nothing.

There is no separate switch for this. Enabling **Remote access** enables it, because a remote session that
cannot say what the Editor is doing is the problem Remote exists to solve. The consent boundary is
observation versus control, and control keeps its own opt-ins below.

What travels:

- **Activity chips** — up to twelve, from the Hub's bottom rail: id, short label, status caption, state,
  optional progress fraction, and the workspace id as a *labelling hint only*. Click and dismiss handlers
  are never serialized, so no chip is remotely actionable.
- **Automation** — the active policy profile and its source, a digest of the command catalog, up to six
  active runs (command id, status, progress, step, transport) and ten recent runs (status, duration,
  diagnostic count, whether verification passed, whether a revert is registered).

Two omissions are deliberate:

- A chip is eligible only if its provider marks it remote-safe. A chip's status caption is
  author-controlled free text, so Core opts in Doctor, automation runs, and the framework-update chip; an
  add-on's activity provider exports nothing until someone reviews what its captions can contain. Add-on
  authors set `remoteSafe: true` on `MolcaHubActivity` to opt in.
- A run's own progress message is projected only for the commands Core ships. A third-party command's run
  still reports status, progress, and step — everything needed to follow it — while its prose stays on the
  machine.

A command's `InputSchemaJson` never leaves the Editor. The dashboard receives only a derived
`none`/`simple`/`advanced` argument tier.

## Running automation remotely

An authorized session can also *drive* the automation kernel: read the command catalog, preview a plan,
start a run, follow it, cancel it, and revert a completed reversible run. Everything goes through the
kernel's own gates — this is not a second execution path. See [Automation](AUTOMATION.md) for what those
gates are.

Two authorization systems meet, and **remote is additive to automation policy, never a substitute**. A
remote run passes, in order:

1. the control plane — feature flags, project capability, session ownership, rate limit, idempotency, and
   for an action the `waiting_confirmation` flow bound to the immutable command row;
2. this Editor — **Allow remote actions** and the local remote action allowlist;
3. the kernel — `MolcaAutomationPolicy` under the active profile, then the mode gate.

State these plainly, because they otherwise read as bugs:

- Under the **Observe** profile every automation action refuses remotely, whatever the remote settings say.
- A command absent from *automation's* action allowlist refuses even if the *remote* allowlist names it.
  They are separate lists and neither implies the other.
- A remote confirmation satisfies the interactive-confirmation requirement only. It never sets batch mode,
  never raises the profile, and never enables Assistant `AutoAll`.

### Why a run returns before it finishes

A remote command row expires 60 seconds after creation. That bounds *delivery and acceptance*, not the
work, so `automation.invoke` returns as soon as the run is accepted — `{ runId, status }` — and the run
proceeds in an owned task the Editor's update loop drives. Progress arrives through the activity chips and
the `automation` state block; the terminal detail (diagnostics, verification evidence, revert availability,
retry classification) comes from a later `automation.run-status` call. A `molca.build` that takes minutes is
therefore not a protocol problem.

### Refusals worth recognizing

| Code | Meaning |
| --- | --- |
| `automation.batch_mode_refused` | The Editor is headless. A detached run would silently stall, because fire-and-forget `Awaitable` chains do not advance without an update loop, so a batch Editor never hosts one. Use the CLI entry points instead. |
| `automation.run_in_flight` | One remote-initiated run at a time. The kernel's coordinator would serialize writers anyway, but a remote queue with no visible owner is worse than an explicit refusal. |
| `automation.catalog_stale` | The request was authorized against a catalog this Editor no longer holds — a package installed, or a profile changed. The capability that was checked may be the wrong one, so it refuses rather than guessing. |
| `automation.arguments_too_large` | The encoded arguments object exceeds 4 KiB. |
| `remote_actions_disabled` / `action_not_allowlisted` | The Editor-local remote gate refused before the kernel was consulted. |

Losing the socket, losing authorization, or disabling Molca Remote for the project cancels the
remote-initiated run: a run nobody can still watch or stop from the browser must not keep mutating the
project.

Arguments are tiered. A command with no arguments runs from a single button; `molca.build` has a curated
form; anything else is `advanced` and stays on the desktop raw-JSON path until someone writes it a form.
That tier is derived in the Editor — a new add-on command appears as `advanced` automatically.

## Execution policy

Remote read-only tools use the same registry and Unity main-thread dispatcher as local MCP tools. Remote
Assistant turns use the same controller as the Hub, so switching Hub tabs or closing the dashboard does not
cancel accepted work. Use **Stop** in either surface to cancel the shared turn.

Remote actions require all of the following:

- the server Remote Actions feature is enabled;
- the dashboard actor currently owns the session and has project action access;
- **Allow remote actions** is enabled in this Editor;
- the tool is on the local MCP action allowlist;
- the Editor mode and the confirmed scene/selection context still match;
- no Assistant turn is concurrently using the Editor action lane.

A direct dashboard action is created in a waiting state and is queued only after confirming its immutable
command ID, tool, arguments, target Editor, and context. Reversible results expose their file-snapshot ID or
Unity Undo group. Irreversible actions are supported and clearly labelled.

Remote Assistant never changes the configured action mode. In **Ask**, **Auto**, or **Plan**, existing local
confirmation rules continue to apply, and action confirmations cannot be answered from the web. In
**AutoAll**, locally allowlisted actions—including irreversible actions—can run without another prompt once
Remote Actions is enabled. Removing authorization stops a remote-origin turn; a transient browser or network
disconnect does not.
