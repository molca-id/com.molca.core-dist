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
target, scenes, options, signing and defines on the right, with **Apply**, **Build This Profile**,
**Duplicate** and **Run Preflight Checks** beneath. Only the platform fields that apply to the selected
profile's target are shown — an iOS profile has no keystore path. **Build All** in the header runs the
gate once and then builds every profile that targets the *active* build target; the editor builds one
target at a time, so profiles for other platforms are named and skipped. The strip above the footer
reports what the last build did, and **Recent Builds** lists the last ten attempts.

**Preflight** runs the same checks a build runs (`MolcaBuildGate.CheckIds`) and reports them in the Hub.
Use it before a long build: a refused build otherwise says only that it did not run, and the reason is in
the Console.

Selecting a Build Settings or Version Settings asset in the Project window shows a summary and a button
back to the Hub; those inspectors are not a second place to edit.

### Scenes

A profile with an empty **Scenes** list builds the enabled Editor Build Settings scenes — what every
profile did before the list existed, and still the default. Listing scenes on a profile makes that
profile build exactly those, in order, and the Build Settings list is ignored for it. The reference audit
and the Doctor checks follow the profile's set, so a per-profile scene list does not trip the
[reference gate](REFERENCE_SYSTEM.md)'s processed-scene check.

A profile whose list contains a deleted scene is refused rather than built without it.

### Profile identity

Each profile carries a stable `id`, assigned once and never rewritten, alongside its editable `name`.
`BuildSettings.GetProfile` and `TryGetProfile` accept either. Systems that bind configuration to a
profile — a network environment's *Enabled Build Profiles*, for instance — should store the id: a name is
a label somebody edits, and renaming a profile silently unbinds anything that stored the old one. The
Doctor reports a network environment bound to a profile that no longer exists.

From code:

```csharp
await BuildManager.BuildAsync("production");  // runs the pre-build gate, then builds
BuildManager.Build("production");             // no gate — for callers that already ran it
BuildManager.ApplyProfile("production");      // apply a profile's settings without building
```

Each profile carries its own options (development flags, IL2CPP vs Mono, compression, debugging,
Android format and architectures, signing) and may opt into building Addressables content first.

Output lands in one folder per platform, profile and version. Every supported target has an explicit rule
— `.app` for macOS, an extensionless binary for Linux, a site folder for WebGL, an Xcode project for iOS
— and a target with no rule is refused before anything is mutated rather than built to a guessed path.

**Signing fails closed.** A profile that enables custom Android signing but whose keystore, alias, or
either password environment variable is missing refuses to build. It used to warn and continue, producing
an artifact signed with Unity's debug keystore that looked exactly like a real one.

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

## Post-build steps

`IMolcaPostBuildStep` is the same seam for work that happens once a player exists — uploading debug
symbols, publishing an artifact, recording a release row, notifying a channel. It is handed a
`MolcaPostBuildContext`: the profile, the resolved output path, the build record, and the pre-build
context, so it can read the facts the pre-build steps recorded.

Two contracts differ from the pre-build steps, deliberately:

- **Post steps run only for a build that produced an artifact.** Work that must happen however a build
  ends belongs in a Unity postprocessor in the `PostGeneratedCleanup` band.
- **Every post step runs, even after one fails, and a failure does not fail the build.** The player
  already exists; skipping a symbol upload because an unrelated webhook was down would lose data nothing
  can recover later. Failures are logged at error severity and recorded on the build record.

## Build records

Every attempt — including the ones that never run — is appended to `Library/Molca/build-history.json`
through `MolcaBuildRecordStore`: profile, target, outcome (`Succeeded`, `Failed`, `Refused`), version,
build number, git commit and branch, output path, size, duration and one line of detail. The Hub reads it,
so an outcome survives the domain reload a build-target switch causes, and a build started by CI or by the
automation workflow is reported there too.

It is `Library/`, not `ProjectSettings/`: this is one machine's account of what it tried, and committing it
would mean a merge conflict per build. What belongs to the project is recorded in the changelog, in the
release tags, and — for builds that shipped — in the control plane.

## The control-plane build ledger

A build attempt is reported to the Molca control plane against the build token minted for it, giving the
project a server-side answer to "what did we ship, and what is failing". The build id is the one already
baked into the player, so a shipped player's usage reports and the ledger row describe the same build.

Two paths report, because they need different things to be true:

| Path | Reports | Why not the other one |
|---|---|---|
| `control-plane-build-record`, a post-build step | a build that produced a player | Post steps run only when an artifact exists — that is their contract |
| `BuildManager`, directly | an attempt that ended in `Failed` or `Refused` | There is no artifact, so no post step runs |

| | |
|---|---|
| **Sent** | profile, target, outcome, reason code, semantic version (pre-release included), build number, commit, branch, Unity version, size, duration, scene count |
| **Not sent** | the output path; the record's `detail` line; any identity — the server reads that from the entitlement and project binding on the request |
| **Durability** | reports spool to `Library/Molca/BuildRecords` first, so an offline machine or an editor closed right after a build delays the record rather than losing it |
| **Immutability** | one row per build id, write-once; a repeat report is accepted and ignored |

### A failure is a code, never a message

`appBuildRecord` 2 added the outcome and, for anything that did not ship, a **reason code** — a
`MolcaBuildReasonCode` constant naming the gate or step that refused, or `build-failed`. The `detail` line
never leaves the machine: it is written for the person sitting at it and may name a scene, a path, or a
count. The server accepts a reason as a lowercase kebab **pattern** rather than a bounded string, so console
output, a Windows path, and a stack trace are all rejected rather than truncated — a length limit would have
admitted all three. A non-conforming value becomes `unspecified` rather than being cleaned up into a code,
because slugifying `Assets/Game/Boss.cs(42): error CS1002` produces a valid-looking code that has leaked the
path and the line.

Reporting *that* a build failed necessarily widens what the control plane knows about a project's private
working state. The code-not-message split is the whole of the mitigation, and it is recorded in
`docs/internal/LICENSING.md` beside the reasoning for why build authorization has no opt-out.

### Which gate refused

A gate refuses by throwing `BuildFailedException`. Unity catches it and hands back a report whose result is
`Failed` — indistinguishable from a compile error. So a gate calls `MolcaBuildRefusal.Record(...)`
immediately before throwing, and `BuildManager` reads what it recorded. One line at each throw site, and no
parsing; the alternative was classifying the report's messages, which means reading the text this design
keeps local. The reason is stored on the build session, so it expires with the build rather than latching
into the next one.

### The gap that remains

An attempt that ends **before the license gate mints a token** cannot be reported at all: an invalid
profile, an unresolvable scene set, a scene-reference problem caught pre-pipeline, a pre-build step
refusing, the pre-build Doctor gate, and the license gate itself. Those are recorded in
`build-history.json` and nowhere else. The customer health view says exactly that — "builds were authorized
but none reported an outcome" — rather than reporting them as builds that never happened.

Reporting **skips silently** for any build that minted no token — `File → Build`, a project that is not
connected, or a distribution where licensing is unconfigured — because there is nothing on the control plane
for such a build to be a record of. It cannot fail a build: a failing post step is reported and recorded on
the build row, never turned into a build failure, and the direct report from `BuildManager` swallows its own
errors because a build that already failed must not also throw from the code reporting that it failed.

There is no per-machine switch to turn it off. A ledger with silent gaps is worse than no ledger, since a gap
reads identically to a build that never happened; a project that does not want one does not connect a project,
and then no token is minted and nothing is reported. See `docs/internal/LICENSING.md` for the server contract.

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
player for `Molca.BuildInfo` to read at runtime, including the profile it was built from. Every attempt,
successful or not, is also appended to the build record (below).

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

A release's identity is `GetReleaseVersionString()` — the numeric version plus any pre-release identifier,
without build metadata (SemVer §10: metadata is not part of version precedence). So `1.4.0-rc.1` tags
`v1.4.0-rc.1` and leaves `v1.4.0` for the release itself. `CreateRelease` verifies the tag does not already
exist *before* it syncs anything or writes a changelog entry, so a refused release leaves the project
untouched; a bump clears the pre-release identifier but never rewinds the build number, which app stores
require to increase across every upload.

`SuggestBump` measures from the most recent `v*` tag. With no such tag there is no baseline, so it returns
`None` with `HasBaseline == false` — the first release is one a person picks, not one derived from an
arbitrary window of recent commits.

Each changelog entry records the commit it was written at, and that is the lower bound of the next entry's
commit range. The anchor used to live in `EditorPrefs`, which is per-machine: the second developer to build,
and every fresh CI container, silently reported "the last ten commits" instead.

### What a build does to the version

Before the player is written, the version is synced to `PlayerSettings.bundleVersion`. That string is
the **full semantic version** — including a pre-release identifier and build metadata when authored —
except on iOS, where `CFBundleShortVersionString` must be one to three integers, so iOS gets
`Major.Minor.Patch`. Android's `bundleVersionCode` and iOS's `buildNumber` are set from the build number.

**An iOS player cannot carry a pre-release identifier anywhere.** Both Apple version fields are numeric,
so `rc.1` has nowhere to live and building iOS with one set logs a warning saying so. (This document
previously claimed the identity "travels in the build number". It does not, and never did.) It survives in
`build-info.json` and in the embedded build-info asset, which both record the full semantic version —
`Molca.BuildInfo.SemanticVersion` at runtime, beside `Version` for the numeric form.

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

Postprocessors have their own bands, because they had none and two Core callbacks both took `int.MaxValue`
while each documented itself as running last:

| Band | For |
|---|---|
| `PostObserver` (`0`) | Read-and-report only. |
| `PostGeneratedCleanup` | Removing files a `GeneratedArtifacts` preprocessor wrote. After the observers, so a reporter can still read a stamp. |
| `PostVersionAdvance` (`int.MaxValue`) | Advancing recorded build state — the build number and changelog. Core's alone: the "readers see the built version" guarantee only holds if exactly one callback is last. A test enforces that. |

A callback implementing **both** halves is ordered by its gate band — Unity reads one `callbackOrder` for
both interfaces — so the post bands apply to postprocess-only callbacks.

An add-on or project callback is free to pick its own number, but the same reasoning applies: if it can
abort, order it below `LastGate`; if it must run late after a build, order it just below
`PostVersionAdvance`, not alongside it.

## See also

- [The Molca Hub](HUB.md)
- [Onboarding](ONBOARDING.md)
- [Extending Molca Doctor with Custom Checks](DOCTOR_CHECKS.md)
- [Scene Reference System](REFERENCE_SYSTEM.md)
