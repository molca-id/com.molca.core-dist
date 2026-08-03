---
title: Remediation
category: Tooling
order: 300
---

# Remediation — the "Fix Safe Issues" button

Molca's audit engines tell you what is wrong. Remediation is the layer that repairs the subset of
those findings that have exactly one correct answer, and tells you plainly about everything it did
not touch.

**Molca Hub → Remediation.**

---

## What the button actually does

It is not "fix everything". It is:

> Apply every fix that is unambiguously safe, in one undo group per domain, then show exactly what
> was fixed and exactly what still needs a human decision — with the reason attached to each.

```
Remediation            4 applied · 8 need review    [ Check All ] [ Fix Safe Issues (All) ]

  Bootstrap            clean                                  [ Check ] [ Fix Safe Issues ]
  Networking           1 fixable · 3 need review              [ Check ] [ Fix Safe Issues ]
    ▸ Would fix (1)
    ▾ Needs your decision (3)
    ▸ Review other fixes (1)
```

The **"Needs your decision"** list is the point of the feature. Singular groups in small sections open directly;
repeated groups start collapsed with exact finding/asset counts and retain their context on expansion. A
pass that repairs 4 of 12 findings and shows only a tick is worse than no pass at all.

Opening the workspace runs nothing. Auditing is read-only, but a read-only scan can open scenes, so
it happens when you click.

## Why some findings are never fixed

A finding earns a fix only when all four hold:

1. **Deterministic** — the result is computable with no input from you.
2. **Non-destructive** — no authored data is discarded.
3. **Reversible** — Unity Undo, or a file snapshot on the MCP undo stack.
4. **Locally decidable** — the right answer does not depend on intent recorded nowhere.

Most report-only findings fail (4). A duplicate Ref Id with inbound references, a failing contrast
pair, a missing translation, two environments where one must be the default — each has several
defensible answers, and picking one silently is how tooling destroys work while reporting success.
Those stay on the "needs your decision" list forever, by design.

Note that (1) and (3) are independent. A schema migration is perfectly deterministic and still writes
to disk, so Ctrl+Z will not bring it back; it therefore reverts by file snapshot and sits in
**Review other fixes** rather than the safe pass.

## The three policies

| Policy | Includes | Where it runs |
|---|---|---|
| `SafeOnly` | deterministic ∧ non-destructive ∧ Unity-Undo | The button. The only policy applied without per-fix confirmation. |
| `DeterministicReversible` | adds destructive and file-snapshot fixes | "Review other fixes", on a checked subset only |
| `All` | every deterministic fix | Automation only, never a UI default |

A fix that needs arguments is never run by any policy in a blanket pass — it has to be requested
explicitly.

## Reverting

- One Unity Undo group **per domain**, so you can revert a single domain's pass with Ctrl+Z.
- Fixes that rewrite or create files report an `McpUndoStack` entry id instead; revert those with
  `molca_undo_last_action`.
- The report lists which mechanisms a pass actually used, so the revert instruction is never a guess.

## What ships today

| Domain | Safe pass repairs | Notable refusals |
|---|---|---|
| **Bootstrap** | null entries in `GlobalSettings.modules` and `BootstrapExtensions` | duplicate module types; a missing project-settings asset (that means a broken install) |
| **Upgrade** | â€” | retired source and UnityEvent callbacks are reported at their exact locations; token choices are never guessed |
| **Networking** | the default environment, when exactly one is authored | anything with 0 or >1 environments; suspected secrets are never touched or echoed |
| **Content Packages** | duplicate labels, duplicate dependencies, blank labels | missing dependencies, cycles, versioning, identity |
| **Colour Theme** | — | everything except regenerating derived output; alias removal is separately gated |
| **Localization** | registering the project's only LocalizationModule, when it exists but is unregistered | translations, placeholders, plurals, fallbacks, fonts — all need a person |
| **Scenes** (Doctor) | GPU instancing, GI contribution, missing LOD groups | polygon budget and subsystem placement are judgment calls |
| **Sequence** | empty step Ref Ids (per controller) | an unresolved reference is never cleared to make validation pass |

Opt-in, in **Review other fixes**: legacy networking/catalog schema migration, colour-theme output
regeneration, texture import size, self-dependency removal, creating a `GlobalSettings` asset,
creating a setting module a subsystem declares it requires, and assigning a sole `RuntimeManager`
prefab.

## Reading a long list

A real project can produce hundreds of findings, so results group by finding code — the level at
which they share a cause and a remedy. The header carries the count, so *"34 duplicate providers"* is
one line rather than thirty-four. Groups expand automatically while a section is short; past that they
start collapsed, and you get **Expand all** / **Collapse all** and a filter that matches finding codes
and paths.

Each group renders at most 25 rows, then says how many more share that cause. Nothing is hidden — the
count on the header always reflects every finding, whether or not its row is drawn.

## Two surfaces are separate on purpose

- **References** are repaired in Molca Hub → References. Reference repair is a revision-pinned
  transaction that refuses a plan built against a project that has since changed — a guarantee a
  generic sweep cannot keep — so it is approved there.
- **Sequence** remediation targets one controller and lives on that controller's surface. A
  project-wide button would have to invent which controllers to touch.

## From automation

```
molca_remediation_plan  { domain?, policy? }
molca_remediation_apply { domain?, policy?, fixIds?, dryRun? }
```

Omit `domain` to cover every registered domain. `dryRun` is identical to planning. An unknown domain
name is an error listing the real ones, never a silent empty run.

`molca_references_plan_fix` / `molca_references_apply_fix` remain the reference-repair pair.

## Adding a fix (forks and add-ons)

Implement `IMolcaFix` — or extend `MolcaFixBase`, which supplies the common facets:

```csharp
internal sealed class MyFix : MolcaFixBase
{
    public override string Id => "mydomain.do-the-thing";
    public override string Description => "What this changes, in the user's terms.";
    public override string HandledFindingCode => "mydomain.the-finding";

    public override MolcaFixOutcome Apply(
        MolcaFixTarget target, bool dryRun, JObject args, CancellationToken ct)
    {
        if (!CanDecideLocally(target))
            return MolcaFixOutcome.NotApplied("Why this needs a human — this text reaches the user.");

        if (dryRun) return new MolcaFixOutcome(true, "Would …", before, after);
        // … mutate through the domain's own editing service, never a raw SerializedObject …
        return new MolcaFixOutcome(true, "Did …", before, after);
    }
}
```

Rules that are not negotiable:

- **Declare facets honestly.** If the fix writes a file, it is `FileSnapshot`, however deterministic
  it is. If it creates an asset, it is `FileSnapshot` — Unity Undo cannot reliably remove one, so also
  record the created path with `McpUndoStack.RecordCreated` and return its id.
- **Never falsify a facet to control where a fix runs.** `IsDestructive` means the change discards
  authored data; it is not a "keep this out of the safe pass" lever. If a fix is mechanically possible
  but shouldn't run, the answer is that it shouldn't exist — a fix that clears a blocking error by
  inventing content makes the problem silent instead of solving it.
- **Route through the domain's existing mutation service.** Never open your own `SerializedObject`
  against an asset a service already owns, and never add a second writer for a settings asset.
- **Never mutate on a scan.** A fix runs only from an explicit pass. Audits, refreshes, Inspector
  draws and build gates stay read-only.
- **Honour `dryRun`.** Planning must change nothing; if the underlying service has no preview mode,
  answer "would this change anything?" from the data.
- **Be idempotent.** Re-running must be a no-op. This is what lets the fixpoint loop terminate.
- **Explain refusals.** The message in `NotApplied` is shown to the user as the reason.

To put a whole audit behind the button, implement `IMolcaRemediationDomainProvider` and return a
`MolcaRemediationDomain` whose `CreateRequest` re-runs your read-only audit — it is called once per
fixpoint iteration, so returning a captured snapshot would hide any finding a fix exposed.

Wrapping an existing per-domain fix abstraction? Supply it through `IMolcaFixContributor` and mark
the adapter `[MolcaFixSuppliedByContributor]`, as the scene and sequence adapters do.

## Design record

`docs/internal/UNIFIED_REMEDIATION_DESIGN.md` in the platform repo holds the full per-finding
classification for every domain, and the numbered list of places the implementation corrected the
design.
