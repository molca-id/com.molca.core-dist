---
title: Localization
category: Localization & Audio
order: 700
---

# Localization

Molca localization builds on Unity Localization. `LocalizationManager` owns locale policy and change
coordination, `LocalizedText` binds a StringTable entry to TextMeshPro, and `LocalizedValue`
represents an explicit catalog, inline, or empty source. `DynamicLocalization` is the compatibility
name for serialized schema-v1 fields.

Unity Localization's `SelectedLocale` is the runtime source of truth. `LocalizationModule` declares the
supported-language policy and persists the last valid selection; it is not a second runtime authority.

## Runtime pieces

| Type | Role |
|---|---|
| `LocalizationManager` | Validates startup configuration, selects locales, persists valid changes, and refreshes consumers. |
| `LocalizationModule` | Declares supported BCP-47 codes, presentation profiles, and explicit fallback edges. |
| `LocalizedText` | Reactively displays a `LocalizedString` and applies a reusable text style. |
| `LocalePresentationProfile` | Declares TMP font chain, required glyphs, writing direction, and line-breaking guidance. |
| `LocalizedLayoutDirectionAdapter` | Explicitly opts selected text and horizontal layouts into RTL adaptation. |
| `LocalizedValue` | Schema-v2 catalog, inline, or empty localized value and binding factory. |
| `DynamicLocalization` | Compatibility subclass that preserves schema-v1 serialization and APIs. |
| `LocalizedTextStyleInfo` | Reusable TMP font, style, and size preset. |

All runtime types are in `Molca.Localization`.

## Configure languages

Create a module with **Create > Molca > Settings > Localization**, then register it in
`GlobalSettings`. Every `Languages` row must have:

- a non-empty, unique BCP-47 code;
- a matching Unity Locale asset in **Available Locales**;
- an optional display name and flag sprite;
- a locale presentation profile with an explicit writing direction and primary/fallback TMP fonts;
- any explicit fallback locale codes.

The first valid row is the default fallback. Startup fails clearly when the module is missing, contains
no valid codes, or none of its codes has a Unity Locale asset. A previously persisted code is used only
when it is still valid; otherwise Molca uses the current valid Unity locale or the first valid row.

Useful policy APIs:

| Member | Purpose |
|---|---|
| `LanguageCode` | Returns configured codes. |
| `ActiveLanguage` | Returns the last valid persisted selection. |
| `ActiveLanguageEntry` | Returns the matching descriptor. |
| `HasLanguage(code)` | Performs case-insensitive policy membership validation. |
| `GetFlagForLanguage(code)` | Returns the configured flag. |
| `SetLanguage(index)` | Requests a configured row; invalid indexes are rejected. |

## Select and read locales

Resolve the subsystem through injection or `RuntimeManager`:

```csharp
[Inject] private LocalizationManager _localization;

private async void Start()
{
    try
    {
        await RuntimeManager.WaitForInitialization();
        if (this == null) return;
        LocalizationManager.SetLanguage("id");
    }
    catch (System.Exception exception)
    {
        Debug.LogError(exception);
    }
}
```

`LocalizationManager.CurrentLanguage` returns Unity's selected locale code.
`DefaultLanguageCode` returns the first valid module code. `SetLanguage` rejects codes that are absent
from either Molca policy or Unity Available Locales.

`GetLocalizedStringAsync(key, languageCode)` honors the explicit language argument. An unknown requested
locale returns the key fallback instead of silently resolving the current locale.

Successful Unity locale changes are persisted and dispatched through `TypedEvents.LanguageChanged`.
Dynamic registrations use weak references so abandoned serializable values are not retained forever.

## Bind TextMeshPro

Add `LocalizedText` to a GameObject with `TextMeshProUGUI`, then assign a `LocalizedString` and optional
`LocalizedTextStyleInfo`.

```csharp
label.SetLocalizedString(
    LocalizationManager.GetLocalizedString("UI", "start-button"));
label.SetStyle(myStyleInfo);
```

Rebinding refreshes immediately. Unity's `StringChanged` callback applies the delivered value directly,
and generation guards prevent an older asynchronous request from overwriting a newer binding.
Disable/enable cycles unregister and restore both manager and string subscriptions.

### Labels filled by code

A label whose text arrives at runtime is authored with an empty `LocalizedString`. Tick **Runtime
Assigned** on it. The flag is a declaration, and it is read from both sides:

- The localization audit stops reporting `localization-reference-empty` for that label — an empty slot
  somebody has claimed is intent, not an omission.
- It reports `localization-runtime-assigned-authored` if the label is *also* given an authored
  reference, because the authored value renders until code replaces it.
- At runtime the component asserts the promise: a Runtime Assigned label that reaches the end of an
  enabled lifetime without a `SetLocalizedString` call logs a warning naming the object. Nothing is
  checked on a timer, so a label waiting on a fetch is never accused mid-wait — only one that stayed
  blank the whole time it was visible.

Leaving the flag clear keeps the old behaviour: an empty reference is reported as a warning, which
never blocks a build.

## Inline and catalog values

`LocalizedValue` has three explicit source kinds:

- **Inline mode:** language-code/value rows resolve directly from serialized data. Inline values never
  mutate a shared Unity StringTable at runtime.
- **Catalog mode:** a Unity `LocalizedString` resolves through Unity Localization.
- **None:** intentionally resolves empty until an author selects a source.

Await `InitAsync` before immediately resolving:

```csharp
await _greeting.InitAsync("scene-intro-greeting");
string text = await _greeting.GetLocalizedString();
```

Inline fallback order follows the requested locale's explicit fallback graph and then the module
default before using the first authored value. Localized audio uses the same graph. Catalog mode
uses the Unity reference. Retained schema-v1 data resolves through a compatibility adapter until an
explicit migration copies it to schema v2.
`SetTextForLanguage` updates inline data directly and raises `ValueChanged` when the resolved value
changes. Blank language codes are rejected.

`CreateBinding(arguments)` returns a disposable, generation-safe `LocalizedValueBinding`. It refreshes
on Unity locale changes, retains the last successful result, and prevents an older asynchronous resolve
from overwriting a newer source or Smart String argument set.

## Safe Inspector authoring

The `LocalizedValue` drawer never resizes, reorders, stamps, or deletes translation rows during
ordinary drawing. It reports:

- missing configured languages;
- blank or unknown/orphaned codes;
- duplicate codes.

Choose **Add Missing Languages** to append only missing rows. Existing and orphaned rows remain in place
for an explicit author decision. Multi-object editing does not run structural repairs.

## Doctor

Doctor delegates to `LocalizationAuditEngine`; it does not maintain a separate discovery implementation.
Each run returns a `LocalizationAuditSnapshot` with a stable snapshot id, catalog/source fingerprint,
deterministically ordered findings, and explicit declared/scanned/ignored/failed coverage.

The compatibility `dynamic-localization-locale-invalid` check surfaces focused finding ids including:

- missing or multiple `LocalizationModule` assets;
- blank/duplicate module codes and missing Unity Locale assets;
- blank, unknown, or duplicate inline rows;
- missing required language rows and empty required values.
- retained schema-v1 localized values that should be migrated.
- invalid fallback graphs, missing locale profiles/fonts/glyphs/direction, plural or Smart String
  mismatches, and horizontal RTL surfaces without an explicit adapter.

It scans prefabs and ScriptableObjects in `Assets`, embedded `Packages/com.molca.*` content, and loaded
scenes. A serialized-YAML prefilter skips assets that cannot contain `LocalizedValue` or
`LocalizedText`, keeping full scans responsive without narrowing coverage. The scan is read-only;
repairs are explicit authoring actions.

`dynamic-localization-init-contract` continues to detect fire-and-forget initialization races and reads
from never-initialized fields.

## Hub and MCP

Open the first-class **Molca Hub > Localization** workspace to see:

- configured locale policy;
- audit status and source fingerprint;
- declared and scanned asset/scene coverage;
- stable errors and warnings;
- an explicit production preflight;
- **Add or Repair Locale** and archive transaction previews;
- a stable-identity String Catalog browser and previewed cell editor;
- previewed CSV import and deterministic CSV export.
- a legacy-value inventory and previewed schema-v2 migration.
- globalization policy summaries and non-mutating pseudo/overflow previews.

The workspace opens on a lightweight landing card. Choose **Run Audit & Open** to start its explicit
project-wide scan and load the complete authoring surface; restoring the tab after a script reload does
not silently rescan and block the Editor. Legacy-value inventory is separately started with
**Scan & Preview Migration**, because it must inspect every eligible serialized object.

`molca_localization_coverage` returns the same snapshot fields through schema version 2 while retaining
the older loaded-scene entry list for compatibility.

`molca_localization_status` includes each locale's presentation profile, writing direction, font,
missing-glyph count, fallback edges, and resolved chain. `molca_localization_pseudo_preview`,
`molca_localization_pseudo_catalog`, and `molca_localization_pseudo_overflow` provide read-only stress
workflows for expansion/accenting, visible missing keys, RTL ordering, and loaded UI bounds.

Value migration is also two-step. `molca_localization_migration_inventory` returns stable
asset/object/property locators and a fingerprint. `molca_localization_plan_migrate_values` creates an
expiring preview; `molca_localization_migrate_values` refuses stale previews, migrates every writable
target as one Undo transaction, and returns a fresh Doctor audit. Package-owned targets are reported
but never rewritten from the consuming project.

Adding a locale is deliberately a two-step operation in both Hub and MCP. Preview first with
`molca_localization_plan_add_language`; the returned plan lists every Molca module, Locale,
Addressables, StringTable, and AssetTable mutation and binds them to the current catalog fingerprint.
Execute its `planId` with `molca_localization_add_language`. Execution refuses expired or stale plans,
repairs partially configured locales, verifies all postconditions, and rolls back the entire operation
if any asset fails. A successful transaction is one Unity Undo group.

Locale removal is archive-first. Preview with `molca_localization_plan_archive_language`, then execute
the returned plan through `molca_localization_archive_language`. Archive removes the Molca policy row,
Unity registration, table-collection membership, and Addressables entries, but deliberately preserves
the Locale asset, table assets, and inline rows. Those retained values support Undo, later restore, and
a separately confirmed destructive cleanup workflow. Archive refuses to remove the final configured
locale and warns when removing the current default changes fallback behavior.

Catalog values use the same preview/apply rule. The Hub catalog shows collection GUID, entry ID, key,
locale, current value, missing state, and ownership. A cell edit or new key is checked against the current
audit fingerprint and placeholder set before it can run, then saved and verified as one Undo group.
Package-owned tables are reported but cannot be edited.

CSV export uses RFC 4180 quoting and schema `molca.localization.catalog.v1`. Stable collection and entry
IDs are authoritative; names and keys are also included so a rename or identity mismatch is visible.
Import is all-or-nothing: unknown/stale identities, locale/key mismatch, placeholder mismatch, smart
metadata changes, conflicting duplicate rows, and read-only targets block the complete plan. Catalog v1
changes values and can fill a missing locale cell; it never deletes keys or changes smart metadata.
Doctor reports placeholder differences against the configured default locale. Missing catalog values
are warnings during interactive authoring and errors when a production build requires completeness.
Export prefixes spreadsheet-formula-leading cells with a reversible tab guard, so opening the CSV does
not execute authored text as a formula. Import removes only that schema-defined guard and enforces a
10 MB / 250,000-row limit; split larger catalogs by stable collection ID.

## Remote catalog overlays

Optional signed overlays deliver post-ship translation changes without modifying shipped StringTables.
They are project/channel scoped, identity and placeholder allowlisted, bounded, cached as reverified
last-known-good snapshots, and activated atomically. Configure, repair, preview, and publish them from
the Localization Hub. See [Remote localization catalogs](LOCALIZATION_REMOTE_CATALOGS.md) for the trust,
publication, runtime, offline, and rollback workflow.

## Production build gate

`LocalizationBuildGate` runs for Molca Build Manager, Unity **Build Player**, and CI because it is an
`IPreprocessBuildWithReport`.

Development builds fail on definite configuration/content errors but allow warning-level missing values
and incomplete coverage. Production builds additionally:

- require complete inline values for every configured locale;
- fail when a declared input cannot be scanned;
- require Addressables **Build Addressables on Player Build** to be
  **Build Addressables With Player**.

Rebuilding Addressables as part of the player build is the freshness guarantee; the gate does not trust
file timestamps or an old catalog merely because it exists.

## See also

- [Audio](AUDIO.md)
- [Remote localization catalogs](LOCALIZATION_REMOTE_CATALOGS.md)
- [Doctor checks](DOCTOR_CHECKS.md)
- [Settings](SETTINGS.md)
- [Subsystems](SUBSYSTEMS.md)
- [UI tokens](UI_TOKENS.md)
