# Colour migration guide (1.x → 2.x)

Core 2.x removes the 1.x colour runtime, but it ships the editor-only migrators needed for a direct
upgrade. Do not downgrade or install an intermediate Core version.

## The short version

1. Upgrade the Core package to 2.x and let Unity compile.
2. Open **Molca ▸ Upgrade ▸ Report 1.x → 2.x Readiness**.
3. If the report names a theme prerequisite, open **Molca Hub ▸ Themes**. Review the project's 1.x
   palette values, then create or choose exactly one validated `ColorThemeSet` and install one
   `ColorThemeSettings` module that references it. The audit never writes a stock palette over this choice.
4. Return to Remediation and run the offered colour fixes.
5. Run the report again; resolve any refused sites it names.
6. Replace retired API usage reported in consumer C#.

The report is authoritative. It discovers detectors and fixes by type, so every subsystem contributes
its own upgrade knowledge without a central list that can silently omit it.

## Colour mappings

| 1.x shape | 2.x shape |
|---|---|
| `ColorModule` palettes | `ColorThemeSettings` + `ColorThemeSet` variants |
| `ColorID` component | `ColorThemeBinding` + canonical `ColorTokenReference` |
| `ColorIDReference` field | `ColorTokenReference` |
| `IColorProvider` lookup | `IColorThemeService.TryResolve` |
| `IColorSchemeService` switching | `IColorThemeService.SetVariant` / `TrySetVariant` |
| `ColorUtility` one-shot application | a persistent `ColorThemeBinding` |
| UI Token Catalog swatch/colour pair | canonical colour token |

`ColorSchemeDropdown` and `ColorSchemeToggle` already use the 2.x variant API. Their component and script
GUIDs are unchanged, so existing prefab references survive the package upgrade.

## How missing components are migrated

Unity serializes a MonoBehaviour reference using its script GUID. That GUID remains in a prefab or scene
after the C# class is deleted, which gives the 2.x editor migrator a durable identity for 1.x `ColorID`
components.

Once that reviewed theme is installed, the migration:

- scans serialized prefabs and scenes for the retired script GUID;
- reads the old swatch, colour ID, target components, and alpha policy from YAML;
- translates the old pair through the installed theme set's `LegacyColorAlias` entries;
- writes canonical `ColorThemeBinding` data;
- carries supported prefab-instance overrides;
- removes the retired component and verifies that it is gone after serialization.

No deleted runtime type is loaded or referenced by the migrator.

## Refused sites

The repair deliberately refuses a site when choosing a replacement would require guessing. Common
reasons are:

- no single validated, installed theme is available yet;
- the theme set has no authored alias for the old pair;
- the old component has no recoverable target list;
- an instance override cannot be represented safely on the new schema;
- a package-owned asset is not writable from the consumer project.

The report includes the asset and reason. Add an alias or author the binding explicitly, then rerun the
report. A refusal is not counted as migrated.

## Consumer C#

Source code is reported, not rewritten. The readiness report names each retired API usage with file,
line, and replacement guidance. This keeps application-specific control flow and ownership decisions in
the consumer's hands.

Typical replacement:

```csharp
await RuntimeManager.WaitForInitialization(destroyCancellationToken);
var theme = RuntimeManager.GetService<IColorThemeService>();

if (theme != null && theme.TryResolve("action/primary/fill", out Color colour))
    image.color = colour;
```

Prefer a serialized `ColorThemeBinding` when the colour should follow future variant switches.

## UI Token Catalogs

The unified repair converts legacy colour entries through the same alias map. The 2.x resolver refuses
an unmigrated pair instead of recreating a deleted `ColorID` component. New entries should use
`MolcaUiToken.NewColorToken(id, canonicalTokenId)`.

## Validate the result

After remediation:

- the readiness report should show no colour component or reference findings;
- Doctor should show no unresolved canonical colour references;
- every selectable variant should resolve its required tokens;
- entering Play Mode should publish a non-degraded `IColorThemeService`.

See [Colour themes](COLOR_ID.md) for the 2.x runtime API.
