---
title: "Settings: Project, Global & Modules"
category: Settings
order: 800
---

# Settings: Project, Global & Modules

Molca has two layers of settings. **`MolcaProjectSettings`** is the single per-project configuration
asset (company/project identity, the `RuntimeManager` prefab, `GlobalSettings`, bootstrap hooks).
**`GlobalSettings`** owns an array of **`SettingModule`** assets — one per feature area (audio, canvas
scale, content packages, …) — each pairing *authored defaults* (on the ScriptableObject) with *mutable
runtime state* (on a plain C# `SettingState`). This split is the framework's answer to the rule that
ScriptableObjects are read-only config: defaults live on the SO, everything that changes at play time
lives on the state object.

## MolcaProjectSettings

The project-wide settings singleton, resolved through `MolcaProjectSettings.Instance`.

```csharp
var settings = MolcaProjectSettings.Instance;
string project = settings.ProjectName;
Sprite logo    = settings.ProjectLogo;
```

| Member | Type | Purpose |
|---|---|---|
| `Instance` | `MolcaProjectSettings` | The resolved settings asset (see loading below). |
| `GlobalSettings` | `GlobalSettings` | The assigned global-settings asset. |
| `RuntimeManager` | `RuntimeManager` | The RuntimeManager prefab bootstrap instantiates. |
| `CompanyName` / `ProjectName` | `string` | Product identity. |
| `ProjectLogo` | `Sprite` | Splash/branding sprite. |
| `ProjectId` | `string` | Stable project identifier. |
| `BootstrapExtensions` | `IReadOnlyList<BootstrapExtension>` | Layer-specific bootstrap hooks (see [Runtime Manager & Bootstrap](RUNTIME_MANAGER.md)). |

The **live** asset lives in *consumer space* at `Assets/_Molca/Settings/MolcaProjectSettings.asset`,
never inside the read-only Core package. On first access in the editor it is seeded by cloning the
package's read-only default; at runtime it is loaded through **Addressables** under the key
`MolcaProjectSettings` (WebGL uses the async `LoadAsync()` path). Editor-only fields live in a separate
`MolcaProjectSettings.Editor.cs` partial and are not compiled into player builds.

## GlobalSettings

A ScriptableObject holding the ordered `SettingModule[]`. Access it statically:

```csharp
// GlobalSettings.main == MolcaProjectSettings.Instance.GlobalSettings
var audio = GlobalSettings.GetModule<AudioSettings>();   // null if not configured
GlobalSettings.main.SaveAllSettings();                    // persist every module
```

| Member | Purpose |
|---|---|
| `GlobalSettings.main` | Shortcut to `MolcaProjectSettings.Instance.GlobalSettings`. |
| `GetModule<T>()` | Resolves a configured module by type (cached after `Initialize`), or `null`. |
| `SaveAllSettings()` / `LoadAllSettings()` | Persist / reload every module through its `SettingState`. |
| `Quality` / `SetQuality(int)` | Read/apply the persisted `QualitySettings` level. |

`GlobalSettings.Initialize()` runs during bootstrap: it calls `Initialize()` then `CreateState()` on
each module and caches them by type. `GetModule<T>()` is safe to call before configuration completes —
it returns `null` rather than throwing.

## SettingModule — authored defaults

`SettingModule` (in `Molca.Settings`) is the base class for a feature's configuration. Subclass it as a
ScriptableObject that holds **defaults only**; never mutate its `SerializeField`s at play time.

- **Folder:** `Assets/YourProject/ScriptableObjects/` (the asset) + `Assets/YourProject/Scripts/` (the class).
- **Base class:** `Molca.Settings.SettingModule`.
- **Registration:** add the asset to `GlobalSettings.modules`; resolve it with `GlobalSettings.GetModule<T>()`.

| Member | Purpose |
|---|---|
| `SettingId` / `ModuleKey` | Persistence namespace, derived from the type's full name in `Initialize()`. |
| `State` | The paired `SettingState` (owned by `GlobalSettings`), or `null` for read-only modules. |
| `CreateState()` | Factory for the paired state; override to opt into runtime mutation. Default `null`. |
| `LoadSettings()` / `SaveSettings()` | Abstract; move values between `State` and the backing store. |
| `FieldKey(name)`, `SaveFloat/LoadFloat`, `SaveInt/LoadInt`, `SaveString/LoadString` | `PlayerPrefs` helpers namespaced under `ModuleKey`. |
| `ResetToDefaults()` | Restores defaults (re-runs `LoadSettings()` unless overridden). |

## SettingState — mutable runtime state

`SettingState` is a plain C# object (not a ScriptableObject) holding the values that change while the
app runs. `GlobalSettings` calls `Load(owner)` during bootstrap and `Save(owner)` on shutdown; a module
may also call `Save` from a property setter for immediate write-through.

```csharp
public class DifficultySettings : SettingModule
{
    [SerializeField] private int _defaultLevel = 1;   // authored default (read-only at runtime)

    public override SettingState CreateState() => new DifficultyState();
    public override void LoadSettings() => (State as DifficultyState)?.Load(this);
    public override void SaveSettings() => (State as DifficultyState)?.Save(this);

    public int Level
    {
        get => ((DifficultyState)State).Level;
        set { ((DifficultyState)State).Level = value; SaveSettings(); }
    }

    public int DefaultLevel => _defaultLevel;
}

public class DifficultyState : SettingState
{
    public int Level;
    public override void Load(SettingModule owner) =>
        Level = owner.LoadInt(nameof(Level), ((DifficultySettings)owner).DefaultLevel);
    public override void Save(SettingModule owner) => owner.SaveInt(nameof(Level), Level);
}
```

`CanvasScaleModule` and `ContentPackageSettings` (see [Content Packages](CONTENT_PACKAGES.md)) are two
shipped modules that follow this pattern.

## Editor-only settings

Editor tooling has its own, unrelated store: editor settings assets are created through
`MolcaEditorSettingsAsset.GetOrCreate<T>(fileName)` and live under `Assets/_Molca/Editor/`. That is a
maintainer/tooling concern — keep runtime configuration in the `SettingModule`/`SettingState` system
described above.

## See also

- [Runtime Subsystems](SUBSYSTEMS.md)
- [Runtime Manager & Bootstrap](RUNTIME_MANAGER.md)
- [Content Packages (Addressable DLC)](CONTENT_PACKAGES.md)
- [Localization](LOCALIZATION.md)
- [Audio](AUDIO.md)
