---
title: Authoring Hub Docs
category: Tooling
order: 940
---

# Authoring Hub Docs

Reference guides shipped by Molca Core and any `com.molca.*` package are browsable in-editor from
**Molca → Hub → Docs**, rendered natively by the shared Markdown renderer. This page describes how the
docs system discovers, structures, and links documents — everything an SDK fork or project needs to add
its own docs.

## Where docs live

Each package's guides live under `Documentation~/reference/*.md`. The `Documentation~` folder ends in
`~`, so Unity never imports it into the AssetDatabase; the Hub reads the files straight from disk. Core's
built-in provider scans **every installed `com.molca.*` package** — so a fork adds docs simply by shipping
Markdown files there, no code required.

## Front-matter

Every doc begins with a YAML front-matter block that drives its place in the rail. It is parsed as
metadata and never rendered:

```
---
title: Assistant Vision
category: Assistant
order: 40
---
```

| Field | Meaning |
|---|---|
| `title` | Display name in the rail and the doc header. Falls back to the first heading. |
| `category` | The rail parent this doc nests under. Falls back to `Reference`. |
| `order` | Sort order within the category (ascending). Docs without one sort last. |
| `id` | Optional stable id for `molca://doc/` links. Defaults to the file name (without `.md`). |
| `product` | Optional product/documentation-set label (the switcher grouping — see below). Defaults to the owning package's display name. |

Categories are ordered by their lowest doc `order`, so a small `order` on one doc floats its whole
category up.

## Products (the switcher)

Docs are grouped by **product** — one documentation set per owning package (Core, the shared SDK, a fork,
or the project). When more than one product ships docs, a switcher appears above the rail and each product
gets the whole rail to itself; with a single product the switcher is hidden. Products order Core → SDK →
other `com.molca.*` packages → forks/other → project.

You rarely set `product` by hand: a doc's product defaults to its owning package's `displayName`
(from `package.json`), and un-owned project docs group under "Project". Set the `product` front-matter
field only to override that label (e.g. a friendlier `product: VR Training SDK`). Keep it consistent across
a package's docs — they group by owning package, and the first non-blank `product` in the group wins the
label.

## Linking

The renderer understands ordinary Markdown links plus a Molca editor link scheme. For each kind below the
code block shows the syntax and the **Try it** line is a live link you can click in this viewer.

### Cross-doc

A plain link to a sibling guide navigates the browser in-place *and* still renders on GitHub; you can also
target a doc by its id:

```
[Sequence Validation](SEQUENCE_VALIDATION.md)
[Validation](molca://doc/SEQUENCE_VALIDATION)
```

Try it: [Sequence Validation](SEQUENCE_VALIDATION.md) · [Sequence Authoring, by id](molca://doc/SEQUENCE_AUTHORING)

### Project assets

`molca://asset/<guid-or-path>` selects and pings an asset in the Project window — either a 32-char GUID or
an asset path:

```
[Editor tokens](molca://asset/2f7c41a9d8b34e6e9a1c7b5e3f0d6a21)
[Editor tokens](molca://asset/Packages/com.molca.core/Editor/UI/MolcaEditorTokens.uss)
```

Try it: [MolcaEditorTokens.uss](molca://asset/Packages/com.molca.core/Editor/UI/MolcaEditorTokens.uss) ·
[MolcaEditorComponents.uss](molca://asset/Packages/com.molca.core/Editor/UI/Components/MolcaEditorComponents.uss)

### Source files and web

A `file.cs:line` reference or a `[label](path/File.cs:42)` link opens the file at that line; `http(s)`
links open in the browser on click.

Prefer the path form of `molca://asset` for readability; the GUID form is handy when a tool hands you a
GUID. For a fork or project doc, point these at your own prefabs, scenes, and ScriptableObjects so a
reader can jump straight to the thing being described.

## Contributing docs from a fork

Two ways, from lowest to highest effort:

1. **Drop files** — put `*.md` (with front-matter) under your package's `Documentation~/reference/`. Core's
   built-in provider discovers them automatically.
2. **Custom provider** — for docs that are not on-disk files (generated, remote, or assembled), subclass
   `MolcaDocsProvider` and return `MolcaDocEntry` descriptors. Non-abstract subclasses are discovered via
   `TypeCache`; a provider that throws is logged and skipped, never breaking the Hub.

## See also

- [Editor Design Language](EDITOR_DESIGN_LANGUAGE.md)
- [UI Tokens](UI_TOKENS.md)
- [Core MCP Tools](CORE_MCP_TOOLS.md)
