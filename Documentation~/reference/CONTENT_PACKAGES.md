---
title: Content Packages (Addressable DLC)
category: Content & Assets
order: 600
---

# Content Packages (Addressable DLC)

The Content Package system is Molca's runtime downloadable-content layer, built on Unity Addressables.
It installs, updates, and removes remotely-hosted asset packages with dependency resolution, a
cache/storage budget, and a bounded-concurrency download queue. A `PackageSubsystem` drives it, a
`PackageService` performs the operations, and packages are declared as data on a
`ContentPackageSettings` module.

## PackageSubsystem

`PackageSubsystem` is a `RuntimeSubsystem` (place it on the RuntimeManager prefab; recommended init
priority ~150 — after core systems). Resolve it the usual way and reach the service through it:

```csharp
var packages = RuntimeManager.GetSubsystem<PackageSubsystem>();
PackageService service = packages.PackageService;
PackageDownloadQueue queue = packages.DownloadQueue;
```

On bootstrap the subsystem warms up Addressables and the remote catalog on the critical path, then
kicks off **required-package** installs off the critical path (keyed on `ShutdownToken`, so a large
required download never stalls boot or trips the init timeout).

## Installing & managing packages

`PackageService` exposes the package operations as `Awaitable`-returning, cancellable methods. Each
returns an `OperationResult` with `Success`, `WasCancelled`, and `ErrorMessage`:

```csharp
var result = await service.InstallPackageAsync(packageId, progress: null, ct);
if (result.Success)
    Debug.Log("Installed");
else if (!result.WasCancelled)
    Debug.LogError(result.ErrorMessage);
```

| Method | Purpose |
|---|---|
| `InstallPackageAsync(packageId, progress, ct)` | Download + install a package (progress optional). |
| `UninstallPackageAsync(...)` | Remove an installed package. |
| `UpdatePackageAsync(...)` | Update to a newer catalog version. |
| `GetDownloadSizeAsync(packageId)` | Bytes to download before install. |
| `RefreshCatalogAsync(ct)` | Re-fetch the remote catalog. |
| `FreeUpSpaceAsync(...)` / `EnforceCacheBudgetAsync(ct)` | Reclaim storage against the cache budget. |
| `SwitchContentVersionAsync(...)` | Move between content versions. |
| `GetPackageState(packageId)` | Current `PackageState` (its `status` is a `PackageStatus`). |

`PackageStatus` values include `Available`, `Downloading`, `Installed`, and `Failed`. Use the
`PackageReference` struct (with `PackageId` / `IsValid`) to reference a package from a serialized field.

### Download queue

For multiple installs, schedule through `PackageDownloadQueue` rather than awaiting each call directly.
It enforces bounded concurrency (`ContentPackageSettings.MaxConcurrentDownloads`), supports
pause/resume, and reports aggregate progress by raising events through the
[EventDispatcher](EVENTS.md).

## Declaring packages — ContentPackageSettings

Packages are authored as data on `ContentPackageSettings`, a [SettingModule](SETTINGS.md). Add it to
`GlobalSettings.modules`; the subsystem reads it via `GlobalSettings.GetModule<ContentPackageSettings>()`.

Each entry in `packageConfigs` is a `PackageConfig`:

| Field | Meaning |
|---|---|
| `packageId` | Stable id used by every service call. |
| `displayName` / `metadata` | Presentation + version/author/tags. |
| `isRequired` | Auto-installed at bootstrap if not already present. |
| `isVisible` | Whether it appears in package-management UI. |
| `dependencies` | Other packages that must install first. |
| `addressableLabels` | Addressable labels that make up the package's content. |

Module-level `MaxConcurrentDownloads` and `EnableVerboseLogging` tune the queue and diagnostics.

## Remote storage

Content is fetched from an Addressables remote catalog hosted on cloud object storage. Core ships
storage-provider integrations for **AWS S3**, **Cloudflare R2**, and **Google Cloud Storage** under
`Runtime/ContentPackage/Storage/`.

## Diagnostics

The `content-package-valid` Molca Doctor check validates the content-package configuration (see
[Extending Molca Doctor](DOCTOR_CHECKS.md)); run it after editing `ContentPackageSettings`.

## See also

- [Networking: HttpClient & Requests](NETWORKING.md)
- [Settings: Project, Global & Modules](SETTINGS.md)
- [Runtime Subsystems](SUBSYSTEMS.md)
- [Async Contract](ASYNC_CONTRACT.md)
