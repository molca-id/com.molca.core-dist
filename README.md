# Molca Core — Distribution Package

This repository holds the **distributable build of Molca Core** (`com.molca.core`),
consumed as a Unity UPM package. It is generated from Molca's private framework repo and is
**read-only for consumers** — do not edit package files here; changes are overwritten on the
next publish.

## Install

Add to your project's `Packages/manifest.json`:

```json
"com.molca.core": "https://github.com/molca-id/com.molca.core-dist.git#<version>"
```

…or in Unity: **Package Manager → + → Add package from git URL…** using the same URL.

Pin to a released **version tag** (e.g. `#1.8.9`) rather than a branch, so your project
gets a stable, reproducible Core. Available versions are the git tags on this repo.

### Unity & dependencies

- Unity version: see `unity` / `unityRelease` in `package.json`.
- Registry dependencies (resolved automatically by Package Manager): Addressables,
  Localization, Input System — see `dependencies` in `package.json`.

## What's here

The package at the repository root: `Runtime/`, `Editor/`, `Samples~/`, `package.json`,
`CHANGELOG.md`. Framework tests, internal dev tooling, and internal documentation are not
distributed.

`PUBLISH_MANIFEST.txt` lists exactly what shipped in the current version (and the source
commit it was built from).

## Extending Core

Core is extended by **subclassing**, never by editing the package. Fork/SDK code lives
outside the package (e.g. `Assets/_MolcaSDK/[Layer]/…` or your project's `Assets/`). Runtime
systems extend `RuntimeSubsystem`; use the Molca registration/bootstrap conventions.

See **[FORK_GUIDE.md](FORK_GUIDE.md)** for the full consumer/fork guide: where settings live,
the supported extension surface, migrating an existing fork onto this package, and how to
request a new Core extension point.

## Support / changelog

- Changelog: `CHANGELOG.md` in this repo.
- Issues and extension requests: contact the Molca framework team (this is a generated
  mirror; do not open PRs against it).
