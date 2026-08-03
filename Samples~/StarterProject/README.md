# Starter Project Content

Everything a Molca project needs that **cannot be generated from code**: fonts, UI prefabs, a wired
`RuntimeManager`, brand colour palettes, URP profiles, localization tables, input actions and lighting.

Import it once from **Package Manager ▸ Molca ▸ Samples**, and it is yours. The files land under
`Assets/Samples/…`, where your project owns them outright — edit them, delete them, restyle them. A Core
upgrade cannot touch them, because nothing here is read from the package at runtime.

## Why this is a sample and not part of the package

An asset shipped *inside* a package is un-ownable. You cannot write to a file in an immutable UPM
install, and the next upgrade replaces it, so an edit disappears with no error anywhere. Core used to
ship seventeen such files and the SDK a hundred and forty-five; all of them are gone.

A sample is the one mechanism without that problem. The `Samples~` folder ends in `~`, so Unity never
imports it — the files only enter your project when you ask, and then they belong to you.

## What it does *not* contain

Configuration that can be generated. **Molca ▸ Hub ▸ Onboarding ▸ Project Starter** creates the
`GlobalSettings` graph and one of every setting module from code, so those are never shipped as files.

The one deliberate overlap is `Settings/Global/`. It carries a **complete, already-configured**
settings graph — including the two brand palettes, which no generator can invent — so that importing
this sample gives you a working project rather than a pile of parts.

## Import order matters

**Import this sample first, then run the Project Starter.**

The starter skips any setting module that is already registered, so importing first gives you the
branded, pre-configured modules and the starter fills in only what is genuinely missing. Run the
starter first and you get blank generated modules instead, and then two of everything.

After importing, point **Project Settings ▸ Molca** at this sample's `Global Settings.asset` — the
project keeps whatever it had before, so the switch is yours to make deliberately.

## The colour theme is load-bearing

`Settings/Global/Global Settings.asset` registers `Color Theme Settings.asset`, which points at
`Settings/Global/Themes/Molca Color Theme Set.asset`. Those GUID-backed links travel with the sample's
`.meta` files, so the Runtime Manager publishes the configured variants as soon as the sample boots.

Keep that settings graph together. Removing the theme set leaves the 2.x colour service explicitly
unconfigured, and the upgrade report identifies the missing link.

## Layout

| Folder | Contents |
|---|---|
| `Art/` | 3D models, materials, shared textures, sky variants |
| `Prefabs/` | `Runtime Manager`, UI controls, modals, media handlers |
| `ScriptableObjects/` | Scene-name references and the base HTTP request |
| `Settings/Global/` | The configured settings graph and canonical colour theme |
| `Settings/Fonts/` | Molca and Poppins faces, plus the localized text styles |
| `Settings/Rendering/` | URP profiles, quality tiers, volume profile |
| `Settings/Localization/` | Locales and string tables |
| `Settings/Audio/`, `Build/`, `Notification/`, `ContentPackage/`, `UI Tokens/` | Per-system content |
| `Shaders/`, `Shared/`, `Http Requests/` | Shaders, shared media, request definitions |
