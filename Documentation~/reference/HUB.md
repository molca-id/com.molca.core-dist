---
title: The Molca Hub
category: Tooling
order: 900
---

# The Molca Hub

The **Molca Hub** (menu **Molca → Hub**) is the single editor window that fronts the framework's
tooling: project settings, the [Doctor](DOCTOR_CHECKS.md), the [Assistant](ASSISTANT_RESILIENCE.md),
sequence tools, MCP status, networking, and this docs browser. It is organized as a home **Settings**
workspace plus a set of **workspace tabs**, and both are extension points a fork or project can add to
without editing Core.

## Layout

- **Settings (home).** A nested rail of configuration sections — Project, Editor, Build & Version,
  Integrations, MCP, Network, Runtime, Sequences, Tasks, Assistant — and the **Docs** branch, which
  renders every `Documentation~/reference/*.md` shipped by an installed `com.molca.*` package (see
  [Authoring Hub Docs](DOCS_AUTHORING.md)).
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
