# SDK Packaging — `com.molca.sdk`

The shared SDK layer is a UPM package, `com.molca.sdk`, sitting between Core and the fork/project layers.

## Layer model

```
com.molca.core   (UPM package)   — RuntimeManager, DI, Events, Sequence, Settings, Networking, …
      ▲ depends on
com.molca.sdk    (UPM package)   — shared app scaffolding: Auth, Media, Modals, Home, Preload,
      ▲ depends on                 UI building blocks (prefabs), shared Art/Audio/Settings config
fork / project   (Assets/)       — _VR (XR layer), project content, and project-level bootstrap config
```

`com.molca.sdk` depends on `com.molca.core` (one-way). The fork's own layer (`_VR`, project scenes,
project bootstrap) stays in `Assets/`.

## Standalone closure

`com.molca.sdk` is standalone as an SDK package only when `com.molca.core` resolves through its package
dependency. The closure gate is a clean consumer project that installs `com.molca.sdk` and lets UPM resolve
Core from the current Core dist package. SDK should only declare extra dependencies for packages that are
not guaranteed by Core and are used directly by SDK code/assets.

## Package layout

| Path | Contents | Assembly |
|---|---|---|
| `Runtime/Scripts`, `Runtime/Shaders` | shared runtime code + shaders | `MolcaSDK` |
| `Editor/Scripts` | shared editor tooling | `MolcaSDK.Editor` |
| `Runtime/Art`, `Runtime/Shared` | shared sprites/models/materials/audio | (assets) |
| `Runtime/Prefabs`, `Runtime/ScriptableObjects` | shared UI prefabs + config SOs | (assets) |
| `Runtime/Settings` | read-only config: Audio, Build, ContentPackage, Fonts, Localization, Notification, Rendering, Sentry | (assets) |
| `Tests/ShaderWarmup` | edit-mode tests | `MolcaSDK.ShaderWarmup.Tests` |
| `Samples~/Scenes`, `Samples~/Tests` | sample scenes + test fixtures (opt-in import) | `MolcaSDK.SceneTests` |
| `Samples~/QuickSetup/Settings` | re-GUID'd starter settings (copied into consumer space by `QuickSetupInstaller`) | (assets) |

Assembly **names are unchanged** from the embedded layer (`MolcaSDK`, `MolcaSDK.Editor`, …), so fork
asmdefs that reference them by name keep resolving; Core is referenced by GUID. Dependency is one-way
(SDK → Core); no Core assembly references the SDK.

## Held back in `Assets/` (not packaged)

Project-level / bootstrap config that a UPM package (immutable at import) must not own — per the
Sprint-35.5 relocation decision:

- `Assets/_MolcaSDK/Settings/Global/` — `GlobalSettings` + its module assets, wired via
  `MolcaProjectSettings` (which lives in `Assets/_Molca/`, outside the SDK). Moving these into an
  immutable package would break the bootstrap wiring / reintroduce the split-brain.
- `Assets/_MolcaSDK/Settings/Molca InputSystem_Actions.inputactions` and `Standard Lighting Settings.lighting`
  — project-level config.

These reference packaged assets by GUID, which resolves fine across the package boundary.

## Quick setup templates

The package ships starter copies of the held-back bootstrap settings as a quick setup option, but those
copies are **templates only**. The active settings must be copied into consumer space before use, never
edited in-place under `Packages/com.molca.sdk`.

As shipped:

- Canonical starter settings live under `Samples~/QuickSetup/Settings/` (mirroring the held-back
  `Assets/_MolcaSDK/Settings/` layout: `Global/` + input actions + lighting). They sit in a `Samples~`
  folder, so Unity never imports them into the dev repo's asset database.
- **GUIDs are deliberately re-generated** for every template asset (and their internal cross-references
  rewired to match), so the templates are GUID-**disjoint** from the held-back active settings. Copying a
  template into a project can therefore never collide with an existing active asset or a separately
  imported sample — this is why the templates are *not* a straight `.meta` duplicate of the active set.
- `QuickSetupInstaller` (`Editor/Scripts/Setup/`, menu **Molca ▸ SDK ▸ Quick Setup**) copies the templates
  into `Assets/_MolcaSDK/Settings/`. The copy is **idempotent**: existing consumer files are kept by
  default ("Install Starter Settings"); an explicit, confirmed "Repair (Overwrite)" replaces them. The
  package-owned templates are never written back to.
- The installer reads the templates via `System.IO` from the resolved package path (not `AssetDatabase`,
  since `Samples~` is hidden), then refreshes. Full first-run wizard polish continues in the onboarding
  sprint; this is the supported copy path until then.

> The held-back active settings still live in consumer/project space (`Assets/_MolcaSDK/Settings/`, or the
> project-standard `_Molca` settings path if the bootstrap wiring moves there) — quick setup seeds them, it
> does not make the package own them.

## GUID preservation (the prime directive)

Every asset moved with its `.meta` (`git mv`), so GUIDs are unchanged and all cross-asset references
(prefab→script, scene→prefab, settings→module, fork→shared) survive. Verification gate: the `.meta`
GUID multiset before vs. after the move is identical except for dissolved container-folder metas — **zero
GUIDs added, no re-GUIDing**.

## Publishing the dist package

The SDK is developed as an embedded package here but distributed from a separate **private** repo so
consumers never see the dev repo — the same model as Core. `tools/publish-sdk.ps1` mirrors the package to
the dist repo root, writes a `PUBLISH_MANIFEST.txt`, and makes one release commit + version tag.

```powershell
# Dry stage (no push): clone, mirror, commit + tag locally, print the push commands to review.
pwsh tools/publish-sdk.ps1 -DistRepoUrl https://github.com/molca-id/com.molca.sdk-dist.git

# Publish: commit, tag <version>, and push both to the dist repo.
pwsh tools/publish-sdk.ps1 -DistRepoUrl https://github.com/molca-id/com.molca.sdk-dist.git -Push
```

- **Excluded:** `Tests/` (the framework's own tests, including the package-boundary guard — a dev gate) and
  `Documentation~/sprints/`. **Kept:** `Samples~/` (opt-in scenes/fixtures **and** `Samples~/QuickSetup`,
  the source `QuickSetupInstaller` copies starter settings from).
- **Core is pinned by version, not Git URL.** Unity forbids Git URLs in a package's `dependencies`, so
  `com.molca.sdk`'s `package.json` keeps `"com.molca.core": "<x.y.z>"`. The script fails fast if that
  becomes a Git URL.
- **Consumers therefore list both dist Git-URLs** in their project `Packages/manifest.json` (this is the
  resolution Unity cannot do from the package dependency alone):

  ```json
  "com.molca.core": "https://github.com/molca-id/com.molca.core-dist.git#1.9.7",
  "com.molca.sdk":  "https://github.com/molca-id/com.molca.sdk-dist.git#0.1.0"
  ```

- **Commit the dev-repo SDK changes before publishing** so the release tag maps to a real source commit
  (the script mirrors the working tree, and `PUBLISH_MANIFEST.txt` records the source commit hash).
- Re-running with a tag that already exists in the dist repo is refused — bump `version` first.

## Fork cutover (Sprint 61) — runs in the fork repos

Each fork (`molca-sdk-vr`, `molca-sdk-dt`) currently carries its own embedded copy of the shared layer.
Cutover, **per fork, in one atomic commit** (the embedded copy and the package share identical GUIDs, so
the two cannot safely coexist — a duplicate-GUID window must never open):

1. **Pre-cut reconciliation** — diff the fork's embedded shared files against this package; any fork-local
   edits to shared files are either upstreamed into `com.molca.sdk` (bump + re-pin) or reclassified as
   fork-local and kept.
2. **Atomic swap** — in one commit: add `com.molca.sdk` to the fork's `Packages/manifest.json` (pinned,
   referenced the same way `com.molca.core` already is) **and** delete the embedded shared files (the set
   this package now owns), leaving only `_VR`/fork-only content + the held bootstrap config. Identical
   GUIDs mean all `_VR` and asset references re-resolve to the package automatically.
3. **Verify before the delete is final** — package resolves; `MolcaSDK`/`MolcaSDK.Editor` + fork
   assemblies compile; the fork's test suite passes; a representative scene smokes (world-space UI, a
   sequence, a networked call). Only then is the commit finalized. Rollback is a single `git revert`
   (nothing was re-GUIDed).
4. **DT mirrors VR.**

After cutover the shared layer has exactly one home: this package. `framework-unity` already consumes it
as an embedded package under `Packages/`.
