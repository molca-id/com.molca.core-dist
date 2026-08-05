---
title: The Molca Hub
category: Tooling
order: 900
---

# The Molca Hub

The **Molca Hub** (menu **Molca → Hub**) is the single editor window that fronts the framework's
tooling: project settings, the [Doctor](DOCTOR_CHECKS.md), the [Assistant](ASSISTANT_RESILIENCE.md),
sequence tools, localization and theme authoring, content packaging, licensed add-ons, MCP status,
networking, automation, and this docs browser.
It is organized as a home **Settings** workspace plus a set of **workspace tabs**, and both are extension
points a fork or project can add to without editing Core.

## Layout

- **Settings (home).** A nested rail of configuration sections, grouped as **Framework** (Project,
  Build & Version, Runtime & Global, Editor), **Tooling** (Integrations, Tasks, MCP, Network Activity) and
  **Add-ons** (Browse, Installed), followed by **Assistant** and **About**. Provider-contributed leaves join
  one of those categories or collect under **Extensions**.
  Reference docs are *not* in this rail — they are read-only content in their own right-anchored **Docs**
  workspace tab, which renders every `Documentation~/reference/*.md` shipped by an installed `com.molca.*`
  package (see [Authoring Hub Docs](DOCS_AUTHORING.md)).
- **Workspace tabs.** Full-window tools contributed alongside Settings. Core ships, by group:
  - *Quality* — **Onboarding** (see [Onboarding](ONBOARDING.md)), **Doctor**,
    **References** (see [Scene Reference System](REFERENCE_SYSTEM.md#the-references-workspace)),
    **Remediation** (see [Remediation](REMEDIATION.md));
  - *Infrastructure* — **Network** (see [Routes & Catalog](NETWORKING_CATALOG.md)),
    **Automation** (see [Automation](AUTOMATION.md));
  - *Assistance* — **Assistant**;
  - *Authoring* — **Localization** (see [Localization](LOCALIZATION.md)),
    **Themes** (see [Color ID](COLOR_ID.md)), **Content** (see [Content Packages](CONTENT_PACKAGES.md));
  - *Reference* — **Docs**, right-anchored.

  `com.molca.sequence` contributes **Sequence** (Authoring) and `com.molca.integration.sentry` contributes
  **Sentry** (Integrations), both on the same seam.

  Note the two different **Network** surfaces: the *workspace tab* authors the route catalog, while
  Settings ▸ Tooling ▸ **Network Activity** watches what the running app is actually doing.

### The toolbar at any width

The tab strip renders correctly at any provider count and any window width down to the 520px minimum. It
measures itself and degrades in order:

1. **Full fidelity** — `[icon] [label]` for every tab.
2. **Icon-only** — labels move into tooltips. All-or-nothing, and only when *every* tab resolves an icon: a
   row of blanks reads worse than a menu. The Settings tab and the active tab keep their labels either way.
3. **Overflow** — the remaining tabs move into a `» N` menu, grouped by [group](#semantic-groups), with a
   **Manage tabs…** entry that lands on Settings ▸ Editor. Nothing is ever silently dropped.

Which tabs keep a slot is decided by: Settings (anchored), then the active tab, then pinned tabs, then
recently used, then declared order. The active tab outranks a pinned one — you are looking at it, so it
cannot be the thing that disappears. A pinned tab carries a small accent dot in its top-right corner, so
what is holding a slot is visible without opening a menu. Hiding still wins over pinning, and the default
is no pins.

### Managing which tabs are shown

The **⋮ tabs menu** at the right end of the strip is always visible and is the primary surface for this: it
lists every workspace the project *could* show, checked when it is currently in the toolbar, so clicking one
shows or hides it. It also carries a **Pin** submenu for the tabs that are in the toolbar, and a
**Manage tabs…** entry that lands on the fuller Settings ▸ Editor ▸ Workspace Tabs card (rows, counts, and
both toggles at once).

Right-clicking an individual tab is the shortcut for the same operations on that one tab — **Pin/Unpin** and
**Hide tab**. The `×` on a tab does the same thing as **Hide tab**. Note that it can only *hide*: a hidden
workspace is filtered out of the tab set entirely, so nothing is left to right-click, and showing it again
goes through the tabs menu or the settings card.

One word, one operation: everything here **hides**. The Hub deliberately does not say "close" anywhere,
because nothing is being dismissed — hiding a tab is a persisted per-project setting
(`MolcaHubWorkspaceRegistry.SetHidden`) that survives restarts until you show the tab again. Hiding a tab
leaves the other workspaces' cached state untouched.

## Extension seams

All seams are discovered via `TypeCache`, so contributing is drop-in — implement the seam in an
Editor assembly and it appears automatically; a provider that throws is logged and skipped, never
breaking the Hub.

| Seam | Placement | Adds |
|---|---|---|
| `MolcaHubWorkspaceProvider` | `Editor/Hub/` | One or more workspace tabs (`MolcaHubWorkspaceItem`: id, title, order, group, factory). |
| `MolcaHubSettingsLeafProvider` | `Editor/Hub/` | One or more Settings-rail panels (`MolcaHubSettingsLeafItem`). |
| `MolcaDocsProvider` | `Editor/Hub/Docs/` | Reference docs (or drop Markdown files that the built-in provider already scans). |

### Which seam?

> Contribute a **workspace tab** when your surface is a full-window tool with its own toolbar, its own
> long-running work, and its own activity chips. Contribute a **settings leaf** when it is one panel of
> configuration or status. If it would look at home next to *Network* or *MCP*, it is a leaf.

The toolbar used to be the default destination because it was the only seam. It is not any more — reach for
the leaf first, and the toolbar stays a list of tools rather than a list of everything.

```csharp
// One panel of configuration, in the Settings rail (no Core edit).
internal sealed class MyLeafProvider : MolcaHubSettingsLeafProvider
{
    public override IEnumerable<MolcaHubSettingsLeafItem> GetLeaves() => new[]
    {
        new MolcaHubSettingsLeafItem("my-service", "My Service", () => new MyServicePanel(),
            group: MolcaHubSettingsLeafRegistry.Tooling),
    };
}
```

A leaf's `group` names the rail category it joins — `Framework`, `Tooling`, or `Addons` on
`MolcaHubSettingsLeafRegistry`. Anything else (or nothing) collects under an **Extensions** root that only
appears when at least one leaf lands there. Leaf ids are stored as `ext:<id>`, so a provider can never
collide with a Core section name.

### Semantic groups

A workspace tab declares a **group** rather than guessing a global `Order` integer:
`MolcaHubWorkspaceGroups.Quality`, `.Infrastructure`, `.Assistance`, `.Authoring`, `.Integrations`,
`.General` (the default), `.Reference`. Tabs sort by group rank first, so `Order` only has to be chosen
*within* your own group —
the one scope you can actually observe. A group Core does not declare is fine; it sorts after the declared
ones. Groups also drive the toolbar's group separators and the overflow menu's submenus.

`Infrastructure` is for surfaces describing how the project itself is wired — Core's **Network** (how it
talks to the world) and **Automation** (how it is driven and built). It is distinct from `Integrations`,
which is about connecting to one external product, and it sits next to `Quality` because you check project
health, then configure how the project talks to the world.

Declaring a group is not cosmetic. A tab that omits one lands in `General`, which sorts *after*
`Integrations` — so a Core-level authoring surface that forgets its group renders among the third-party
tabs. Give every tab a group, and pick `Order` only against the other members of that group.

Core's **Add-ons** are their own Settings-rail root with **Browse** (`MolcaHubSection.AddOnsBrowse`) and
**Installed** (`MolcaHubSection.AddOnsInstalled`) leaves, not the seams above; see
[Add-ons](ADDONS.md) for the consumer workflow.

### Search

The Settings rail's search box filters section rows *and* workspace tabs: matching tabs appear first under a
**Workspaces** group, and Enter activates the first match. Because the box lives in the Settings rail panel,
it is a Settings-surface affordance — it is not reachable while another workspace is active.

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
        new MolcaHubWorkspaceItem("my-tool", "My Tool", order: 10, () => new MyToolElement(),
            icon: "doctor", group: MolcaHubWorkspaceGroups.Integrations),
    };
}
```

Workspaces can be hidden per-project (`MolcaHubWorkspaceRegistry.SetHidden(id, hidden)`) and pinned
(`SetPinned(id, pinned)`); the Settings tab is the anchored home and is always present, always effectively
pinned, and never overflows.

### Keeping view state across tab switches

By default a workspace view is built when its tab is selected and detached when you leave — that
`DetachFromPanelEvent` is how a view cancels runs and disposes controllers, and it is a contract Core keeps.
Set `cacheContent: true` to opt out of the rebuild:

```csharp
new MolcaHubWorkspaceItem("my-tool", "My Tool", order: 10, () => new MyToolElement(),
    cacheContent: true),
```

An opted-in view is **hidden, not detached**, on a tab switch. That means:

- Its scroll position, filters, and in-progress state survive a round trip.
- Its work keeps running while hidden, and it will *not* receive `DetachFromPanelEvent` between activations —
  so any cleanup keyed on detach no longer runs on every switch. Detach still fires on eviction (a bounded
  number of views are kept, least-recently-used first — the bound is set above the number of workspaces
  that opt in, so rotating through them does not thrash) and when the view's own tab is hidden, so keep the
  cleanup. Hiding an *unrelated* tab no longer evicts you.
- **`AttachToPanelEvent` fires exactly once in the view's life.** This is the trap: attach is not an "I am
  on screen again" signal for a cached view, so anything that has to run per activation — consuming a
  pending deep link, re-reading state another surface may have changed while you were hidden — must not
  live in the attach handler.

Implement `IMolcaHubCachedView` when you need that signal; the host calls `OnWorkspaceActivated()` each
time it shows an already-built view:

```csharp
internal sealed class MyToolElement : VisualElement, IMolcaHubCachedView
{
    internal static string PendingTarget { get; set; }

    void IMolcaHubCachedView.OnWorkspaceActivated() => ConsumePendingTarget();
}
```

Core opts in **Docs** (scroll position and selected page), **Localization**, **Network**, **References**
and **Content**. Docs, Network and References also implement `IMolcaHubCachedView`, because all three are
deep-link targets. **Doctor** and **Assistant** stay uncached on purpose: both own long-running work whose
interaction with a hidden-but-live view deserves its own review, and the activity rail already carries
their status across tabs. **Sequence**, **Themes**, **Onboarding**, **Remediation** and **Automation** are
uncached today and rebuild on each activation.

## Bottom activity rail

The rail along the bottom of the window shows one chip per ongoing process or piece of live context, so a
long Doctor scan or an automation run stays visible from any tab. Contribute chips by subclassing
`MolcaHubActivityProvider` — discovered via `TypeCache`, so no Core edit and no registration call. A
provider is a *stateful observer*: observe your source in the constructor, call `NotifyChanged()` when your
chips change, return the current set from `GetActivities()`, and unsubscribe in `Dispose()`.

```csharp
internal sealed class MyActivityProvider : MolcaHubActivityProvider
{
    public override IEnumerable<MolcaHubActivity> GetActivities() => new[]
    {
        new MolcaHubActivity("my-export", "Export", "3/8 · textures",
            MolcaHubActivityState.Running, progress: 0.375f, workspaceId: "my-tool",
            remoteSafe: true), // opt in only if the caption is safe to leave the machine
    };
}
```

`remoteSafe` defaults to `false`, so a chip is not projected into a
[Molca Remote](REMOTE_EDITOR.md) session unless its provider opts in. The status caption is
author-controlled free text and Core cannot review what a third-party provider routes into it; set the flag
once you have confirmed your captions carry no customer names, ticket bodies, file paths, or credentials.
`OnClick` and `OnDismiss` are never serialized either way, so a projected chip is not remotely actionable.

## See also

- [Authoring Hub Docs](DOCS_AUTHORING.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Core MCP Tools](CORE_MCP_TOOLS.md)
- [Build System & Versioning](BUILD_SYSTEM.md)
- [Onboarding](ONBOARDING.md)
- [Add-ons](ADDONS.md)
