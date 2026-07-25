---
title: The Molca Hub
category: Tooling
order: 900
---

# The Molca Hub

The **Molca Hub** (menu **Molca → Hub**) is the single editor window that fronts the framework's
tooling: project settings, the [Doctor](DOCTOR_CHECKS.md), the [Assistant](ASSISTANT_RESILIENCE.md),
sequence tools, licensed add-ons, MCP status, networking, and this docs browser. It is organized as a home **Settings**
workspace plus a set of **workspace tabs**, and both are extension points a fork or project can add to
without editing Core.

## Layout

- **Settings (home).** A nested rail of configuration sections — Project, Editor, Build & Version,
  Integrations, MCP, Network, Runtime, Sequences, Tasks, Assistant, **Add-ons**, and **About** — and the
  **Docs** branch, which renders every `Documentation~/reference/*.md` shipped by an installed
  `com.molca.*` package (see [Authoring Hub Docs](DOCS_AUTHORING.md)).
- **Workspace tabs.** Full-window tools contributed alongside Settings — Core ships **Doctor**,
  **Assistant**, and **Sequence**.

## Extension seams

Both halves are discovered via `TypeCache`, so contributing is drop-in — implement the seam in an
Editor assembly and it appears automatically; a provider that throws is logged and skipped, never
breaking the Hub.

| Seam | Placement | Adds |
|---|---|---|
| `MolcaHubWorkspaceProvider` | `Editor/Hub/` | One or more workspace tabs (`MolcaHubWorkspaceItem`: id, title, order, factory). |
| `MolcaDocsProvider` | `Editor/Hub/Docs/` | Reference docs (or drop Markdown files that the built-in provider already scans). |

Core's **Add-ons** are their own Settings-rail root with **Browse** (`MolcaHubSection.AddOnsBrowse`) and
**Installed** (`MolcaHubSection.AddOnsInstalled`) leaves, not the workspace-tab seam above; see
[Add-ons](ADDONS.md) for the consumer workflow.

## About and framework updates

**About** (`MolcaHubSection.About`, the last rail leaf) reports what this project is actually running and
whether a newer Core exists.

- **Versions.** Every installed `com.molca.*` package with its version and install source (registry, git,
  embedded, local), the editor version and scripting runtime, wire-schema versions, and the installed
  add-on count. **Copy diagnostics** puts all of it on the clipboard as markdown for a bug report. A fork's
  own Molca packages appear here automatically — the list is enumerated, not hardcoded.
- **Updates.** Reads the control plane's release feed (`GET /framework/releases/latest`), authenticated by
  the same developer entitlement the add-on catalog uses. Answers are cached for six hours; **Check now**
  bypasses the cache, and the check never runs in batch mode.
- **License** mirrors the stored developer entitlement read-only, with a link to sign-in.

What the update card offers depends on how Core is installed, because that is what decides whether an
upgrade can be applied from here at all:

| Install source | Offered |
|---|---|
| Registry | **Update to x.y.z** — a confirmed `Client.Add` of the version the feed published. |
| Git | **Copy manifest line** — the dependency value to paste into `Packages/manifest.json`. |
| Embedded / local | **Copy upgrade spec** — the files are project-owned; nothing is mutated. |

Two behaviours worth knowing:

- A release that raises the minimum Unity is reported but never offered. If the feed also names an older
  release this editor can take, *that* is offered and the blocked one stays visible with its requirement.
- Being offline or not signed in is reported inside the card — no console error, no dialog. Nothing here
  mutates the project without an explicit click, and no code path edits `manifest.json` directly.

Two per-project developer preferences live in the card: whether opening About may check a stale cache (on
by default), and whether an available update also shows as a chip in the bottom activity rail (off by
default).

```csharp
// Add a Hub tab from an SDK layer or project (no Core edit).
internal sealed class MyWorkspaceProvider : MolcaHubWorkspaceProvider
{
    public override IEnumerable<MolcaHubWorkspaceItem> GetWorkspaces() => new[]
    {
        new MolcaHubWorkspaceItem("my-tool", "My Tool", order: 100, () => new MyToolElement()),
    };
}
```

Workspaces can be hidden per-project (`MolcaHubWorkspaceRegistry.SetHidden(id, hidden)`); the Settings
tab is the anchored home and is always present.

## See also

- [Authoring Hub Docs](DOCS_AUTHORING.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Core MCP Tools](CORE_MCP_TOOLS.md)
- [Build System & Versioning](BUILD_SYSTEM.md)
- [Onboarding Wizard](ONBOARDING.md)
- [Add-ons](ADDONS.md)
