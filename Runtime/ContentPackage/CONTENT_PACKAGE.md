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
| **Version** | Authoring seed written into `packages.json` by the build pipeline. Superseded by remote manifest at runtime — bump this before each CDN push. |
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
3. Set the group's **Build & Load Paths** to your remote CDN profile variables.
4. Assign a label matching your package (e.g. `fire-training-env`) to all entries in the group.
5. Back in the Content Package Manager, use **Pick Labels…** to select that label.
6. Click **Scan Assets** to preview asset count and approximate source file size (accurate bundle size is written to `packages.json` at build time).

> **Tip:** Enable **Can Change Post Release** on the group schema. This enables Unity's catalog update workflow and is required for `BuildContentUpdate` to detect changed groups.

---

## Remote Package Manifest

`packages.json` is a platform-specific JSON file written to the build output folder by the build pipeline and deployed to CDN alongside the Addressables catalog. At runtime, `PackageService` fetches it during `RefreshCatalogAsync` and uses it as the authoritative source for:

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

Both remote URLs are **auto-populated** after every successful build — no manual entry is needed:

- **Packages Manifest URL** — `{remoteLoadURL}/{platform}/packages.json`
- **Catalog URL** — `{remoteLoadURL}/{platform}/catalog_{hash}.json` (exact filename read from disk after build)

Both fields are shown as read-only in **System Settings** in the Content Package Manager inspector. They update automatically each build because the catalog hash changes.

To use a different URL (e.g. staging vs production), configure a separate Build Config asset per environment and assign the correct one before building.

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
- **Catalog URL** / **Packages Manifest URL** — read-only; auto-populated after each build.
- **Max Retry Attempts**, **Verbose Logging**.
- Tools: **Import Manifest JSON**, **Validate Configs**, **Export JSON**, **Reset Settings to Defaults**.

---

## Build, Verify & Deploy

The **Build & Deploy** panel (in the Content Package Manager inspector) provides a one-stop workflow. It requires a **Build Config** asset with a **Storage Provider**.

---

### Prerequisites

| Tool | Required for |
|---|---|
| Unity Addressables package | Build step |
| Storage provider CLI (aws, gcloud, etc.) | Deploy step |
| Configured provider credentials | Deploy step |

---

### 1. Create a Build Config

**Assets > Create > Molca > Content Package > Build Config**

| Field | Description |
|---|---|
| **Local Output** | Where Addressables writes bundles. Use `[BuildTarget]` token (e.g. `ServerData/[BuildTarget]`). |
| **Remote Load URL** | Public URL the app uses to load bundles at runtime. Must match the CDN/bucket public URL. Use `[BuildTarget]` token (e.g. `https://cdn.example.com/content/[BuildTarget]`). This is the only URL you configure manually — all other URLs are derived from it automatically. |
| **Storage Provider** | Assign an `AWSS3StorageProvider`, `GCSStorageProvider`, or `CloudflareR2StorageProvider` asset. |

Open the `ContentPackageSettings` asset, expand **Build & Deploy**, and assign the Build Config asset.

---

### 2. Create a Storage Provider

**Assets > Create > Molca > Content Package > Storage > [provider]**

Each provider type has its own fields. See the provider-specific docs:

- [AWS S3](Storage/AWSS3StorageProvider.md)
- [Google Cloud Storage](Storage/GCSStorageProvider.md)
- [Cloudflare R2](Storage/CloudflareR2StorageProvider.md)

Assign the created asset to the **Storage Provider** field on the Build Config.

---

### 3. Configure Addressables Profile Variables

The Build panel writes **Local Output** and **Remote Load URL** into the active Addressables profile. Ensure the profile has these two variables:

| Variable | Purpose |
|---|---|
| `RemoteBuildPath` | Local folder where bundles are written |
| `RemoteLoadPath` | URL baked into the catalog for runtime loading |

Create them in **Window > Asset Management > Addressables > Profiles** if they do not exist.

For each package group set **Build Path → RemoteBuildPath**, **Load Path → RemoteLoadPath**, and enable **Can Change Post Release**.

---

### 4. Build

#### Build Player Content
Full rebuild. Use this:
- The first time you publish content.
- After adding or removing Addressables groups.
- After renaming labels or moving entries between groups.

#### Build Content Update
Incremental rebuild — only groups changed since the last full build. Requires `addressables_content_state.bin` from a prior full build.

> **Rule of thumb:** full build when structure changes, content update when only asset data changes.

After a successful build the pipeline automatically:
1. Writes `packages.json` to the build output folder with per-package `bundleSizeBytes` measured from actual `.bundle` files.
2. Scans the output folder for `catalog_*.json` and updates `_remoteCatalogUrl` in `ContentPackageSettings`.
3. Derives `packages.json` URL from `remoteLoadURL` and updates `_remotePackagesManifestUrl` in `ContentPackageSettings`.

---

### 5. Verify

Click **Run Verification** to check the output:

- Each enabled package is listed with a **green ●** (bundles found) or **red ●** (no bundles).
- Shows bundle count and total size per package.
- Flags packages with no labels or labels missing from the catalog.

| Problem | Fix |
|---|---|
| `no labels configured` | Add Addressables labels in the package detail panel. |
| `no bundles found` | The label has no entries in any group, or the group's build path is wrong. |
| Bundle count too low | Check all expected assets are labeled correctly in the Addressables Groups window. |

---

### 6. Deploy

The Deploy section shows the fully resolved CLI command before running it.

**Recommended workflow:**

1. Enable **Dry Run** on the storage provider, click **Deploy** — review the log.
2. Disable **Dry Run**, click **Deploy** — live upload.
3. Watch the streaming log. Click **Abort** to cancel mid-transfer.

The entire build output folder is uploaded, including:
- All `.bundle` files
- `catalog_*.json` and `catalog_*.hash`
- `packages.json`

#### Typical bucket structure after deploy

```
cdn-bucket/
  content/
    StandaloneWindows64/
      catalog_2026-05-09-10-00-00.json
      catalog_2026-05-09-10-00-00.hash
      packages.json
      fire_training_env_assets_all_<hash>.bundle
      core_assets_all_<hash>.bundle
    Android/
      ...
```

---

### Release Checklist

```
[ ] Bump package version(s) in ContentPackageSettings for changed content
[ ] Run Build Player Content (or Build Content Update for patches)
[ ] Run Verification — all packages green
[ ] Deploy with Dry Run — review log
[ ] Deploy live
[ ] Smoke test: run the app, confirm PackageService fetches new catalog and manifest
```

The Catalog URL and Packages Manifest URL are auto-populated — no manual step needed.

---

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
