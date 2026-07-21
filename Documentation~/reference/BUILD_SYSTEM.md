---
title: Build System & Versioning
category: Tooling
order: 910
---

# Build System & Versioning

Molca's build system turns a **build profile** (Development / Staging / Production) into a player
build, drives it identically from the editor and from CI, and manages the project version through
conventional-commit-aware bumping and release creation. Configuration lives on a Build Settings asset
under **Project Settings → Molca Settings**; output lands in `Builds/`.

## Building

From code (or a Doctor/tooling context), one call per profile:

```csharp
BuildManager.Build("development");            // or "staging" / "production"
await BuildManager.BuildAsync(...);           // awaitable variant
BuildManager.ApplyProfile("production");      // apply a profile's settings without building
```

From the editor, use **Molca → Build → [Profile]** or the shortcuts `Ctrl+Alt+D` / `Ctrl+Alt+S` /
`Ctrl+Alt+P`, or the inline **Build** button on the Build Settings asset's *Build Profiles* tab.

Each profile carries its own options (development flags, IL2CPP vs Mono, compression, debugging) and
version-increment policy — Development/Staging auto-increment their channel version; Production
increments the build number.

## CI / command line

CI invokes `CommandLineBuild` entry points via Unity's `-executeMethod`; they build then exit:

```bash
Unity -quit -batchmode -nographics \
  -projectPath "/path/to/project" \
  -buildTarget Win64 \
  -executeMethod Molca.Editor.CommandLineBuild.BuildProduction \
  -logFile build.log
```

| Method | Profile |
|---|---|
| `Molca.Editor.CommandLineBuild.BuildDevelopment` | development |
| `Molca.Editor.CommandLineBuild.BuildStaging` | staging |
| `Molca.Editor.CommandLineBuild.BuildProduction` | production |
| `Molca.Editor.CommandLineBuild.BuildWithProfile` | pass `-profile "name"` |

Ready-made GitHub Actions, GitLab CI, and Jenkins configurations ship under
`Editor/BuildSystem/CI_Examples/`.

## Versioning & releases

`ReleaseTool` drives version bumps and releases; it reads history through `GitLogReader` and
`ConventionalCommits` to suggest a bump from commit messages:

```csharp
BumpSuggestion suggestion = ReleaseTool.SuggestBump();          // from conventional commits
ReleaseTool.ApplyBump(versionSettings, VersionBump.Minor);      // write the new version
ReleaseResult result = ReleaseTool.CreateRelease(versionSettings, createGitTag: true, notes);
```

`BuildVersionProcessor` applies the version to the build automatically, and `ReleaseTool.ReleaseCreated`
fires so integrations (webhooks, changelog writers) can react.

## Validation

The build path is guarded by Doctor checks — `build-scenes-valid`, `build-profile-valid`, and
`version-settings-valid` — plus `SceneReferenceBuildValidator`, which confirms
[scene references](REFERENCE_SYSTEM.md) resolve in the build scene set. Run the Doctor before a release
build (see [Extending Molca Doctor](DOCTOR_CHECKS.md)).

## See also

- [The Molca Hub](HUB.md)
- [Onboarding Wizard](ONBOARDING.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Scene Reference System](REFERENCE_SYSTEM.md)
