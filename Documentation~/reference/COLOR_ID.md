---
title: Colour Themes
category: UI & Presentation
order: 540
---

# Colour themes

Molca Core 2.x uses one canonical token system. A `ColorThemeSet` declares the token vocabulary and
variants, `ColorSchemeManager` publishes an immutable resolved variant through `IColorThemeService`,
and `ColorThemeBinding` applies tokens to supported Unity components.

The 1.x runtime types (`ColorID`, `ColorIDReference`, `ColorModule`, `IColorProvider`,
`IColorSchemeService`, and their static helpers) are not part of the 2.x runtime. Editor-only upgrade
tools remain in the package so a consumer can upgrade directly from 1.x without first installing an
intermediate release.

## Install the theme configuration

Use **Molca ▸ ColorID ▸ Install Color Theme Settings (V1 → V2)**. The installer creates or locates a
`ColorThemeSet`, creates `ColorThemeSettings`, and registers the settings module in `GlobalSettings`.

The runtime reports an error when no theme set is installed. There is no hidden legacy-palette fallback
in 2.x.

## Bind a component to a token

Add `ColorThemeBinding` to a GameObject and add one binding for each target component. A binding stores:

- a canonical `ColorTokenReference`, such as `action/primary/fill`;
- the component to update;
- the target channel or material property;
- the alpha policy.

Bindings subscribe after `RuntimeManager` initialization and refresh whenever the service publishes a
new theme generation. Built-in adapters cover uGUI graphics, TMP text, renderers, lights, cameras, and
other supported targets. Implement `IColorTargetAdapter` beside a custom component to extend the
registry without editing a central switch.

At edit time, `ColorThemeBindingAuthoring.ApplyToken(...)` discovers supported targets and writes the
binding. UI Token Catalog colour entries use this same path and must carry canonical tokens.

## Resolve a token in code

Wait for runtime initialization, resolve `IColorThemeService`, and use `TryResolve` when absence is a
normal possibility:

```csharp
await RuntimeManager.WaitForInitialization(destroyCancellationToken);

var theme = RuntimeManager.GetService<IColorThemeService>();
if (theme != null && theme.TryResolve("text/primary", out Color colour))
    label.color = colour;
```

`Resolve(tokenId)` returns magenta and logs when a token is missing. `TryResolve(tokenId, out colour)`
returns `false` and leaves the reporting decision to the caller.

Use `ColorTokenReference` for serialized fields:

```csharp
[SerializeField] private ColorTokenReference _background =
    new ColorTokenReference("surface/canvas");
```

The field stores identity only. Resolve it through `IColorThemeService`; it does not perform an implicit
global lookup.

## Switch variants

`IColorThemeService` exposes `VariantIds`, `ActiveVariantId`, `ThemeSet`, and `ThemeChanged`.

```csharp
var theme = RuntimeManager.GetService<IColorThemeService>();
theme.SetVariant("light", save: true);
```

`TrySetVariant` also returns a `ColorThemeActivationResult`, which distinguishes activation,
already-active, unknown-variant, validation, and persistence outcomes. Failed activation preserves the
last known-good immutable theme.

`ColorSchemeDropdown` and `ColorSchemeToggle` use this interface directly. Display labels come from each
variant's `DisplayName`; selection and persistence use stable variant IDs.

## Upgrade a 1.x project

Open **Molca ▸ Upgrade ▸ Report 1.x → 2.x Readiness** (or Hub ▸ Remediation). The unified report detects:

- serialized 1.x `ColorID` components by their script GUID;
- legacy `ColorIDReference` fields on Core components;
- legacy UI Token Catalog colour pairs;
- consumer C# references to retired APIs;
- the other Core subsystem migrations required for the 2.x upgrade.

Run the offered repair. The colour migrator reads missing components from serialized YAML, writes
canonical bindings, carries supported prefab-instance overrides, and removes retired component slots.
It does not need the deleted 1.x class to compile. Sites without an authored alias or recoverable target
are refused with a concrete location and reason instead of being guessed.

`LegacyColorAlias` and `LegacyColorKey` intentionally remain. They are 2.x migration data mapping a
durable 1.x key to a canonical token; they are not a legacy runtime lookup surface.

## Audit and generated output

The Color Theme workspace and Doctor audit validate structural theme errors, required-token coverage,
serialized canonical references, contrast requirements, and generated UI Toolkit output. UI Toolkit
themes are generated from the same resolved variants, so uGUI and UI Toolkit share one source of truth.

## See also

- [Colour content migration](COLOR_ID_MIGRATION.md)
- [Runtime Manager](RUNTIME_MANAGER.md)
- [Dependency injection](DEPENDENCY_INJECTION.md)
