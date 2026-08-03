---
title: Onboarding
category: Tooling
order: 920
---

# Onboarding

The onboarding checklist (**Molca Hub → Onboarding**, or menu **Molca → Onboarding**) shows where a
project stands against what Molca expects: one row per check, each with a state derived from the project
and, where there is a single correct next move, a button that takes it.

It is not a wizard. Nothing is sequential, nothing has to be run, and no progress is recorded — every row
is re-checked from the project on each refresh, so it cannot claim something is done that is not. Delete
the asset that satisfied a row and the row goes back to *To do* on the next look.

> Onboarding is **post-compile only**: a fresh project compiles first (that's the package's job via its
> declared dependencies and shipped content), *then* the checklist offers convenience setup. It is never
> the thing that makes the project compile. Nothing it does writes into `Packages/`.

## The two groups

The list is split, and the split is the important part.

| Group | Where the rows come from | What a row means |
|---|---|---|
| **Required** | A Molca audit domain reported it | Something the framework asserts the project got wrong. The project may not start correctly until it is resolved. |
| **Recommended** | The [project starter](#recommended-rows) and optional tooling | What a fully-featured Molca project looks like. Declining any of these is a choice, not a fault. |

The groups are never merged. A project that ships without telemetry is not misconfigured — it made a
choice — so the starter's suggestions are never reported as findings, and an audit finding is never
softened into a suggestion.

## Required rows

One row per registered remediation domain (Bootstrap, References, Network, and whatever a fork adds).

These rows **do not run the audit**. A domain sweep is a project-wide scan, and opening a window should not
start one; the row reports what the Remediation workspace last found, and says *Not checked yet* when
nothing has run. Its action opens [Remediation](REMEDIATION.md), which owns the pass, its safety policy,
and the review step.

An unaudited row is amber, not red, and the summary counts it as *to check* rather than *required*. Both
are outstanding work, but only a real finding is an accusation — a fresh project showing one red row per
audit domain would be crying wolf on day one, and nobody reads a surface that does that.

## Recommended rows

| Row | What it does |
|---|---|
| **Global Settings**, **Setting Modules**, **RuntimeManager**, **Performance Budgets** | The [project starter](GETTING_STARTED.md) steps, each runnable on its own from its row. Everything is generated from code into `Assets/_Molca/Settings/`; nothing is copied out of a package. |
| **Project Settings** | Opens the consumer-space `MolcaProjectSettings` asset. Core seeds it lazily on first access, so this row usually reports *Done* without you doing anything. |
| **Coding-Agent Instructions** | Generates a project-root `CLAUDE.md` pointing at the installed packages' reference docs, stating that they are read-only. Only writes when absent — an existing file is never touched. |
| **MCP Proxy** | Builds the TypeScript MCP proxy from the package's `Tools~/molca-mcp` source into a writable `<project>/molca-mcp/`. |
| **Knowledge Graph** | Builds the Graphify graph over this project's source and docs. |

A row expands to show what the check actually found and, where it helps, why the row matters at all.
Rows with nothing outstanding start collapsed.

## Where it appears

- **Hub → Onboarding** — first tab in the Quality group, ahead of Doctor and Remediation, because it is
  the surface that tells you which of them this project needs.
- **Activity rail chip** — while anything is outstanding, the Hub's bottom rail carries an *Onboarding*
  chip with the counts. Dismiss it and it stays dismissed for you on this project; the rows themselves are
  unaffected, because dismissing a reminder is not resolving what it reminded you of.
- **Molca → Onboarding** — a standalone window hosting the same view, for when the Hub is not where you
  started.
- **First run** — a project with no settings asset is offered the checklist once, and never nagged again.

## Extending it

A layer contributes rows without Core knowing it exists. Prefer the surface that already owns the concern:

- an **audit** → ship an `IMolcaRemediationDomainProvider`; the Required row appears automatically
- an **opinionated setup step** → ship an `IMolcaStarterStep`; the Recommended row appears automatically
- anything else → ship an `IMolcaOnboardingItemProvider`

```csharp
internal sealed class MyOnboardingItems : IMolcaOnboardingItemProvider
{
    public IEnumerable<MolcaOnboardingItem> GetItems() => new[]
    {
        new MolcaOnboardingItem(
            id: "onboarding.my-thing",
            title: "My Thing",
            summary: "What this is, in the user's terms.",
            check: () => MyThing.Exists
                ? MolcaOnboardingCheck.Done("Already set up.")
                : MolcaOnboardingCheck.Todo("Not created yet."),
            actionLabel: "Set Up",
            act: MyThing.Create,
            why: "Why it matters — the line a newcomer needs and a veteran skips."),
    };
}
```

Two rules for a check: it must **never write anything**, and it must be **cheap**. If answering honestly
needs a project-wide scan, report what is already known and let the action navigate to the surface that
runs the scan.

## See also

- [The Molca Hub](HUB.md)
- [Getting Started](GETTING_STARTED.md)
- [Remediation](REMEDIATION.md)
- [Settings: Project, Global & Modules](SETTINGS.md)
- [Build System & Versioning](BUILD_SYSTEM.md)
