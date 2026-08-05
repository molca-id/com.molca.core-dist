# Content Package System

The Content Package system manages downloadable content (DLC) using Unity Addressables. Packages are defined in a `ContentPackageSettings` ScriptableObject, downloaded on demand, and their states are persisted across sessions. A remote `packages.json` manifest deployed alongside the Addressables catalog provides authoritative version strings, bundle sizes, and changelog data without requiring a new app binary.

---

## Table of Contents

1. [Concepts](#concepts)
2. [Setup](#setup)
3. [Defining Packages](#defining-packages)
4. [Addressables Setup](#addressables-setup)
5. [Remote Package Manifest](#remote-package-manifest)
6. [Runtime API](#runtime-api)
7. [Package Lifecycle](#package-lifecycle)
8. [Dependencies](#dependencies)
9. [Events](#events)
10. [Runtime UI](#runtime-ui)
11. [Editor — Content Package Manager](#editor--content-package-manager)
12. [Build, Verify & Deploy](#build-verify--deploy)
13. [System Settings Reference](#system-settings-reference)

---

## Concepts

| Term | Description |
|---|---|
| **Package** | A named bundle of Addressables content identified by one or more labels. |
| **PackageConfig** | Authored definition of a package stored in `ContentPackageSettings`. Read-only at runtime. |
| **PackageState** | Mutable runtime state (status, download progress, installed version). Persisted to a JSON file in `persistentDataPath`. |
| **PackageStatus** | Enum: `Available`, `Downloading`, `Installed`, `Failed`, `UpdateAvailable`. |
| **PackageService** | Core service. Handles install, uninstall, update, catalog refresh, remote manifest fetch, and state queries. |
| **PackageSubsystem** | `RuntimeSubsystem` wrapper. Boots `PackageService` during framework initialization. |
| **RemotePackageManifest** | Platform-specific `packages.json` deployed alongside the Addressables catalog. Contains authoritative version, bundle size, description, tags, and changelog per package. |

---

## Setup

### 1. Create the Settings Asset

**Assets > Create > Molca > Settings > Content Package Settings**

Place it anywhere under `Assets/`. One asset per project.

### 2. Register with GlobalSettings

Open the `GlobalSettings` asset and add your `ContentPackageSettings` asset to the **Modules** list. The framework loads it automatically on startup.

### 3. Add PackageSubsystem to RuntimeManager

On the **RuntimeManager prefab**, add `PackageSubsystem` as a child component. Recommended initialization priority: **150**.

No code registration is needed.

---

## Defining Packages

Open the `ContentPackageSettings` asset. The **Content Package Manager** inspector shows a two-column editor.

Click **+ New Package** to create an entry, then fill in the right panel:

| Field | Description |
|---|---|
| **Package ID** | Unique machine-readable identifier. Use `kebab-case` (e.g. `fire-training-env`). |
| **Display Name** | Human-readable name shown in UI. |
| **Description** | Authoring default shown before remote manifest is fetched. Superseded by remote manifest at runtime. |
| **Version** | Authoring seed. Superseded by the release manifest at runtime — bump it before publishing a new `contentVersion`. |
| **Author** | Optional author name. |
| **Tags** | Array of string tags. Exposed in the remote manifest for UI filtering. |
| **Addressables Labels** | One or more Addressables labels whose content belongs to this package. |
| **Dependencies** | Other packages that must be installed first. |
| **Visible** | Hidden packages are excluded from the runtime UI list but still resolved as dependencies. |
| **Required** | Required packages are auto-installed on startup and cannot be uninstalled. |

### Health Indicators

Each package card shows a colored dot:

- **Green** — ID set, display name set, at least one label assigned.
- **Yellow** — Missing display name or no labels assigned.
- **Red** — Missing package ID.

---

## Addressables Setup

The system maps **one package → one or more Addressables labels**. The recommended layout is one Addressables Group per package:

1. Open **Window > Asset Management > Addressables > Groups**.
2. Create a group named after your package (e.g. `FireTrainingEnv`).
3. Set the group's **Build & Load Paths** to your remote profile variables. Under the release protocol the load path is resolved from the signed release at runtime, not baked to a CDN URL you host.
4. Assign a label matching your package (e.g. `fire-training-env`) to all entries in the group.
5. Back in the Content Package Manager, use **Pick Labels…** to select that label.
6. Click **Scan Assets** to preview asset count and approximate source file size (accurate bundle size is written to `packages.json` at build time).

> **Tip:** Enable **Can Change Post Release** on the group schema. This enables Unity's catalog update workflow and is required for `BuildContentUpdate` to detect changed groups.

---

## Remote Package Manifest

> [!NOTE]
> **Legacy path.** `packages.json` belongs to the pre-release-protocol scheme. It is read only during the
> migration window named in `docs/internal/CONTENT_PACKAGE_RAILWAY_STORAGE_REVAMP_IMPLEMENTATION_PLAN.md`;
> new content is published as an immutable, signed release (see **Publishing** below and
> `contracts/content-release-v1.md`). There is no CDN endpoint for you to host or configure.

`packages.json` is a platform-specific JSON file written to the build output folder by the build pipeline. At runtime the legacy reader fetches it during `RefreshCatalogAsync` and uses it as the authoritative source for:

| Data | Source after fetch |
|---|---|
| Version string | `RemotePackageEntry.version` |
| Bundle size (bytes) | `RemotePackageEntry.bundleSizeBytes` — measured from actual `.bundle` files at build time |
| Description | `RemotePackageEntry.description` |
| Tags | `RemotePackageEntry.tags` |
| Changelog | `RemotePackageEntry.changelog` |

If the remote manifest has not been fetched (offline or URL not set), `PackageService` falls back to the Addressables download-size check for update detection and shows no size in the UI.

### Schema

```json
{
  "schemaVersion": "1",
  "generatedAt": "2026-05-09T10:00:00.000Z",
  "packages": [
    {
      "packageId": "fire-training-env",
      "version": "1.2.0",
      "description": "Fire suppression training environment.",
      "author": "Molca Studio",
      "tags": ["training", "fire"],
      "bundleSizeBytes": 52428800,
      "changelog": "Added extinguisher interaction, improved particle effects."
    }
  ]
}
```

### URL Configuration

These apply to the **legacy schema-v1 path only**. On the release protocol every URL is derived from the signed
release manifest and none of this is configured by hand.

Set both in **System Settings** on the `ContentPackageSettings` asset:

- **Packages Manifest URL** — `{remoteLoadURL}/{platform}/packages.json`
- **Catalog URL** — `{remoteLoadURL}/{platform}/catalog_{hash}.json`

These were populated automatically by the build until that write-back was removed. A build wrote into a shared,
version-controlled asset, so whoever built last silently decided the URLs for everyone, and the change surfaced in
an unrelated commit. The catalog hash changes on every build, so re-set the Catalog URL when you rebuild — or move
to the release protocol, where this problem does not exist.

---

## Runtime API

Access the service after `WaitForInitialization()`:

```csharp
await RuntimeManager.WaitForInitialization();
var pkg = RuntimeManager.GetSubsystem<PackageSubsystem>().PackageService;
```

Or inject:

```csharp
[Inject] private PackageSubsystem _packageSubsystem;
// then: _packageSubsystem.PackageService
```

### Install a Package

```csharp
var cts      = new CancellationTokenSource();
var progress = new Progress<float>(p => Debug.Log($"Download: {p:P0}"));

var result = await pkg.InstallPackageAsync("fire-training-env", progress, cts.Token);

if (result.Success)        Debug.Log("Installed!");
else if (result.WasCancelled) Debug.Log("Cancelled.");
else                       Debug.LogError(result.ErrorMessage);
```

`InstallPackageAsync` resolves and installs dependencies first, in topological order. It is also used to apply an update — call it again on a package whose status is `UpdateAvailable`.

### Uninstall a Package

```csharp
var result = await pkg.UninstallPackageAsync("fire-training-env", cts.Token);
```

Blocked if other installed packages depend on this one, or if the package is marked `isRequired`.

### Update a Package

```csharp
// Identical to install — re-downloads the latest bundle and updates the state.
var result = await pkg.InstallPackageAsync("fire-training-env", progress, cts.Token);
```

Or use `UpdatePackageAsync` which validates the package is actually in `UpdateAvailable` state first:

```csharp
var result = await pkg.UpdatePackageAsync("fire-training-env", progress, cts.Token);
```

### Clear Cache

```csharp
// Removes local Addressables cache for this package without touching state.
var result = await pkg.ClearPackageCacheAsync("fire-training-env", cts.Token);
```

### Query State

```csharp
// Synchronous — no await needed.
bool installed     = pkg.IsPackageInstalled("fire-training-env");
PackageState state = pkg.GetPackageState("fire-training-env");

// state.status           — PackageStatus enum
// state.downloadProgress — 0.0–1.0 while Downloading
// state.installedVersion — version string when Installed
// state.errorMessage     — set when Failed

List<PackageState> installed = pkg.GetInstalledPackages();
List<PackageState> available = pkg.GetAvailablePackages();
```

### Get Download Size

```csharp
// Queries real Addressables size including uninstalled dependencies.
long bytes = await pkg.GetDownloadSizeAsync("fire-training-env");
```

### Get Remote Metadata

```csharp
// Returns null until RefreshCatalogAsync has completed at least once.
RemotePackageEntry entry = pkg.GetRemoteMetadata("fire-training-env");
if (entry != null)
{
    Debug.Log($"v{entry.version} — {SizeFormatter.Format(entry.bundleSizeBytes)}");
    Debug.Log(entry.changelog);
}
```

### Cloud Status

```csharp
// Synchronous — no await needed. Always reflects the last refresh attempt.
PackageCloudStatus status = pkg.CloudStatus;

// status.State           — CloudConnectionState enum
// status.LastSyncTime    — UTC DateTime? of last successful fetch (null if never)
// status.ManifestGeneratedAt — generatedAt string from packages.json
// status.RemotePackageCount  — package count from remote manifest
// status.ErrorMessage    — set when State is Unreachable

// React to transitions:
pkg.OnCloudStatusChanged += s => Debug.Log($"CDN: {s.State}, last sync: {s.LastSyncTime}");
```

`CloudStatus.State` values:

| Value | Meaning |
|---|---|
| `Unknown` | No refresh has been attempted this session. |
| `Connected` | Last `packages.json` fetch succeeded. |
| `Unreachable` | Last fetch failed (network error, HTTP error, or parse error). `ErrorMessage` contains the reason. |
| `NotConfigured` | `RemotePackagesManifestUrl` is not set; no fetch is attempted. |

`CloudStatus` is updated on every `RefreshCatalogAsync` call. Cancelled fetches do not change the state — the last known value is preserved.

### Refresh Catalog

```csharp
var result = await pkg.RefreshCatalogAsync(cts.Token);
```

Performs in order:
1. `Addressables.CheckForCatalogUpdatesAsync` (if enabled in settings)
2. `Addressables.UpdateCatalogsAsync`
3. Fetches `packages.json` from `RemotePackagesManifestUrl`
4. Runs update detection on all installed packages
5. Auto-installs any `isRequired` packages that are missing

Called automatically on startup when **Check for Updates** is enabled.

### Resolve Dependencies (sync)

```csharp
var result = pkg.ResolveDependencies("fire-training-env");
if (result.Success)
    foreach (var id in result.Data) // List<string> in topological order
        Debug.Log(id);
```

---

## Package Lifecycle

```
Available
    │
    │  InstallPackageAsync()
    ▼
Downloading ──── cancel ────► Available
    │
    │  download complete
    ▼
Installed ◄───────────────── InstallPackageAsync() (re-install / update)
    │              │
    │  catalog     │  UninstallPackageAsync()
    │  + manifest  ▼
    │  update   Available
    ▼
UpdateAvailable
```

`Failed` is reached from `Downloading` on error. Calling `InstallPackageAsync` again retries.

State is persisted to `{persistentDataPath}/Molca/packages_manifest.json` on every transition. On first run after upgrading from an older build, PlayerPrefs data is migrated automatically and the key is deleted.

---

## Dependencies

Add dependency entries in the **Dependencies** section of the package detail form.

Each dependency is a `packageId` reference. `InstallPackageAsync` performs topological sorting and installs all non-installed dependencies in order before the requested package. Cycles are detected and reported as an error.

`UninstallPackageAsync` checks reverse dependencies — if package A requires B, you cannot uninstall B while A is installed.

Hidden packages (`isVisible = false`) are fully resolved as dependencies even though they do not appear in the UI list. This is the correct pattern for shared base packages.

---

## Events

Subscribe on the `PackageService` instance:

```csharp
pkg.OnPackageStateChanged += (packageId, newStatus) => { };
pkg.OnDownloadProgress    += (packageId, progress)  => { }; // 0.0–1.0
pkg.OnPackageError        += (packageId, error)      => { };
pkg.OnCatalogRefreshed    += ()                      => { };
pkg.OnCloudStatusChanged  += (status)                => { }; // CloudConnectionState transition
```

Subscribe in `Activate()` / unsubscribe in `Deactivate()` for subsystems, or after `WaitForInitialization()` / in `OnDestroy()` for MonoBehaviours.

---

## Runtime UI

`ContentPackageManagerUI` (in `MolcaSDK`) provides a ready-made two-panel DLC browser.

- **Left panel** — scrollable package list with status dot, name, ID, status label, bundle size, and inline download progress bar per row.
- **Right panel** — package detail showing name, ID, version (from remote manifest), description (from remote manifest), bundle size, pending download size (fetched async on selection), tags, changelog, dependencies, used-by packages, status, error row, and download progress.
- **Action buttons** — Install / Update / Uninstall / Cancel, plus **Update All** when one or more packages have updates available.
- **Footer** — installed package count and total installed size (summed from remote manifest bundle sizes).
- **Header** — Refresh Catalog button with live status text.

See [PREFAB_SETUP.md](../../_MolcaSDK/Code/Scripts/UI/ContentPackage/PREFAB_SETUP.md) for the full prefab hierarchy and Inspector wiring.

---

## Editor — Content Package Manager

The custom inspector on `ContentPackageSettings` provides a full management UI.

### Left Panel — Package List

- Search bar filters by ID or display name.
- Each card shows a health dot, display name, package ID, and runtime status (in Play Mode).
- Click a card to select it for editing.
- **+ New Package** adds a new entry.
- **Delete** removes the selected package (with confirmation).

### Right Panel — Package Detail

Sections:

- **Identity** — Package ID, Display Name.
- **Addressables Labels** — Label picker integrated with Addressables settings. Validity dots show whether each label exists in the catalog. **Scan Assets** walks `AssetDatabase.GetDependencies` to calculate real source file sizes (for reference; accurate bundle sizes come from build output).
- **Metadata** — Version, Description, Author, Tags. These are authoring seeds — the remote manifest supersedes them at runtime.
- **Dependencies** — Add/remove dependency entries.
- **Flags** — Visible, Required toggles.
- **Runtime Status** *(Play Mode only)* — Live status badge, download progress bar, error message.

### Bottom — System Settings

Expand **System Settings** to configure:

- **Check for Updates** — whether to refresh the catalog on startup.
- **Catalog URL** / **Packages Manifest URL** — for the legacy delivery path; set these yourself. Builds no longer write them back.
- **Max Retry Attempts**, **Verbose Logging**.
- Tools: **Import Manifest JSON**, **Validate Configs**, **Export JSON**, **Reset Settings to Defaults**.

---

## Publishing content

Publishing goes through the **Content workspace in the Molca Hub** (Window > Molca Hub > Content). Bundles are
uploaded straight to Molca-managed storage using short-lived presigned URLs: no storage credential ever enters
this project, and no Molca credential is ever attached to a storage request.

The Hub is where this lives rather than the inspector because the Hub already holds the project binding and the
developer entitlement that authorize a publish. An inspector that could publish without knowing which project it
was pointed at was the problem, not the convenience.

### Tabs

| Tab | What it is for |
|---|---|
| **Packages** | Every package definition, its validation findings, and — once a build layout exists — its real bundle ownership and download size. |
| **Release** | The bound project, target platform, content version, app compatibility range, and changelog for the next release. |
| **Verify** | Runs the shared validation engine over the configuration, then over a clean build. |
| **Publish** | Uploads, finalizes, and optionally promotes. Lists every blocker first; nothing uploads until they clear. |

### Publishing

1. **Verify > Build Clean and Verify.** Builds Addressables into a fresh staging directory with the build layout
   enabled, then resolves each package's bundles from that layout. Sizes and ownership come from the layout, never
   from filenames — a package's download size includes the dependency bundles a player actually fetches.
2. **Release.** Set the content version (SemVer), the app compatibility range, and a changelog. Leave the maximum
   app version empty unless the content is known to break on a newer app.
3. **Publish > Publish Draft.** Uploads and verifies without changing what players resolve. This is the default.
4. **Publish and Promote** when you want it live. It asks first, because it changes what every player resolves on
   their next launch.

The channel is not a field. It is resolved server-side from your build token's policy, so a client cannot widen
its own access by asking.

### Release checklist

```
[ ] Bump package version(s) for changed content
[ ] Verify > Build Clean and Verify — no blocking errors
[ ] Release > set content version, compatibility range, changelog
[ ] Publish Draft, and confirm the release verifies server-side
[ ] Promote when you are ready for players to receive it
```

### Legacy delivery (schema v1)

The **Build & Deploy** panel in the `ContentPackageSettings` inspector remains for projects still on the flat
`packages.json` path, and is retired at the end of the compatibility window. Two things it no longer does:

- It does **not** write the resolved catalog and manifest URLs back into `ContentPackageSettings`. A build used to
  mutate that shared, version-controlled asset to whichever machine built last. Set **Catalog URL** and
  **Packages Manifest URL** yourself for that path.
- It does **not** rewrite the Addressables profile. If the profile disagrees with the build config, the build says
  so and lets you fix it in the Addressables Profiles window.

The storage-provider assets and the CLI deploy step are gone. They shelled out to an external `aws`/`gcloud`
binary configured by an asset in the project, which is exactly the credential handling the release protocol
exists to remove.


### Troubleshooting

**"CLI not found in PATH"**
Install the required CLI tool and restart the Unity Editor (it inherits PATH at launch time).

**"Build output folder not found"**
Run a build first. Verify and deploy require a completed build.

**"Storage provider not configured"**
Assign a storage provider asset to the Build Config.

**Deploy succeeds but app loads old content**
The catalog `.hash` may be cached. Call `pkg.RefreshCatalogAsync()` or enable **Check for Updates** in System Settings to force a re-check on startup.

**Content update build produces an empty diff**
Nothing changed since the last full build. New assets must be in a group marked **Can Change Post Release**.

**`packages.json` has zero bundle sizes**
The bundle file naming convention may not match the group name. Check the log for `[AddressablesBuild] Package '...': 0 B` and verify group names match the prefix pattern (`groupname_assets_all_<hash>.bundle`).

---

## System Settings Reference

| Setting | Default | Description |
|---|---|---|
| **Check for Updates** | true | Refresh catalog and fetch remote manifest on every startup. |
| **Max Retry Attempts** | 3 | Total attempts for a failed package download or remote-manifest fetch (1 = no retry). Retries use exponential backoff between attempts. |
| **Verbose Logging** | false | Emit detailed `[PackageService]` logs to the console. |
| **Catalog URL** | *(auto)* | Remote URL for Addressables catalog updates. Auto-populated after build. |
| **Packages Manifest URL** | *(auto)* | URL of `packages.json`. Auto-populated after build. |

Hard-coded operational constants (not configurable):

| Constant | Value | Applies to |
|---|---|---|
| `DownloadTimeoutSeconds` | 300 s | Per-attempt timeout for a package download; a timeout is a retryable failure. |
| `InitialRetryDelay` | 1 s | Backoff before the first retry. |
| `MaxRetryDelay` | 30 s | Upper bound for the doubling backoff. |
