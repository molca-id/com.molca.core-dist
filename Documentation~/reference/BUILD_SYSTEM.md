---
title: Build System & Versioning
category: Tooling
order: 910
---

# Build System & Versioning

Molca's build system turns a **build profile** into a player build, drives it identically from the
editor and from CI, and manages the project version through conventional-commit-aware bumping and
release creation. Profiles and versions are authored in **Molca Hub → Settings → Build & Version**;
output lands in `Builds/`.

## Building

The Hub's Build & Version section is the authoring surface: profiles on the left, that profile's
target, options, signing and defines on the right, with **Apply**, **Build This Profile** and
**Duplicate** beneath. **Build All** in the header builds every profile that targets the *active* build
target — the editor builds one target at a time, so profiles for other platforms are named and skipped.
The strip above the footer reports what the last build did.

Selecting a Build Settings or Version Settings asset in the Project window shows a summary and a button
back to the Hub; those inspectors are not a second place to edit.

From code:

```csharp
await BuildManager.BuildAsync("production");  // runs the pre-build gate, then builds
BuildManager.Build("production");             // no gate — for callers that already ran it
BuildManager.ApplyProfile("production");      // apply a profile's settings without building
```

Each profile carries its own options (development flags, IL2CPP vs Mono, compression, debugging,
Android format and architectures, signing) and may opt into building Addressables content first.

## Pre-build steps

A system contributes work to the Molca build path by implementing `IMolcaBuildStep` in any Editor
assembly — no Core edit, the same way `IDoctorCheck` extends Doctor:

```csharp
public sealed class MyContentStep : IMolcaBuildStep
{
    public string Id => "my-content";
    public string DisplayName => "My content";
    public int Order => 200;                       // Core's own steps use multiples of 100
    public bool ShouldRun(MolcaBuildContext c) => c.Profile.developmentBuild == false;

    public MolcaBuildStepResult Run(MolcaBuildContext c)
    {
        // ... do the work ...
        c.SetFact("my-content.built");             // readable by later steps and by build gates
        return MolcaBuildStepResult.Ok("done");
    }
}
```

Steps run in `Order` (ties break on `Id`), before any PlayerSettings mutation, and a failing step aborts
the build. Facts recorded on the context are readable through `MolcaBuildSession.Current` — which is
null outside a Molca build, and must be treated as "nothing is known" rather than as a default.

Steps run for the **Molca** build path only. `File → Build` and a raw `BuildPipeline.BuildPlayer` cannot
be given a profile, so work that must gate *every* build belongs in an `IPreprocessBuildWithReport`
instead (see `ReferenceBuildGate`).

Core ships one step: `addressables-content`, which builds the content bundles a player ships
immediately before the player, when the profile opts in.

## CI / command line

CI invokes `CommandLineBuild` entry points via Unity's `-executeMethod`. They run the pre-build gate,
build, and then exit the editor with the build's exit code:

```bash
Unity -batchmode -nographics \
  -projectPath "/path/to/project" \
  -buildTarget Win64 \
  -executeMethod Molca.Editor.CommandLineBuild.BuildProduction \
  -logFile build.log
```

> **No `-quit`.** The gate is asynchronous, so the editor has to stay alive past `-executeMethod`
> returning — the same contract `MolcaDoctor.RunCI` uses. A `-quit` command line is refused with exit 1
> rather than exiting 0 having built nothing. A runner that bakes in `-quit` (game-ci's `unity-builder`)
> can pass `-molcaSkipBuildGate` to build ungated on purpose; run `MolcaDoctor.RunCI` as a separate step
> when you do.

| Method | Profile |
|---|---|
| `Molca.Editor.CommandLineBuild.BuildDevelopment` | development |
| `Molca.Editor.CommandLineBuild.BuildStaging` | staging |
| `Molca.Editor.CommandLineBuild.BuildProduction` | production |
| `Molca.Editor.CommandLineBuild.BuildWithProfile` | pass `-profile "name"` |

Optional version overrides, so CI can inject the version it owns: `-version 1.4.0` (or `1.4.0.250`) and
`-buildNumber 250`.

Ready-made GitHub Actions, GitLab CI, and Jenkins configurations ship under
`Editor/BuildSystem/CI_Examples/`.

A successful build writes `build-info.json` beside its output — version, build number, git commit and
branch, target, options, scene list, size and timestamp — and the same provenance is embedded in the
player for `Molca.BuildInfo` to read at runtime.

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

### What a build does to the version

Before the player is written, the version is synced to `PlayerSettings.bundleVersion`. That string is
the **full semantic version** — including a pre-release identifier and build metadata when authored —
except on iOS, where `CFBundleShortVersionString` must be one to three integers, so iOS gets
`Major.Minor.Patch` and the pre-release identity travels in the build number. Android's
`bundleVersionCode` and iOS's `buildNumber` are set from the build number.

**After the build succeeds**, and only then, the changelog entry is appended (naming the version that
was just built) and the build number advances. A build that fails, or that a pre-build gate aborts,
leaves both untouched — history records what shipped, not what was attempted. Both halves are opt-in
per project (*Auto Changelog* and *Auto Increment Build* under **Advanced**).

## Validation

The build path is guarded by the pre-build Doctor gate — `MolcaBuildGate.CheckIds`, currently
`build-scenes-valid`, `version-settings-valid`, `build-profile-valid`, `unresolvable-scene-reference`,
`content-package-valid` and `network-catalog` — plus `ReferenceBuildGate`, which confirms
[scene references](REFERENCE_SYSTEM.md) resolve in the build scene set. Run the Doctor before a release
build (see [Extending Molca Doctor](DOCTOR_CHECKS.md)).

`ReferenceBuildGate` is an `IPreprocessBuildWithReport`, so it runs for **every** build entry point —
Molca Build Manager, **File → Build**, and batch-mode CI — not only Molca's own build command. It fails
**closed**: a coverage gap or a scan failure aborts a production build rather than passing green, and a
build that processes a scene the gate never validated fails rather than letting an explicit
`BuildPlayerOptions.scenes` list bypass it. A development build may lower the coverage and scan-failure
findings; duplicate providers, ambiguous fallbacks and wrong target types stay errors either way.

### Build callback ordering

Unity discovers `IPreprocessBuildWithReport` by type, so the only thing coordinating a dozen
independent callbacks is the number each picks for itself. `MolcaBuildCallbackOrder` names the bands;
use a constant from it rather than a literal:

| Band | For |
|---|---|
| `VersionSync` (`int.MinValue`) | Idempotent state later callbacks read. Runs before the gates, so nothing here may outlive an aborted build. |
| `LicenseGate` … `LastGate` | Anything that can throw `BuildFailedException`. Always before player settings are mutated, so an abort needs no restore. |
| `Observer` (`0`) | Read-and-report only — notifications, activity routing. Never aborts. |
| `GeneratedArtifacts` | Creates files the player carries, removed by a postprocessor. Must be above **every** gate: a throwing preprocessor skips all postprocessors, so a generator below a gate leaves its output in the project. |

A test asserts no Core preprocessor sits between the bands. That is the shape of the mistake worth
catching — the network-catalog and colour-theme validators once aborted at `+100`, above the callback
that writes the runtime build-info asset, so those two failures leaked a generated file into `Assets/`.

An add-on or project callback is free to pick its own number, but the same reasoning applies: if it can
abort, order it below `LastGate`.

## See also

- [The Molca Hub](HUB.md)
- [Onboarding](ONBOARDING.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Scene Reference System](REFERENCE_SYSTEM.md)
