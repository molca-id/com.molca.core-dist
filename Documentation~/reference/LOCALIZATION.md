---
title: Localization
category: Localization & Audio
order: 700
---

# Localization

Molca's localization layer sits on top of Unity's **Localization** package. `LocalizationManager` (a
`RuntimeSubsystem`) owns locale selection and change notifications; `LocalizedText` binds a TMP label to
a `LocalizedString`; and `DynamicLocalization` handles strings whose translations are authored on a
component or supplied at runtime. Which languages exist, and which one is active, is configured through a
`LocalizationModule` settings asset.

## Pieces at a glance

| Type | Kind | Role |
|---|---|---|
| `LocalizationManager` | `RuntimeSubsystem` (`Runtime/Localization/`) | Applies locales, broadcasts language changes, backs the Dynamic string table. |
| `LocalizationModule` | `SettingModule` ScriptableObject | Declares the supported `Languages` and stores the active one in `LocalizationState`. |
| `LocalizedText` | `MonoBehaviour` (`[RequireComponent(typeof(TextMeshProUGUI))]`) | Displays a `LocalizedString` on a TMP label and restyles/refreshes on locale change. |
| `LocalizedTextStyleInfo` | `ScriptableObject` (`IReferenceable`) | Reusable font/style/size preset applied by `LocalizedText`. |
| `DynamicLocalization` | `[Serializable]` plain class | Embeddable field for per-component or runtime-authored translations. |
| `DynamicLocalizationEntry` | `[Serializable]` record | One `languageCode` + `text` row inside a `DynamicLocalization`. |

All types live in the `Molca.Localization` namespace under `Packages/com.molca.core/Runtime/Localization/`.

## Configuring languages — `LocalizationModule`

Locale selection is driven by a `LocalizationModule` settings asset, authored through
**Create → Molca → Settings → Localization**. It carries a `Languages` array of entries:

```csharp
[Serializable]
public struct LanguageEntry
{
    public string Name;   // display name
    public string Code;   // BCP-47 code, e.g. "en", "id"
    public Sprite Flag;   // optional flag sprite
}
```

The **first** entry is treated as the default language. The active code is persisted in the module's
`LocalizationState` (`ActiveLanguage`), so a chosen locale survives across sessions. Useful members:

| Member | Returns |
|---|---|
| `LanguageCode` | `string[]` of all configured codes (derived fresh each access). |
| `ActiveLanguage` | The current active code. |
| `ActiveLanguageEntry` | The full `LanguageEntry` for the active code. |
| `GetFlagForLanguage(code)` | The flag sprite for a code, or `null`. |
| `SetLanguage(int index)` / `SetLanguage(string code)` | Sets and saves the active language. |

See [Settings](SETTINGS.md) for how `SettingModule` / `SettingState` assets are registered and loaded.

## Reading and switching the locale — `LocalizationManager`

`LocalizationManager` is a subsystem, so resolve it the usual way — never `FindObjectOfType`:

```csharp
// Injected into a subsystem, MonoBehaviour, or DI-created object.
[Inject] private LocalizationManager _localization;

// Or, from a non-injectable context:
var localization = RuntimeManager.GetSubsystem<LocalizationManager>();
```

For code that has no instance handy, static entry points route through the service locator:

| Static member | Purpose |
|---|---|
| `LocalizationManager.CurrentLanguage` | BCP-47 code of the active locale (empty if not ready). |
| `LocalizationManager.DefaultLanguageCode` | First code defined in the module. |
| `LocalizationManager.SetLanguage(string lang)` | Switches the active locale. |
| `LocalizationManager.GetLocalizedString(collection, entryKey)` | Builds a `LocalizedString` for any string-table collection + entry. |

Instance methods cover the rest:

| Instance member | Purpose |
|---|---|
| `GetAvailableLanguages()` | All BCP-47 codes registered in `LocalizationSettings`. |
| `HasLanguage(code)` | `true` if the code is a registered locale. |
| `GetLocalizedStringAsync(key, languageCode = null)` | Resolves a Dynamic-table key; returns the key itself as fallback. |
| `RegisterText` / `UnregisterText` | Subscribe a `LocalizedText` to language-change refreshes. |
| `RegisterDynamicLocalization` / `UnregisterDynamicLocalization` | Same, for `DynamicLocalization`. |

When the locale changes, the manager updates the module's active language, dispatches
`TypedEvents.LanguageChanged` with the new code, and refreshes every registered `LocalizedText` and
`DynamicLocalization`. Listen for the switch through the event dispatcher rather than polling:

```csharp
TypedEvents.LanguageChanged.Register(this, code =>
{
    // React to the new active language code.
});
```

Because it is a subsystem, wait for the runtime before touching it:

```csharp
private async void Start()
{
    await RuntimeManager.WaitForInitialization();
    if (this == null) return;
    LocalizationManager.SetLanguage("id");
}
```

## Binding a label — `LocalizedText`

Add `LocalizedText` to a GameObject that has a `TextMeshProUGUI` (the `[RequireComponent]` enforces it),
assign a `LocalizedString` and an optional `LocalizedTextStyleInfo`, and the component handles the rest:
it registers with the manager, subscribes to the string's `StringChanged`, fetches the translation
asynchronously, and rebuilds layout when the text changes. You can also drive it from code:

```csharp
// Point the label at a different table entry.
label.SetLocalizedString(
    LocalizationManager.GetLocalizedString("UI", "start-button"));

// Swap its font/size preset.
label.SetStyle(myStyleInfo);
```

`LocalizedTextStyleInfo` is a ScriptableObject preset (**Create → Molca → Localization → Text Style**)
holding font, `FontStyles`, and min/preferred/max size. It implements `IReferenceable`, so it carries a
stable `RefId` and can be resolved through the reference system. The [UI Tokens](UI_TOKENS.md) layer
names these presets as its `text/*` tokens.

## Runtime and per-component translations — `DynamicLocalization`

`DynamicLocalization` is a `[Serializable]` field you embed on your own components (not a MonoBehaviour).
It has two modes, chosen by the `useLocalizedString` flag:

- **Authored translations** (default): a `translations` list of `DynamicLocalizationEntry`
  (`languageCode` + `text`). On init these are pushed into the manager's **Dynamic** string table under a
  key you supply, so they participate in Unity's localization pipeline like any other entry.
- **`LocalizedString` mode** (`useLocalizedString = true`): the field wraps an existing `LocalizedString`
  and the `translations` list is ignored.

Initialize before you resolve. `InitAsync` registers the field and pre-populates the Dynamic table;
`Init` is a fire-and-forget `async void` shim for entry points that cannot await:

```csharp
[SerializeField] private DynamicLocalization _greeting;

private async void Start()
{
    try
    {
        // Await when you resolve immediately afterwards — this closes the
        // init/resolve race that the async-void Init overload exposes.
        await _greeting.InitAsync("scene-intro-greeting");
        if (this == null) return;

        string text = await _greeting.GetLocalizedString();
        // ... display text ...
    }
    catch (System.Exception e) { Debug.LogError(e); }
}
```

`GetLocalizedString()` resolves through a fallback chain — Unity's localization system, then the local
`translations` for the current language, then the default language, then the first authored entry. The
synchronous `String` property returns the last resolved value without triggering a fetch (empty while
`disabled`). `SetTextForLanguage(text, languageCode)` updates a translation and, in play mode, pushes it
into the Dynamic table; it refuses a blank language code because such a row is unmatchable at runtime.

## Diagnostics

Two [Doctor checks](DOCTOR_CHECKS.md) guard `DynamicLocalization` usage:

| Check id | What it flags |
|---|---|
| `dynamic-localization-locale-invalid` | Serialized translation rows (in prefabs, ScriptableObjects, and open scenes) whose language code is **blank** (Warning — unmatchable at runtime) or **not defined in any `LocalizationModule`** (Error — never resolves). |
| `dynamic-localization-init-contract` | Source that calls the fire-and-forget `Init(...)` and then `await`s `GetLocalizedString()` on the same field (a resolve race), or reads `.String` / `GetLocalizedString()` on a field that is never initialized (Warning — only authored fallback returns). |

The locale-validity check compares against every `LocalizationModule` in the project (a code is valid if
any module defines it) and only scans loaded scenes, so run it with the relevant scenes open. Suppress a
false positive on the init-contract check with a `doctor:ignore` marker on the line.

## See also

- [Audio](AUDIO.md)
- [Settings](SETTINGS.md)
- [UI Tokens](UI_TOKENS.md)
- [Subsystems](SUBSYSTEMS.md)
