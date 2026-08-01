---
title: Color ID Migration Guide (V1 → V2)
category: UI & Presentation
order: 511
---

# Color ID Migration Guide (V1 → V2)

This is the upgrade path from the V1 `ColorModule` palette to the V2 Color Theme Set. For what the two
systems *are*, read [COLOR_ID.md](COLOR_ID.md) first — this document only covers moving between them.

**Nothing here is urgent.** V1 content keeps working. Every legacy `(swatch, colorId)` pair is translated
through an alias map at resolution time, so 194 existing `ColorID` components render the same colour under
V2 as they did under V1. What follows is how to move at your own pace, and what the deadlines actually are.

## The one-paragraph version

Install a Color Theme Set, point a `ColorThemeSettings` module at it, and your existing content resolves
through the alias map with no edits. From then on, author new colour with `ColorThemeBinding` and canonical
token IDs. Convert old content when you touch it. The alias map stays until Core 2.0.0.

## What is deprecated, and what replaces it

Every item below still works and is still tested. Each is scheduled for removal in **Core 2.0.0** — a major
release, which is the only kind that may remove any of them.

| Deprecated | Replacement | Why |
|---|---|---|
| Implicit `ColorIDReference` → `Color` | `reference.TryResolve(themeService, out var color)` | The implicit form reads hidden global state, so it returns the right colour after bootstrap and magenta before it, with nothing at the call site to suggest that ordering matters. |
| `ColorModule.AddColor` / `RemoveColor` / `UpdateColor` / `AddSwatch` / `RemoveSwatch` | Author a `ColorThemeSet` through `ColorThemeSetEditing`; switch appearance with `IColorThemeService.TrySetVariant` | A `ColorModule` is read-only config. These write the asset in the editor and only the in-memory cache in a player, so the same call means two different things depending on where it runs. |
| `Molca.ColorID.ColorUtility` (whole class) | `ColorThemeBinding` with a canonical token | It applies a colour once, so nothing it touches follows a later variant switch; it takes an unvalidatable `colorId` string; and it is invisible to the colour audit. |
| `MolcaUiToken.NewColor(id, swatch, colorId)` | `MolcaUiToken.NewColorToken(id, canonicalTokenId)` | A catalog entry authored as a legacy pair depends on the alias map, which has an end date. |

Two things are deliberately **not** deprecated:

- **`ColorIDReference.Color`.** It is the like-for-like replacement for the implicit conversion, and it at
  least makes the read explicit. Use it wherever threading a theme service through is not worth it.
- **`ColorID` components and `ColorIDReference` fields themselves.** Serialized content is not deprecated;
  only the ways of *resolving* it that hide their dependencies are. There is no need to rewrite a prefab to
  silence a warning.

`ColorUtility.LerpColor` has no replacement by design. Interpolating two theme colours produces a third that
exists in no variant, which is precisely what a token contract exists to prevent. Bind the endpoints and
animate a material or canvas property instead.

## Step 1 — install V2

Run **Molca → ColorID → Create or Update Colour Vocabulary Asset**, then **Install Color Theme Settings
(V1 → V2)**. The first writes the shipped vocabulary — 36 tokens, Dark and Light, 22 legacy aliases, 6
contrast requirements — to `Assets/_MolcaSDK/Settings/Global/Themes/`. The second registers a
`ColorThemeSettings` module in `Global Settings.asset` and points it at that asset.

The installer refuses a theme set that fails validation, because installing one would force the degraded
emergency fallback and every colour in the project would come from it. It is idempotent, and it leaves your
`ColorModule` palettes listed in `Global Settings.asset` — inert under V2, but there, so reverting is a
one-line change.

Verify with **Molca → ColorID → Report Colour Theme Installation**.

## Step 2 — check what the alias map covers

Run **Molca → ColorID → Report Compatibility Usage**. It reports:

- how many colour references are canonical versus legacy;
- per alias, how many sites use it, split into project-owned content and installed-package content;
- **legacy keys that match no alias** — these render magenta today and are the only urgent item in the
  report.

If the report says `NOT CONCLUSIVE`, a declared input was skipped and the counts are a lower bound. Fix the
coverage before drawing conclusions from it; the numbers are useless as evidence otherwise.

There is a headless form for CI:

```
Unity -batchmode -projectPath <path> \
  -executeMethod Molca.ColorID.Editor.ColorThemeDeprecationReportMenu.ReportFromCli
```

It exits non-zero only when the report is inconclusive. Remaining legacy usage is expected during the
compatibility window and does not fail the build.

## Step 3 — author new colour the V2 way

Add a **`ColorThemeBinding`** to the object and give it a canonical token. It discovers its own targets,
follows variant switches, and is visible to the audit — none of which a `ColorID` component or a one-shot
`ColorUtility` call does.

To read a colour in your own code:

```csharp
var theme = RuntimeManager.GetService<IColorThemeService>();
if (theme != null && theme.TryResolve("text/primary", out Color color))
{
    label.color = color;
}
```

Hold a `ColorTokenReference` for anything authored in the inspector. It has no implicit conversion and no
static resolution path on purpose: every read takes a service, so a test or tool can resolve against a theme
it chose rather than whatever global happens to be installed.

## Step 4 — convert old content when you touch it

Two conversions are automated:

- **UI Token Catalog** — `MolcaUiTokenCatalogMigration` converts legacy pairs to canonical tokens. Only
  exact aliases migrate, and a conversion is blocked when the canonical token is missing from any variant.
  Preview it before applying; there is a CLI form (`MolcaUiTokenCatalogMigrationCli`).
- **Interchange** — export a theme set to DTCG-shaped JSON, edit it elsewhere, and preview the import. The
  importer builds and *resolves* an in-memory candidate first, and reports contrast regressions against the
  current set, so a round trip cannot silently degrade accessibility.

Prefab and scene content is not converted automatically. It resolves correctly through the alias map, so
there is no correctness reason to rewrite it; do it when you are already editing the object.

## What removal will require

Core 2.0.0 may remove the deprecated APIs and the legacy alias map. It will not do so blind. Removal of any
alias requires all of:

1. the alias declares a removal version — every shipped alias declares `2.0.0`;
2. a **conclusive** compatibility usage report — no skipped inputs, usage index present;
3. **zero usage in installed package content**, which a consuming project cannot rewrite;
4. zero usage in project content, or an automated migration for it.

An alias with no declared removal version is never removable at any usage count, because nothing recorded
which release consumers were told to expect. `ColorThemeDeprecationReport` computes all of this; it is the
gate, not a summary of one.

## Behaviour changes to know about

**The Light variant's de-emphasised text changed.** `Text.60` and `Text.40` resolve to `text/muted` and
`text/subtle`. In Dark they are byte-identical to V1. In Light their alpha was raised — 0.60 → 0.67 and
0.40 → 0.53 — because the V1 values measured 3.80:1 and 2.28:1 against the Light canvas and failed WCAG AA
and even the large-text threshold respectively. V1 could not detect this: nothing recorded that those
colours were foregrounds. Dark is the shipped default variant, so a project that has not switched to Light
sees no change. Every other mapped legacy key is byte-identical in both variants, and a test asserts that.

**`status/error/text` still fails in Light** at 2.88:1 against the large-text threshold. It is recorded at
`Warning` severity with the measured ratio rather than being changed, because fixing it means re-picking a
brand hue rather than adjusting a neutral's opacity — a design decision, not a mechanical one.

## See also

- [COLOR_ID.md](COLOR_ID.md) — what V1 and V2 are.
- [UI_TOKENS.md](UI_TOKENS.md) — the catalog layer above both.
- [DOCTOR_CHECKS.md](DOCTOR_CHECKS.md) — the colour checks that run in Doctor and the build gate.
