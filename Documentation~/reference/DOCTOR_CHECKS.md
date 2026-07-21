---
title: Extending Molca Doctor with Custom Checks
category: Diagnostics
order: 1100
---

# Extending Molca Doctor with Custom Checks

Molca Doctor validates project conventions. Like the rest of the framework
(`architecture.md`), **Core defines the built-in checks; an SDK layer or consumer project extends by
*adding* checks, never by modifying Core.** A check is contributed by implementing an interface in an
Editor assembly — no Core file changes, no asmdef edits to Core, no registry edits.

This mirrors the extension contract of `SequenceValidatorRegistry` (sequence validators) and the MCP
tool registry: implementations are discovered by `TypeCache`, instantiated once, de-duplicated by id,
and run together.

## The contract

Implement `Molca.Editor.Doctor.IDoctorCheck` in any **Editor** assembly (your check runs only in the
editor):

| Member | Required | Purpose |
|---|---|---|
| `Id` | yes | Stable, globally-unique, kebab-case id used in reports, the window toggle list, `.doctorignore`, and suppressions. A duplicate id is rejected loudly at discovery. |
| `Description` | yes | One-line summary shown in the Doctor window. |
| `Category` | no | Group the check appears under in the Doctor window, and the unit by which a run can be scoped to a related subset. **Defaults** to a value derived from the `Id` prefix (`DoctorCategories.Derive`), so most checks need not set it. Override only when the id prefix would group the check wrongly. |
| `RunAsync(context, ct)` | yes | Runs the check and returns all findings (never null). Side-effect free — a check *reports* issues, it never fixes them. |

Requirements for discovery:

- A **public parameterless constructor** (the registry instantiates via `Activator.CreateInstance`).
- A **unique, non-empty `Id`**. Empty or duplicate ids are skipped and surfaced in
  `DoctorCheckRegistry.Errors` (logged as a warning).

That's it. The check appears automatically in the Doctor window, in `MolcaDoctor.RunCI`, and in the
`molca_doctor` MCP tools.

## Threading

`RunAsync` receives a shared `DoctorContext` (cached source files, scan scope) and a
`CancellationToken` (cancelled on user-abort or editor teardown; observe it cooperatively —
`OperationCanceledException` is expected, not a failure):

- **CPU-only** checks (text/reflection scans) should hop to a background thread via
  `Awaitable.BackgroundThreadAsync()` so the editor stays responsive.
- Checks that touch `AssetDatabase`, `SerializedObject`, or the scene graph **must stay on the main
  thread** and yield periodically with `Awaitable.NextFrameAsync(cancellationToken)`.

Report sub-check progress with `context.ReportStatus("Prefabs 3/12")`.

## Minimal example

```csharp
using System.Collections.Generic;
using System.Threading;
using Molca.Editor.Doctor;
using UnityEngine;

namespace MyProject.Editor.Doctor
{
    /// <summary>Flags runtime scripts that reference the legacy Foo API.</summary>
    public sealed class LegacyFooUsageCheck : IDoctorCheck
    {
        public string Id => "legacy-foo-usage";
        public string Description => "Runtime code must not call the deprecated Foo API.";

        public async Awaitable<IReadOnlyList<DoctorIssue>> RunAsync(
            DoctorContext context, CancellationToken cancellationToken)
        {
            await Awaitable.BackgroundThreadAsync(); // pure text scan — off the main thread
            var issues = new List<DoctorIssue>();

            foreach (var file in context.RuntimeSources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int i = 0; i < file.Lines.Length; i++)
                {
                    var line = file.Lines[i];
                    if (line.Contains("Foo.Bar(") && !DoctorContext.IsSuppressed(line))
                        issues.Add(new DoctorIssue(
                            Id, DoctorSeverity.Warning,
                            "Deprecated Foo.Bar call.", file.Path, i + 1));
                }
            }

            return issues;
        }
    }
}
```

## Ordering

`DoctorCheckRegistry` runs Core's built-in checks first, in their curated order
(`DoctorCheckRegistry.BuiltInOrder`), then every other discovered check sorted by `Id` (ordinal).
Checks are side-effect free and independent, so order only affects report/UI grouping — never results.
Prefix related project checks with a common id stem (e.g. `myproject-…`) to keep them grouped.

In the Doctor window the checks are further organized into **categories** (see `Category` above): each
category is a collapsible section (collapsed by default) with an enabled/total count and a single
whole-group toggle, so you can run just the scene checks, just the networking checks, and so on without
hand-toggling each chip — expand a category only when you want per-check control. Category order follows
the first check of that category in the run order above. A check whose id matches no known prefix — and that does not set
`Category` — lands in the `General` catch-all; giving it a shared id stem, or setting `Category`
explicitly, groups it instead.

## Scoping what gets scanned

A check reads sources from `DoctorContext`, which already excludes third-party/vendor locations. A
fork or project can tune exclusions **without touching Core**:

- A `.doctorignore` file at the project root (one glob per line, `#` comments).
- An inline `// doctor:ignore` marker suppresses any finding on that line.

## Testing a check

Drive it directly — no Unity run required for pure text-scan checks:

```csharp
var issues = await new LegacyFooUsageCheck().RunAsync(new DoctorContext(), CancellationToken.None);
```

For the discovery/ordering contract itself, see `DoctorCheckRegistry.BuildChecks` (exposed `internal`
for tests) and `Tests/Editor/DoctorCheckRegistryTests.cs`.

## See also

- [Extending MCP from a Fork](MCP_FORK_PROVIDERS.md)
- [Core MCP Tools](CORE_MCP_TOOLS.md)
- [Editor Design Language](EDITOR_DESIGN_LANGUAGE.md)
