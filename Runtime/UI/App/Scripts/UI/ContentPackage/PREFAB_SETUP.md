# ContentPackageManagerUI — Prefab Setup

Two scripts need prefabs wired in the Inspector. Below is the exact hierarchy and component layout to build.

Buttons use `ColorIDButton` (the Molca button component) in toggle or normal mode. The list container uses a `ColorIDButtonGroup` configured as radio (single selection, `allowSwitchOff = false`).

---

## ContentPackageManager Prefab

```
ContentPackageManager              ← Canvas (Screen Space - Overlay) + ContentPackageManagerUI
  Background                       ← Image (dark panel, full screen)
  Root                             ← Vertical Layout Group, padding 12
    Header                         ← Horizontal Layout Group, height 40
      Title                        ← TextMeshPro "Content Packages"        →  _titleLabel
      Spacer                       ← Flexible Space element
      StatusLabel                  ← TextMeshPro (small, right-align)       →  _statusLabel
      RefreshButton                ← ColorIDButton                          →  _refreshButton
        Label                      ← TextMeshPro "Refresh Catalog"          →  _refreshButtonLabel
    Body                           ← Horizontal Layout Group, flex height
      LeftPanel                    ← Vertical Layout Group, width 260
        ScrollView                 ← ScrollRect, vertical only
          Viewport                 ← Mask
            Content                ← Vertical Layout Group + Content Size Fitter
                                      ColorIDButtonGroup (radio, allowSwitchOff=false)
                                      →  _listContainer  /  _listButtonGroup
        EmptyLabel                 ← TextMeshPro "No packages configured."  →  _emptyLabel
      Divider                      ← Image (thin vertical line), width 1
      DetailPanel                  ← Vertical Layout Group, flex width       →  _detailPanel
        DetailName                 ← TextMeshPro bold large                  →  _detailName
        DetailId                   ← TextMeshPro small muted                 →  _detailId
        StatusRow                  ← Horizontal Layout Group
          StatusLabel              ← TextMeshPro "Status:"
          DetailStatus             ← TextMeshPro (colored)                   →  _detailStatus
        MetaRow                    ← Horizontal Layout Group
          VersionLabel             ← TextMeshPro "Version:"
          DetailVersion            ← TextMeshPro                             →  _detailVersion
          SizeLabel                ← TextMeshPro "Size:"
          DetailSize               ← TextMeshPro                             →  _detailSize
          DownloadSizeLabel        ← TextMeshPro "Download:"
          DetailDownloadSize       ← TextMeshPro (shown before install)      →  _detailDownloadSize
        TagsRow                    ← Horizontal Layout Group
          TagsLabel                ← TextMeshPro "Tags:"
          DetailTags               ← TextMeshPro small muted                 →  _detailTags
        DetailDescription          ← TextMeshPro (word wrap, 3 lines max)    →  _detailDescription
        ChangelogRow               ← Vertical Layout Group                   →  _detailChangelogRow
                                      (SetActive false by default)
          ChangelogLabel           ← TextMeshPro bold "What's New"
          DetailChangelog          ← TextMeshPro (word wrap, 4 lines max)    →  _detailChangelog
        DepsLabel                  ← TextMeshPro bold "Dependencies"
        DetailDependencies         ← TextMeshPro small                       →  _detailDependencies
        UsedByLabel                ← TextMeshPro bold "Required By"
        DetailUsedBy               ← TextMeshPro small                       →  _detailUsedBy
        ErrorRow                   ← Horizontal Layout Group                 →  _errorRow
                                      (SetActive false by default)
          DetailErrorMessage       ← TextMeshPro red                         →  _detailErrorMessage
        ProgressRow                ← Vertical Layout Group                   →  _progressRow
                                      (SetActive false by default)
          ProgressSlider           ← Slider, no handle                       →  _progressSlider
          ProgressLabel            ← TextMeshPro "0%"                        →  _progressLabel
        ButtonRow                  ← Horizontal Layout Group
          InstallButton            ← ColorIDButton green                     →  _installButton
            Label                  ← TextMeshPro "Install"                   →  _installButtonLabel
          UninstallButton          ← ColorIDButton red                       →  _uninstallButton
            Label                  ← TextMeshPro "Uninstall"
          UpdateAllButton          ← ColorIDButton blue                      →  _updateAllButton
                                      (SetActive false by default — shown when updates exist)
            Label                  ← TextMeshPro "Update All"                →  _updateAllButtonLabel
          CancelButton             ← ColorIDButton grey                      →  _cancelButton
                                      (SetActive false by default)
            Label                  ← TextMeshPro "Cancel"
    Footer                         ← Horizontal Layout Group, height 28
      InstalledCountLabel          ← TextMeshPro small                       →  _footerInstalledCountLabel
      Spacer                       ← Flexible Space
      InstalledSizeLabel           ← TextMeshPro small muted                 →  _footerInstalledSizeLabel
```

---

## PackageListItem Prefab

```
PackageListItem                    ← Horizontal Layout Group, height 60
                                      ColorIDButton (toggle mode)            →  _button
  StatusDot                        ← Image (circle sprite), width 12         →  _statusDot
  Info                             ← Vertical Layout Group, flex width
    NameLabel                      ← TextMeshPro bold                        →  _nameLabel
    IdLabel                        ← TextMeshPro small muted                 →  _idLabel
  RightColumn                      ← Vertical Layout Group, width 90, right-align
    StatusLabel                    ← TextMeshPro small                       →  _statusLabel
    SizeLabel                      ← TextMeshPro small muted                 →  _sizeLabel
    ProgressBar                    ← Image (fill, horizontal), height 4      →  _progressBar
                                      (SetActive false by default)
```

---

## ContentPackageManagerUI Inspector Wiring

After building the prefab, assign all fields on the `ContentPackageManagerUI` component:

### Header

| Field | Object |
|---|---|
| Title Label | Root/Header/Title |
| Refresh Button | Root/Header/RefreshButton |
| Refresh Button Label | Root/Header/RefreshButton/Label |
| Status Label | Root/Header/StatusLabel |

### Package List

| Field | Object |
|---|---|
| List Container | Root/Body/LeftPanel/ScrollView/Viewport/Content |
| List Button Group | Root/Body/LeftPanel/ScrollView/Viewport/Content |
| List Item Prefab | PackageListItem prefab asset |
| Empty Label | Root/Body/LeftPanel/EmptyLabel |

### Detail Panel

| Field | Object |
|---|---|
| Detail Panel | Root/Body/DetailPanel |
| Detail Name | Root/Body/DetailPanel/DetailName |
| Detail Id | Root/Body/DetailPanel/DetailId |
| Detail Version | Root/Body/DetailPanel/MetaRow/DetailVersion |
| Detail Size | Root/Body/DetailPanel/MetaRow/DetailSize |
| Detail Download Size | Root/Body/DetailPanel/MetaRow/DetailDownloadSize |
| Detail Tags | Root/Body/DetailPanel/TagsRow/DetailTags |
| Detail Description | Root/Body/DetailPanel/DetailDescription |
| Detail Changelog Row | Root/Body/DetailPanel/ChangelogRow |
| Detail Changelog | Root/Body/DetailPanel/ChangelogRow/DetailChangelog |
| Detail Status | Root/Body/DetailPanel/StatusRow/DetailStatus |
| Detail Dependencies | Root/Body/DetailPanel/DetailDependencies |
| Detail Used By | Root/Body/DetailPanel/DetailUsedBy |
| Detail Error Message | Root/Body/DetailPanel/ErrorRow/DetailErrorMessage |
| Error Row | Root/Body/DetailPanel/ErrorRow |
| Progress Row | Root/Body/DetailPanel/ProgressRow |
| Progress Slider | Root/Body/DetailPanel/ProgressRow/ProgressSlider |
| Progress Label | Root/Body/DetailPanel/ProgressRow/ProgressLabel |

### Action Buttons

| Field | Object |
|---|---|
| Install Button | Root/Body/DetailPanel/ButtonRow/InstallButton |
| Install Button Label | Root/Body/DetailPanel/ButtonRow/InstallButton/Label |
| Uninstall Button | Root/Body/DetailPanel/ButtonRow/UninstallButton |
| Update All Button | Root/Body/DetailPanel/ButtonRow/UpdateAllButton |
| Update All Button Label | Root/Body/DetailPanel/ButtonRow/UpdateAllButton/Label |
| Cancel Button | Root/Body/DetailPanel/ButtonRow/CancelButton |

### Footer

| Field | Object |
|---|---|
| Footer Installed Count Label | Root/Footer/InstalledCountLabel |
| Footer Installed Size Label | Root/Footer/InstalledSizeLabel |

### Status Colors

Configure color swatches in the Inspector to match your theme:

| Field | Suggested color |
|---|---|
| Color Available | `#8C8C8C` — grey |
| Color Downloading | `#FFBF00` — amber |
| Color Installed | `#1ACC1A` — green |
| Color Failed | `#FF3F3F` — red |
| Color Update | `#4DB8FF` — blue |

---

## Usage in a Scene

1. Drop the `ContentPackageManager` prefab into a scene.
2. `ContentPackageManagerUI` uses `[Inject]` to receive `PackageSubsystem` — no manual wiring needed.
3. Toggle the root `GameObject` active/inactive to show or hide the UI.

```csharp
[SerializeField] private GameObject _packageManagerUI;

void OpenPackageManager()  => _packageManagerUI.SetActive(true);
void ClosePackageManager() => _packageManagerUI.SetActive(false);
```

---

## Behaviour Notes

- `WaitForInitialization()` is called in `Start()` — safe to activate the UI at any point after the scene loads.
- **Download size** is fetched asynchronously on package selection for packages that are not yet installed. It clears immediately when the selection changes so stale values are never shown.
- **Update All** appears automatically when one or more packages have `UpdateAvailable` status. It installs them sequentially and reports a summary on completion.
- **Cancel** cancels the active install/uninstall operation. It does not affect in-flight catalog refreshes.
- Status updates arrive through `PackageService` events — no polling.
- Bundle sizes displayed in the UI come from the remote manifest (`packages.json`). They show `""` until a catalog refresh has fetched the manifest at least once.
